using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;
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

        private sealed class LightState
        {
            public Light light;
            public uint originalRenderingLayerMask;
            public UniversalAdditionalLightData additionalLightData;
            public bool originalCustomShadowLayers;
            public RenderingLayerMask originalRenderingLayers;
            public RenderingLayerMask originalShadowRenderingLayers;
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

        private const int MaxBakeTargets = 31;
        private const uint BakeLayerBaseBit = 1;

        private static readonly List<RuntimeLightmapBaker> ActiveBakers = new();

        private readonly List<TargetEntry> _activeEntries = new();
        private readonly List<RealTimeLightBakerFeature.BakeTarget> _bakeTargets = new();
        private readonly List<LightState> _activeLights = new();

        private bool _forceBake;
        private bool _isWaitingForPass;
        private uint _combinedBakeMask;
        private int _propertyId;

        /// <summary>
        /// Returns the first lightmap render texture (for backward compatibility with previous API).
        /// </summary>
        public RenderTexture LightmapRT => targets.Count > 0 ? targets[0]?.lightmap : null;

        private void Awake()
        {
            CachePropertyId();
        }

        private void OnEnable()
        {
            CachePropertyId();
            EnsureResources();
            RenderPipelineManager.beginCameraRendering += HandleBeginCameraRendering;
            _forceBake = true;
        }

        private void OnDisable()
        {
            RenderPipelineManager.beginCameraRendering -= HandleBeginCameraRendering;
            CancelActiveSession();
            ReleaseAllRenderTextures();
        }

        private void OnValidate()
        {
            defaultLightmapSize = Mathf.Clamp(Mathf.ClosestPowerOfTwo(defaultLightmapSize), 128, 4096);
            CachePropertyId();

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
            _forceBake = true;
        }

        private void HandleBeginCameraRendering(ScriptableRenderContext context, Camera camera)
        {
            if (!ShouldBakeThisCamera(camera) || !ShouldBakeNow() || _isWaitingForPass)
            {
                return;
            }

            if (!EnsureResources())
            {
                return;
            }

            PrepareAndEnqueueBake();
        }

        private void PrepareAndEnqueueBake()
        {
            var feature = RealTimeLightBakerFeature.Instance;
            if (feature == null || bakerMaterial == null)
            {
                return;
            }

            _activeEntries.Clear();
            _bakeTargets.Clear();
            _combinedBakeMask = 0u;

            int assignedCount = 0;
            foreach (var entry in targets)
            {
                if (entry == null || entry.renderer == null || entry.lightmap == null)
                {
                    continue;
                }

                if (assignedCount >= MaxBakeTargets)
                {
                    Debug.LogWarning("RuntimeLightmapBaker: Exceeded maximum supported targets for a single bake pass. Remaining targets are skipped.");
                    break;
                }

                entry.originalRenderingLayerMask = entry.renderer.renderingLayerMask;
                entry.bakeRenderingLayerMask = 1u << (int)(BakeLayerBaseBit + assignedCount);
                entry.renderer.renderingLayerMask = entry.originalRenderingLayerMask | entry.bakeRenderingLayerMask;

                _combinedBakeMask |= entry.bakeRenderingLayerMask;
                _activeEntries.Add(entry);

                var bakeTarget = new RealTimeLightBakerFeature.BakeTarget(
                    entry.renderer,
                    entry.lightmap,
                    clear: true,
                    Color.clear,
                    Rect.zero,
                    entry.bakeRenderingLayerMask);

                _bakeTargets.Add(bakeTarget);
                assignedCount++;
            }

            if (_activeEntries.Count == 0)
            {
                return;
            }

            CaptureLightStates(_combinedBakeMask);

            feature.settings.bakeMaterial = bakerMaterial;
            feature.settings.enableDilation = dilationMaterial != null;
            feature.settings.dilationMaterial = dilationMaterial;
            feature.settings.dilationPassIndex = dilationPassIndex;

            feature.EnqueueTargets(_bakeTargets);

            if (!ActiveBakers.Contains(this))
            {
                ActiveBakers.Add(this);
            }

            _isWaitingForPass = true;
        }

        private void CaptureLightStates(uint bakeMask)
        {
            _activeLights.Clear();

            var lights = UnityEngine.Object.FindObjectsByType<Light>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            for (int i = 0; i < lights.Length; i++)
            {
                var light = lights[i];
                if (light == null)
                {
                    continue;
                }

                var state = new LightState
                {
                    light = light,
                    originalRenderingLayerMask = (uint)light.renderingLayerMask
                };

                if (light.TryGetComponent(out UniversalAdditionalLightData additionalData))
                {
                    state.additionalLightData = additionalData;
                    state.originalCustomShadowLayers = additionalData.customShadowLayers;
                    state.originalRenderingLayers = additionalData.renderingLayers;
                    state.originalShadowRenderingLayers = additionalData.shadowRenderingLayers;

                    additionalData.customShadowLayers = true;

                    uint renderingLayersMask = (uint)additionalData.renderingLayers | bakeMask;
                    additionalData.renderingLayers = (RenderingLayerMask)renderingLayersMask;

                    uint shadowRenderingLayersMask = (uint)additionalData.shadowRenderingLayers | bakeMask;
                    additionalData.shadowRenderingLayers = (RenderingLayerMask)shadowRenderingLayersMask;
                }

                light.renderingLayerMask = (int)(((uint)light.renderingLayerMask) | bakeMask);
                _activeLights.Add(state);
            }
        }
        private void CancelActiveSession()
        {
            if (!_isWaitingForPass)
            {
                return;
            }

            RestoreRenderers();
            RestoreLights();
            _activeEntries.Clear();
            _activeLights.Clear();
            _isWaitingForPass = false;
            _combinedBakeMask = 0u;
            ActiveBakers.Remove(this);
        }

        private void RestoreRenderers()
        {
            foreach (var entry in _activeEntries)
            {
                if (entry?.renderer != null)
                {
                    entry.renderer.renderingLayerMask = entry.originalRenderingLayerMask;
                }
            }
        }

        private void RestoreLights()
        {
            foreach (var state in _activeLights)
            {
                if (state.light == null)
                {
                    continue;
                }

                state.light.renderingLayerMask = (int)state.originalRenderingLayerMask;

                if (state.additionalLightData != null)
                {
                    state.additionalLightData.customShadowLayers = state.originalCustomShadowLayers;
                    state.additionalLightData.renderingLayers = state.originalRenderingLayers;
                    state.additionalLightData.shadowRenderingLayers = state.originalShadowRenderingLayers;
                }
            }
        }
        internal static void NotifyBakePassFinished(CommandBuffer cmd)
        {
            if (ActiveBakers.Count == 0)
            {
                return;
            }

            for (int i = 0; i < ActiveBakers.Count; i++)
            {
                var baker = ActiveBakers[i];
                baker?.FinalizeBake(cmd);
            }

            ActiveBakers.Clear();
        }

        private void FinalizeBake(CommandBuffer cmd)
        {
            _ = cmd;
            if (!_isWaitingForPass)
            {
                return;
            }

            if (autoApplyToTargets)
            {
                foreach (var entry in _activeEntries)
                {
                    if (entry != null && entry.renderer != null && entry.lightmap != null)
                    {
                        ApplyToRenderer(entry);
                    }
                }
            }

            RestoreRenderers();
            RestoreLights();

            _activeEntries.Clear();
            _activeLights.Clear();
            _isWaitingForPass = false;
            _combinedBakeMask = 0u;
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





