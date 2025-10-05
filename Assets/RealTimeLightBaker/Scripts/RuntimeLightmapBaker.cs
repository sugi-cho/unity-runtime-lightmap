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
            [NonSerialized] public RTHandle rtHandle;
            [NonSerialized] public MaterialPropertyBlock mpb;
            [NonSerialized] public uint originalRenderingLayerMask;
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
        [SerializeField] private bool bakeEveryFrame = true;
        [SerializeField] private bool autoApplyToTargets = true;
        [SerializeField] private string runtimeLightmapProperty = "_RuntimeLightmap";
        [SerializeField, Range(128, 4096)] private int defaultLightmapSize = 1024;

        private const int MaxBakeTargets = 31;
        private const uint BakeLayerBaseBit = 1;

        private static readonly List<RuntimeLightmapBaker> ActiveBakers = new();

        private readonly List<TargetEntry> _activeEntries = new();
        private readonly List<RealTimeLightBakerFeature.BakeTarget> _bakeTargets = new();
        private readonly List<LightState> _activeLights = new();

        private bool _forceBake;
        private bool _isWaitingForPass;
        private uint _combinedBakeMask;
        private bool _loggedPipelineSettings;
        private int _propertyId;

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
            _loggedPipelineSettings = false;
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
            var feature = RealTimeLightBakerFeature.Instance;
            var settings = feature?.settings;
            if (settings != null)
            {
                settings.bakeMaterial = material;
            }
            else
            {
                Debug.LogWarning("RuntimeLightmapBaker: RealTimeLightBakerFeature instance is not available.");
            }

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

        private void LogPipelineSettingsOnce()
        {
            if (_loggedPipelineSettings) return;

            if (UniversalRenderPipeline.asset is not UniversalRenderPipelineAsset urpAsset)
            {
                Debug.LogWarning("RuntimeLightmapBaker: Universal Render Pipeline asset is not set. Runtime baking requires an active URP asset.");
                _loggedPipelineSettings = true;
                return;
            }

            var rendererData = urpAsset.scriptableRenderer;
            var renderingMode = RenderingMode.Forward;
            var usesForwardPlus = false;

            if (rendererData is UniversalRendererData universalData)
            {
                renderingMode = universalData.renderingMode;
                usesForwardPlus = universalData.usesClusterLightLoop;
            }

            Debug.Log($"RuntimeLightmapBaker: URP settings -> RendererMode={renderingMode}, ForwardPlus={usesForwardPlus}, UseRenderingLayers={urpAsset.useRenderingLayers}, AdditionalLightsMode={urpAsset.additionalLightsRenderingMode}, AdditionalLightShadows={urpAsset.supportsAdditionalLightShadows}, AdditionalLightsPerObject={urpAsset.maxAdditionalLightsCount}");

            if (!urpAsset.useRenderingLayers)
            {
                Debug.LogWarning("RuntimeLightmapBaker: Use Rendering Layers is disabled on the URP asset. Enable it to ensure bake isolation.");
            }

            if (urpAsset.additionalLightsRenderingMode != LightRenderingMode.PerPixel)
            {
                Debug.LogWarning("RuntimeLightmapBaker: Additional Lights must be set to Per Pixel for runtime baking.");
            }

            if (!urpAsset.supportsAdditionalLightShadows)
            {
                Debug.LogWarning("RuntimeLightmapBaker: Additional Light Shadows are disabled. Point/spot light shadows will not bake.");
            }

            if (urpAsset.maxAdditionalLightsCount < 8)
            {
                Debug.LogWarning($"RuntimeLightmapBaker: Additional Lights Per Object Limit is {urpAsset.maxAdditionalLightsCount}. Consider increasing it to 8 or more for stable results.");
            }

            _loggedPipelineSettings = true;
        }

        private uint CollectUsedRenderingLayers(Light[] lights)
        {
            // Gather rendering layer bits already in use so we can assign unique ones per target.
            uint usedMask = 0u;

            if (targets != null)
            {
                for (int i = 0; i < targets.Count; i++)
                {
                    var entry = targets[i];
                    if (entry?.renderer == null)
                    {
                        continue;
                    }

                    usedMask |= SanitizeRenderingLayerMask(entry.renderer.renderingLayerMask);
                }
            }

            if (lights != null)
            {
                for (int i = 0; i < lights.Length; i++)
                {
                    var light = lights[i];
                    if (light == null)
                    {
                        continue;
                    }

                    usedMask |= SanitizeRenderingLayerMask((uint)light.renderingLayerMask);

                    if (light.TryGetComponent(out UniversalAdditionalLightData additionalData))
                    {
                        usedMask |= SanitizeRenderingLayerMask(additionalData.renderingLayers);
                        usedMask |= SanitizeRenderingLayerMask(additionalData.shadowRenderingLayers);
                    }
                }
            }

            for (int i = 0; i < ActiveBakers.Count; i++)
            {
                var baker = ActiveBakers[i];
                if (baker == null)
                {
                    continue;
                }

                usedMask |= baker._combinedBakeMask;
            }

            return usedMask;
        }

        public static uint ToValidRenderingLayers(uint renderingLayers)
        {
            uint validRenderingLayers = RenderingLayerMask.GetDefinedRenderingLayersCombinedMaskValue();
            return validRenderingLayers & renderingLayers;
        }
        private static uint SanitizeRenderingLayerMask(uint mask)
        {
            return ToValidRenderingLayers(mask);
        }

        private static uint SanitizeRenderingLayerMask(RenderingLayerMask mask)
        {
            return SanitizeRenderingLayerMask(mask.value);
        }

        private static uint ReserveRenderingLayerBit(ref uint usedMask)
        {
            for (int bit = (int)BakeLayerBaseBit; bit < 32; bit++)
            {
                uint candidate = 1u << bit;
                if ((usedMask & candidate) == 0u)
                {
                    usedMask |= candidate;
                    return candidate;
                }
            }

            Debug.LogWarning("RuntimeLightmapBaker: No free rendering layer bit was available. Reusing the base bake bit (1).");
            uint fallback = 1u << (int)BakeLayerBaseBit;
            usedMask |= fallback;
            return fallback;
        }

        private void PrepareAndEnqueueBake()
        {
            var feature = RealTimeLightBakerFeature.Instance;
            var settings = feature?.settings;
            if (settings == null || settings.bakeMaterial == null)
            {
                return;
            }
            _activeEntries.Clear();
            _bakeTargets.Clear();
            _combinedBakeMask = 0u;

            var sceneLights = UnityEngine.Object.FindObjectsByType<Light>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            uint usedMask = CollectUsedRenderingLayers(sceneLights);

            int processedCount = 0;
            foreach (var entry in targets)
            {
                if (entry == null || entry.renderer == null || entry.lightmap == null)
                {
                    continue;
                }

                if (entry.rtHandle == null || entry.rtHandle.rt == null)
                {
                    continue;
                }

                if (processedCount >= MaxBakeTargets)
                {
                    Debug.LogWarning("RuntimeLightmapBaker: Exceeded maximum supported targets for a single bake pass. Remaining targets are skipped.");
                    break;
                }

                uint layerBit = ReserveRenderingLayerBit(ref usedMask);

                entry.originalRenderingLayerMask = entry.renderer.renderingLayerMask;

                uint sanitizedOriginalMask = SanitizeRenderingLayerMask(entry.originalRenderingLayerMask);
                entry.renderer.renderingLayerMask = sanitizedOriginalMask | layerBit;

                _combinedBakeMask |= layerBit;
                _activeEntries.Add(entry);

                var bakeTarget = new RealTimeLightBakerFeature.BakeTarget(
                    entry.lightmap,
                    entry.rtHandle,
                    clear: true,
                    Color.clear,
                    Rect.zero,
                    layerBit);

                _bakeTargets.Add(bakeTarget);
                processedCount++;
            }

            if (_activeEntries.Count == 0 || _combinedBakeMask == 0u)
            {
                return;
            }

            LogPipelineSettingsOnce();
            CaptureLightStates(_combinedBakeMask, sceneLights);

            feature.EnqueueTargets(_bakeTargets);

            if (!ActiveBakers.Contains(this))
            {
                ActiveBakers.Add(this);
            }

            _isWaitingForPass = true;
        }


        private void CaptureLightStates(uint bakeMask, Light[] lights)
        {
            _activeLights.Clear();

            if (lights == null || lights.Length == 0)
            {
                return;
            }

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
        internal static void OnBakePassFinished()
        {
            if (ActiveBakers.Count == 0)
            {
                return;
            }

            for (int i = 0; i < ActiveBakers.Count; i++)
            {
                var baker = ActiveBakers[i];
                baker?.FinalizeBake();
            }

            ActiveBakers.Clear();
        }

        private void FinalizeBake()
        {
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
            var settings = RealTimeLightBakerFeature.Instance?.settings;
            if (settings == null || settings.bakeMaterial == null)
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

                if (entry.lightmap != null)
                {
                    if (entry.rtHandle == null || entry.rtHandle.rt != entry.lightmap)
                    {
                        entry.rtHandle?.Release();
                        entry.rtHandle = RTHandles.Alloc(entry.lightmap);
                    }
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
            if (entry == null)
            {
                return;
            }

            entry.rtHandle?.Release();
            entry.rtHandle = null;

            if (entry.lightmap == null)
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

                if (entry.rtHandle == null || entry.rtHandle.rt == null)
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









