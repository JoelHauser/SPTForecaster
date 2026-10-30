using System;
using System.Reflection;
using HarmonyLib;
using SPT.Reflection.Patching;
using UnityEngine;

namespace Forecaster
{
    /// <summary>
    /// Return Of Thunderstorms (com.killdg.returnofthunderstorms, by killdg) adds storms of its own: it rolls each
    /// rain period into a thunderstorm (heavy rain, hurricane wind, lightning and thunder, lamps flickering at
    /// night). Next to the forecast that made two storm systems (Joel, 2026-10-30), so in a raid on the forecast's
    /// weather they share one:
    ///
    /// - Its storm runs while the forecast has a thunderstorm within 2 km, instead of its roll on any rain (which
    ///   could turn the radar's light shower into a hurricane): its wind picks up and its lamps flicker as the
    ///   storm comes in. Its heavy rain (the in-game boost, the recording that replaces the game's rain, and the
    ///   turning down of other sounds under it) waits until the storm is overhead, so the rain matches the radar
    ///   (Joel's pick, 2026-10-30; overhead only, the first rule, left it off for every storm a few km out).
    /// - The thunder is the forecast's (distance, indoors, reverb); its own strikes are held back, and each of
    ///   ours flickers its lamps instead.
    /// - Its rain volume control, which takes every source on the game's rain mixer groups for rain, leaves the
    ///   forecast's thunder alone: that thunder plays through those groups.
    ///
    /// Raids without the forecast's weather (Factory, Labs, "Raid weather follows the forecast" off) leave it
    /// entirely to itself. Everything here is by name from its 1.2.1 source; a renamed piece is skipped with a
    /// warning, and the two run side by side as before.
    /// </summary>
    internal static class ReturnOfThunderstormsCompat
    {
        private const string Guid = "com.killdg.returnofthunderstorms";
        private static bool _tried;
        private static Assembly _assembly;

        /// <summary>The forecast's thunder is running this raid (its weather is applied and Lightning is on).</summary>
        internal static bool OursActive => Thunder.ForecastRaid && ForecasterPlugin.Lightning.Value;

        /// <summary>How near the forecast's thunderstorm brings its storm on (wind, lamps).</summary>
        private const double StormReachMetres = 2000; // about a map away

        /// <summary>Its heavy rain waits: a forecast raid, and the storm not yet overhead.</summary>
        private static bool HoldRain => OursActive && !Thunder.StormOverhead();

        /// <summary>At the first raid, once every plugin has loaded.</summary>
        public static void TryEnable()
        {
            if (_tried) return;
            _tried = true;
            if (!BepInEx.Bootstrap.Chainloader.PluginInfos.TryGetValue(Guid, out var info) || info.Instance == null) return;
            _assembly = info.Instance.GetType().Assembly;

            var missing = "";
            if (!Enable(new SoundPatch(), SoundPatch.Target())) missing += " rain volume";
            if (!Enable(new EpisodePatch(), EpisodePatch.Target())) missing += " storm timing";
            if (!Enable(new ThunderPatch(), ThunderPatch.Target())) missing += " thunder";
            if (!Enable(new RainBoostPatch(), RainBoostPatch.Target())) missing += " rain boost";
            if (!Enable(new RecordedRainPatch(), RecordedRainPatch.Target())) missing += " rain recording";
            if (!Enable(new DuckingPatch(), DuckingPatch.Target())) missing += " sound ducking";
            ForecasterPlugin.Log.LogInfo(missing.Length == 0
                ? "Return Of Thunderstorms found: its storms follow the forecast, and the thunder is the forecast's."
                : "Return Of Thunderstorms found, but not its" + missing + "; those parts run on their own.");
        }

        private static bool Enable(ModulePatch patch, MethodBase target)
        {
            if (target == null) return false;
            ForecasterPlugin.Enable(patch);
            return true;
        }

        private static Type Type(string name) => _assembly?.GetType("ReturnOfThunderstorms." + name);

        private static MethodBase Method(string type, string name, Type[] args = null)
        {
            var t = Type(type);
            return t == null ? null : args != null ? AccessTools.Method(t, name, args) : AccessTools.Method(t, name);
        }

