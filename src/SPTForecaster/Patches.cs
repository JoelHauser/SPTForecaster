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
    /// lands at the raid's first second. Each block holds steady and blends into the next over its last
    /// two real minutes, so a change lands when the forecast said it would.
    /// </summary>
    internal sealed class SetWeatherNodesPatch : ModulePatch
    {
        /// <summary>The blend into the next block, in Tarkov seconds (two real minutes at x7).</summary>
        private const double BlendSeconds = 14 * 60;

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

                var blocks = Schedule.Get(force: true);
                if (blocks == null || blocks.Count == 0) return;

                var raidClock = world.GameDateTime.Calculate();
                var timeOfDay = raidClock.TimeOfDay.TotalSeconds;

                // Which slot this raid is: whichever of now and now + 12 h has the raid clock's time of day.
                var now = Schedule.TarkovNow;
                var rightSlot = Maps.CircularDistance(now + Schedule.HalfDaySeconds, timeOfDay) < Maps.CircularDistance(now, timeOfDay);
                var start = rightSlot ? now + Schedule.HalfDaySeconds : now;
                var locked = ForecasterPlugin.LockOnConfirm.Value && WeatherLock.TryGet(location, rightSlot, out start);
                if (!locked) start = rightSlot ? now + Schedule.HalfDaySeconds : now;

                var built = Build(blocks, start, raidClock.Ticks);
                if (built.Length < 2) return;
                nodes = built;

                if (ForecasterPlugin.VerboseLog.Value)
                {
                    var first = blocks.Find(b => b.Start <= start && b.End > start);
                    var forecast = Forecast.Build(blocks, start, 60);
                    ForecasterPlugin.Log.LogInfo(
                        $"Raid weather for {location}: {built.Length} nodes from Tarkov {Maps.Clock(start)} ({(locked ? "locked at confirm" : "not locked, from now")}), raid clock {raidClock:HH:mm:ss}. " +
                        (first != null ? $"Opening block clouds {first.Clouds:0.##} rain {first.Rain:0} @ {first.RainIntensity:0.##} fog {first.Fog:0.####} wind {first.WindSpeed:0}. " : "") +
                        $"Forecast for this start: {forecast.Summary}.");
                }
            }
            catch (Exception ex)
            {
                ForecasterPlugin.Log.LogError("Could not apply the forecast weather; the raid keeps SPT's: " + ex);
            }
        }

        private static WeatherNode[] Build(List<Block> blocks, double start, long raidTicks)
        {
            // Plenty either side: three Tarkov hours before, sixteen after (over two real hours of raid).
            var from = start - 3 * 3600;
            var to = start + 16 * 3600;
            var list = new List<WeatherNode>();
            long last = long.MinValue;

            foreach (var b in blocks)
            {
                if (b.End <= from || b.Start >= to) continue;
                var hold = Math.Min(BlendSeconds, (b.End - b.Start) * 0.25);
                Add(list, b, b.Start, start, raidTicks, ref last);
                Add(list, b, b.End - hold, start, raidTicks, ref last);
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
