using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RendererUtils;
using UnityEngine.Rendering.Universal;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.RenderGraphModule.Util;

namespace RealTimeLightBaker
{
    public sealed class RealTimeLightBakerFeature : ScriptableRendererFeature
    {
        [System.Serializable]
        public class Settings
        {
            public Material bakeMaterial;
            public RenderPassEvent renderPassEvent = RenderPassEvent.AfterRenderingShadows;
            public bool enableDilation;
            public Material dilationMaterial;
            public int dilationPassIndex;
        }

        private sealed class BakePass : ScriptableRenderPass
        {
            private readonly Settings _settings;
            private readonly ProfilingSampler _profilingSampler = new ProfilingSampler("Runtime Lightmap Bake");
            private readonly List<BakeTarget> _targets = new();
            private static readonly ShaderTagId[] k_ShaderTags =
            {
                new ShaderTagId("UniversalForward"),
                new ShaderTagId("UniversalForwardOnly"),
                new ShaderTagId("SRPDefaultUnlit")
            };
            private static readonly int BaseMapId = Shader.PropertyToID("_RTLB_BaseMap");
            private static readonly int BumpMapId = Shader.PropertyToID("_RTLB_BumpMap");
            private static readonly int SpecGlossMapId = Shader.PropertyToID("_RTLB_SpecGlossMap");
            private static readonly int BaseMapStId = Shader.PropertyToID("_RTLB_BaseMap_ST");
            private static readonly int BumpMapStId = Shader.PropertyToID("_RTLB_BumpMap_ST");
            private static readonly int SpecGlossMapStId = Shader.PropertyToID("_RTLB_SpecGlossMap_ST");
            private static readonly int BaseColorId = Shader.PropertyToID("_RTLB_BaseColor");
            private static readonly int SpecColorId = Shader.PropertyToID("_RTLB_SpecColor");
            private static readonly int SmoothnessId = Shader.PropertyToID("_RTLB_Smoothness");
            private static readonly int HasSpecGlossMapId = Shader.PropertyToID("_RTLB_HasSpecGlossMap");

            public BakePass(Settings settings)
            {
                _settings = settings;
            }

            public void Setup(IReadOnlyList<BakeTarget> targets)
            {
                _targets.Clear();
                if (targets == null)
                {
                    return;
                }

                for (int i = 0; i < targets.Count; i++)
                {
                    _targets.Add(targets[i]);
                }
            }
            
            private sealed class RenderGraphPassData
            {
                public RendererListHandle rendererList;
                public TextureHandle targetHandle;
                public bool clear;
                public Color clearColor;
                public bool hasViewport;
                public Rect viewport;
                public Vector2Int size;
                public TextureHandle baseMap;
                public TextureHandle bumpMap;
                public TextureHandle specGlossMap;
                public Vector4 baseMapST;
                public Vector4 bumpMapST;
                public Vector4 specGlossMapST;
                public Color baseColor;
                public Color specColor;
                public float smoothness;
                public float hasSpecGlossMap;
            }

            private sealed class DilationPassData
            {
                public TextureHandle source;
                public TextureHandle destination;
                public bool hasViewport;
                public Rect viewport;
                public Vector2Int size;
                public Material material;
                public int passIndex;
            }

            private sealed class CleanupPassData { }

