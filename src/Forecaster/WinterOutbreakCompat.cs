using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using SPT.Reflection.Patching;

namespace Forecaster
{
    /// <summary>
    /// Winter Outbreak (com.harmonyzt.winteroutbreak, by harmonyzt) rolls a storm for each Winter raid (none,
    /// Snowstorm or Blizzard, by its storm settings) and holds it for the whole raid: its WeatherCurve postfix
    /// replaces rain, fog, clouds and wind, and its SnowFlakes prefix sets the storm snow. Joel's rule
    /// (2026-11-18): where it runs, Winter Outbreak's weather always wins, and the forecast adapts to it.
    ///
    /// - In raid nothing needs doing to let it win (its curve sits over our nodes; with no storm rolled our
    ///   weather passes through it), except that <see cref="BlizzardDriver"/> stands down, so the snowflakes
    ///   are its alone.
    /// - It rolls its storm at the deploy countdown, after the location screen. If it ever offers a static
    ///   PeekStorm(string locationId) returning "Normal", "Snowstorm", "Blizzard" (or null where it won't run),
    ///   asked for in an issue on its repo, the box shows that. Until then we roll each map and slot's storm
    ///   ourselves when the box first shows it, with its settings and its sums (RollPreset), and a prefix on its
    ///   RollPreset hands that storm over for the confirmed map and slot. Its odds stay its own; only the moment
    ///   of the roll moves. Anything we didn't roll (no confirmed slot, settings changed since, the patch
    ///   missing) is its own roll. With Fika installed the box gives its odds instead: a Fika client takes the
    ///   host's storm, which this menu can't know.
    ///
    /// It runs only when the raid's season is Winter (not the Storm season), and not on its Disabled Maps.
    /// Everything is by name from its 1.0.0 and 1.0.1 source; anything missing falls back to the odds, or to
    /// nothing. 1.0.1 moved its settings to numbered sections ("Storm Chances" -> "1. Storms", "Aurora" ->
    /// "7. Aurora") and dropped Aurora / Force Show, so settings are read from its ConfigEntry fields first,
    /// then from either section.
    /// </summary>
    internal static class WinterOutbreakCompat
    {
        private const string Guid = "com.harmonyzt.winteroutbreak";
        private static readonly string[] StormSections = { "1. Storms", "Storm Chances" };
        private static readonly string[] AuroraSections = { "7. Aurora", "Aurora" };

        private static bool _tried;
        private static BepInEx.BaseUnityPlugin _plugin;
        private static MethodInfo _peek;
        private static bool _rollPatched;

        /// <summary>Our storm per map and slot ("Normal", "Snowstorm", "Blizzard"), with the settings it was rolled under.</summary>
        private static readonly Dictionary<string, (string Storm, string Settings)> Rolls = new Dictionary<string, (string, string)>();
        private static readonly Random Dice = new Random();

        /// <summary>Winter Outbreak is installed and loaded.</summary>
        public static bool Loaded
        {
            get
            {
                if (_tried) return _plugin != null;
                _tried = true;
                if (!BepInEx.Bootstrap.Chainloader.PluginInfos.TryGetValue(Guid, out var info) || info.Instance == null) return false;
                _plugin = info.Instance;
                _peek = AccessTools.Method(_plugin.GetType(), "PeekStorm", new[] { typeof(string) });
                if (_peek != null && (!_peek.IsStatic || _peek.ReturnType != typeof(string))) _peek = null;
                ForecasterPlugin.Log.LogInfo(_peek != null
                    ? "Winter Outbreak found: its winter storms take over the weather, and the forecast shows them."
                    : "Winter Outbreak found: its winter storms take over the weather.");
                return true;
            }
        }

        /// <summary>Patches its storm roll at start-up, if it is there and nothing (PeekStorm, Fika) makes that pointless.</summary>
        public static void TryEnable()
        {
            if (_rollPatched || !Loaded || _peek != null) return;
            if (BepInEx.Bootstrap.Chainloader.PluginInfos.ContainsKey("com.fika.core"))
            {
                ForecasterPlugin.Log.LogInfo("Winter Outbreak with Fika: the forecast gives its storm odds (the Fika host rolls the storm).");
                return;
            }

            if (RollPatch.Target() == null)
            {
                ForecasterPlugin.Log.LogInfo("Winter Outbreak's storm roll not found: the forecast gives its storm odds.");
                return;
            }

            ForecasterPlugin.Enable(new RollPatch());
            _rollPatched = true;
            ForecasterPlugin.Log.LogInfo("Winter Outbreak: the forecast rolls its storms early, with its odds, and shows them.");
        }

