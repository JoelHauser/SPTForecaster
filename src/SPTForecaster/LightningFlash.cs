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
    /// - Direct light: a smaller share is added to the sun/moon light, tinted, and for the flash the light
    ///   is turned towards straight down (all the way at night, hardly at all against the day's sun), so it
    ///   comes from the cloud base overhead, not from the moon's side of the sky, and roofs and walls still
    ///   block it (the light's own shadows). Left at the moon's angle it read as lit from one side, with
    ///   its source visible (2026-10-29).
    /// - The sky dome (TOD's sky colours) and the clouds light up: the game's clouds take the sun/moon light
    ///   as they render; CloudSix's get an even glow (its ambient colour, not its moon light, which lit only
    ///   the clouds' moon side) through a postfix on its material update.
    ///
    /// All of it scales with the time of day (lightning barely shows at noon, fills the night) and the
    /// config's brightness. Everything but the light's intensity is rewritten by the game every frame, so
    /// nothing builds up; the intensity, which TOD only sets every 0-1 s by shadow quality, is put back.
    /// Applied in Camera.onPreCull: after every LateUpdate (TOD, ToDController, CloudSix), before rendering.
    /// </summary>
    internal static class LightningFlash
    {
        private static readonly Color Tint = new Color(0.78f, 0.86f, 1f);

        /// <summary>
        /// Added at Flash 1, night, brightness 1: sun/moon light, ambient (linear), sky colour. Lowered from
        /// 0.5 / 0.45 / 0.6 after "very bright for night time" (2026-10-29); TraceSix also bounces the
        /// light into its GI, and may keep a flash in a GI slice it refreshed mid-flash for a while.
        /// </summary>
        private const float DirectPeak = 0.3f, AmbientPeak = 0.3f, SkyPeak = 0.45f;

        /// <summary>Straight down, the way the light points while it carries the flash.</summary>
        private static readonly Quaternion Overhead = Quaternion.LookRotation(Vector3.down, Vector3.forward);

        private static bool _hooked;
        private static int _frame = -1;

        // The light's intensity and direction as the game last set them, and as we last wrote them. Below
        // shadow quality 3, TOD sets the direction only every second or so, so it has to be put back too.
        private static Light _light;
        private static float _base, _written;
        private static Quaternion _baseRotation, _writtenRotation;
        private static bool _holding;

        private static bool? _traceSix;

        private static bool TraceSixLoaded
        {
            get
            {
                if (_traceSix == null)
                {
                    _traceSix = BepInEx.Bootstrap.Chainloader.PluginInfos.ContainsKey("com.matsix.tracesix");
                    if (_traceSix.Value) ForecasterPlugin.Log.LogInfo("Lightning: TraceSix found, flashes light the sky and ambient only (not its sun).");
                }

                return _traceSix.Value;
            }
        }

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
            var turn = light.transform;
            if (!_holding || light != _light || !Mathf.Approximately(light.intensity, _written)) _base = light.intensity;
            if (!_holding || light != _light || Quaternion.Angle(turn.rotation, _writtenRotation) > 0.01f) _baseRotation = turn.rotation;
            _light = light;

            if (f <= 0.001f)
            {
                Release();
                return;
            }

            // With TraceSix the sun/moon light is left alone. TraceSix takes the map out of the game's sun
            // shadow whenever that light shines and gives it back half a second after it stops, so at night
            // every flash flipped some 40,000 meshes there and back (its log, 2026-10-29). The flash is
            // then sky and ambient light only, a little stronger.
            var ambient = AmbientPeak;
            if (TraceSixLoaded) ambient *= 1.4f;
            else
            {
                var add = f * DirectPeak;
                var share = add / (_base + add + 0.001f);
                light.intensity = _base + add;
                light.color = Color.Lerp(light.color, Tint, share);
                turn.rotation = Quaternion.Slerp(_baseRotation, Overhead, share);
                _written = light.intensity;
                _writtenRotation = turn.rotation;
                _holding = true;
            }

            var todc = WeatherController.Instance != null ? WeatherController.Instance.TimeOfDayController : null;
            if (todc != null && todc.AmbientLightScript != null)
            {
                var sh = todc.SH;
                sh.AddAmbientLight(Tint * (f * ambient * 0.4f));
                sh.AddDirectionalLight(Vector3.up, Tint, f * ambient);
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
            if (_holding && _light != null)
            {
                _light.intensity = _base;
                _light.transform.rotation = _baseRotation;
            }

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
            // Lightning lights the cloud deck from within, evenly: its ambient colour, not its moon light,
            // which comes from the moon's side and showed where the "source" was.
            cloudMaterial.SetColor(AmbientColor, cloudMaterial.GetColor(AmbientColor) + Tint * (f * 1.3f));
        }
    }
}
