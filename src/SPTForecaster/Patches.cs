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

                // Whatever time the radar was given on the last visit is stale now (a raid or a relaunch
                // ago); the panel gives it a fresh one below if a map is selected, and if none is, the radar
                // falls back to the remembered slot at the current time.
                RadarLayer.TargetTime = null;
                RadarLayer.Attach(__instance._locationButtonsContainer, __instance._locationPathsContainer);
                RadarKey.Attach(__instance._locationButtonsContainer?.parent?.Find("SPTForecasterRadar"), __instance);

                // The game selects the last map before it shows the screen, so the forecast was first set up
                // while the screen was hidden: the toggles read as inactive (always the left slot) and the bar's
                // parts couldn't be found. Now the screen is up, build and read everything again.
                ForecastPanel.RefreshAll();
            }
            catch (Exception ex)
            {
                ForecasterPlugin.Log.LogError("Location screen setup failed: " + ex);
            }
        }
    }

    /// <summary>
    /// The location screen sets up its time panel: attach the forecast. The same panel is on the
    /// confirm screen (MatchMakerAcceptScreen) and the raid invite window, which pass takeFromCurrent
    /// false; the forecast stays off those, so it never sits over the deploy preview Deploy Screen
    /// Overhaul (LoadingRaid) draws there.
    /// </summary>
    internal sealed class ConditionsPanelSetPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(LocationConditionsPanel), nameof(LocationConditionsPanel.Set));

        [PatchPostfix]
        private static void Postfix(LocationConditionsPanel __instance, RaidSettings raidSettings, bool takeFromCurrent)
        {
            try
            {
                if (!takeFromCurrent) return;
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
    /// Keeps the player's time slot across maps that have none. Selecting Labs or Labyrinth makes the game
    /// set the slot back to the left one, and nothing puts it back on the next map, so the right slot's
    /// forecast jumped to the left one's. The slot last picked on any map with the toggles (Factory
    /// included) is remembered and handed back when such a map is selected again.
    /// </summary>
    internal static class SlotMemory
    {
        private static readonly AccessTools.FieldRef<MatchMakerSelectionLocationScreen, EDateTime> SelectedTime =
            AccessTools.FieldRefAccess<MatchMakerSelectionLocationScreen, EDateTime>("_selectedDateTime");

        private static readonly AccessTools.FieldRef<MatchMakerSelectionLocationScreen, RaidSettings> Settings =
            AccessTools.FieldRefAccess<MatchMakerSelectionLocationScreen, RaidSettings>("_raidSettings");

        private static EDateTime? _chosen;

        /// <summary>Whether the slot last chosen is the right-hand one (+12 h).</summary>
        public static bool RightSlot => _chosen == EDateTime.PAST;

        /// <summary>
        /// A map with the two slot toggles. Factory counts: its toggles pick day or night Factory, but they are
        /// the same left and right slots, so a choice made there carries on to the next map (and back).
        /// </summary>
        private static bool FreeChoice(string id) =>
            id != null && Array.IndexOf(LocationSettings.Location.AvailableMaps, id) >= 0 && id != "laboratory" && !Maps.OneSlot(id);

        /// <summary>The game reports every slot change here, the player's and its own; only an ordinary map's counts.</summary>
        public static void Record(MatchMakerSelectionLocationScreen screen, EDateTime time)
        {
            var id = Settings(screen)?.SelectedLocation?.Id;
            if (!FreeChoice(id) || _chosen == time) return;
            _chosen = time;
            if (ForecasterPlugin.VerboseLog.Value) ForecasterPlugin.Log.LogInfo($"Slot: {Name(time)} chosen on {id}.");
        }

        /// <summary>Before an ordinary map is selected, put the remembered slot back where the game reads it.</summary>
        public static void Restore(MatchMakerSelectionLocationScreen screen, LocationSettings.Location location)
        {
            if (_chosen == null || !FreeChoice(location?.Id)) return;
            var settings = Settings(screen);
            if (settings == null || settings.SelectedDateTime == _chosen.Value) return;
            if (ForecasterPlugin.VerboseLog.Value)
            {
                ForecasterPlugin.Log.LogInfo($"Slot: {location.Id} selected with the {Name(settings.SelectedDateTime)} slot; putting back the {Name(_chosen.Value)} one.");
            }

            SelectedTime(screen) = _chosen.Value;
            settings.SelectedDateTime = _chosen.Value;
        }

        /// <summary>
        /// After a map is selected: if the slot still isn't the chosen one (something between the click and
        /// the panel reset it), switch the toggle over, the same as the player clicking it.
        /// </summary>
        public static void Enforce(MatchMakerSelectionLocationScreen screen, LocationSettings.Location location)
        {
            if (_chosen == null || !FreeChoice(location?.Id)) return;
            var settings = Settings(screen);
            if (settings == null || settings.SelectedDateTime == _chosen.Value) return;
            if (ForecasterPlugin.VerboseLog.Value)
            {
                ForecasterPlugin.Log.LogInfo($"Slot: {location.Id} ended up on the {Name(settings.SelectedDateTime)} slot after selecting; switching to the {Name(_chosen.Value)} one.");
            }

            var conditions = screen._conditions;
            var toggle = conditions == null ? null : _chosen.Value == EDateTime.PAST ? conditions._pmTimeToggle : conditions._amTimeToggle;
            if (toggle != null && toggle.gameObject.activeInHierarchy) toggle.isOn = true;
            else screen.UpdateSelectedTime(_chosen.Value);
        }

        private static string Name(EDateTime time) => time == EDateTime.PAST ? "right" : "left";
    }

    /// <summary>A slot change on the location screen (a toggle, or the game's own reset).</summary>
    internal sealed class SlotChangedPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(MatchMakerSelectionLocationScreen), nameof(MatchMakerSelectionLocationScreen.UpdateSelectedTime));

        [PatchPostfix]
        private static void Postfix(MatchMakerSelectionLocationScreen __instance, EDateTime time) => SlotMemory.Record(__instance, time);
    }

    /// <summary>A map is clicked on the location screen.</summary>
    internal sealed class LocationSelectedPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(MatchMakerSelectionLocationScreen), nameof(MatchMakerSelectionLocationScreen.OnLocationSelected));

        [PatchPrefix]
        private static void Prefix(MatchMakerSelectionLocationScreen __instance, LocationSettings.Location location)
        {
            try
            {
                SlotMemory.Restore(__instance, location);
            }
            catch (Exception ex)
            {
                ForecasterPlugin.Log.LogError("Could not keep the time slot: " + ex.Message);
            }
        }

        [PatchPostfix]
        private static void Postfix(MatchMakerSelectionLocationScreen __instance, LocationSettings.Location location)
        {
            try
            {
                SlotMemory.Enforce(__instance, location);
            }
            catch (Exception ex)
            {
                ForecasterPlugin.Log.LogError("Could not keep the time slot: " + ex.Message);
            }
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
            if (!TryGetLatest(location, out start) || _rightSlot != rightSlot) return false;
            return true;
        }

        /// <summary>The last confirmed start on this location, whichever slot it was.</summary>
        public static bool TryGetLatest(string location, out double start)
        {
            start = 0;
            if (_at < 0 || Time.realtimeSinceStartup - _at > MaxAgeSeconds) return false;
            if (!Maps.Same(_location, location)) return false;
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
        /// <summary>Tarkov seconds of weather beyond the raid either way, for a mod moving the raid clock (24 h).</summary>
        private const double DayMargin = 24 * 3600;

        /// <summary>The shortest blend between blocks, in real seconds.</summary>
        private const double MinBlendRealSeconds = 120;

        /// <summary>
        /// CloudSix (com.matsix.cloudsix) draws its own clouds from the weather curve and slows coverage
        /// changes so a full clear/overcast swing takes at least its "Min Cloud Transition Time" (200 s by
        /// default). Blending at least that slowly keeps its clouds in step with the forecast instead of
        /// trailing a change by up to a minute or more.
        /// </summary>
        internal static double BlendRealSeconds()
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

                // This map's weather: the field read at its button, from three Tarkov hours before the raid to
                // well past its end. Raid times differ between servers, so the span is this server's raid time
                // for the map with half again and half an hour to spare (a mod may lengthen a raid), and never
                // under sixteen Tarkov hours; past the last node the game would run the weather backwards.
                var raidMinutes = Maps.RaidMinutes(location);
                var after = Math.Max(16 * 3600, (raidMinutes > 0 ? raidMinutes * 1.5 + 30 : 400) * 60 * Schedule.Acceleration);
                // A day either side as well: TimeWeatherChanger (and the like) can move the raid clock up to
                // 23 h either way, and the clock must still land on this map's weather, not off the curve.
                var before = DayMargin;
                after += DayMargin;
                var blocks = Field.Blocks(x, y, start - before, start + after);
                var blendReal = BlendRealSeconds();
                SmoothClouds(blocks, blendReal);
                ThickenFog(blocks, ForecasterPlugin.FogThickness.Value);
                var built = Build(blocks, start, start - before, start + after, raidClock.Ticks, blendReal * Schedule.Acceleration);
                if (built.Length < 2) return;
                nodes = built;
                BlizzardDriver.Set(blocks, start, raidClock.Ticks);
                Thunder.Set(blocks, start, raidClock.Ticks, x, y, raidMinutes);

                if (ForecasterPlugin.VerboseLog.Value)
                {
                    var first = blocks.Find(b => b.Start <= start && b.End > start);
                    var forecast = Forecast.Build(blocks, start, raidMinutes > 0 ? raidMinutes : 60);
                    ForecasterPlugin.Log.LogInfo(
                        $"Raid weather for {location}: {built.Length} nodes from Tarkov {Maps.Clock(start)}, covering {(after - DayMargin) / 60 / Schedule.Acceleration:0} raid min (+ a Tarkov day either way) (server raid time {(raidMinutes > 0 ? raidMinutes + " min" : "unknown")}) ({(locked ? "locked at confirm" : "not locked, from now")}), raid clock {raidClock:HH:mm:ss}, changes blend over {blendReal:0} s. " +
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
        /// The "Fog thickness" setting: scales each fog above the clear-air baseline (Field.FogValues[0]), so
        /// how thick mist through thick fog look can be tuned in game. The words are unchanged.
        /// </summary>
        internal static void ThickenFog(List<Block> blocks, double scale)
        {
            if (Math.Abs(scale - 1) < 0.001) return;
            var clear = Field.FogValues[0];
            foreach (var b in blocks)
            {
                if (b.FogLevel > 0) b.Fog = clear + (b.Fog - clear) * scale;
            }
        }

        /// <summary>
        /// Blocks are two real minutes long, so a big cloud change (rain arriving under a clear sky) can be
        /// quicker than CloudSix lets its clouds move. Averaging cloud cover over neighbouring blocks spreads
        /// such a change over at least the blend time. Rain and fog are left as they are, so the card stays exact.
        /// </summary>
        internal static void SmoothClouds(List<Block> blocks, double blendRealSeconds)
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

        private static WeatherNode[] Build(List<Block> blocks, double start, double from, double to, long raidTicks, double blendSeconds)
        {
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

    /// <summary>
    /// The lightning flash: TOD_Sky sets its sun/moon light every frame in LateUpdate; after it, add the flash
    /// (Thunder.Flash) and, while it lasts, full shadow strength, so walls block it completely (at night the
    /// moonlight's shadows are weak, which would let a flash glow through them). Windows and doorways let it in
    /// as they let in daylight. TOD resets both next frame.
    /// </summary>
    internal sealed class ThunderLightPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(TOD_Sky), nameof(TOD_Sky.LateUpdate));

        [PatchPostfix]
        private static void Postfix(TOD_Sky __instance)
        {
            var flash = Thunder.Flash;
            if (flash <= 0.001f) return;
            var light = __instance.Components != null ? __instance.Components.LightSource : null;
            if (light == null) return;
            light.intensity += flash;
            light.shadowStrength = 1f;
        }
    }
}
