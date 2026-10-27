using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Common.Models.Logging;

namespace SPTForecaster.Server;

/// <summary>Builds (or loads) the schedule at start, so the first menu visit is instant and the log says the mod is there.</summary>
[Injectable(TypePriority = OnLoadOrder.PostLoad)]
public class Startup(ForecastSchedule schedule, ISptLogger<Startup> logger) : IOnLoad
{
    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        var blocks = schedule.Snapshot();
        logger.Success($"[SPTForecaster] 0.0.2 loaded, {blocks.Count} weather blocks ready.");
        return Task.CompletedTask;
    }
}
