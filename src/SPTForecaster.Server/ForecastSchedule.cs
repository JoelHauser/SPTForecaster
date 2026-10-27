using System.Text.Json;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Generators.Weather;
using SPTarkov.Server.Core.Helpers;
using SPTarkov.Server.Core.Models.Enums;
using SPTarkov.Server.Core.Models.Spt.Config;
using SPTarkov.Common.Models.Logging;
using SPTarkov.Server.Core.Services.Server;

namespace SPTForecaster.Server;

/// <summary>One stretch of weather on the Tarkov clock. Start and End are Tarkov seconds (see <see cref="ForecastSchedule.TarkovNow"/>).</summary>
public record ForecastBlock
{
    public long Start { get; set; }
    public long End { get; set; }
    public string Preset { get; set; } = "";
    public double Clouds { get; set; }
    public double WindSpeed { get; set; }
    public int WindDirection { get; set; }
    public double WindGustiness { get; set; }
    public double Rain { get; set; }
    public double RainIntensity { get; set; }
    public double Fog { get; set; }
    public double Temperature { get; set; }
    public double Pressure { get; set; }
}

/// <summary>
/// The weather schedule, laid on the Tarkov clock instead of the real one.
///
/// Vanilla picks a raid's weather from the real clock, so both time slots get the same block. Here a
/// block covers a stretch of in-game time, so the right-hand slot (12 in-game hours ahead) gets the
/// weather due 12 in-game hours later. The values come from SPT's own <see cref="WeatherGenerator"/>,
/// so weather.json edits still apply, and block lengths use weather.json's timePeriod scaled by the
/// time acceleration, so weather changes at the vanilla pace during a raid.
///
/// The schedule is saved next to the DLL and reloaded on start, so a server restart keeps it.
/// </summary>
[Injectable(InjectionType.Singleton)]
public class ForecastSchedule(
    WeatherGenerator weatherGenerator,
    SeasonalEventService seasonalEventService,
    WeightedRandomHelper weightedRandomHelper,
    WeatherConfig weatherConfig,
    ISptLogger<ForecastSchedule> logger)
{
    private const long DaySeconds = 86400;

    /// <summary>Tarkov time is real time × acceleration plus three hours (WeatherHelper.GetInRaidTime).</summary>
    private const long OffsetSeconds = 3 * 3600;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    private readonly object _gate = new();
    private readonly List<ForecastBlock> _blocks = [];
    private Dictionary<WeatherPreset, double>? _weights;
    private WeatherPreset? _previous;
    private bool _loaded;

    public double Acceleration => weatherConfig.Acceleration is > 0 ? weatherConfig.Acceleration.Value : 7;

    /// <summary>Now, in Tarkov seconds. Its time of day matches the game's Tarkov clock.</summary>
    public long TarkovNow => OffsetSeconds + (long)(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0 * Acceleration);

    public int Season => (int)seasonalEventService.GetActiveWeatherSeason();

    public int Count
    {
        get { lock (_gate) { return _blocks.Count; } }
    }

    /// <summary>
    /// The schedule from a little before now to two and a half Tarkov days ahead, which covers the far
    /// slot plus the longest raid with room to spare.
    /// </summary>
    public List<ForecastBlock> Snapshot()
    {
        lock (_gate)
        {
            var now = TarkovNow;
            Ensure(now + 2 * DaySeconds + DaySeconds / 2);
            var from = now - DaySeconds / 4;
            return _blocks.Where(b => b.End > from).ToList();
        }
    }

    private void Ensure(long until)
    {
        if (!_loaded)
        {
            _loaded = true;
            Load();
        }

        var now = TarkovNow;
        var before = _blocks.Count;

        // Drop what has passed, keeping a quarter day so a raid in progress still has its blocks.
        _blocks.RemoveAll(b => b.End < now - DaySeconds / 4);

        var t = _blocks.Count > 0 ? _blocks[^1].End : now / DaySeconds * DaySeconds;
        var season = seasonalEventService.GetActiveWeatherSeason();
        var added = 0;
        while (t < until)
        {
            _weights ??= weatherGenerator.GetWeatherPresetWeightsBySeason(season).ToDictionary(kv => kv.Key, kv => kv.Value);
            var weights = _weights;
            var weather = weatherGenerator.GenerateWeather(season, ref weights, null, _previous);
            _weights = weights;
            _previous = weather.SptChosenPreset;

            // weather.json's timePeriod, as vanilla uses it; 15 or 30 minutes if it is missing.
            var period = weatherConfig.Weather?.TimePeriod;
            var minutes = period?.Values is { Count: > 0 } values && period.Weights is { Count: > 0 } periodWeights
                ? weightedRandomHelper.WeightedRandom(values, periodWeights).Item
                : (Random.Shared.Next(3) == 0 ? 15 : 30);
            var length = (long)(minutes * 60 * Acceleration);

            _blocks.Add(new ForecastBlock
            {
                Start = t,
                End = t + length,
                Preset = weather.SptChosenPreset?.ToString() ?? "",
                Clouds = Num(weather.Cloud),
                WindSpeed = Num(weather.WindSpeed),
                WindDirection = (int)Num(weather.WindDirection),
                WindGustiness = Num(weather.WindGustiness),
                Rain = Num(weather.Rain),
                RainIntensity = Num(weather.RainIntensity),
                Fog = Num(weather.Fog),
                Temperature = Num(weather.Temperature),
                Pressure = Num(weather.Pressure),
            });
            t += length;
            added++;
        }

        if (added > 0 || _blocks.Count != before)
        {
            Save();
        }
    }

    private static double Num(object? value) => value switch
    {
        null => 0,
        Enum e => Convert.ToInt32(e),
        IConvertible c => c.ToDouble(System.Globalization.CultureInfo.InvariantCulture),
        _ => 0,
    };

    private static string FilePath
    {
        get
        {
            var dir = Path.GetDirectoryName(typeof(ForecastSchedule).Assembly.Location);
            if (string.IsNullOrEmpty(dir))
            {
                dir = Path.Combine(AppContext.BaseDirectory, "user", "mods", "SPTForecaster");
            }

            return Path.Combine(dir, "schedule.json");
        }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                return;
            }

            var saved = JsonSerializer.Deserialize<List<ForecastBlock>>(File.ReadAllText(FilePath), JsonOptions);
            if (saved == null)
            {
                return;
            }

            // Only a contiguous run is usable; anything else came from a different acceleration or a bad edit.
            saved.Sort((a, b) => a.Start.CompareTo(b.Start));
            for (var i = 1; i < saved.Count; i++)
            {
                if (saved[i].Start != saved[i - 1].End)
                {
                    logger.Warning("[SPTForecaster] Saved schedule has a gap; starting a fresh one.");
                    return;
                }
            }

            _blocks.AddRange(saved);
            logger.Info($"[SPTForecaster] Loaded the saved schedule ({saved.Count} blocks).");
        }
        catch (Exception ex)
        {
            logger.Warning($"[SPTForecaster] Could not read the saved schedule, starting a fresh one: {ex.Message}");
        }
    }

    private void Save()
    {
        try
        {
            File.WriteAllText(FilePath, JsonSerializer.Serialize(_blocks, JsonOptions));
        }
        catch (Exception ex)
        {
            logger.Warning($"[SPTForecaster] Could not save the schedule: {ex.Message}");
        }
    }
}
