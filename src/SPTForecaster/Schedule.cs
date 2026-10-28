using System;
using Newtonsoft.Json.Linq;
using SPT.Common.Http;
using UnityEngine;

namespace SPTForecaster
{
    /// <summary>One stretch of a location's weather. Start and End are Tarkov seconds.</summary>
    internal sealed class Block
    {
        public long Start;
        public long End;
        public double Clouds;
        public double WindSpeed;
        public int WindDirection;
        public double WindGustiness;
        public double Rain;
        public double RainIntensity;
        public double Fog;
        public double Temperature;
        public double Pressure;
    }

    /// <summary>
    /// What the server mod provides: the weather seed (saved on the server, so a restart keeps the same
    /// weather), the season and the time acceleration. Everything else is worked out here from the seed by
    /// <see cref="Field"/>, so the forecast, the radar and the raid's weather all come from one place.
    ///
    /// Tarkov seconds: real Unix seconds × acceleration + 3 hours. Their time of day is the game's Tarkov
    /// clock (WeatherHelper.GetInRaidTime on the server), and the right-hand slot is that plus 12 hours.
    /// </summary>
    internal static class Schedule
    {
        public const long DaySeconds = 86400;
        public const long HalfDaySeconds = 43200;
        private const long OffsetSeconds = 3 * 3600;
        private const float RefreshSeconds = 60f;
        private const float RetrySeconds = 15f;

        private static float _fetchedAt = -1000f;
        private static float _failedAt = -1000f;
        private static bool _ready;
        private static bool _warned;

        public static double Acceleration { get; private set; } = 7;

        /// <summary>ESeason as the server reports it (2 is winter).</summary>
        public static int Season { get; private set; }

        public static bool Winter => Season == 2;

        public static double TarkovNow =>
            OffsetSeconds + (DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds * Acceleration;

        /// <summary>"Now" as the location screen shows it: the Tarkov clock plus the debug preview offset. Raids use TarkovNow.</summary>
        public static double ScreenNow => TarkovNow + ForecasterPlugin.PreviewHours.Value * 3600.0;

        /// <summary>
        /// Whether the server's seed is in hand, fetching it if it is missing or a minute old. False when the
        /// server mod is not answering. Main thread only (it uses Time and SPT's request handler).
        /// </summary>
        public static bool Ready(bool force = false)
        {
            var now = Time.realtimeSinceStartup;
            if ((force || !_ready || now - _fetchedAt > RefreshSeconds) && now - _failedAt > RetrySeconds)
            {
                Fetch();
            }

            return _ready;
        }

        private static void Fetch()
        {
            try
            {
                var body = RequestHandler.PostJson("/sptforecaster/field", "{}");
                if (string.IsNullOrEmpty(body)) throw new Exception("empty response");

                var json = JObject.Parse(body);
                Acceleration = json.Value<double?>("acceleration") ?? 7;
                Season = json.Value<int?>("season") ?? 0;
                Field.Seed = json.Value<int>("seed");
                _ready = true;
                _fetchedAt = Time.realtimeSinceStartup;
                _warned = false;
            }
            catch (Exception ex)
            {
                _failedAt = Time.realtimeSinceStartup;
                if (!_warned)
                {
                    _warned = true;
                    ForecasterPlugin.Log.LogWarning($"Could not get the weather seed from the server ({ex.Message}). Is the SPTForecaster 0.0.2 server mod installed?");
                }
            }
        }

        public static bool IsNight(double tarkovSeconds)
        {
            var hour = (int)(Mod(tarkovSeconds, DaySeconds) / 3600);
            return hour > 21 || hour < 5;
        }

        public static double Mod(double value, double modulus) => ((value % modulus) + modulus) % modulus;
    }
}