        /// <summary>Winter Outbreak decides this map's weather: a Winter raid on a map it doesn't leave out.</summary>
        public static bool Covers(string locationId)
        {
            if (Schedule.Season != 2 || string.IsNullOrEmpty(locationId) || !Loaded) return false;
            // Its own rule: any listed name found anywhere in the location id, ignoring case.
            var disabled = Setting("_disabledMaps", "Disabled Maps", "laboratory,labyrinth,terminal,icebreaker");
            foreach (var part in disabled.Split(','))
            {
                var name = part.Trim();
                if (name.Length > 0 && locationId.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0) return false;
            }

            return true;
        }

        /// <summary>
        /// The forecast box for a map Winter Outbreak covers: its storm (if it or we can say which) or its odds
        /// in place of ours. A storm it holds all raid has no changes to name.
        /// </summary>
        public static void Adjust(ForecastResult forecast, string locationId, bool rightSlot)
        {
            if (forecast == null || !Covers(locationId)) return;
            try
            {
                string storm = null;
                var known = false;
                if (_peek != null)
                {
                    storm = _peek.Invoke(null, new object[] { locationId }) as string;
                    known = true;
                }
                else if (_rollPatched)
                {
                    storm = Roll(locationId, rightSlot);
                    known = true;
                }

                if (known)
                {
                    if (storm == "Blizzard") Replace(forecast, "Blizzard all raid", "Blizzard", "Strong wind");
                    else if (storm == "Snowstorm") Replace(forecast, "Snowstorm all raid", "HeavySnow", "Breezy");
                    return; // "Normal" or null: the forecast's own weather
                }

                var chance = Math.Max(0, Math.Min(100, Setting("_stormChance", "Storm Chance", 100)));
                var snowstorm = Math.Max(0, Setting("_snowstormWeight", "Snowstorm Weight", 50));
                var blizzard = Math.Max(0, Setting("_blizzardWeight", "Blizzard Weight", 50));
                if (chance == 0 || snowstorm + blizzard == 0) return;

                var blizzardPercent = (int)Math.Round(chance * blizzard / (double)(snowstorm + blizzard));
                var snowstormPercent = chance - blizzardPercent;
                if (chance < 100)
                {
                    // The forecast's weather is likely enough to stay; the storm odds go under it.
                    forecast.Wind = chance + " % chance of a winter storm";
                    return;
                }

                if (snowstorm == 0) Replace(forecast, "Blizzard all raid", "Blizzard", "Strong wind");
                else if (blizzard == 0) Replace(forecast, "Snowstorm all raid", "HeavySnow", "Breezy");
                else Replace(forecast, "Snowstorm or blizzard", "Blizzard", $"Blizzard {blizzardPercent} %, snowstorm {snowstormPercent} %");
            }
            catch (Exception ex)
            {
                ForecasterPlugin.Log.LogWarning("Could not read Winter Outbreak's storm, not asking again: " + ex.Message);
                _peek = null;
                _plugin = null;
                _rollPatched = false;
            }
        }

        /// <summary>
        /// Its northern lights, for the box's heading, or null. Its rule: once per raid at the countdown, if its
        /// Aurora / Enabled is on, a raid whose clock reads 22:00-04:00 gets them one time in four (a Fika client
        /// takes the host's); 1.0.0's Force Show gave every raid them. They're drawn whatever the clouds, so the
        /// odds are the whole story. The slot's clock keeps moving until the countdown, as the heading's does.
        /// </summary>
        public static string Aurora(string locationId, double start)
        {
            if (!Covers(locationId) || !Setting("_auroraEnabled", "Enabled", true, AuroraSections)) return null;
            if (Setting("_auroraForce", "Force Show", false, AuroraSections)) return "with northern lights";
            var hour = Schedule.Mod(start, Schedule.DaySeconds) / 3600;
            return hour >= 22 || hour < 4 ? "25 % chance of northern lights" : null;
        }

