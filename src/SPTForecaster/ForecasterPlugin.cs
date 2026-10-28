using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;

namespace SPTForecaster
{
    [BepInPlugin("com.mybutthasarash.sptforecaster", "SPTForecaster", PluginVersion)]
    public class ForecasterPlugin : BaseUnityPlugin
    {
        /// <summary>Must match the csproj Version and the server mod's.</summary>
        public const string PluginVersion = "0.1.0";

        internal static ManualLogSource Log;

        internal static ConfigEntry<bool> ShowForecast;
        internal static ConfigEntry<bool> ApplyWeather;
        internal static ConfigEntry<bool> LockOnConfirm;
        internal static ConfigEntry<bool> ShowRadar;
        internal static ConfigEntry<float> RadarOpacity;
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

            ShowRadar = Config.Bind("Radar", "Show weather radar", true,
                "Draw the weather radar over the map on the location screen, for the selected time slot.");
            RadarOpacity = Config.Bind("Radar", "Radar opacity", 1f,
                new ConfigDescription("How strongly the radar is drawn. 1 is the default light wash; lower lets more of the map show through.",
                    new AcceptableValueRange<float>(0.2f, 1.5f)));

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
