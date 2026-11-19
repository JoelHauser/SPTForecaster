using System;
using System.Reflection;
using HarmonyLib;

namespace Forecaster
{
    /// <summary>
    /// Winter Outbreak (com.harmonyzt.winteroutbreak, by harmonyzt) rolls a storm for each Winter raid (none,
    /// Snowstorm or Blizzard, by its Storm Chances settings) and holds it for the whole raid: its WeatherCurve
    /// postfix replaces rain, fog, clouds and wind, and its SnowFlakes prefix sets the storm snow. Joel's rule
    /// (2026-11-18): where it runs, Winter Outbreak's weather always wins, and the forecast adapts to it.
    ///
    /// - In raid nothing needs doing to let it win (its curve sits over our nodes; with no storm rolled our
    ///   weather passes through it), except that <see cref="BlizzardDriver"/> stands down, so the snowflakes
    ///   are its alone.
    /// - On the location screen its storm isn't rolled yet (it rolls at the deploy countdown), so the forecast
    ///   box gives its odds. If it ever offers a static PeekStorm(string locationId) returning "Normal",
    ///   "Snowstorm", "Blizzard" (or null where it won't run), asked for in an issue on its repo, the box shows
    ///   that storm instead.
    ///
    /// It runs only when the raid's season is Winter (not the Storm season), and not on its Disabled Maps.
    /// Everything is by name from its 1.0.0 source; anything missing falls back to the odds, or to nothing.
    /// </summary>
    internal static class WinterOutbreakCompat
    {
        private const string Guid = "com.harmonyzt.winteroutbreak";
        private const string Section = "Storm Chances";

        private static bool _tried;
        private static BepInEx.BaseUnityPlugin _plugin;
        private static MethodInfo _peek;

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
                    : "Winter Outbreak found: its winter storms take over the weather; the forecast gives their odds.");
                return true;
            }
        }

        /// <summary>Winter Outbreak decides this map's weather: a Winter raid on a map it doesn't leave out.</summary>
        public static bool Covers(string locationId)
        {
            if (Schedule.Season != 2 || string.IsNullOrEmpty(locationId) || !Loaded) return false;
            // Its own rule: any listed name found anywhere in the location id, ignoring case.
            var disabled = Setting("Disabled Maps", "laboratory,labyrinth,terminal,icebreaker");
            foreach (var part in disabled.Split(','))
            {
                var name = part.Trim();
                if (name.Length > 0 && locationId.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0) return false;
            }

            return true;
        }

        /// <summary>
        /// The forecast box for a map Winter Outbreak covers: its storm (if it can say which) or its odds in
        /// place of ours. A storm it holds all raid has no changes to name.
        /// </summary>
        public static void Adjust(ForecastResult forecast, string locationId)
        {
            if (forecast == null || !Covers(locationId)) return;
            try
            {
                if (_peek != null)
                {
                    var storm = _peek.Invoke(null, new object[] { locationId }) as string;
                    if (storm == "Blizzard") Replace(forecast, "Blizzard all raid", "Blizzard", "Strong wind");
                    else if (storm == "Snowstorm") Replace(forecast, "Snowstorm all raid", "HeavySnow", "Breezy");
                    return; // "Normal" or null: the forecast's own weather
                }

                var chance = Math.Max(0, Math.Min(100, Setting("Storm Chance", 100)));
                var snowstorm = Math.Max(0, Setting("Snowstorm Weight", 50));
                var blizzard = Math.Max(0, Setting("Blizzard Weight", 50));
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
            }
        }

        /// <summary>
        /// Its northern lights, for the box's heading, or null. Its rule: once per raid at the countdown, if its
        /// Aurora / Enabled is on, Force Show always, else a raid whose clock reads 22:00-04:00 gets them one
        /// time in four (a Fika client takes the host's). They're drawn whatever the clouds, so the odds are the
        /// whole story. The slot's clock keeps moving until the countdown, as the heading's does.
        /// </summary>
        public static string Aurora(string locationId, double start)
        {
            if (!Covers(locationId) || !Setting("Enabled", true, "Aurora")) return null;
            if (Setting("Force Show", false, "Aurora")) return "with northern lights";
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

        private static T Setting<T>(string key, T fallback, string section = Section)
        {
            return _plugin != null && _plugin.Config.TryGetEntry<T>(section, key, out var entry) ? entry.Value : fallback;
        }
    }
}
