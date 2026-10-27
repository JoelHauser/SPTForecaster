using System.Text.Json;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Models.Spt.Config;
using SPTarkov.Server.Core.Services.Server;

namespace SPTForecaster.Server;

/// <summary>
/// The seed the client builds all weather from (storms, patterns, every map's conditions), with the
/// season and time acceleration it needs. Saved next to the DLL, so a server restart keeps the same
/// weather, and shared by every client of this server (Fika peers included), so they all see the same.
/// </summary>
[Injectable(InjectionType.Singleton)]
public class WeatherSeed(SeasonalEventService seasonalEventService, WeatherConfig weatherConfig, ISptLogger<WeatherSeed> logger)
{
    private readonly object _gate = new();
    private int? _seed;

    public double Acceleration => weatherConfig.Acceleration is > 0 ? weatherConfig.Acceleration.Value : 7;

    public int Season => (int)seasonalEventService.GetActiveWeatherSeason();

    public int Seed
    {
        get
        {
            lock (_gate)
            {
                _seed ??= LoadOrCreate();
                return _seed.Value;
            }
        }
    }

    private static string FilePath
    {
        get
        {
            var dir = Path.GetDirectoryName(typeof(WeatherSeed).Assembly.Location);
            if (string.IsNullOrEmpty(dir))
            {
                dir = Path.Combine(AppContext.BaseDirectory, "user", "mods", "SPTForecaster");
            }

            return Path.Combine(dir, "weather-seed.json");
        }
    }

    private int LoadOrCreate()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(FilePath));
                var saved = doc.RootElement.GetProperty("seed").GetInt32();
                logger.Info($"[SPTForecaster] Loaded the saved weather seed.");
                return saved;
            }
        }
        catch (Exception ex)
        {
            logger.Warning($"[SPTForecaster] Could not read the saved weather seed, rolling a new one: {ex.Message}");
        }

        var seed = Random.Shared.Next(1, int.MaxValue);
        try
        {
            File.WriteAllText(FilePath, JsonSerializer.Serialize(new { seed }));
        }
        catch (Exception ex)
        {
            logger.Warning($"[SPTForecaster] Could not save the weather seed; the weather will change on restart: {ex.Message}");
        }

        return seed;
    }
}
