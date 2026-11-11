using System;
using System.Runtime.CompilerServices;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace Ecaty.Flecs.Capabilities
{
    [Capability("CapPostProcessing", "world appearance", 40)]
    public sealed class PostProcessingConfig
    {
        public float exposure, contrast, saturation, temperature, bloom, threshold = .9f, vignette;
        public int tonemap;
    }
    public sealed class PostProcessingCapability : CapabilityInstance<PostProcessingConfig>
    {
        private readonly UniversalAdditionalCameraData camera;
        private sealed class CameraUse
        {
            public int Count;
            public bool PreviousPostProcessing;
        }
        private static readonly ConditionalWeakTable<UniversalAdditionalCameraData, CameraUse> cameraUses = new();
        private bool ownsCamera;
        private Volume volume;
        private VolumeProfile profile;

        public PostProcessingCapability(CapabilityContext context) : base(context)
        {
            if (!UniversalRenderPipeline.asset || !context.Runtime.ViewCamera)
                throw new InvalidOperationException("Post-processing requires a URP view camera.");
            camera = context.Runtime.ViewCamera.GetUniversalAdditionalCameraData();
            int layers = camera.volumeLayerMask.value;
            if (layers == 0)
                throw new InvalidOperationException("The view camera has no Volume layers.");
            int layer = 0;
            while ((layers & (1 << layer)) == 0)
                layer++;
            Host.layer = layer;
        }

        protected override void Apply(PostProcessingConfig config)
        {
            if (!profile)
            {
                profile = ScriptableObject.CreateInstance<VolumeProfile>();
                volume = Host.AddComponent<Volume>();
                volume.isGlobal = true;
                volume.priority = 100;
                volume.sharedProfile = profile;
            }
            var color = Effect<ColorAdjustments>();
            color.postExposure.Override(Mathf.Clamp(config.exposure, -5f, 5f));
            color.contrast.Override(config.contrast);
            color.saturation.Override(config.saturation);
            Effect<WhiteBalance>().temperature.Override(config.temperature);
            var bloom = Effect<Bloom>();
            bloom.intensity.Override(Mathf.Clamp(config.bloom, 0f, 4f));
            bloom.threshold.Override(Mathf.Clamp(config.threshold, 0f, 10f));
            Effect<Vignette>().intensity.Override(config.vignette);
            Effect<Tonemapping>().mode.Override((TonemappingMode)Mathf.Clamp(config.tonemap, 0, 2));
            if (!ownsCamera)
            {
                var usage = cameraUses.GetValue(camera, _ => new CameraUse());
                if (usage.Count == 0)
                    usage.PreviousPostProcessing = camera.renderPostProcessing;
                usage.Count++;
                ownsCamera = true;
            }
            camera.renderPostProcessing = true;
        }

        private T Effect<T>() where T : VolumeComponent =>
            profile.TryGet<T>(out var effect) ? effect : profile.Add<T>();

        public override void Dispose()
        {
            if (volume)
            {
                volume.enabled = false;
                volume.sharedProfile = null;
            }
            if (ownsCamera && camera)
            {
                var usage = cameraUses.GetValue(camera, _ => new CameraUse());
                if (--usage.Count == 0)
                {
                    camera.renderPostProcessing = usage.PreviousPostProcessing;
                    cameraUses.Remove(camera);
                }
            }
            ownsCamera = false;
            if (!profile)
                return;
            foreach (var component in profile.components)
                UnityEngine.Object.Destroy(component);
            UnityEngine.Object.Destroy(profile);
        }
    }
}