            private static readonly Vector4 k_RenderGraphScaleBias = new Vector4(1f, 1f, 0f, 0f);
            private static readonly ProfilingSampler k_CleanupSampler = new ProfilingSampler("Runtime Lightmap Bake Cleanup");
            private const string k_RenderGraphSampleName = "Runtime Lightmap Bake";

            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameContext)
            {
                if (_targets.Count == 0 || _settings.bakeMaterial == null)
                {
                    return;
                }

                var renderingData = frameContext.Get<UniversalRenderingData>();
                var cameraData = frameContext.Get<UniversalCameraData>();
                var lightData = frameContext.Get<UniversalLightData>();

                var drawingSettings = RenderingUtils.CreateDrawingSettings(k_ShaderTags[0], renderingData, cameraData, lightData, SortingCriteria.None);
                for (int tagIndex = 1; tagIndex < k_ShaderTags.Length; tagIndex++)
                {
                    drawingSettings.SetShaderPassName(tagIndex, k_ShaderTags[tagIndex]);
                }
                drawingSettings.overrideMaterial = _settings.bakeMaterial;
                drawingSettings.overrideMaterialPassIndex = 0;

                var perObjectData = PerObjectData.LightData | PerObjectData.LightIndices | PerObjectData.ShadowMask | PerObjectData.Lightmaps | PerObjectData.LightProbe | PerObjectData.OcclusionProbe;
                if (Enum.TryParse("RenderingLayers", out PerObjectData renderingLayersFlag))
                {
                    perObjectData |= renderingLayersFlag;
                }
                drawingSettings.perObjectData = perObjectData;

                for (int targetIndex = 0; targetIndex < _targets.Count; targetIndex++)
                {
                    var target = _targets[targetIndex];
                    if (target.RenderTexture == null)
                    {
                        continue;
                    }

                    if (target.RenderTargetHandle == null || target.RenderTargetHandle.rt == null)
                    {
                        continue;
                    }

                    var filteringSettings = new FilteringSettings(RenderQueueRange.all, layerMask: -1, renderingLayerMask: target.RenderingLayerMask);
                    var rendererListParams = new RendererListParams(renderingData.cullResults, drawingSettings, filteringSettings);
                    var rendererList = renderGraph.CreateRendererList(rendererListParams);

                    var targetTextureHandle = renderGraph.ImportTexture(target.RenderTargetHandle);
                    var viewport = target.HasViewport ? target.Viewport : new Rect(0f, 0f, target.RenderTexture.width, target.RenderTexture.height);
                    var size = new Vector2Int(target.RenderTexture.width, target.RenderTexture.height);
                    var baseMapHandle = renderGraph.ImportTexture(target.BaseMap);
                    var bumpMapHandle = renderGraph.ImportTexture(target.BumpMap);
                    var specGlossMapHandle = renderGraph.ImportTexture(target.SpecGlossMap);

                    using (var builder = renderGraph.AddRasterRenderPass<RenderGraphPassData>($"Runtime Lightmap Bake (Target {targetIndex})", out var passData, _profilingSampler))
                    {
                        passData.rendererList = rendererList;
                        passData.targetHandle = targetTextureHandle;
                        passData.clear = target.Clear;
                        passData.clearColor = target.ClearColor;
                        passData.hasViewport = target.HasViewport;
                        passData.viewport = viewport;
                        passData.size = size;
                        passData.baseMap = baseMapHandle;
                        passData.bumpMap = bumpMapHandle;
                        passData.specGlossMap = specGlossMapHandle;
                        passData.baseMapST = target.BaseMapST;
                        passData.bumpMapST = target.BumpMapST;
                        passData.specGlossMapST = target.SpecGlossMapST;
                        passData.baseColor = target.BaseColor;
                        passData.specColor = target.SpecColor;
                        passData.smoothness = target.Smoothness;
                        passData.hasSpecGlossMap = target.HasSpecGlossMap;

                        builder.AllowGlobalStateModification(true);
                        builder.UseRendererList(rendererList);
                        builder.SetRenderAttachment(targetTextureHandle, 0);
                        builder.UseTexture(baseMapHandle);
                        builder.UseTexture(bumpMapHandle);
                        builder.UseTexture(specGlossMapHandle);

                        builder.SetRenderFunc<RenderGraphPassData>(ExecuteRenderGraphPass);
                    }

                    if (_settings.enableDilation && _settings.dilationMaterial != null)
                    {
                        var tempDesc = new TextureDesc(size.x, size.y)
                        {
                            format = target.RenderTexture.graphicsFormat,
                            filterMode = target.RenderTexture.filterMode,
                            wrapMode = target.RenderTexture.wrapMode,
                            useMipMap = target.RenderTexture.useMipMap,
                            autoGenerateMips = false,
                            msaaSamples = MSAASamples.None,
                            clearBuffer = false,
                            name = "_RuntimeLightmapDilationTemp"
                        };

                        var dilationHandle = renderGraph.CreateTexture(tempDesc);

                        using (var builder = renderGraph.AddRasterRenderPass<DilationPassData>($"Runtime Lightmap Dilation (Target {targetIndex})", out var passData, _profilingSampler))
                        {
                            passData.source = targetTextureHandle;
                            passData.destination = dilationHandle;
                            passData.hasViewport = target.HasViewport;
                            passData.viewport = viewport;
                            passData.size = size;
                            passData.material = _settings.dilationMaterial;
                            passData.passIndex = _settings.dilationPassIndex;

                            builder.UseTexture(targetTextureHandle);
                            builder.SetRenderAttachment(dilationHandle, 0, AccessFlags.Write);

                            builder.SetRenderFunc<DilationPassData>(ExecuteDilationPass);
                        }

                        renderGraph.AddCopyPass(dilationHandle, targetTextureHandle, passName: $"Runtime Lightmap Dilation Copy (Target {targetIndex})");
                    }
                }


                using (var builder = renderGraph.AddRasterRenderPass<CleanupPassData>("Runtime Lightmap Bake Cleanup", out var passData, k_CleanupSampler))
                {
                    builder.AllowPassCulling(false);
                    builder.SetRenderFunc<CleanupPassData>((data, context) =>
                    {
                        RuntimeLightmapBaker.OnBakePassFinished();
                    });
                }
            }