        private static void Replace(ForecastResult forecast, string headline, string glyph, string wind)
        {
            forecast.Headline = new ForecastLine { Text = headline, Glyph = glyph };
            forecast.Wind = wind;
            forecast.Changes.Clear();
            forecast.More = null;
        }

        /// <summary>Its setting: the plugin's ConfigEntry field, else the key in any of the sections, else the fallback.</summary>
        private static T Setting<T>(string field, string key, T fallback, string[] sections = null)
        {
            if (_plugin == null) return fallback;
            if (AccessTools.Field(_plugin.GetType(), field)?.GetValue(_plugin) is BepInEx.Configuration.ConfigEntry<T> own) return own.Value;
            foreach (var section in sections ?? StormSections)
                if (_plugin.Config.TryGetEntry<T>(section, key, out var entry)) return entry.Value;
            return fallback;
        }

        private static string SettingsKey() =>
            Setting("_stormChance", "Storm Chance", 100) + "/" + Setting("_snowstormWeight", "Snowstorm Weight", 50)
            + "/" + Setting("_blizzardWeight", "Blizzard Weight", 50);

        private static string RollKey(string locationId, bool rightSlot) => locationId.ToLowerInvariant() + (rightSlot ? " right" : " left");

        /// <summary>This map and slot's storm, rolled once (again if its settings changed), its way: chance, then weights.</summary>
        private static string Roll(string locationId, bool rightSlot)
        {
            var key = RollKey(locationId, rightSlot);
            var settings = SettingsKey();
            if (Rolls.TryGetValue(key, out var kept) && kept.Settings == settings) return kept.Storm;

            var chance = Setting("_stormChance", "Storm Chance", 100);
            var snowstorm = Setting("_snowstormWeight", "Snowstorm Weight", 50);
            var total = snowstorm + Setting("_blizzardWeight", "Blizzard Weight", 50);
            var storm = total <= 0 || Dice.Next(100) >= chance ? "Normal" : Dice.Next(total) < snowstorm ? "Snowstorm" : "Blizzard";
            Rolls[key] = (storm, settings);
            if (ForecasterPlugin.VerboseLog.Value) ForecasterPlugin.Log.LogInfo($"Winter Outbreak storm rolled for {key}: {storm}.");
            return storm;
        }

        /// <summary>
        /// Its RollPreset (private, returns its Snowstorm or Blizzard preset, or null for no storm): the storm the
        /// box showed for the confirmed map and slot. Every roll is cleared after, so the next raid rolls afresh, as
        /// it would itself.
        /// </summary>
        private sealed class RollPatch : ModulePatch
        {
            internal static MethodBase Target() =>
                _plugin == null ? null : AccessTools.Method(_plugin.GetType(), "RollPreset", Type.EmptyTypes);

            protected override MethodBase GetTargetMethod() => Target();

            [PatchPrefix]
            private static bool Prefix(object __instance, ref object __result)
            {
                try
                {
                    if (!_rollPatched || !WeatherLock.TryGetConfirmed(out var location, out var rightSlot)) return true;
                    if (!Rolls.TryGetValue(RollKey(location, rightSlot), out var kept) || kept.Settings != SettingsKey()) return true;

                    object preset = null;
                    if (kept.Storm != "Normal")
                    {
                        preset = AccessTools.Field(__instance.GetType(), kept.Storm)?.GetValue(null);
                        if (preset == null) return true; // its preset isn't where 1.0.x keeps it: its own roll
                    }

                    ForecasterPlugin.Log.LogInfo($"Winter Outbreak storm for {location}: {kept.Storm}, as the forecast showed.");
                    __result = preset;
                    return false;
                }
                catch (Exception ex)
                {
                    ForecasterPlugin.Log.LogWarning("Could not hand Winter Outbreak the forecast's storm, it rolls its own: " + ex.Message);
                    return true;
                }
                finally
                {
                    Rolls.Clear();
                }
            }
        }
    }
}
