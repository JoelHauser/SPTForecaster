using System;
using System.Collections.Generic;
using Comfort.Common;
using EFT;
using EFT.Weather;
using UnityEngine;

namespace Forecaster
{
    /// <summary>
    /// Brings the game's own blizzard into a Winter raid while the forecast says "Blizzard". The game has one
    /// (RainController's WinterStorm state: storm snowflake layers with sideways speed, SnowFlakes.StormFactor,
    /// and the storm look on snow-covered ground, SnowWetRenderer.StormEnabled), but only switches it on for
    /// a live-server event or the Storm season. Here StormFactor is eased in and out over
    /// <see cref="RampRealSeconds"/>, centred on the block boundaries the card's times name, the same as the
    /// weather nodes' blends. The Storm season is left to the game, which keeps its blizzard on throughout.
    /// Follows the raid clock, so a mod moving the time (TimeWeatherChanger) moves the blizzard with the
    /// weather; and steps aside while the game's weather debug override (that mod's panel) is on.
    /// </summary>
    internal sealed class BlizzardDriver : MonoBehaviour
    {
        private const float RampRealSeconds = 40f;

        /// <summary>Blizzard spans on the raid clock (ticks), merged where they touch.</summary>
        private static readonly List<long[]> Spans = new List<long[]>();

        private float _factor;
        private bool _touched;

        /// <summary>Called with the raid's blocks when its weather nodes are set.</summary>
        public static void Set(List<Block> blocks, double start, long raidTicks)
        {
            Spans.Clear();
            if (Schedule.Season == 2)
            {
                foreach (var b in blocks)
                {
                    if (!b.Blizzard) continue;
                    var from = raidTicks + (long)((b.Start - start) * TimeSpan.TicksPerSecond);
                    var to = raidTicks + (long)((b.End - start) * TimeSpan.TicksPerSecond);
                    if (Spans.Count > 0 && Spans[Spans.Count - 1][1] >= from) Spans[Spans.Count - 1][1] = to;
                    else Spans.Add(new[] { from, to });
                }
            }

            var weather = WeatherController.Instance;
            if (weather == null) return;
            var driver = weather.GetComponent<BlizzardDriver>();
            if (driver == null) driver = weather.gameObject.AddComponent<BlizzardDriver>();
            driver._factor = 0f;
            if (ForecasterPlugin.VerboseLog.Value && Spans.Count > 0)
            {
                ForecasterPlugin.Log.LogInfo($"Blizzard: {Spans.Count} spell(s) in this raid's weather.");
            }
        }

        private void Update()
        {
            try
            {
                if (Schedule.Season != 2 || (Spans.Count == 0 && !_touched)) return;
                var world = Singleton<GameWorld>.Instance;
                var rain = WeatherController.Instance != null ? WeatherController.Instance.RainController : null;
                if (world == null || world.GameDateTime == null || rain == null || rain._snowFlakes == null) return;

                // Half the ramp before a boundary to half after, in raid-clock ticks (the clock runs Acceleration times real time).
                var now = world.GameDateTime.Calculate().Ticks;
                var half = (long)(RampRealSeconds / 2 * Schedule.Acceleration * TimeSpan.TicksPerSecond);
                var target = 0f;
                // While the game's weather debug override is on (TimeWeatherChanger's panel), the weather is
                // the player's, not the forecast's: no blizzard of ours on top of it.
                var overridden = WeatherController.Instance.WeatherDebug != null && WeatherController.Instance.WeatherDebug.Enabled;
                foreach (var span in Spans)
                {
                    if (overridden) break;
                    if (now >= span[0] - half && now < span[1] - half) { target = 1f; break; }
                }

                _factor = Mathf.MoveTowards(_factor, target, Time.deltaTime / RampRealSeconds);
                rain._snowFlakes.StormFactor = _factor;
                if (rain._snowWetRenderer != null) rain._snowWetRenderer.StormEnabled = _factor > 0.5f;
                _touched = _factor > 0f;
            }
            catch (Exception ex)
            {
                ForecasterPlugin.Log.LogError("Blizzard update failed, leaving the snow as it is: " + ex.Message);
                Spans.Clear();
                _touched = false;
            }
        }
    }
}
