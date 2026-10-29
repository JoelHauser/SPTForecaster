using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Common.Models.Logging;

namespace SPTForecaster.Server;

/// <summary>Loads (or rolls) the weather seed at start, so the log says the mod is there.</summary>
[Injectable(TypePriority = OnLoadOrder.PostLoad)]
public class Startup(WeatherSeed weather, ISptLogger<Startup> logger) : IOnLoad
{
    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        _ = weather.Seed;
        logger.Success("[SPTForecaster] 0.2.0 loaded, weather seed ready.");
        return Task.CompletedTask;
    }
}
