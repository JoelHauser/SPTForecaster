# SPTForecaster

A weather forecast for SPT's map selection screen. Above each time slot you see what the weather will do during that raid, so the day and night slots become a real choice.

**Status: design stage.** No code yet. The design below is settled; the open questions at the end come first.

Preview: open [docs/forecast-preview.html](docs/forecast-preview.html) in a browser. It simulates the map selection screen and generates a weather schedule with SPT's own rules and default `weather.json` weights.

## What it shows

The forecast is built into the game's own time toggles, so it reads as part of the location screen rather than an overlay:

```
 LOCATION
 CUSTOMS
 Raid time 40 min · Typical raid ~25 min · Average level 20

 TIME AND WEATHER
 ┌───────────────────────┐  ┌───────────────────────┐
 │ 14:32:07              │  │ 02:32:07              │
 │ ☀ DAY                 │  │ ☾ NIGHT               │
 │ ───────────────────── │  │ ───────────────────── │
 │ ☁ Overcast            │  │ ☾ Clear all raid      │
 │ ☂ Rain from ~15 min   │  │ ≈ Breezy              │
 │ ☀ Clearing by ~35 min │  │                       │
 └───────────────────────┘  └───────────────────────┘
 Forecast for the whole raid. Weather is set when you press Next.
```

- The clock ticks like the game's own; the selected toggle inverts (light fill, dark text) as the game's toggles do.
- A headline condition, then up to two changes with the raid minute they happen at. A change in the first couple of minutes reads "within minutes".
- Fog under rain or snow is named too ("Rain and dense fog").
- If more changes follow than fit, a last line says "More changes after ~X min". The card only says "all raid" when the weather really doesn't change.
- A glyph for every condition: clear, partly cloudy (each with a night moon version), overcast, stormy skies, light/normal/heavy rain, light/normal/heavy snow, fog, dense fog, calm, breezy, strong wind.
- No temperatures.
- In winter, precipitation is described as snow.

### Maps

| Map | Forecast | Time slots |
|---|---|---|
| Customs, Interchange, Lighthouse, Reserve, Ground Zero, Shoreline, Streets of Tarkov, Woods | Yes | Both |
| Factory, The Lab, Labyrinth | No | Unchanged |
| Icebreaker ([Manimal-Icebreaker](https://github.com/danauraborealis/ManimalIcebreaker)) | No | One slot, 23:00 (the hour Icebreaker always plays at) |

## How it works

### Why vanilla SPT can't do this

SPT's `RaidWeatherService` generates 24 hours of weather in 15- or 30-minute blocks and picks the raid's weather from the **real** clock. The chosen time slot only moves the in-game clock (`WeatherHelper.IsNightTime` adds 12 hours for the right-hand slot); it never affects which weather block the raid gets. So in vanilla, both slots always get identical weather.

### What the mod changes

1. **Weather on the Tarkov clock.** The server generates the schedule against in-game time (real time × 7 + 3 hours) using the same preset picks and value tables as vanilla. Blocks last 105 or 210 in-game minutes, which is the same 15 or 30 real minutes, so weather changes at the vanilla pace during a raid. The right-hand slot gets the weather due 12 in-game hours later.
2. **One lookup for both.** The forecast and the raid use the same function on the same schedule, so they cannot disagree.
3. **Locked at confirm.** When you confirm a slot, the client tells the server the slot and the time. The raid's weather starts from that moment, so loading time (1–3 real minutes, 7–21 in-game) can't push the raid into a different block.
4. **Survives restarts.** Vanilla keeps the schedule in memory only and rolls a new one on every server start. The mod saves it to `user/mods/SPTForecaster/` and reloads it, dropping blocks that have passed.

Parts: a server mod (schedule, persistence, forecast route, slot lock) and a BepInEx client plugin (cards and glyphs on the map selection screen, sending the chosen slot).

## Open questions

- **How the client applies weather in raid.** It receives a list of weather nodes from `/client/localGame/weather`. How it picks and blends between them during the raid needs to be read from the client code before the server side is built. A client patch may be needed.
- **Winter rain as snow.** The assumption is that the client renders rain as snow in the winter season. To confirm in game.
- **Wording cut-offs.** The thresholds that turn raw values into words (e.g. how much rain counts as "light rain") need tuning against screenshots.
- **Other weather mods** (SVM, Realism seasons) may change the schedule after it's read. Detect and warn, or read last.
- **Icebreaker's two slots.** Icebreaker doesn't appear to change the time selection, so the game likely shows two slots. To confirm in game before trimming to one.

## Verifying accuracy

The first build logs, at raid start, the weather the client actually applied next to what the card promised:

```
[Forecast] promised Rain, applied clouds 1.0 rain 3 -> match
```

A dozen raids across both slots and a few block changes should show any mismatch before release.