            private static void ExecuteRenderGraphPass(RenderGraphPassData data, RasterGraphContext context)
            {
                var cmd = context.cmd;
                cmd.BeginSample(k_RenderGraphSampleName);

                if (data.hasViewport)
                {
                    cmd.SetViewport(data.viewport);
                }
                else
                {
                    cmd.SetViewport(new Rect(0f, 0f, data.size.x, data.size.y));
                }

                if (data.clear)
                {
                    cmd.ClearRenderTarget(RTClearFlags.Color, data.clearColor, 1f, 0);
                }

                cmd.SetGlobalTexture(BaseMapId, data.baseMap);
                cmd.SetGlobalVector(BaseMapStId, data.baseMapST);
                cmd.SetGlobalTexture(BumpMapId, data.bumpMap);
                cmd.SetGlobalVector(BumpMapStId, data.bumpMapST);
                cmd.SetGlobalTexture(SpecGlossMapId, data.specGlossMap);
                cmd.SetGlobalVector(SpecGlossMapStId, data.specGlossMapST);
                cmd.SetGlobalVector(BaseColorId, data.baseColor);
                cmd.SetGlobalVector(SpecColorId, data.specColor);
                cmd.SetGlobalFloat(SmoothnessId, data.smoothness);
                cmd.SetGlobalFloat(HasSpecGlossMapId, data.hasSpecGlossMap);

                cmd.DrawRendererList(data.rendererList);
                cmd.EndSample(k_RenderGraphSampleName);
            }

            private static void ExecuteDilationPass(DilationPassData data, RasterGraphContext context)
            {
                var cmd = context.cmd;
                if (data.hasViewport)
                {
                    cmd.SetViewport(data.viewport);
                }
                else
                {
                    cmd.SetViewport(new Rect(0f, 0f, data.size.x, data.size.y));
                }

                Blitter.BlitTexture(cmd, data.source, k_RenderGraphScaleBias, data.material, data.passIndex);
            }
        }

        public static RealTimeLightBakerFeature Instance { get; private set; }

        public Settings settings = new Settings();

        private BakePass _pass;
        private readonly List<BakeTarget> _pendingTargets = new();

        public override void Create()
        {
            Instance = this;
            _pass = new BakePass(settings)
            {
                renderPassEvent = settings.renderPassEvent
            };
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            if (_pendingTargets.Count == 0 || settings.bakeMaterial == null)
            {
                _pendingTargets.Clear();
                return;
            }

            _pass.renderPassEvent = settings.renderPassEvent;
            _pass.Setup(_pendingTargets);
            renderer.EnqueuePass(_pass);
            _pendingTargets.Clear();
        }

        public void EnqueueTargets(List<BakeTarget> bakeTargets)
        {
            if (bakeTargets == null || bakeTargets.Count == 0)
            {
                return;
            }

            _pendingTargets.Clear();
            _pendingTargets.AddRange(bakeTargets);
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (Instance == this)
            {
                Instance = null;
            }
        }

        public readonly struct BakeTarget
        {
            public BakeTarget(RenderTexture renderTexture, RTHandle renderTargetHandle, bool clear, Color clearColor, Rect viewport, uint renderingLayerMask, RTHandle baseMap, Vector4 baseMapST, RTHandle bumpMap, Vector4 bumpMapST, RTHandle specGlossMap, Vector4 specGlossMapST, Color baseColor, Color specColor, float smoothness, float hasSpecGlossMap)
            {
                RenderTexture = renderTexture;
                RenderTargetHandle = renderTargetHandle;
                Clear = clear;
                ClearColor = clearColor;
                Viewport = viewport;
                HasViewport = viewport.width > 0f && viewport.height > 0f;
                RenderingLayerMask = renderingLayerMask;
                BaseMap = baseMap;
                BaseMapST = baseMapST;
                BumpMap = bumpMap;
                BumpMapST = bumpMapST;
                SpecGlossMap = specGlossMap;
                SpecGlossMapST = specGlossMapST;
                BaseColor = baseColor;
                SpecColor = specColor;
                Smoothness = smoothness;
                HasSpecGlossMap = hasSpecGlossMap;
            }

            public RenderTexture RenderTexture { get; }
            public bool Clear { get; }
            public Color ClearColor { get; }
            public Rect Viewport { get; }
            public bool HasViewport { get; }
            public uint RenderingLayerMask { get; }
            public RTHandle RenderTargetHandle { get; }
            public RTHandle BaseMap { get; }
            public Vector4 BaseMapST { get; }
            public RTHandle BumpMap { get; }
            public Vector4 BumpMapST { get; }
            public RTHandle SpecGlossMap { get; }
            public Vector4 SpecGlossMapST { get; }
            public Color BaseColor { get; }
            public Color SpecColor { get; }
            public float Smoothness { get; }
            public float HasSpecGlossMap { get; }
        }
    }
}


