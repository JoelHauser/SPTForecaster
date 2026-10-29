using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;

namespace SPTForecaster
{
    [BepInPlugin("com.mybutthasarash.sptforecaster", "SPTForecaster", PluginVersion)]
    public class ForecasterPlugin : BaseUnityPlugin
    {
        /// <summary>Must match the csproj Version and the server mod's.</summary>
        public const string PluginVersion = "0.2.0";

        internal static ManualLogSource Log;

        internal static ConfigEntry<bool> ShowForecast;
        internal static ConfigEntry<bool> ApplyWeather;
        internal static ConfigEntry<bool> LockOnConfirm;
        internal static ConfigEntry<float> FogThickness;
        internal static ConfigEntry<bool> ShowRadar;
        internal static ConfigEntry<float> RadarOpacity;
        internal static ConfigEntry<bool> ShowRadarKey;
        internal static ConfigEntry<bool> PlayRadar;
        internal static ConfigEntry<bool> Lightning;
        internal static ConfigEntry<float> ThunderVolume;
        internal static ConfigEntry<bool> VerboseLog;
        internal static ConfigEntry<WeatherPattern> ForcePattern;
        internal static ConfigEntry<int> PreviewHours;

        private void Awake()
        {
            Log = Logger;

            ShowForecast = Config.Bind("Forecast", "Show forecast", true,
                "Show the selected time slot's weather forecast above the time bar on the location screen.");
            ApplyWeather = Config.Bind("Forecast", "Raid weather follows the forecast", true,
                "Give the raid the weather the forecast showed. Off leaves raid weather to SPT, and the forecast may then be wrong.");
            LockOnConfirm = Config.Bind("Forecast", "Lock weather when the slot is confirmed", true,
                "The raid's weather starts from the moment you press Next or Ready, so loading time cannot move it into a different block.");
            FogThickness = Config.Bind("Forecast", "Fog thickness", 1f,
                new ConfigDescription("How thick mist, fog, dense fog and thick fog are in raid (1 is the mod's default, 2 twice as thick above clear air). Takes effect from the next raid; the forecast's words don't change. Thicker fog also hides you from bots.",
                    new AcceptableValueRange<float>(0.5f, 3f)));

            ShowRadar = Config.Bind("Radar", "Show weather radar", true,
                "Draw the weather radar over the map on the location screen, for the selected time slot.");
            RadarOpacity = Config.Bind("Radar", "Radar opacity", 1f,
                new ConfigDescription("How strongly the radar is drawn. 1 is the default light wash; lower lets more of the map show through.",
                    new AcceptableValueRange<float>(0.2f, 1.5f)));
            ShowRadarKey = Config.Bind("Radar", "Show radar key", true,
                "Show which radar colour means which weather, in the map's bottom-right corner.");
            Lightning = Config.Bind("Storms", "Lightning and thunder", true,
                "Lightning flashes and thunder while a thunderstorm is over or near the map. Walls block the flash and the game's own indoor sound muffles the thunder.");
            ThunderVolume = Config.Bind("Storms", "Thunder volume", 1f,
                new ConfigDescription("How loud thunder is, on top of the game's own volume settings.", new AcceptableValueRange<float>(0f, 1.5f)));
            PlayRadar = Config.Bind("Radar", "Play the raid on the radar", true,
                "When you pick a map and time, the radar plays that raid's weather through twice (as long as the map's raid time), then rests on the raid's start.");

            VerboseLog = Config.Bind("Debug", "Log weather details", true,
                "Write the forecast and the raid's applied weather to the BepInEx log, for checking that they match.");
            ForcePattern = Config.Bind("Debug", "Force weather pattern", WeatherPattern.Off,
                "Make the whole region use one weather pattern, to see how it looks. The radar, the forecast and raid weather all follow it. Off uses the server's seeded patterns.");
            PreviewHours = Config.Bind("Debug", "Preview hours ahead", 0,
                new ConfigDescription("Show the location screen's radar and forecast this many in-game hours ahead, to watch weather develop. Raids keep the real time.",
                    new AcceptableValueRange<int>(0, 48)));

            ShowForecast.SettingChanged += (_, __) => ForecastPanel.RefreshAll();
            ApplyForcedPattern();
            ForcePattern.SettingChanged += (_, __) =>
            {
                ApplyForcedPattern();
                RadarLayer.Invalidate();
                ForecastPanel.RefreshAll();
            };
            PreviewHours.SettingChanged += (_, __) => ForecastPanel.RefreshAll();

            ShowRadar.SettingChanged += (_, __) => RadarLayer.Invalidate();
            RadarOpacity.SettingChanged += (_, __) => RadarLayer.Invalidate();

            // Each on its own, so one that can't find its target doesn't stop the rest.
            Enable(new LocationScreenShowPatch());
            Enable(new ConditionsPanelSetPatch());
            Enable(new ConditionsPanelUpdatePatch());
            Enable(new NextButtonPatch());
            Enable(new ReadyButtonPatch());
            Enable(new SlotChangedPatch());
            Enable(new LocationSelectedPatch());
            Enable(new SetWeatherNodesPatch());
            Enable(new ThunderLightPatch());
            Thunder.LoadClips(this, System.IO.Path.GetDirectoryName(Info.Location));

            Log.LogInfo($"SPTForecaster {PluginVersion} loaded.");
        }

        /// <summary>The debug choices for <see cref="ForcePattern"/>, in the field's own pattern order.</summary>
        internal enum WeatherPattern
        {
            Off,
            FairWeather,
            Unsettled,
            ProlongedRain,
            CoastalFog,
            StormFront,
            Overcast,
        }

        private static void ApplyForcedPattern()
        {
            switch (ForcePattern.Value)
            {
                case WeatherPattern.FairWeather: Field.ForcedPattern = "fair"; break;
                case WeatherPattern.Unsettled: Field.ForcedPattern = "unsettled"; break;
                case WeatherPattern.ProlongedRain: Field.ForcedPattern = "longrain"; break;
                case WeatherPattern.CoastalFog: Field.ForcedPattern = "coastfog"; break;
                case WeatherPattern.StormFront: Field.ForcedPattern = "storms"; break;
                case WeatherPattern.Overcast: Field.ForcedPattern = "grey"; break;
                default: Field.ForcedPattern = null; break;
            }
        }

        private static void Enable(SPT.Reflection.Patching.ModulePatch patch)
        {
            try
            {
                patch.Enable();
            }
            catch (System.Exception ex)
            {
                Log.LogError($"Could not enable {patch.GetType().Name}: {ex.Message}");
            }
        }
    }
}
