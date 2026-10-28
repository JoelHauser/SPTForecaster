# SPTForecaster

Weather for SPT's location screen: a radar over the map showing storms moving across the region, and a forecast on top of the time bar saying what the weather will do during that raid. Each map has its own weather, and the raid gets exactly what the forecast said.

**Status: 0.0.2 pre-release, not yet tested in game.** Requires SPT 4.1.

## Install

Download `SPTForecaster-<version>.zip` from [Releases](https://github.com/JoelHauser/SPTForecaster/releases) and extract it into your SPT folder (the one with `EscapeFromTarkov.exe` and `SPT_Runtime`). It adds:

- `BepInEx/plugins/SPTForecaster.dll` (client)
- `SPT_Runtime/user/mods/SPTForecaster/SPTForecaster.Server.dll` (server)

Both halves are needed. Settings (F12):
- **Forecast:** show the forecast, make raid weather follow it, lock the weather when you confirm a slot.
- **Radar:** show the radar, radar opacity.
- **Debug:** log the forecast and the applied raid weather; force one weather pattern everywhere (radar, forecast and raid); preview the location screen up to 48 in-game hours ahead.

**Works with:**
- [Deploy Screen Overhaul](https://github.com/JoelHauser/LoadingRaid) (releases after 1.1.0): it lights your PMC for the weather the forecast gives the raid. The forecast box stays on the location screen and never appears on the confirm screen.
- [CloudSix](https://github.com/matsixx/CloudSix): its clouds follow the forecast. SPTForecaster reads CloudSix's "Min Cloud Transition Time" and keeps cloud changes at least that gradual, so the clouds keep pace with the card.
- [TushonkaTerritories](https://github.com/minesettimi/TushonkaTerritories): its faction map shows through underneath the radar.

Previews (open in a browser): [docs/radar-prototype.html](docs/radar-prototype.html) is the radar and forecast as built in 0.0.2; [docs/forecast-preview.html](docs/forecast-preview.html) is the earlier single-schedule design.

## What it shows

**The radar.** Over the location map, below the map buttons: rain in radar colours (light green through yellow and red to magenta for the heaviest cores; blues and whites for snow in winter), fog as a faint grey veil (fog forms overnight, rolls in off the coast and burns off by midday), and cloud as a fainter white one, in patches when partly cloudy and nearly solid when overcast. Light rain is a faint wash so the map stays readable; only small heavy cores are strong. It fades out towards its edges, and shows the selected time slot and moves on as the clock does.

**The forecast**, in a box on top of the game's time bar, drawn like that bar, in the game's own font, label style and weather icons. It shows the selected time slot; pick the other slot and it switches. It is always the same size:

```
 FORECAST FOR THE 14:32 RAID:
 ☁  Overcast                      ☂ Rain starting in ~15 min
    Breezy                        ☀ Rain stopping in ~35 min
```

- The weather the raid starts with and the wind, then up to two changes with how far into the raid they come ("within minutes" for the first couple of minutes).
- Factory, Labs and Labyrinth have no weather of their own, so they get no box.
- Changes too close together to get their own time are folded into one, so a card never lists two changes at the same minute.
- Fog under rain or snow is named ("Rain and dense fog"). "All raid" only when the weather really doesn't change. No temperatures.
- Icons are the game's own weather icons.

**Weather patterns.** Every 8 in-game hours the region rolls one, and they hand over gradually:

| Pattern | How often | What it brings |
|---|---|---|
| Fair weather | ~32% | Mostly clear, a stray shower |
| Unsettled | ~30% | Scattered showers, sometimes a front |
| Prolonged rain | ~14% | A wide, slow rain shield for 8+ hours |
| Coastal fog | ~13% | A fog bank along the coast over Lighthouse and Shoreline |
| Storm front | ~10% | A fast squall line with heavy cores |

### Maps

| Map | Forecast | Time slots |
|---|---|---|
| Customs, Interchange, Lighthouse, Reserve, Ground Zero, Shoreline, Streets of Tarkov, Woods | Yes | Both |
| Factory, The Lab, Labyrinth | No | Unchanged |
| Icebreaker ([Manimal-Icebreaker](https://github.com/danauraborealis/ManimalIcebreaker)) | No | One slot, 23:00 (the hour Icebreaker always plays at) |

## How it works

**Why vanilla SPT can't do this.** SPT picks a raid's weather from one schedule on the real clock. Every map shares it, and the time slot you pick only moves the in-game clock, so both slots always get the same weather.

**What the mod does.**
1. **Weather is a field over the region,** built from one seed: rain cells, fronts, squall lines, rain shields and fog banks drift with a slowly turning wind and grow and fade, shaped by the current weather pattern, with noise for ragged edges and cellular rain.
2. **Each map reads the field at its own button.** The game places each map's button at a fixed fraction of the map (`IconX`, `IconY` in its `base.json`), and the field uses the same fractions, so the radar lines up with the buttons at any resolution and modded maps work too.
3. **The radar, the forecast and the raid come from one calculation.** The client works out all three from the same code and seed. The game samples its weather curve with the raid's in-game clock, and the mod hands it that map's readings on that clock, so the raid gets what the card says.
4. **Slots differ.** Time is in-game time, so the right-hand slot gets the weather due 12 in-game hours later.
5. **Locked at confirm.** The weather starts from the moment you press Next or Ready, so loading time can't move it.
6. **Survives restarts.** The server saves the seed, so the same weather continues after a restart. Every client of the server gets the same seed.

## Open questions

- **Winter rain as snow.** The client is expected to render rain as snow in winter. To confirm in game.
- **Wording cut-offs.** How much rain counts as "light rain" and so on needs tuning against screenshots.
- **Other weather mods** (SVM, Realism seasons) may change the weather after the mod sets it.
- **Fika co-op** is untested.

## Verifying accuracy

With "Log weather details" on, `BepInEx/LogOutput.log` gets the forecast for each slot, a line when you confirm, and at raid start the weather the raid was given with the forecast for that start. A few raids across both slots, waiting for a change the card named, should show any mismatch.
