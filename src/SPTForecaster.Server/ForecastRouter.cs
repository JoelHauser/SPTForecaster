using System.Text.Json;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Utils;

namespace SPTForecaster.Server;

/// <summary>
/// One route: the weather seed, season and time acceleration. The client builds the forecast, the radar
/// and the raid's weather from these, so all three come from one calculation and cannot disagree.
/// </summary>
[Injectable(TypePriority = OnLoadOrder.Routers)]
public class ForecastRouter(JsonUtil jsonUtil, WeatherSeed weather)
    : StaticRouter(
        jsonUtil,
        [
            new RouteAction<EmptyRequestData>(
                "/sptforecaster/field",
                async (url, info, sessionId, output, cancellationToken) =>
                    await ValueTask.FromResult(Respond(weather))),
        ])
{
    private static string Respond(WeatherSeed weather) =>
        JsonSerializer.Serialize(new
        {
            version = "0.2.0",
            seed = weather.Seed,
            season = weather.Season,
            acceleration = weather.Acceleration,
        });
}
