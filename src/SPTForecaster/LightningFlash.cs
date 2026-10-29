using System;
using System.Reflection;
using Comfort.Common;
using EFT.Weather;
using HarmonyLib;
using SPT.Reflection.Patching;
using UnityEngine;
using UnityEngine.Rendering;

namespace SPTForecaster
{
    /// <summary>
    /// Draws Thunder.Flash (0 none, about 1 a strike overhead) into the scene, the way lightning lights a
    /// place: the whole sky at once, blue-white, a brief flat glow rather than a second sun.
    ///
    /// - Sky light: the game's outdoor ambient (the spherical harmonics ToDController hands AmbientLight
    ///   each frame) gets a blue-white glow, mostly from above. Interiors have their own ambient volumes,
    ///   so it lights the outdoors, and what is seen through windows and doorways, not the rooms.
    /// - Direct light: a smaller share is added to the sun/moon light, tinted, so windows facing the sky
    ///   catch it and walls still block it (it uses the light's own shadows and cloud shadows).
    /// - The sky dome (TOD's sky colours) and the clouds light up: the game's clouds take the sun/moon light
    ///   as they render; CloudSix's are given it through a postfix on its material update.
    ///
    /// All of it scales with the time of day (lightning barely shows at noon, fills the night) and the
    /// config's brightness. Everything but the light's intensity is rewritten by the game every frame, so
    /// nothing builds up; the intensity, which TOD only sets every 0-1 s by shadow quality, is put back.
    /// Applied in Camera.onPreCull: after every LateUpdate (TOD, ToDController, CloudSix), before rendering.
    /// </summary>
    internal static class LightningFlash
    {
        private static readonly Color Tint = new Color(0.78f, 0.86f, 1f);

        /// <summary>Added at Flash 1, night, brightness 1: sun/moon light, ambient (linear), sky colour.</summary>
        private const float DirectPeak = 0.5f, AmbientPeak = 0.35f, SkyPeak = 0.6f;

        private static bool _hooked;
        private static int _frame = -1;

        // The light's intensity as the game last set it, and as we last wrote it.
        private static Light _light;
        private static float _base, _written;
        private static bool _holding;

        public static void Hook()
        {
            if (_hooked) return;
            _hooked = true;
            Camera.onPreCull += OnPreCull;
            CloudSixFlashPatch.TryEnable();
        }

        public static void Unhook()
        {
            if (!_hooked) return;
            _hooked = false;
            Camera.onPreCull -= OnPreCull;
            Release();
        }

        /// <summary>How much of the flash shows now: all of it at night, a quarter at midday, times the config.</summary>
        public static float Visible(float flash)
        {
            var sky = MonoBehaviourSingleton<TOD_Sky>.Instance;
            var day = sky != null ? sky.LerpValue : 0f;
            return flash * Mathf.Lerp(1f, 0.25f, day) * ForecasterPlugin.LightningBrightness.Value;
        }

        private static void OnPreCull(Camera camera)
        {
            if (Time.frameCount == _frame) return;
            _frame = Time.frameCount;
            try
            {
                Apply(Visible(Thunder.Flash));
            }
            catch (Exception ex)
            {
                ForecasterPlugin.Log.LogError("Lightning flash failed, turning it off for this raid: " + ex.Message);
                Unhook();
            }
        }

        private static void Apply(float f)
        {
            var sky = MonoBehaviourSingleton<TOD_Sky>.Instance;
            var light = sky != null && sky.Components != null ? sky.Components.LightSource : null;
            if (light == null) return;

            // The game's latest intensity: anything other than what we wrote is the game's new value.
            if (!_holding || light != _light || !Mathf.Approximately(light.intensity, _written)) _base = light.intensity;
            _light = light;

            if (f <= 0.001f)
            {
                Release();
                return;
            }

            var add = f * DirectPeak;
            light.intensity = _base + add;
            light.color = Color.Lerp(light.color, Tint, add / (_base + add + 0.001f));
            _written = light.intensity;
            _holding = true;

            var todc = WeatherController.Instance != null ? WeatherController.Instance.TimeOfDayController : null;
            if (todc != null && todc.AmbientLightScript != null)
            {
                var sh = todc.SH;
                sh.AddAmbientLight(Tint * (f * AmbientPeak * 0.4f));
                sh.AddDirectionalLight(Vector3.up, Tint, f * AmbientPeak);
                todc.AmbientLightScript.SetSH(sh);
            }

            var ids = sky.Resources;
            if (ids != null)
            {
                Shader.SetGlobalColor(ids.ID_MoonSkyColor, sky.MoonSkyColor + Tint * (f * SkyPeak));
                Shader.SetGlobalColor(ids.ID_SunSkyColor, sky.SunSkyColor + Tint * (f * SkyPeak * 0.5f));
            }
        }

        /// <summary>The flash is over: give the light back the game's intensity.</summary>
        private static void Release()
        {
            if (_holding && _light != null) _light.intensity = _base;
            _holding = false;
        }
    }

    /// <summary>
    /// CloudSix (com.matsix.cloudsix) draws its own clouds and bakes them in its WeatherController.LateUpdate
    /// postfix, right after CustomCloudController.UpdateMaterial sets their light. Lighting them up after
    /// that method lets its baked and direct cloud modes both show the flash. Enabled only with CloudSix.
    /// </summary>
    internal sealed class CloudSixFlashPatch : ModulePatch
    {
        private static bool _tried;
        private static readonly int MoonIntensity = Shader.PropertyToID("_MoonIntensity");
        private static readonly int MoonColor = Shader.PropertyToID("_MoonColor");
        private static readonly int AmbientColor = Shader.PropertyToID("_AmbientColor");
        private static readonly Color Tint = new Color(0.78f, 0.86f, 1f);

        private static MethodBase Target()
        {
            if (!BepInEx.Bootstrap.Chainloader.PluginInfos.TryGetValue("com.matsix.cloudsix", out var info) || info.Instance == null) return null;
            var type = info.Instance.GetType().Assembly.GetType("CloudSix.Source.CustomCloudController");
            return type != null ? AccessTools.Method(type, "UpdateMaterial", new[] { typeof(Material), typeof(float) }) : null;
        }

        /// <summary>At the first raid with lightning, once every plugin has loaded.</summary>
        public static void TryEnable()
        {
            if (_tried) return;
            _tried = true;
            if (Target() == null)
            {
                if (BepInEx.Bootstrap.Chainloader.PluginInfos.ContainsKey("com.matsix.cloudsix"))
                {
                    ForecasterPlugin.Log.LogWarning("CloudSix found but not its cloud material update; its clouds won't flash.");
                }

                return;
            }

            ForecasterPlugin.Enable(new CloudSixFlashPatch());
            ForecasterPlugin.Log.LogInfo("Lightning lights CloudSix's clouds.");
        }

        protected override MethodBase GetTargetMethod() => Target();

        [PatchPostfix]
        private static void Postfix(Material cloudMaterial)
        {
            var f = Thunder.Flash;
            if (f <= 0.001f || cloudMaterial == null) return;
            f = LightningFlash.Visible(f);
            // Its moon light on the clouds tops out at 0.05; a flash outshines it many times over.
            cloudMaterial.SetFloat(MoonIntensity, cloudMaterial.GetFloat(MoonIntensity) + f * 1.5f);
            cloudMaterial.SetColor(MoonColor, Color.Lerp(cloudMaterial.GetColor(MoonColor), Tint, Mathf.Clamp01(f)));
            cloudMaterial.SetColor(AmbientColor, cloudMaterial.GetColor(AmbientColor) + Tint * (f * 0.5f));
        }
    }
}
