using System;

namespace SPTForecaster
{
    /// <summary>Which locations get a forecast, and which play a single fixed slot.</summary>
    internal static class Maps
    {
        /// <summary>Icebreaker (Manimal-Icebreaker) always plays at its pinned hour, Plugin.TodHour, 23:00 by default.</summary>
        public const string IcebreakerTime = "23:00:00";

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
