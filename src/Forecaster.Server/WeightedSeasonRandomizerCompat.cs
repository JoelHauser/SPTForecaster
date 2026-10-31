using System.Reflection;
using HarmonyLib;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Callbacks;
using SPTarkov.Server.Core.DI;

namespace Forecaster.Server;

/// <summary>
/// Weighted Season Randomizer (sp-mod.com/mod/2479, ca.bushtail.weightedseasonrandomizer 2.0.1) rolls a new
/// season in a prefix on WeatherCallbacks.GetLocalWeather, which the game calls while a raid loads: after the
/// player has read the forecast, so the raid played in a season the forecast never saw. Here its roll is
/// moved to the end of each raid (MatchCallbacks.EndLocalRaidAsync), so the season the forecast shows is the
/// one the next raid gets. Its start-up roll is left alone. All by name; without the mod this does nothing.
/// </summary>
[Injectable(TypePriority = OnLoadOrder.PostLoad + 2)]
public class WeightedSeasonRandomizerCompat(WeatherSeed weather, ISptLogger<WeightedSeasonRandomizerCompat> logger) : IOnLoad
{
    private static Action? _pushNewSeason;
    private static ISptLogger<WeightedSeasonRandomizerCompat>? _logger;

    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        var rollPatch = FindType("WeightedSeasonRandomizer.Patches.GetLocalWeather")?.GetMethod("Prefix", BindingFlags.Public | BindingFlags.Static);
        if (rollPatch == null) return Task.CompletedTask;

        try
        {
            var push = FindType("WeightedSeasonRandomizer.WeightedSeasonRandomizer")?.GetMethod("PushNewSeason", BindingFlags.Public | BindingFlags.Static);
            var raidEnd = typeof(MatchCallbacks).GetMethod(nameof(MatchCallbacks.EndLocalRaidAsync));
            if (push == null || raidEnd == null)
            {
                logger.Warning("[Forecaster] Weighted Season Randomizer found, but not the methods Forecaster expects; it keeps rolling as the raid loads, so raids may not get the forecast's season.");
                return Task.CompletedTask;
            }

            _pushNewSeason = (Action)Delegate.CreateDelegate(typeof(Action), push);
            _logger = logger;
            var harmony = new Harmony("com.mybutthasarash.forecaster.weightedseasonrandomizer");
            harmony.Patch(rollPatch, prefix: new HarmonyMethod(typeof(WeightedSeasonRandomizerCompat), nameof(SkipLoadRoll)));
            harmony.Patch(raidEnd, postfix: new HarmonyMethod(typeof(WeightedSeasonRandomizerCompat), nameof(RollAfterRaid)));
            logger.Info($"[Forecaster] Weighted Season Randomizer found: it now rolls the season after each raid, so the forecast shows the next raid's season (now {(weather.Season is >= 0 and < 7 ? Startup.Seasons[weather.Season] : "unknown season")}).");
        }
        catch (Exception ex)
        {
            logger.Warning("[Forecaster] Could not set up Weighted Season Randomizer compatibility: " + ex.Message);
        }

        return Task.CompletedTask;
    }

    /// <summary>Skips its roll; true lets GetLocalWeather itself run.</summary>
    private static bool SkipLoadRoll(ref bool __result)
    {
        __result = true;
        return false;
    }

    private static void RollAfterRaid()
    {
        try
        {
            _pushNewSeason?.Invoke();
        }
        catch (Exception ex)
        {
            _logger?.Warning("[Forecaster] Weighted Season Randomizer's season roll failed: " + ex.Message);
        }
    }

    private static Type? FindType(string name)
    {
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            var type = assembly.GetType(name, false);
            if (type != null) return type;
        }

        return null;
    }
}
