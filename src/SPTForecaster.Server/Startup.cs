using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Common.Models.Logging;
using Spectre.Console;

namespace SPTForecaster.Server;

/// <summary>
/// Loads (or rolls) the weather seed at start, then says so in one line: a little radar bar in the radar's
/// rain colours (light rain to the heaviest cores, as the key shows them), the name and the season.
///
/// It goes straight to Spectre.Console's AnsiConsole, the console SPT's own logger writes to, since
/// ISptLogger escapes markup and allows one colour per line. If drawing fails, a plain line goes through
/// the logger instead.
/// </summary>
[Injectable(TypePriority = OnLoadOrder.PostLoad)]
public class Startup(WeatherSeed weather, ISptLogger<Startup> logger) : IOnLoad
{
    /// <summary>The client's RadarRenderer.RainColours, up to the peak and back down: a storm cell passing.</summary>
    private static readonly string[] Radar = ["#56BE5C", "#ECD03A", "#E86828", "#C41C5C", "#E86828", "#ECD03A", "#56BE5C"];

    private static readonly string[] Bars = ["▂", "▃", "▅", "▇", "▅", "▃", "▂"];

    /// <summary>SPT's Season enum, in order.</summary>
    private static readonly string[] Seasons = ["summer", "autumn", "winter", "spring", "late autumn", "early spring", "storm season"];

    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        _ = weather.Seed;
        var season = weather.Season is >= 0 and < 7 ? Seasons[weather.Season] : "unknown season";
        try
        {
            var bar = string.Concat(Bars.Select((b, i) => $"[{Radar[i]}]{b}[/]"));
            AnsiConsole.Console.MarkupLine(
                $" {bar} [bold #A8CEF6]SPTForecaster[/] [grey]{Markup.Escape(ModMetadata.VersionText)} · weather seed ready · {season}[/]");
        }
        catch (Exception)
        {
            logger.Success($"[SPTForecaster] {ModMetadata.VersionText} loaded, weather seed ready ({season}).");
        }

        return Task.CompletedTask;
    }
}