        private static FieldInfo _instance, _lighting, _flickerSetting;
        private static MethodInfo _strike;

        /// <summary>Our strike: its lamps flicker, if it has that on (it flickers only at night itself).</summary>
        public static void Strike()
        {
            if (_assembly == null) return;
            try
            {
                var plugin = Type("Plugin");
                if (_instance == null && plugin != null)
                {
                    _instance = AccessTools.Field(plugin, "instance");
                    _lighting = AccessTools.Field(plugin, "lighting");
                    _flickerSetting = AccessTools.Field(plugin, "lightFlicker");
                    _strike = _lighting != null ? AccessTools.Method(_lighting.FieldType, "Strike") : null;
                }

                var self = _instance?.GetValue(null);
                if (self == null || _strike == null) return;
                if (_flickerSetting?.GetValue(self) is BepInEx.Configuration.ConfigEntry<bool> on && !on.Value) return;
                _strike.Invoke(_lighting.GetValue(self), null);
            }
            catch (Exception ex)
            {
                ForecasterPlugin.Log.LogWarning("Return Of Thunderstorms' lamp flicker failed, not asking again: " + ex.Message);
                _assembly = null;
            }
        }

        /// <summary>Its rain volume control skips the forecast's thunder sources.</summary>
        private sealed class SoundPatch : ModulePatch
        {
            internal static MethodBase Target() => Method("StormtideSound", "Apply", new[] { typeof(AudioSource) });

            protected override MethodBase GetTargetMethod() => Target();

            [PatchPrefix]
            private static bool Prefix(AudioSource source) => source == null || source.gameObject.name != Thunder.SourceName;
        }

        /// <summary>Its "is this rain period a thunderstorm" answer is the forecast's, in a forecast raid.</summary>
        private sealed class EpisodePatch : ModulePatch
        {
            internal static MethodBase Target() => Method("RainEpisode", "Tick");

            protected override MethodBase GetTargetMethod() => Target();

            [PatchPostfix]
            private static void Postfix(bool enabled, ref bool __result)
            {
                if (enabled && OursActive) __result = Thunder.StormWithin(StormReachMetres);
            }
        }

        /// <summary>Its own lightning and thunder wait while the forecast's run.</summary>
        private sealed class ThunderPatch : ModulePatch
        {
            internal static MethodBase Target() => Method("StormtideThunder", "Tick");

            protected override MethodBase GetTargetMethod() => Target();

            [PatchPrefix]
            private static bool Prefix() => !OursActive;
        }

        /// <summary>Its in-game heavy rain (its own patch on ControlRain) waits for the storm overhead.</summary>
        private sealed class RainBoostPatch : ModulePatch
        {
            internal static MethodBase Target() => Method("HeavyRain", "Prefix");

            protected override MethodBase GetTargetMethod() => Target();

            [PatchPrefix]
            private static bool Prefix(ref bool __result)
            {
                if (!HoldRain) return true;
                __result = true; // the game's own ControlRain runs, on the forecast's rain
                return false;
            }
        }

        /// <summary>
        /// Its heavy-rain recording stays silent until the storm is overhead, and fades in and out with it. It keeps
        /// running (silently) because its wind sound reads the recording's indoor/outdoor tracking.
        /// </summary>
        private sealed class RecordedRainPatch : ModulePatch
        {
            internal static MethodBase Target() => Method("StormtideRecordedRain", "Tick");

            protected override MethodBase GetTargetMethod() => Target();

            [PatchPrefix]
            private static void Prefix(ref float progress, ref float volume)
            {
                if (!HoldRain) return;
                progress = 0f;
                volume = 0f;
            }
        }

        /// <summary>Other sounds aren't turned down under a storm whose rain hasn't arrived.</summary>
        private sealed class DuckingPatch : ModulePatch
        {
            internal static MethodBase Target() => Method("WorldAudioDucking", "Tick", new[] { typeof(float) });

            protected override MethodBase GetTargetMethod() => Target();

            [PatchPrefix]
            private static void Prefix(ref float gain)
            {
                if (HoldRain) gain = 1f;
            }
        }
    }
}
