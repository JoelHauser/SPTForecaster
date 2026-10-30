using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Common.Models.Logging;
using Spectre.Console;

namespace Forecaster.Server;

/// <summary>
/// Loads (or rolls) the weather seed at start, then says so in one line: the name in the radar's light
/// snow blue, the rest in grey. Plain ASCII only: the server console's font has no block characters
/// (a radar bar of them came out as boxes, 2026-10-30).
///
/// It goes straight to Spectre.Console's AnsiConsole, the console SPT's own logger writes to, since
/// ISptLogger escapes markup and allows one colour per line. If drawing fails, a plain line goes through
/// the logger instead.
/// </summary>
[Injectable(TypePriority = OnLoadOrder.PostLoad)]
public class Startup(WeatherSeed weather, ISptLogger<Startup> logger) : IOnLoad
{
    /// <summary>SPT's Season enum, in order.</summary>
    private static readonly string[] Seasons = ["summer", "autumn", "winter", "spring", "late autumn", "early spring", "storm season"];

    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        _ = weather.Seed;
        var season = weather.Season is >= 0 and < 7 ? Seasons[weather.Season] : "unknown season";
        try
        {
            AnsiConsole.Console.MarkupLine(
                $"[bold #A8CEF6][[Forecaster]][/] [grey]{Markup.Escape(ModMetadata.VersionText)} loaded, weather seed ready, {season}[/]");
        }
        catch (Exception)
        {
            logger.Success($"[Forecaster] {ModMetadata.VersionText} loaded, weather seed ready, {season}");
        }

        // SPTForecaster (before the rename) would run alongside this one.
        if (File.Exists(Path.Combine(AppContext.BaseDirectory, "user", "mods", "SPTForecaster", "SPTForecaster.Server.dll")))
        {
            logger.Warning("[Forecaster] The old SPTForecaster server mod is still installed. Remove SPT_Runtime/user/mods/SPTForecaster "
                           + "(Forecaster has already copied its weather seed).");
        }

        return Task.CompletedTask;
    }
}
