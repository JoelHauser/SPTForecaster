using System;
using EFT.Weather;

namespace SPTForecaster
{
    /// <summary>
    /// For other mods. With the forecast driving raid weather, the session's own weather (what vanilla
    /// shows on the location screen) is not what the raid will get, so a mod that lights or describes
    /// the coming raid should ask here first.
    ///
    /// Call it by reflection, so there is no hard dependency:
    /// <c>AccessTools.Method("SPTForecaster.ForecasterApi:RaidWeather")?.Invoke(null, new object[] { locationId })</c>
    /// Deploy Screen Overhaul (github.com/JoelHauser/LoadingRaid) does, to light the PMC for the raid's weather.
    /// </summary>
    public static class ForecasterApi
    {
        /// <summary>
        /// The weather a raid on <paramref name="locationId"/> opens with, for the slot the player last
        /// confirmed there (Next or Ready on the location screen): the same WeatherNode values the raid is
        /// given at its first second. Null when the forecast won't set this raid's weather (raid weather
        /// left to SPT in the config, a map without a forecast, nothing confirmed on this map in the last
        /// half hour, or the server's seed not in yet), so the caller keeps its own source.
        /// </summary>
        public static WeatherNode RaidWeather(string locationId)
        {
            try
            {
                if (!ForecasterPlugin.ApplyWeather.Value || string.IsNullOrEmpty(locationId) || Maps.NoForecast(locationId)) return null;
                if (!Schedule.Ready() || !WeatherLock.TryGetLatest(locationId, out var start)) return null;
                if (!Maps.Position(locationId, out var x, out var y)) return null;

                // Enough blocks either side for the raid's cloud smoothing to see the same neighbours.
                var blocks = Field.Blocks(x, y, start - 3600, start + 3600);
                SetWeatherNodesPatch.SmoothClouds(blocks, SetWeatherNodesPatch.BlendRealSeconds());
                var opening = blocks.Find(b => b.Start <= start && b.End > start);
                return opening == null ? null : Forecast.ToNode(opening, 0);
            }
            catch (Exception ex)
            {
                ForecasterPlugin.Log.LogWarning("RaidWeather for another mod failed: " + ex.Message);
                return null;
            }
        }
    }
}
