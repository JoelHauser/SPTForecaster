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

        // The light's intensity, direction and colour as the game last set them, and as we last wrote them.
        // Below shadow quality 3, TOD sets the direction only every second or so, so it has to be put back too;
        // the colour likewise, or each frame's tint would build on the last.
        private static Light _light;
        private static float _base, _written;
        private static Quaternion _baseRotation, _writtenRotation;
        private static Color _baseColor, _writtenColor;
        private static bool _holding;

        private static bool? _traceSix;
        private static BepInEx.Configuration.ConfigEntry<bool> _traceSixEnabled;

        private static bool TraceSixLoaded
        {
            get
            {
                if (_traceSix == null)
                {
                    _traceSix = BepInEx.Bootstrap.Chainloader.PluginInfos.TryGetValue("com.matsix.tracesix", out var info);
                    if (_traceSix.Value && info.Instance != null)
                    {
                        info.Instance.Config.TryGetEntry(new BepInEx.Configuration.ConfigDefinition("1. General", "Enabled"), out _traceSixEnabled);
                    }

                    if (_traceSix.Value)
                    {
                        ForecasterPlugin.Log.LogInfo("Lightning: TraceSix found" + (_traceSixEnabled != null ? ", following its Enabled toggle." : ", but not its Enabled setting (taken as on)."));
                    }
                }

                return _traceSix.Value;
            }
        }

        /// <summary>
        /// TraceSix's global illumination is on now (its toggle key flips this setting). It replaces the game's
        /// sky ambient with its own light volumes, filled in over many frames, so an ambient flash came out as
        /// the map slowly brightening and fading back: "weird" with TraceSix on, "good" with it toggled off
        /// (Joel, 2026-10-29).
        /// </summary>
        private static bool TraceSixLighting => TraceSixLoaded && (_traceSixEnabled == null || _traceSixEnabled.Value);

        // The flash's own light, used while TraceSix's lighting is on: a directional light of its own, high in
        // the sky at each strike's bearing, with shadows, lit only while the flash flickers. TraceSix's
        // volumes take only the game's sun and its lamps, so this one isn't smeared; FogSix looks for its sun
        // when a raid starts, while this light is off.
        private const float OwnPeak = 0.45f;
        private static Light _own;

        /// <summary>The flash's own light, if made yet (FogSixSunPatch keeps FogSix from taking it as its sun).</summary>
        internal static Light Own => _own;
        private static Quaternion _aim = Quaternion.Euler(65f, 0f, 0f);

        /// <summary>
        /// A new strike: where its light comes from, any side, as high as the lit cloud base (about 2 km up) sits
        /// over a strike that far off: overhead nearly straight down, 3 km away about 35 degrees. Always 55-78
        /// degrees, the light only fell on the sill of a window or doorway and never into the room (Joel,
        /// 2026-10-30: the flashes showed outside but didn't come in).
        /// </summary>
        public static void Aim(float metres)
        {
            var height = Mathf.Atan2(2000f, Mathf.Max(metres, 1f)) * Mathf.Rad2Deg;
            var pitch = Mathf.Clamp(height + UnityEngine.Random.Range(-5f, 5f), 22f, 80f);
            _aim = Quaternion.Euler(pitch, UnityEngine.Random.Range(0f, 360f), 0f);
        }

        private static void OwnLight(float f)
        {
            if (f <= 0.001f)
            {
                if (_own != null && _own.enabled) _own.enabled = false;
                return;
            }

            if (_own == null)
            {
                var go = new GameObject("SPTForecasterLightning");
                _own = go.AddComponent<Light>();
                _own.type = LightType.Directional;
                _own.color = Tint;
                _own.shadows = LightShadows.Hard;
                _own.shadowStrength = 1f;
                _own.shadowResolution = LightShadowResolution.Low;
                _own.renderMode = LightRenderMode.ForcePixel;
                _own.enabled = false;
            }

            _own.transform.rotation = _aim;
            _own.intensity = f * OwnPeak;
            if (!_own.enabled) _own.enabled = true;
        }

        public static void Hook()
        {
            if (_hooked) return;
            _hooked = true;
            Camera.onPreCull += OnPreCull;
            CloudSixFlashPatch.TryEnable();
            FogSixSunPatch.TryEnable();
            ReturnOfThunderstormsCompat.TryEnable();
        }

        public static void Unhook()
        {
            if (!_hooked) return;
            _hooked = false;
            Camera.onPreCull -= OnPreCull;
            Release();
            if (_own != null) UnityEngine.Object.Destroy(_own.gameObject);
            _own = null;
        }

        /// <summary>How much of the flash shows now: all of it at night, a quarter at midday, times the config.</summary>
        public static float Visible(float flash)
        {
            if (flash <= 0f) return 0f;
            var sky = MonoBehaviourSingleton<TOD_Sky>.Instance;
            var day = sky != null ? sky.LerpValue : 0f;
            return flash * Mathf.Lerp(1f, 0.25f, day) * ForecasterPlugin.LightningBrightness.Value;
        }

        /// <summary>
        /// How much of the flash's light (the lights and the ambient, not the sky or clouds) reaches the player's
        /// spot: all of it outside and at a window or doorway, none deep inside (Thunder.Openness). Glass doesn't
        /// take shadows, so underground it caught that light through the ground above as if it were outside
        /// (Joel, 2026-10-30). The sky and the clouds always flash: inside they're only seen through windows
        /// and doorways anyway, and with the whole flash faded, deep in a building no flash showed even
        /// through the windows (Joel, the same day).
        /// </summary>
        private static float Reach()
        {
            var open = Thunder.Openness(out var inside);
            // Only underground. Above ground the light casts shadows, so in a building it only comes in through
            // the windows and doorways, lighting the room the way lightning does; faded there too, deep in a
            // building no flash showed at all (Joel, 2026-10-30).
            if (!inside || !Underground()) return 1f;
            return Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.15f, 0.6f, open));
        }

        /// <summary>
        /// The camera is below the ground's surface (a basement, a bunker, a tunnel): more than 1.5 m under the
        /// terrain at its spot. Maps without terrain there (Streets) count as above ground.
        /// </summary>
        internal static bool Underground()
        {
            var camera = Camera.main;
            if (camera == null) return false;
            var p = camera.transform.position;
            foreach (var terrain in Terrain.activeTerrains)
            {
                if (terrain == null || terrain.terrainData == null) continue;
                var origin = terrain.transform.position;
                var size = terrain.terrainData.size;
                if (p.x < origin.x || p.z < origin.z || p.x > origin.x + size.x || p.z > origin.z + size.z) continue;
                return p.y < terrain.SampleHeight(p) + origin.y - 1.5f;
            }

            return false;
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
            if (!_holding || light != _light || light.color != _writtenColor) _baseColor = light.color;
            _light = light;

            var tracing = TraceSixLighting;
            var lit = f > 0.001f ? f * Reach() : 0f;
            OwnLight(tracing ? lit : 0f);
            if (f <= 0.001f)
            {
                Release();
                return;
            }

            // With TraceSix the sun/moon light is left alone. TraceSix takes the map out of the game's sun
            // shadow whenever that light shines and gives it back half a second after it stops, so at night
            // every flash flipped some 40,000 meshes there and back (its log, 2026-10-29). With its lighting
            // on, the flash's own light lights the ground (above) and the ambient is left alone too; with it
            // toggled off, the ambient carries the flash, a little stronger (that looked right in game).
            var ambient = tracing ? 0f : AmbientPeak;
            if (TraceSixLoaded) ambient *= 1.4f;
            else
            {
                var add = lit * DirectPeak;
                var share = add / (_base + add + 0.001f);
                light.intensity = _base + add;
                light.color = Color.Lerp(_baseColor, Tint, share);
                turn.rotation = Quaternion.Slerp(_baseRotation, Overhead, share);
                _written = light.intensity;
                _writtenColor = light.color;
                _writtenRotation = turn.rotation;
                _holding = true;
            }

            var todc = WeatherController.Instance != null ? WeatherController.Instance.TimeOfDayController : null;
            if (ambient > 0f && lit > 0.001f && todc != null && todc.AmbientLightScript != null)
            {
                var sh = todc.SH;
                sh.AddAmbientLight(Tint * (lit * ambient * 0.4f));
                sh.AddDirectionalLight(Vector3.up, Tint, lit * ambient);
                todc.AmbientLightScript.SetSH(sh);
            }

            var ids = sky.Resources;
            if (ids != null)
            {
                Shader.SetGlobalColor(ids.ID_MoonSkyColor, sky.MoonSkyColor + Tint * (f * SkyPeak));
                Shader.SetGlobalColor(ids.ID_SunSkyColor, sky.SunSkyColor + Tint * (f * SkyPeak * 0.5f));
            }
        }

        /// <summary>The flash is over: give the light back the game's intensity, direction and colour.</summary>
        private static void Release()
        {
            if (_holding && _light != null)
            {
                _light.intensity = _base;
                _light.transform.rotation = _baseRotation;
                _light.color = _baseColor;
            }

            _holding = false;
        }
    }

    /// <summary>
    /// FogSix (com.matsix.fogsix) takes the brightest shadowed directional light as its sun, for the fog's
    /// sun shadows, at each scene load and 3, 11 and 26 s after. A flash in one of those moments with
    /// TraceSix's lighting on would hand it the flash's own light, dark between flashes, for the rest of the
    /// raid. Its pick skips that light: the next brightest, as FogSix would have chosen without it.
    /// </summary>
    internal sealed class FogSixSunPatch : ModulePatch
    {
        private static bool _tried;

        private static MethodBase Target()
        {
            if (!BepInEx.Bootstrap.Chainloader.PluginInfos.TryGetValue("com.matsix.fogsix", out var info) || info.Instance == null) return null;
            var type = info.Instance.GetType().Assembly.GetType("FogSix.Source.FogShadows");
            return type != null ? AccessTools.Method(type, "FindSun") : null;
        }

        /// <summary>At the first raid with lightning, once every plugin has loaded.</summary>
        public static void TryEnable()
        {
            if (_tried) return;
            _tried = true;
            if (Target() == null)
            {
                if (BepInEx.Bootstrap.Chainloader.PluginInfos.ContainsKey("com.matsix.fogsix"))
                {
                    ForecasterPlugin.Log.LogWarning("FogSix found but not its sun search; a flash early in a raid could darken its fog shadows.");
                }

                return;
            }

            ForecasterPlugin.Enable(new FogSixSunPatch());
            ForecasterPlugin.Log.LogInfo("Lightning stays out of FogSix's sun search.");
        }

        protected override MethodBase GetTargetMethod() => Target();

        [PatchPostfix]
        private static void Postfix(ref Light __result)
        {
            var own = LightningFlash.Own;
            if (own == null || __result != own) return;
            Light best = null;
            foreach (var l in UnityEngine.Object.FindObjectsOfType<Light>())
            {
                if (l == null || l == own || l.type != LightType.Directional || !l.isActiveAndEnabled || l.shadows == LightShadows.None) continue;
                if (best == null || l.intensity > best.intensity) best = l;
            }

            __result = best;
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
