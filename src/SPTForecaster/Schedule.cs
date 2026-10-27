using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using SPT.Common.Http;
using UnityEngine;

namespace SPTForecaster
{
    /// <summary>One stretch of weather. Start and End are Tarkov seconds, as the server mod defines them.</summary>
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
    /// The server mod's schedule, cached. Both the forecast and the raid's weather are built from it,
    /// which is what keeps them from disagreeing.
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

        private static List<Block> _blocks;
        private static float _fetchedAt = -1000f;
        private static float _failedAt = -1000f;
        private static bool _warned;

        public static double Acceleration { get; private set; } = 7;

        /// <summary>ESeason as the server reports it (2 is winter).</summary>
        public static int Season { get; private set; }

        public static bool Winter => Season == 2;

        public static double TarkovNow =>
            OffsetSeconds + (DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds * Acceleration;

        /// <summary>The cached schedule, refetched once a minute. Null when the server mod is not answering.</summary>
        public static List<Block> Get(bool force = false)
        {
            var now = Time.realtimeSinceStartup;
            var stale = _blocks == null || now - _fetchedAt > RefreshSeconds;
            if ((force || stale) && now - _failedAt > RetrySeconds)
            {
                Fetch();
            }

            return _blocks;
        }

        private static void Fetch()
        {
            try
            {
                var body = RequestHandler.PostJson("/sptforecaster/schedule", "{}");
                if (string.IsNullOrEmpty(body))
                {
                    throw new Exception("empty response");
                }

                var json = JObject.Parse(body);
                Acceleration = json.Value<double?>("acceleration") ?? 7;
                Season = json.Value<int?>("season") ?? 0;

                var list = new List<Block>();
                foreach (var b in (JArray)json["blocks"])
                {
                    list.Add(new Block
                    {
                        Start = b.Value<long>("Start"),
                        End = b.Value<long>("End"),
                        Clouds = b.Value<double>("Clouds"),
                        WindSpeed = b.Value<double>("WindSpeed"),
                        WindDirection = b.Value<int>("WindDirection"),
                        WindGustiness = b.Value<double>("WindGustiness"),
                        Rain = b.Value<double>("Rain"),
                        RainIntensity = b.Value<double>("RainIntensity"),
                        Fog = b.Value<double>("Fog"),
                        Temperature = b.Value<double>("Temperature"),
                        Pressure = b.Value<double>("Pressure"),
                    });
                }

                _blocks = list;
                _fetchedAt = Time.realtimeSinceStartup;
                _warned = false;
            }
            catch (Exception ex)
            {
                _failedAt = Time.realtimeSinceStartup;
                if (!_warned)
                {
                    _warned = true;
                    ForecasterPlugin.Log.LogWarning($"Could not get the weather schedule from the server ({ex.Message}). Is the SPTForecaster server mod installed?");
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
