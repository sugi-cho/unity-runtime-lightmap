using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Events;
using UnityEngine.Rendering.Universal;
#if UNITY_EDITOR
using UnityEditor;
#endif

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
            public Camera bakeCamera;

            [NonSerialized] public RenderTexture lightmap;
            [NonSerialized] public RTHandle rtHandle;
            [NonSerialized] public MaterialPropertyBlock mpb;
            [NonSerialized] public uint originalRenderingLayerMask;
            [SerializeField] public UnityEvent<Texture> OnLightmapCreatedForTarget = new UnityEvent<Texture>();
            [NonSerialized] public uint assignedBakeLayerBit;
            [NonSerialized] public int lastBakedFrame = -1;
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
    [SerializeField] public UnityEvent<Texture> OnLightmapCreated = new UnityEvent<Texture>();
    [SerializeField] private bool autoFixConflictingRenderingLayers = true;
        [SerializeField] private bool bakeEveryFrame = true;
        [SerializeField] private bool autoApplyToTargets = true;
        [SerializeField] private string runtimeLightmapProperty = "_RuntimeLightmap";
        [SerializeField, Range(128, 4096)] private int defaultLightmapSize = 1024;
        [SerializeField] private Camera defaultBakeCamera;

        private const int MaxBakeTargets = 31;
        private const uint BakeLayerBaseBit = 1;

        private static readonly int BaseMapId = Shader.PropertyToID("_BaseMap");
        private static readonly int BumpMapId = Shader.PropertyToID("_BumpMap");
        private static readonly int SpecGlossMapId = Shader.PropertyToID("_SpecGlossMap");
        private static readonly int BakeCameraPosId = Shader.PropertyToID("_RTLB_BakeCameraPos");
        private static readonly List<RuntimeLightmapBaker> ActiveBakers = new();

        private readonly List<TargetEntry> _activeEntries = new();
        private readonly List<RealTimeLightBakerFeature.BakeTarget> _bakeTargets = new();
        private readonly List<LightState> _activeLights = new();
        private readonly List<RTHandle> _transientTextureHandles = new();

        private bool _forceBake;
        private bool _isWaitingForPass;
        private uint _combinedBakeMask;
        private bool _loggedPipelineSettings;
        private bool _missingDefaultBakeCameraLogged;
        private int _propertyId;

        private void Awake()
        {
            CachePropertyId();
        }

        private void OnEnable()
        {
            CachePropertyId();
            EnsureResources();
            AssignRenderingLayerBitsToTargets();
            AssignDefaultBakeCameraIfNeeded();
            RenderPipelineManager.beginCameraRendering += HandleBeginCameraRendering;
            _forceBake = true;
            _missingDefaultBakeCameraLogged = false;
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

            AssignRenderingLayerBitsToTargets();
            EnsureResources();
            AssignDefaultBakeCameraIfNeeded();
            _forceBake = true;
            _missingDefaultBakeCameraLogged = false;
        }

        private void AssignRenderingLayerBitsToTargets()
        {
            // Reserve bits based on currently used layers in the scene to avoid collisions.
            var sceneLights = UnityEngine.Object.FindObjectsByType<Light>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            uint usedMask = CollectUsedRenderingLayers(sceneLights);

            if (targets == null)
                return;

            for (int i = 0; i < targets.Count; i++)
            {
                var entry = targets[i];
                if (entry == null || entry.renderer == null)
                    continue;

                // If already assigned and the bit is still free (not used by others), keep it.
                if (entry.assignedBakeLayerBit != 0u)
                {
                    if ((usedMask & entry.assignedBakeLayerBit) == 0u)
                    {
                        usedMask |= entry.assignedBakeLayerBit;
                        continue;
                    }
                    // assigned bit is now taken, clear and reassign below
                    entry.assignedBakeLayerBit = 0u;
                }

                uint bit = ReserveRenderingLayerBit(ref usedMask);
                entry.assignedBakeLayerBit = bit;

                // Ensure we don't permanently overwrite user masks; store original when needed elsewhere
                entry.originalRenderingLayerMask = entry.renderer.renderingLayerMask;

                uint sanitizedOriginalMask = SanitizeRenderingLayerMask(entry.originalRenderingLayerMask);
                entry.renderer.renderingLayerMask = sanitizedOriginalMask | bit;
            }

            // After assigning bits to targets, detect other renderers that accidentally include
            // these bake bits and either warn or auto-fix them depending on configuration.
            DetectAndFixRenderingLayerConflicts();
        }

        private void DetectAndFixRenderingLayerConflicts()
        {
            // Build a mask of all bake bits assigned by this baker
            uint bakeBits = 0u;
            if (targets != null)
            {
                foreach (var t in targets)
                {
                    if (t != null)
                        bakeBits |= t.assignedBakeLayerBit;
                }
            }

            if (bakeBits == 0u)
                return;

            // Find all renderers in the scene and check for conflicts
            var allRenderers = UnityEngine.Object.FindObjectsByType<Renderer>(FindObjectsInactive.Include, FindObjectsSortMode.None);

            foreach (var r in allRenderers)
            {
                if (r == null)
                    continue;

                // If this renderer is one of our targets, skip
                bool isTarget = false;
                if (targets != null)
                {
                    foreach (var t in targets)
                    {
                        if (t != null && t.renderer == r)
                        {
                            isTarget = true;
                            break;
                        }
                    }
                }
                if (isTarget)
                    continue;

                uint rendererMask = SanitizeRenderingLayerMask((uint)r.renderingLayerMask);
                uint conflict = rendererMask & bakeBits;
                if (conflict != 0u)
                {
                    string msg = $"RuntimeLightmapBaker: Renderer '{r.name}' has bake rendering layer bits set that belong to bake targets. ";
                    if (autoFixConflictingRenderingLayers)
                    {
                        uint fixedMask = rendererMask & ~bakeBits;
                        // Apply only the sanitized fixed mask back to the renderer
                        r.renderingLayerMask = (RenderingLayerMask)fixedMask;
                        msg += "Auto-fixed by removing bake bits from this renderer.";
                    }
                    else
                    {
                        msg += "Consider enabling auto-fix or remove bake bits from this renderer to avoid unintended baking.";
                    }

                    Debug.LogWarning(msg, r);
                }
            }
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
            if (!ShouldBakeThisCamera(camera) || !ShouldBakeNow(camera) || _isWaitingForPass)
            {
                return;
            }

            if (!EnsureResources())
            {
                return;
            }

            PrepareAndEnqueueBake(camera);
        }

        private void LogPipelineSettingsOnce()
        {
            // Emit key URP configuration details once per lifecycle to aid debugging.
            if (_loggedPipelineSettings)
            {
                return;
            }

            if (UniversalRenderPipeline.asset is not UniversalRenderPipelineAsset urpAsset)
            {
                Debug.LogWarning("RuntimeLightmapBaker: Universal Render Pipeline asset is not set. Runtime baking requires an active URP asset.");
                _loggedPipelineSettings = true;
                return;
            }

            var rendererData = TryGetDefaultRendererData(urpAsset);
            RenderingMode renderingMode = RenderingMode.Forward;
            bool usesForwardPlus = false;

            if (rendererData is UniversalRendererData universalData)
            {
                renderingMode = universalData.renderingMode;
                usesForwardPlus = universalData.usesClusterLightLoop;
            }
            else if (rendererData != null)
            {
                var rendererType = rendererData.GetType();
                var renderingModeProperty = rendererType.GetProperty("renderingMode", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (renderingModeProperty != null && renderingModeProperty.PropertyType == typeof(RenderingMode))
                {
                    renderingMode = (RenderingMode)renderingModeProperty.GetValue(rendererData);
                    usesForwardPlus = renderingMode == RenderingMode.ForwardPlus || renderingMode == RenderingMode.DeferredPlus;
                }
                else
                {
                    var usesClusterProperty = rendererType.GetProperty("usesClusterLightLoop", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (usesClusterProperty?.PropertyType == typeof(bool))
                    {
                        usesForwardPlus = (bool)usesClusterProperty.GetValue(rendererData);
                    }
                }
            }
            else
            {
                var supportsForwardPlusProperty = urpAsset.GetType().GetProperty("supportsForwardPlus", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (supportsForwardPlusProperty?.PropertyType == typeof(bool))
                {
                    usesForwardPlus = (bool)supportsForwardPlusProperty.GetValue(urpAsset);
                }

                var renderingModeProperty = urpAsset.GetType().GetProperty("renderingMode", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (renderingModeProperty?.PropertyType == typeof(RenderingMode))
                {
                    renderingMode = (RenderingMode)renderingModeProperty.GetValue(urpAsset);
                }
            }

            bool usesRenderingLayers = urpAsset.useRenderingLayers;
            bool additionalLightsPerPixel = urpAsset.additionalLightsRenderingMode == LightRenderingMode.PerPixel;
            bool additionalLightShadows = urpAsset.supportsAdditionalLightShadows;
            int perObjectLimit = urpAsset.maxAdditionalLightsCount;

            Debug.Log($"RuntimeLightmapBaker: URP settings -> RendererMode={renderingMode}, ForwardPlus={usesForwardPlus}, UseRenderingLayers={usesRenderingLayers}, AdditionalLightsMode={urpAsset.additionalLightsRenderingMode}, AdditionalLightShadows={additionalLightShadows}, AdditionalLightsPerObject={perObjectLimit}");

            if (!usesRenderingLayers)
            {
                Debug.LogWarning("RuntimeLightmapBaker: Use Rendering Layers is disabled on the URP asset. Enable it to ensure bake isolation.");
            }
            if (!additionalLightsPerPixel)
            {
                Debug.LogWarning("RuntimeLightmapBaker: Additional Lights must be set to Per Pixel for runtime baking.");
            }
            if (!additionalLightShadows)
            {
                Debug.LogWarning("RuntimeLightmapBaker: Additional Light Shadows are disabled. Point/spot light shadows will not bake.");
            }
            if (perObjectLimit < 8)
            {
                Debug.LogWarning($"RuntimeLightmapBaker: Additional Lights Per Object Limit is {perObjectLimit}. Consider increasing it to 8 or more for stable results.");
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

                    usedMask |= SanitizeRenderingLayerMask((uint)entry.renderer.renderingLayerMask);
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

        private static ScriptableRendererData TryGetDefaultRendererData(UniversalRenderPipelineAsset asset)
        {
            // Note: This method uses reflection to access the default renderer data from the URP asset.
            // This is necessary because the required fields (`m_RendererDataList`, `m_DefaultRendererIndex`) are not public.
            // This approach is brittle and may break in future URP versions if the internal API changes.
            if (asset == null)
            {
                return null;
            }

            var assetType = asset.GetType();
            var rendererProperty = assetType.GetProperty("scriptableRendererData", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (rendererProperty?.GetValue(asset) is ScriptableRendererData propertyData && propertyData != null)
            {
                return propertyData;
            }

            var rendererListField = assetType.GetField("m_RendererDataList", BindingFlags.Instance | BindingFlags.NonPublic);
            var defaultIndexField = assetType.GetField("m_DefaultRendererIndex", BindingFlags.Instance | BindingFlags.NonPublic);

            if (rendererListField?.GetValue(asset) is ScriptableRendererData[] rendererArray && rendererArray.Length > 0)
            {
                int index = defaultIndexField != null ? (int)defaultIndexField.GetValue(asset) : 0;
                if (index < 0 || index >= rendererArray.Length)
                {
                    index = 0;
                }

                return rendererArray[index];
            }

            return null;
        }
        private void PrepareAndEnqueueBake(Camera activeCamera)
        {
            var feature = RealTimeLightBakerFeature.Instance;
            var settings = feature?.settings;
            if (settings == null || settings.bakeMaterial == null)
            {
                return;
            }

            if (targets == null || targets.Count == 0)
            {
                return;
            }

            if (activeCamera != null)
            {
                Shader.SetGlobalVector(BakeCameraPosId, activeCamera.transform.position);
            }

            _activeEntries.Clear();
            _bakeTargets.Clear();
            _combinedBakeMask = 0u;
            ReleaseTransientTextureHandles();

            var sceneLights = UnityEngine.Object.FindObjectsByType<Light>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            uint usedMask = CollectUsedRenderingLayers(sceneLights);

            int processedCount = 0;
            for (int i = 0; i < targets.Count; i++)
            {
                var entry = targets[i];
                if (entry == null || entry.renderer == null || entry.lightmap == null)
                {
                    continue;
                }

                if (entry.rtHandle == null || entry.rtHandle.rt == null)
                {
                    continue;
                }

                if (entry.lastBakedFrame == Time.frameCount)
                {
                    continue;
                }

                var effectiveCamera = GetEffectiveBakeCamera(entry);
                if (effectiveCamera == null)
                {
                    LogMissingDefaultCameraOnce();
                    continue;
                }

                if (effectiveCamera != activeCamera)
                {
                    continue;
                }

                if (processedCount >= MaxBakeTargets)
                {
                    Debug.LogWarning("RuntimeLightmapBaker: Exceeded maximum supported targets for a single bake pass. Remaining targets are skipped.");
                    break;
                }

                // Prefer the bake bit assigned at OnValidate/OnEnable. If none, reserve one now.
                uint layerBit = entry.assignedBakeLayerBit != 0u ? entry.assignedBakeLayerBit : ReserveRenderingLayerBit(ref usedMask);

                // Store original mask for restoration and apply only the bake bit (preserving other valid bits).
                entry.originalRenderingLayerMask = entry.renderer.renderingLayerMask;
                uint sanitizedOriginalMask = SanitizeRenderingLayerMask(entry.originalRenderingLayerMask);
                entry.renderer.renderingLayerMask = sanitizedOriginalMask | layerBit;

                _combinedBakeMask |= layerBit;
                _activeEntries.Add(entry);

                var baseMapInfo = GetRendererTextureInfo(entry.renderer, BaseMapId);
                var bumpMapInfo = GetRendererTextureInfo(entry.renderer, BumpMapId);
                var specGlossMapInfo = GetRendererTextureInfo(entry.renderer, SpecGlossMapId);

                var baseBinding = CreateTextureBinding(baseMapInfo, Texture2D.whiteTexture);
                var bumpFallback = Texture2D.normalTexture != null ? Texture2D.normalTexture : Texture2D.grayTexture;
                var bumpBinding = CreateTextureBinding(bumpMapInfo, bumpFallback);
                var specBinding = CreateTextureBinding(specGlossMapInfo, Texture2D.blackTexture);

                var bakeTarget = new RealTimeLightBakerFeature.BakeTarget(
                    entry.lightmap,
                    entry.rtHandle,
                    clear: true,
                    Color.clear,
                    Rect.zero,
                    layerBit,
                    baseBinding.Handle,
                    baseBinding.St,
                    bumpBinding.Handle,
                    bumpBinding.St,
                    specBinding.Handle,
                    specBinding.St);

                _bakeTargets.Add(bakeTarget);
                entry.lastBakedFrame = Time.frameCount;
                processedCount++;
            }

            if (_activeEntries.Count == 0 || _combinedBakeMask == 0u)
            {
                ReleaseTransientTextureHandles();
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

        private Camera GetEffectiveBakeCamera(TargetEntry entry)
        {
            if (entry == null)
            {
                return null;
            }

            if (entry.bakeCamera != null)
            {
                return entry.bakeCamera;
            }

            return defaultBakeCamera;
        }

        private void LogMissingDefaultCameraOnce()
        {
            if (_missingDefaultBakeCameraLogged)
            {
                return;
            }

            Debug.LogWarning("RuntimeLightmapBaker: Default bake camera is not assigned. Targets without a specific bake camera will be skipped.");
            _missingDefaultBakeCameraLogged = true;
        }

        private void AssignDefaultBakeCameraIfNeeded()
        {
            if (defaultBakeCamera != null)
            {
                return;
            }

            var taggedCamera = Camera.main;
            if (taggedCamera != null)
            {
                defaultBakeCamera = taggedCamera;
                return;
            }

            var cameras = UnityEngine.Object.FindObjectsByType<Camera>(FindObjectsInactive.Include, FindObjectsSortMode.InstanceID);
            if (cameras == null || cameras.Length == 0)
            {
                return;
            }

            for (int i = 0; i < cameras.Length; i++)
            {
                var camera = cameras[i];
                if (camera == null)
                {
                    continue;
                }

                defaultBakeCamera = camera;
                return;
            }
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
            ReleaseTransientTextureHandles();
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

        private void ReleaseTransientTextureHandles()
        {
            if (_transientTextureHandles.Count == 0)
            {
                return;
            }

            for (int i = 0; i < _transientTextureHandles.Count; i++)
            {
                _transientTextureHandles[i]?.Release();
            }

            _transientTextureHandles.Clear();
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
            ReleaseTransientTextureHandles();

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

            if (camera.cameraType == CameraType.SceneView)
            {
                return false;
            }

            return camera.cameraType == CameraType.Game || camera.cameraType == CameraType.VR || camera.cameraType == CameraType.Preview;
        }

        private bool ShouldBakeNow(Camera activeCamera)
        {
            var settings = RealTimeLightBakerFeature.Instance?.settings;
            if (settings == null || settings.bakeMaterial == null)
            {
                return false;
            }

            bool hasValidTarget = false;
            bool hasCameraMatch = false;

            if (targets != null)
            {
                for (int i = 0; i < targets.Count; i++)
                {
                    var entry = targets[i];
                    if (entry == null || entry.renderer == null)
                    {
                        continue;
                    }

                    hasValidTarget = true;
                    var effectiveCamera = GetEffectiveBakeCamera(entry);
                    if (effectiveCamera == null)
                    {
                        continue;
                    }

                    if (effectiveCamera == activeCamera)
                    {
                        hasCameraMatch = true;
                        break;
                    }
                }
            }

            if (!hasValidTarget || !hasCameraMatch)
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

                bool createdThisEntry = false;
                if (entry.lightmap == null || entry.lightmap.width != size || entry.lightmap.height != size)
                {
                    ReleaseRenderTexture(entry);
                    entry.lightmap = CreateRenderTexture(size);
                    createdThisEntry = entry.lightmap != null;
                }

                if (entry.lightmap != null)
                {
                    if (entry.rtHandle == null || entry.rtHandle.rt != entry.lightmap)
                    {
                        entry.rtHandle?.Release();
                        entry.rtHandle = RTHandles.Alloc(entry.lightmap);
                    }

                    if (createdThisEntry)
                    {
                        // Invoke per-target event
                        try
                        {
                            entry.OnLightmapCreatedForTarget?.Invoke(entry.lightmap);
                        }
                        catch (Exception ex)
                        {
                            Debug.LogException(ex);
                        }

                        // Backwards-compatible single-texture event
                        try
                        {
                            OnLightmapCreated?.Invoke(entry.lightmap);
                        }
                        catch (Exception ex)
                        {
                            Debug.LogException(ex);
                        }

                        // (Removed class-level array event per user request)
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

        private readonly struct TextureBinding
        {
            public TextureBinding(RTHandle handle, Vector4 st)
            {
                Handle = handle;
                St = st;
            }

            public RTHandle Handle { get; }
            public Vector4 St { get; }
        }

        private readonly struct MaterialTextureInfo
        {
            public MaterialTextureInfo(Texture texture, Vector2 scale, Vector2 offset)
            {
                Texture = texture;
                Scale = scale;
                Offset = offset;
            }

            public Texture Texture { get; }
            public Vector2 Scale { get; }
            public Vector2 Offset { get; }
        }

        private TextureBinding CreateTextureBinding(MaterialTextureInfo info, Texture fallback)
        {
            var texture = info.Texture != null ? info.Texture : fallback;
            var handle = RTHandles.Alloc(texture);
            _transientTextureHandles.Add(handle);

            var scale = info.Scale;
            if (Mathf.Approximately(scale.x, 0f) && Mathf.Approximately(scale.y, 0f))
            {
                scale = Vector2.one;
            }

            var offset = info.Offset;
            var st = new Vector4(scale.x, scale.y, offset.x, offset.y);

            return new TextureBinding(handle, st);
        }

        private static MaterialTextureInfo GetRendererTextureInfo(Renderer renderer, int propertyId)
        {
            if (renderer == null)
            {
                return new MaterialTextureInfo(null, Vector2.one, Vector2.zero);
            }

            var sharedMaterials = renderer.sharedMaterials;
            if (sharedMaterials != null && sharedMaterials.Length > 0)
            {
                for (int i = 0; i < sharedMaterials.Length; i++)
                {
                    var material = sharedMaterials[i];
                    if (material == null || !material.HasTexture(propertyId))
                    {
                        continue;
                    }

                    var texture = material.GetTexture(propertyId);
                    if (texture != null)
                    {
                        var scale = material.GetTextureScale(propertyId);
                        var offset = material.GetTextureOffset(propertyId);
                        return new MaterialTextureInfo(texture, scale, offset);
                    }
                }
            }

            var singleMaterial = renderer.sharedMaterial;
            if (singleMaterial != null && singleMaterial.HasTexture(propertyId))
            {
                var texture = singleMaterial.GetTexture(propertyId);
                if (texture != null)
                {
                    var scale = singleMaterial.GetTextureScale(propertyId);
                    var offset = singleMaterial.GetTextureOffset(propertyId);
                    return new MaterialTextureInfo(texture, scale, offset);
                }
            }

            return new MaterialTextureInfo(null, Vector2.one, Vector2.zero);
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
