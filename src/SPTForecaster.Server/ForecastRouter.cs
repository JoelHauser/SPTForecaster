using System.Text.Json;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Utils;

namespace SPTForecaster.Server;

/// <summary>
/// One route: the schedule, with what the client needs to read it.
/// The client both draws the forecast from this and builds the raid's weather from it, so the two
/// cannot disagree.
/// </summary>
[Injectable(TypePriority = OnLoadOrder.Routers)]
public class ForecastRouter(JsonUtil jsonUtil, ForecastSchedule schedule)
    : StaticRouter(
        jsonUtil,
        [
            new RouteAction<EmptyRequestData>(
                "/sptforecaster/schedule",
                async (url, info, sessionId, output, cancellationToken) =>
                    await ValueTask.FromResult(Respond(schedule))),
        ])
{
    private static string Respond(ForecastSchedule schedule)
    {
        var blocks = schedule.Snapshot();
        return JsonSerializer.Serialize(new
        {
            version = "0.0.1",
            acceleration = schedule.Acceleration,
            now = schedule.TarkovNow,
            season = schedule.Season,
            blocks,
        });
    }
}
