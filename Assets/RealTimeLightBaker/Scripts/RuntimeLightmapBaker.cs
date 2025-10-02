using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace RealTimeLightBaker
{
    /// <summary>
    /// Bakes dynamic lighting into target renderer UV2 spaces by drawing each renderer
    /// into its own render texture using a UV-space shader pass.
    /// </summary>
    [ExecuteAlways]
    [DisallowMultipleComponent]
    public sealed class RuntimeLightmapBaker : MonoBehaviour
    {
        [Serializable]
        private sealed class TargetEntry
        {
            public Renderer renderer;
            [Range(128, 4096)] public int lightmapSize = 1024;

            [NonSerialized] public RenderTexture lightmap;
            [NonSerialized] public MaterialPropertyBlock mpb;
            [NonSerialized] public uint originalRenderingLayerMask;
            [NonSerialized] public uint bakeRenderingLayerMask;
        }

        [SerializeField] private List<TargetEntry> targets = new();
        [SerializeField] private Material bakerMaterial;
        [SerializeField] private bool bakeEveryFrame = true;
        [SerializeField] private bool autoApplyToTargets = true;
        [SerializeField] private string runtimeLightmapProperty = "_RuntimeLightmap";
        [SerializeField, Range(128, 4096)] private int defaultLightmapSize = 1024;
        [Header("Optional Dilation")]
        [SerializeField] private Material dilationMaterial;
        [SerializeField] private int dilationPassIndex = 0;

        private static readonly int TempDilationRTId = Shader.PropertyToID("_RuntimeLightmapDilationTemp");
        private static readonly int MainTexId = Shader.PropertyToID("_MainTex");
        private static readonly int MainTexTexelSizeId = Shader.PropertyToID("_MainTex_TexelSize");
        private const int MaxBakeTargets = 31;
        private const uint BakeLayerBaseBit = 1;
        private const PerObjectData BakePerObjectData = PerObjectData.Lightmaps | PerObjectData.LightProbe | PerObjectData.OcclusionProbe | PerObjectData.ShadowMask | PerObjectData.LightData | PerObjectData.LightIndices | PerObjectData.ReflectionProbes;

        private ShaderTagId _shaderTag;
        private bool _shaderTagInitialized;
        private Material _cachedBakerMaterial;
        private bool _forceBake;
        private int _propertyId;

        /// <summary>
        /// Returns the first lightmap render texture (for backward compatibility with previous API).
        /// </summary>
        public RenderTexture LightmapRT => targets.Count > 0 ? targets[0]?.lightmap : null;

        private void Awake()
        {
            EnsureShaderTagInitialized();
        }

        private void OnEnable()
        {
            CachePropertyId();
            EnsureShaderTagInitialized();
            EnsureResources();
            RenderPipelineManager.endCameraRendering += HandleEndCameraRendering;
            _forceBake = true;
        }

        private void OnDisable()
        {
            RenderPipelineManager.endCameraRendering -= HandleEndCameraRendering;
            ReleaseAllRenderTextures();
        }

        private void OnValidate()
        {
            defaultLightmapSize = Mathf.Clamp(Mathf.ClosestPowerOfTwo(defaultLightmapSize), 128, 4096);
            CachePropertyId();
            EnsureShaderTagInitialized();

            if (targets != null)
            {
                foreach (var entry in targets)
                {
                    if (entry == null)
                    {
                        continue;
                    }

                    entry.lightmapSize = Mathf.Clamp(Mathf.ClosestPowerOfTwo(entry.lightmapSize), 128, 4096);
                }
            }

            EnsureResources();
            _forceBake = true;
        }

        /// <summary>
        /// Requests a bake on the next rendering event when <see cref="bakeEveryFrame"/> is disabled.
        /// </summary>
        public void RequestBake()
        {
            _forceBake = true;
        }

        /// <summary>
        /// Returns the runtime lightmap associated with the given renderer, or null if none exists.
        /// </summary>
        public RenderTexture GetLightmap(Renderer renderer)
        {
            if (renderer == null)
            {
                return null;
            }

            foreach (var entry in targets)
            {
                if (entry != null && entry.renderer == renderer)
                {
                    return entry.lightmap;
                }
            }

            return null;
        }

        private void EnsureShaderTagInitialized()
        {
            var currentMaterial = bakerMaterial;
            if (_shaderTagInitialized && currentMaterial == _cachedBakerMaterial)
            {
                return;
            }

            string lightMode = "UniversalForward";
            if (currentMaterial != null)
            {
                var materialLightMode = currentMaterial.GetTag("LightMode", false, lightMode);
                if (!string.IsNullOrEmpty(materialLightMode))
                {
                    lightMode = materialLightMode;
                }
            }

            _shaderTag = new ShaderTagId(lightMode);
            _cachedBakerMaterial = currentMaterial;
            _shaderTagInitialized = true;
        }

        /// <summary>
        /// Replaces the current targets with the provided renderers, using the default lightmap size.
        /// </summary>
        public void SetTargets(Renderer[] newTargets)
        {
            ReleaseAllRenderTextures();
            targets.Clear();

            if (newTargets != null)
            {
                foreach (var renderer in newTargets)
                {
                    if (renderer == null)
                    {
                        continue;
                    }

                    targets.Add(new TargetEntry
                    {
                        renderer = renderer,
                        lightmapSize = Mathf.Clamp(Mathf.ClosestPowerOfTwo(defaultLightmapSize), 128, 4096)
                    });
                }
            }

            _forceBake = true;
        }

        /// <summary>
        /// Sets the baking material and queues a rebake.
        /// </summary>
        public void SetBakerMaterial(Material material)
        {
            bakerMaterial = material;
            _cachedBakerMaterial = null;
            _shaderTagInitialized = false;
            _forceBake = true;
        }

        private void HandleEndCameraRendering(ScriptableRenderContext context, Camera camera)
        {
            if (!ShouldBakeThisCamera(camera) || !ShouldBakeNow())
            {
                return;
            }

            if (!EnsureResources() || targets == null || targets.Count == 0 || bakerMaterial == null)
            {
                return;
            }

            var activeEntries = new List<TargetEntry>(targets.Count);
            try
            {
                int assignedCount = 0;
                foreach (var entry in targets)
                {
                    if (entry == null || entry.renderer == null || entry.lightmap == null)
                    {
                        continue;
                    }

                    if (assignedCount >= MaxBakeTargets)
                    {
                        Debug.LogWarning("RuntimeLightmapBaker: Exceeded maximum supported targets when using DrawRenderers. Remaining targets are skipped.");
                        break;
                    }

                    entry.originalRenderingLayerMask = entry.renderer.renderingLayerMask;
                    entry.bakeRenderingLayerMask = 1u << (int)(BakeLayerBaseBit + assignedCount);
                    entry.renderer.renderingLayerMask = entry.originalRenderingLayerMask | entry.bakeRenderingLayerMask;
                    activeEntries.Add(entry);
                    assignedCount++;
                }

                if (activeEntries.Count == 0)
                {
                    return;
                }

                if (!camera.TryGetCullingParameters(out var cullingParameters))
                {
                    return;
                }

                uint combinedLayerMask = 0u;
                foreach (var entry in activeEntries)
                {
                    combinedLayerMask |= (uint)(1 << entry.renderer.gameObject.layer);
                }

                cullingParameters.cullingMask |= combinedLayerMask;
                cullingParameters.maximumVisibleLights = UniversalRenderPipeline.maxVisibleAdditionalLights;
                cullingParameters.shadowDistance = Mathf.Max(QualitySettings.shadowDistance, camera.farClipPlane);

                var cullingResults = context.Cull(ref cullingParameters);

                var sortingSettings = new SortingSettings(camera) { criteria = SortingCriteria.None };
                var drawingSettings = new DrawingSettings(_shaderTag, sortingSettings)
                {
                    overrideMaterial = bakerMaterial,
                    overrideMaterialPassIndex = 0,
                    perObjectData = BakePerObjectData,
                    enableDynamicBatching = false,
                    enableInstancing = false
                };

                foreach (var entry in activeEntries)
                {
                    var cmd = CommandBufferPool.Get("Runtime UV Lightmap Bake");
                    cmd.SetRenderTarget(entry.lightmap);
                    cmd.SetViewport(new Rect(0, 0, entry.lightmap.width, entry.lightmap.height));
                    cmd.ClearRenderTarget(true, true, Color.clear);
                    context.ExecuteCommandBuffer(cmd);
                    CommandBufferPool.Release(cmd);

                    var filteringSettings = new FilteringSettings(RenderQueueRange.all)
                    {
                        renderingLayerMask = entry.bakeRenderingLayerMask,
                        layerMask = 1 << entry.renderer.gameObject.layer
                    };

                    context.DrawRenderers(cullingResults, ref drawingSettings, ref filteringSettings);

                    if (dilationMaterial != null)
                    {
                        var dilationCmd = CommandBufferPool.Get("Runtime Lightmap Dilation");
                        var desc = entry.lightmap.descriptor;
                        desc.msaaSamples = 1;
                        desc.depthBufferBits = 0;
                        desc.useMipMap = false;
                        desc.autoGenerateMips = false;

                        dilationCmd.GetTemporaryRT(TempDilationRTId, desc, FilterMode.Bilinear);

                        float width = entry.lightmap.width;
                        float height = entry.lightmap.height;
                        var texelSize = new Vector4(1f / width, 1f / height, width, height);
                        dilationCmd.SetGlobalTexture(MainTexId, entry.lightmap);
                        dilationCmd.SetGlobalVector(MainTexTexelSizeId, texelSize);

                        dilationCmd.Blit(entry.lightmap, TempDilationRTId, dilationMaterial, dilationPassIndex);
                        dilationCmd.Blit(TempDilationRTId, entry.lightmap);
                        dilationCmd.ReleaseTemporaryRT(TempDilationRTId);

                        context.ExecuteCommandBuffer(dilationCmd);
                        CommandBufferPool.Release(dilationCmd);
                    }

                    if (autoApplyToTargets)
                    {
                        ApplyToRenderer(entry);
                    }
                }

                context.Submit();
            }
            finally
            {
                foreach (var entry in activeEntries)
                {
                    if (entry != null && entry.renderer != null)
                    {
                        entry.renderer.renderingLayerMask = entry.originalRenderingLayerMask;
                    }
                }
            }

            _forceBake = false;
        }

        private bool ShouldBakeThisCamera(Camera camera)
        {
            if (camera == null)
            {
                return false;
            }

            return camera.cameraType == CameraType.Game || camera.cameraType == CameraType.SceneView;
        }

        private bool ShouldBakeNow()
        {
            if (bakerMaterial == null)
            {
                return false;
            }

            bool hasValidTarget = false;
            foreach (var entry in targets)
            {
                if (entry != null && entry.renderer != null)
                {
                    hasValidTarget = true;
                    break;
                }
            }

            if (!hasValidTarget)
            {
                return false;
            }

            return bakeEveryFrame || _forceBake;
        }

        private bool EnsureResources()
        {
            bool hasValid = false;

            foreach (var entry in targets)
            {
                if (entry == null || entry.renderer == null)
                {
                    continue;
                }

                hasValid = true;

                int size = Mathf.Clamp(Mathf.ClosestPowerOfTwo(entry.lightmapSize), 128, 4096);
                if (entry.lightmapSize != size)
                {
                    entry.lightmapSize = size;
                }

                if (entry.lightmap == null || entry.lightmap.width != size || entry.lightmap.height != size)
                {
                    ReleaseRenderTexture(entry);
                    entry.lightmap = CreateRenderTexture(size);
                }
            }

            return hasValid;
        }

        private RenderTexture CreateRenderTexture(int size)
        {
            if (size <= 0)
            {
                return null;
            }

            var rt = new RenderTexture(size, size, 0, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear)
            {
                name = $"RuntimeLightmapRT_{size}",
                useMipMap = true,
                autoGenerateMips = true,
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear
            };

            rt.Create();
            return rt;
        }

        private void ReleaseAllRenderTextures()
        {
            if (targets == null)
            {
                return;
            }

            foreach (var entry in targets)
            {
                ReleaseRenderTexture(entry);
            }
        }

        private void ReleaseRenderTexture(TargetEntry entry)
        {
            if (entry == null || entry.lightmap == null)
            {
                return;
            }

            if (entry.lightmap.IsCreated())
            {
                entry.lightmap.Release();
            }

            if (Application.isPlaying)
            {
                Destroy(entry.lightmap);
            }
            else
            {
                DestroyImmediate(entry.lightmap);
            }

            entry.lightmap = null;
        }

        private void ApplyToTargets()
        {
            if (targets == null)
            {
                return;
            }

            foreach (var entry in targets)
            {
                if (entry == null || entry.renderer == null || entry.lightmap == null)
                {
                    continue;
                }

                ApplyToRenderer(entry);
            }
        }

        private void ApplyToRenderer(TargetEntry entry)
        {
            entry.mpb ??= new MaterialPropertyBlock();
            entry.renderer.GetPropertyBlock(entry.mpb);
            entry.mpb.SetTexture(_propertyId, entry.lightmap);
            entry.renderer.SetPropertyBlock(entry.mpb);
        }

        private void CachePropertyId()
        {
            if (string.IsNullOrEmpty(runtimeLightmapProperty))
            {
                runtimeLightmapProperty = "_RuntimeLightmap";
            }

            _propertyId = Shader.PropertyToID(runtimeLightmapProperty);
        }
    }
}
