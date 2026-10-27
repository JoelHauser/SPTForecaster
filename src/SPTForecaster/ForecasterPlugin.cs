using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;

namespace SPTForecaster
{
    [BepInPlugin("com.mybutthasarash.sptforecaster", "SPTForecaster", PluginVersion)]
    public class ForecasterPlugin : BaseUnityPlugin
    {
        /// <summary>Must match the csproj Version and the server mod's.</summary>
        public const string PluginVersion = "0.0.1";

        internal static ManualLogSource Log;

        internal static ConfigEntry<bool> ShowForecast;
        internal static ConfigEntry<bool> ApplyWeather;
        internal static ConfigEntry<bool> LockOnConfirm;
        internal static ConfigEntry<float> OffsetX;
        internal static ConfigEntry<float> OffsetY;
        internal static ConfigEntry<float> TextScale;
        internal static ConfigEntry<bool> VerboseLog;

        private void Awake()
        {
            Log = Logger;

            ShowForecast = Config.Bind("Forecast", "Show forecast", true,
                "Show the weather forecast above each time slot on the location screen.");
            ApplyWeather = Config.Bind("Forecast", "Raid weather follows the forecast", true,
                "Give the raid the weather the forecast showed. Off leaves raid weather to SPT, and the forecast may then be wrong.");
            LockOnConfirm = Config.Bind("Forecast", "Lock weather when the slot is confirmed", true,
                "The raid's weather starts from the moment you press Next or Ready, so loading time cannot move it into a different block.");

            OffsetX = Config.Bind("Layout", "Horizontal offset", 0f,
                new ConfigDescription("Moves the forecast sideways from its time slot.", new AcceptableValueRange<float>(-400f, 400f)));
            OffsetY = Config.Bind("Layout", "Vertical offset", 6f,
                new ConfigDescription("Gap between the time slot and the forecast above it.", new AcceptableValueRange<float>(-400f, 400f)));
            TextScale = Config.Bind("Layout", "Text size", 0.55f,
                new ConfigDescription("Forecast text size, relative to the slot's clock.", new AcceptableValueRange<float>(0.3f, 1.2f)));

            VerboseLog = Config.Bind("Debug", "Log weather details", true,
                "Write the forecast and the raid's applied weather to the BepInEx log, for checking that they match.");

            ShowForecast.SettingChanged += (_, __) => ForecastPanel.RefreshAll();
            OffsetX.SettingChanged += (_, __) => ForecastPanel.RefreshAll();
            OffsetY.SettingChanged += (_, __) => ForecastPanel.RefreshAll();
            TextScale.SettingChanged += (_, __) => ForecastPanel.RefreshAll();

            new ConditionsPanelSetPatch().Enable();
            new ConditionsPanelUpdatePatch().Enable();
            new NextButtonPatch().Enable();
            new ReadyButtonPatch().Enable();
            new SetWeatherNodesPatch().Enable();

            Log.LogInfo($"SPTForecaster {PluginVersion} loaded.");
        }
    }
}
