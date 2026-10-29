using System;
using System.Collections.Generic;
using JsonType;

namespace SPTForecaster
{
    /// <summary>Which locations get a forecast, which play a single fixed slot, and where each sits on the map.</summary>
    internal static class Maps
    {
        /// <summary>Icebreaker (Manimal-Icebreaker) always plays at its pinned hour, Plugin.TodHour, 23:00 by default.</summary>
        public const string IcebreakerTime = "23:00:00";

        /// <summary>
        /// IconX / IconY per location id (lower case), from the session's location list when the location
        /// screen opens, so modded maps work too. The defaults are SPT 4.1's base.json values, in case a raid
        /// starts before the screen was ever shown.
        /// </summary>
        private static readonly Dictionary<string, double[]> Icons = new Dictionary<string, double[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["bigmap"] = new double[] { 550, 460 }, ["woods"] = new double[] { 460, 660 }, ["shoreline"] = new double[] { 220, 300 },
            ["interchange"] = new double[] { 660, 360 }, ["rezervbase"] = new double[] { 360, 410 }, ["lighthouse"] = new double[] { 120, 450 },
            ["tarkovstreets"] = new double[] { 670, 620 }, ["sandbox"] = new double[] { 760, 735 }, ["sandbox_high"] = new double[] { 760, 735 },
            ["factory4_day"] = new double[] { 470, 510 }, ["factory4_night"] = new double[] { 470, 510 }, ["laboratory"] = new double[] { 700, 800 },
            ["labyrinth"] = new double[] { 245, 325 }, ["icebreaker"] = new double[] { 91, 747 },
        };

        public static void Remember(LocationSettings settings)
        {
            if (settings?.locations == null) return;
            foreach (var location in settings.locations.Values)
            {
                if (location?.Id == null) continue;
                if (location.EscapeTimeLimit > 0) RaidTimes[location.Id] = location.EscapeTimeLimit;
                if (location.IconX == 0 && location.IconY == 0) continue;
                Icons[location.Id] = new double[] { location.IconX, location.IconY };
            }
        }

        public static void Remember(LocationSettings.Location location)
        {
            if (location?.Id == null) return;
            if (location.EscapeTimeLimit > 0) RaidTimes[location.Id] = location.EscapeTimeLimit;
            if (location.IconX == 0 && location.IconY == 0) return;
            Icons[location.Id] = new double[] { location.IconX, location.IconY };
        }

        /// <summary>
        /// Raid minutes per location id, as this player's server sets them (EscapeTimeLimit). Servers differ
        /// (SVM and the like change them), so nothing assumes a length; 0 when not seen yet.
        /// </summary>
        private static readonly Dictionary<string, int> RaidTimes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        public static int RaidMinutes(string id) => id != null && RaidTimes.TryGetValue(id, out var m) ? m : 0;

        /// <summary>Where a location sits in the field's 600 x 400 frame (y down).</summary>
        public static bool Position(string id, out double x, out double y)
        {
            x = y = 0;
            if (id == null || !Icons.TryGetValue(id, out var icon)) return false;
            x = icon[0] * Field.W / 1000;
            y = (1000 - icon[1]) * Field.H / 1000;
            return true;
        }

        public static bool NoForecast(string id)
        {
            switch (Normalize(id))
            {
                case "factory4_day":
                case "factory4_night":
                case "laboratory":
                case "labyrinth":
                case "icebreaker":
                case "hideout":
                case "":
                    return true;
                default:
                    return false;
            }
        }

        public static bool OneSlot(string id) => Normalize(id) == "icebreaker";

        /// <summary>Ground Zero's level variant (Sandbox_high) is the same place as Sandbox.</summary>
        public static bool Same(string a, string b) => Normalize(a).Replace("_high", "") == Normalize(b).Replace("_high", "");

        private static string Normalize(string id) => (id ?? "").Trim().ToLowerInvariant();

        public static string Clock(double tarkovSeconds)
        {
            var s = (long)Schedule.Mod(tarkovSeconds, Schedule.DaySeconds);
            return new TimeSpan(0, 0, (int)s).ToString(@"hh\:mm");
        }

        internal static double CircularDistance(double a, double b)
        {
            var d = Math.Abs(Schedule.Mod(a, Schedule.DaySeconds) - Schedule.Mod(b, Schedule.DaySeconds));
            return Math.Min(d, Schedule.DaySeconds - d);
        }
    }
}
