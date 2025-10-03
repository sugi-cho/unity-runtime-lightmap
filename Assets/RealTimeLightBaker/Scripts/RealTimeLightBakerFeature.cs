using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

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
            private static readonly int TempDilationRTId = Shader.PropertyToID("_RuntimeLightmapDilationTemp");
            private static readonly int MainTexId = Shader.PropertyToID("_MainTex");
            private static readonly int MainTexTexelSizeId = Shader.PropertyToID("_MainTex_TexelSize");

            private readonly Settings _settings;
            private readonly ProfilingSampler _profilingSampler = new ProfilingSampler("Runtime Lightmap Bake");
            private readonly List<BakeTarget> _targets = new();
            private static readonly ShaderTagId[] k_ShaderTags =
            {
                new ShaderTagId("UniversalForward"),
                new ShaderTagId("UniversalForwardOnly"),
                new ShaderTagId("SRPDefaultUnlit")
            };

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

            public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
            {
                if (_targets.Count == 0 || _settings.bakeMaterial == null)
                {
                    return;
                }

                var drawingSettings = CreateDrawingSettings(k_ShaderTags[0], ref renderingData, SortingCriteria.None);
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

                var cullResults = renderingData.cullResults;
                var cmd = CommandBufferPool.Get(_profilingSampler.name);

                using (new ProfilingScope(cmd, _profilingSampler))
                {
                    context.ExecuteCommandBuffer(cmd);
                    cmd.Clear();

                    for (int i = 0; i < _targets.Count; i++)
                    {
                        var target = _targets[i];
                        if (target.RenderTexture == null)
                        {
                            continue;
                        }

                        Rect viewport = target.HasViewport ? target.Viewport : new Rect(0f, 0f, target.RenderTexture.width, target.RenderTexture.height);

                        cmd.SetRenderTarget(target.RenderTexture);
                        cmd.SetViewport(viewport);
                        if (target.Clear)
                        {
                            cmd.ClearRenderTarget(true, true, target.ClearColor);
                        }

                        context.ExecuteCommandBuffer(cmd);
                        cmd.Clear();

                        var filteringSettings = new FilteringSettings(RenderQueueRange.all, -1, target.RenderingLayerMask);
                        context.DrawRenderers(cullResults, ref drawingSettings, ref filteringSettings);

                        if (_settings.enableDilation && _settings.dilationMaterial != null)
                        {
                            var desc = target.RenderTexture.descriptor;
                            desc.msaaSamples = 1;
                            desc.depthBufferBits = 0;
                            desc.useMipMap = false;
                            desc.autoGenerateMips = false;

                            cmd.GetTemporaryRT(TempDilationRTId, desc, FilterMode.Bilinear);

                            float width = target.RenderTexture.width;
                            float height = target.RenderTexture.height;
                            var texelSize = new Vector4(1f / width, 1f / height, width, height);
                            cmd.SetGlobalTexture(MainTexId, target.RenderTexture);
                            cmd.SetGlobalVector(MainTexTexelSizeId, texelSize);

                            cmd.Blit(target.RenderTexture, TempDilationRTId, _settings.dilationMaterial, _settings.dilationPassIndex);
                            cmd.Blit(TempDilationRTId, target.RenderTexture);
                            cmd.ReleaseTemporaryRT(TempDilationRTId);

                            context.ExecuteCommandBuffer(cmd);
                            cmd.Clear();
                        }
                    }
                }

                context.ExecuteCommandBuffer(cmd);
                CommandBufferPool.Release(cmd);
            }

            public override void FrameCleanup(CommandBuffer cmd)
            {
                _targets.Clear();
            }

            public override void OnCameraCleanup(CommandBuffer cmd)
            {
                RuntimeLightmapBaker.OnBakePassFinished();
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
            public BakeTarget(RenderTexture renderTexture, bool clear, Color clearColor, Rect viewport, uint renderingLayerMask)
            {
                RenderTexture = renderTexture;
                Clear = clear;
                ClearColor = clearColor;
                Viewport = viewport;
                HasViewport = viewport.width > 0f && viewport.height > 0f;
                RenderingLayerMask = renderingLayerMask;
            }

            public RenderTexture RenderTexture { get; }
            public bool Clear { get; }
            public Color ClearColor { get; }
            public Rect Viewport { get; }
            public bool HasViewport { get; }
            public uint RenderingLayerMask { get; }
        }
    }
}

