using System;
using System.Collections.Generic;
using System.Reflection;
using Comfort.Common;
using EFT;
using EFT.UI.Matchmaker;
using EFT.Weather;
using HarmonyLib;
using JsonType;
using SPT.Reflection.Patching;
using UnityEngine;

namespace SPTForecaster
{
    /// <summary>The location screen opens: learn every location's map position, and put the radar on the map.</summary>
    internal sealed class LocationScreenShowPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(MatchMakerSelectionLocationScreen), nameof(MatchMakerSelectionLocationScreen.Show),
                new[] { typeof(IEftSession), typeof(RaidSettings), typeof(MatchmakerPlayersController) });

        [PatchPostfix]
        private static void Postfix(MatchMakerSelectionLocationScreen __instance, IEftSession session)
        {
            try
            {
                Maps.Remember(session?.LocationSettings);
                RadarLayer.Attach(__instance._locationButtonsContainer, __instance._locationPathsContainer);
            }
            catch (Exception ex)
            {
                ForecasterPlugin.Log.LogError("Location screen setup failed: " + ex);
            }
        }
    }

    /// <summary>The location screen sets up its time panel: attach the forecast.</summary>
    internal sealed class ConditionsPanelSetPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(LocationConditionsPanel), nameof(LocationConditionsPanel.Set));

        [PatchPostfix]
        private static void Postfix(LocationConditionsPanel __instance, RaidSettings raidSettings)
        {
            try
            {
                ForecastPanel.Attach(__instance, raidSettings);
            }
            catch (Exception ex)
            {
                ForecasterPlugin.Log.LogError("Could not attach the forecast: " + ex);
            }
        }
    }

    /// <summary>Runs every frame the time panel is up (it ticks the clocks): keep the forecast current.</summary>
    internal sealed class ConditionsPanelUpdatePatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(LocationConditionsPanel), nameof(LocationConditionsPanel.UpdateConditions));

        [PatchPostfix]
        private static void Postfix(LocationConditionsPanel __instance)
        {
            var fp = __instance.GetComponent<ForecastPanel>();
            if (fp != null) fp.Tick();
        }
    }

    /// <summary>
    /// The slot the player confirmed. Recording the moment means the raid gets the weather the forecast
    /// showed at that moment, however long the following screens and the loading take.
    /// </summary>
    internal static class WeatherLock
    {
        private const float MaxAgeSeconds = 30 * 60;

        private static string _location;
        private static bool _rightSlot;
        private static double _start;
        private static float _at = -1f;

        public static void Record(MatchMakerSelectionLocationScreen screen)
        {
            var settings = AccessTools.Field(typeof(MatchMakerSelectionLocationScreen), "_raidSettings")?.GetValue(screen) as RaidSettings;
            var location = settings?.SelectedLocation;
            if (location == null) return;

            _location = location.Id;
            _rightSlot = settings.SelectedDateTime == EDateTime.PAST;
            _start = Schedule.TarkovNow + (_rightSlot ? Schedule.HalfDaySeconds : 0);
            _at = Time.realtimeSinceStartup;

            if (ForecasterPlugin.VerboseLog.Value && !Maps.NoForecast(_location))
            {
                ForecasterPlugin.Log.LogInfo($"Confirmed {_location}, {Maps.Clock(_start)} slot: raid weather locked from here.");
            }
        }

        public static bool TryGet(string location, bool rightSlot, out double start)
        {
            start = 0;
            if (_at < 0 || Time.realtimeSinceStartup - _at > MaxAgeSeconds) return false;
            if (!Maps.Same(_location, location) || _rightSlot != rightSlot) return false;
            start = _start;
            return true;
        }
    }

    /// <summary>Next on the location screen (the accept button's listener).</summary>
    internal sealed class NextButtonPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(MatchMakerSelectionLocationScreen), "CG_Awake");

        [PatchPrefix]
        private static void Prefix(MatchMakerSelectionLocationScreen __instance) => WeatherLock.Record(__instance);
    }

    /// <summary>Ready on the location screen, which skips the following screens.</summary>
    internal sealed class ReadyButtonPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(MatchMakerSelectionLocationScreen), "CG_Awake2");

        [PatchPrefix]
        private static void Prefix(MatchMakerSelectionLocationScreen __instance) => WeatherLock.Record(__instance);
    }

    /// <summary>
    /// The raid hands the weather curve its nodes. The curve is sampled by the raid's in-game clock
    /// (GameWorld.GameDateTime), so nodes are laid on that clock: the block that covers the raid's start
    /// lands at the raid's first second. Each block holds steady, and the change to the next is a blend
    /// centred on the block boundary, so a change is half done at the minute the forecast names.
    /// </summary>
    internal sealed class SetWeatherNodesPatch : ModulePatch
    {
        /// <summary>The shortest blend between blocks, in real seconds.</summary>
        private const double MinBlendRealSeconds = 120;

        /// <summary>
        /// CloudSix (com.matsix.cloudsix) draws its own clouds from the weather curve and slows coverage
        /// changes so a full clear/overcast swing takes at least its "Min Cloud Transition Time" (200 s by
        /// default). Blending at least that slowly keeps its clouds in step with the forecast instead of
        /// trailing a change by up to a minute or more.
        /// </summary>
        private static double BlendRealSeconds()
        {
            try
            {
                if (BepInEx.Bootstrap.Chainloader.PluginInfos.TryGetValue("com.matsix.cloudsix", out var info) && info.Instance != null
                    && info.Instance.Config.TryGetEntry<float>("Weather", "Min Cloud Transition Time (sec)", out var entry))
                {
                    return Math.Max(MinBlendRealSeconds, entry.Value + 10);
                }
            }
            catch (Exception ex)
            {
                ForecasterPlugin.Log.LogWarning("Could not read CloudSix's cloud transition time: " + ex.Message);
            }

            return MinBlendRealSeconds;
        }

        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(WeatherController), nameof(WeatherController.SetWeatherNodes));

        [PatchPrefix]
        private static void Prefix(ref WeatherNode[] nodes)
        {
            try
            {
                if (!ForecasterPlugin.ApplyWeather.Value) return;

                var world = Singleton<GameWorld>.Instance;
                if (world == null || world.GameDateTime == null) return;

                var location = world.LocationId;
                if (Maps.NoForecast(location)) return;
                if (!Schedule.Ready(force: true)) return;
                if (!Maps.Position(location, out var x, out var y))
                {
                    ForecasterPlugin.Log.LogWarning($"No map position for '{location}'; the raid keeps SPT's weather.");
                    return;
                }

                var raidClock = world.GameDateTime.Calculate();
                var timeOfDay = raidClock.TimeOfDay.TotalSeconds;

                // Which slot this raid is: whichever of now and now + 12 h has the raid clock's time of day.
                var now = Schedule.TarkovNow;
                var rightSlot = Maps.CircularDistance(now + Schedule.HalfDaySeconds, timeOfDay) < Maps.CircularDistance(now, timeOfDay);
                var start = rightSlot ? now + Schedule.HalfDaySeconds : now;
                var locked = ForecasterPlugin.LockOnConfirm.Value && WeatherLock.TryGet(location, rightSlot, out start);
                if (!locked) start = rightSlot ? now + Schedule.HalfDaySeconds : now;

                // This map's weather: the field read at its button, three Tarkov hours either side of the raid
                // and sixteen after its start (over two real hours of raid).
                var blocks = Field.Blocks(x, y, start - 3 * 3600, start + 16 * 3600);
                var blendReal = BlendRealSeconds();
                SmoothClouds(blocks, blendReal);
                var built = Build(blocks, start, raidClock.Ticks, blendReal * Schedule.Acceleration);
                if (built.Length < 2) return;
                nodes = built;

                if (ForecasterPlugin.VerboseLog.Value)
                {
                    var first = blocks.Find(b => b.Start <= start && b.End > start);
                    var forecast = Forecast.Build(blocks, start, 60);
                    ForecasterPlugin.Log.LogInfo(
                        $"Raid weather for {location}: {built.Length} nodes from Tarkov {Maps.Clock(start)} ({(locked ? "locked at confirm" : "not locked, from now")}), raid clock {raidClock:HH:mm:ss}, changes blend over {blendReal:0} s. " +
                        (first != null ? $"Opening block clouds {first.Clouds:0.##} rain {first.Rain:0} @ {first.RainIntensity:0.##} fog {first.Fog:0.####} wind {first.WindSpeed:0}. " : "") +
                        $"Forecast for this start: {forecast.Summary}.");
                }
            }
            catch (Exception ex)
            {
                ForecasterPlugin.Log.LogError("Could not apply the forecast weather; the raid keeps SPT's: " + ex);
            }
        }

        /// <summary>
        /// Blocks are two real minutes long, so a big cloud change (rain arriving under a clear sky) can be
        /// quicker than CloudSix lets its clouds move. Averaging cloud cover over neighbouring blocks spreads
        /// such a change over at least the blend time. Rain and fog are left as they are, so the card stays exact.
        /// </summary>
        private static void SmoothClouds(List<Block> blocks, double blendRealSeconds)
        {
            var reach = (int)Math.Ceiling(blendRealSeconds / 240.0); // blocks either side; 2 real min per block
            if (reach < 1 || blocks.Count < 3) return;
            var original = blocks.ConvertAll(b => b.Clouds);
            for (var i = 0; i < blocks.Count; i++)
            {
                double sum = 0;
                var n = 0;
                for (var k = Math.Max(0, i - reach); k <= Math.Min(blocks.Count - 1, i + reach); k++) { sum += original[k]; n++; }
                blocks[i].Clouds = sum / n;
            }
        }

        private static WeatherNode[] Build(List<Block> blocks, double start, long raidTicks, double blendSeconds)
        {
            var from = start - 3 * 3600;
            var to = start + 16 * 3600;
            var list = new List<WeatherNode>();
            long last = long.MinValue;

            // Each block is held from half a blend after its start to half a blend before its end; the curve
            // runs linearly between one block's last node and the next block's first, so the blend straddles
            // the boundary. A block never gives up more than half its length to blending.
            foreach (var b in blocks)
            {
                if (b.End <= from || b.Start >= to) continue;
                var half = Math.Min(blendSeconds / 2, (b.End - b.Start) * 0.25);
                Add(list, b, b.Start + half, start, raidTicks, ref last);
                Add(list, b, b.End - half, start, raidTicks, ref last);
            }

            return list.ToArray();
        }

        private static void Add(List<WeatherNode> list, Block b, double tarkovSeconds, double start, long raidTicks, ref long last)
        {
            var ticks = raidTicks + (long)((tarkovSeconds - start) * TimeSpan.TicksPerSecond);
            if (ticks <= last) return;
            last = ticks;
            list.Add(Forecast.ToNode(b, ticks));
        }
    }
}
