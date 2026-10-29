# SPTForecaster

Weather for SPT's location screen: a radar over the map showing storms moving across the region, and a forecast on top of the time bar saying what the weather will do during that raid. Each map has its own weather, and the raid gets exactly what the forecast said.

**Status: 0.2.1.** The location screen (radar and forecast) was tested in game in 0.1.0; 0.2.x's additions (radar key and playback, seasonal St Petersburg weather, fog strengths, blizzards, thunderstorms with lightning and thunder) and in-raid weather matching the forecast are not confirmed in game yet. Requires SPT 4.1.

## Install

Download `SPTForecaster-<version>.zip` from [Releases](https://github.com/JoelHauser/SPTForecaster/releases) and extract it into your SPT folder (the one with `EscapeFromTarkov.exe` and `SPT_Runtime`). It adds:

- `BepInEx/plugins/SPTForecaster.dll` (client)
- `BepInEx/plugins/SPTForecaster/thunder/*.ogg` (thunder sounds)
- `SPT_Runtime/user/mods/SPTForecaster/SPTForecaster.Server.dll` (server)

Both halves are needed. Settings (F12):
- **Forecast:** show the forecast, make raid weather follow it, lock the weather when you confirm a slot, fog thickness (how thick fog looks in raid; the words don't change).
- **Radar:** show the radar, radar opacity, show the radar key, play the raid on the radar.
- **Storms:** lightning and thunder on or off, lightning brightness, thunder volume.
- **Debug:** log the forecast and the applied raid weather; force one weather pattern everywhere (radar, forecast and raid); preview the location screen up to 48 in-game hours ahead.

**Works with:**
- [Deploy Screen Overhaul](https://github.com/JoelHauser/LoadingRaid) (releases after 1.1.0): it lights your PMC for the weather the forecast gives the raid. The forecast box stays on the location screen and never appears on the confirm screen.
- [CloudSix](https://github.com/matsixx/CloudSix): its clouds follow the forecast. SPTForecaster reads CloudSix's "Min Cloud Transition Time" and keeps cloud changes at least that gradual, so the clouds keep pace with the card. Lightning lights CloudSix's clouds too.
- [TushonkaTerritories](https://github.com/minesettimi/TushonkaTerritories): its faction map shows through underneath the radar.
- [TimeWeatherChanger](https://gitlab.com/flir063-spt/timeweatherchanger) (2.8.0): its panel's weather is the game's debug override, which sits on top of the forecast's weather while it is on and hands back to it when switched off; SPTForecaster's blizzard steps aside meanwhile. Moving the time with it moves you along this map's forecast weather (the raid's weather covers a day either way). While its weather is on, the forecast card describes the forecast, not what you set.

Previews (open in a browser): [docs/radar-prototype.html](docs/radar-prototype.html) is the radar and forecast model the mod is built from; [docs/forecast-preview.html](docs/forecast-preview.html) is the earlier single-schedule design.

## What it shows

**The radar.** Over the location map, below the map buttons: rain in one colour per level the forecast names (green light rain, yellow rain, orange heavy rain, magenta the heaviest cores; pale to deep blue for snow in winter), changing exactly where the forecast's level changes, so the colour over a map is the word on its card; fog as a faint veil in four strengths, deeper in colour as it thickens (pale lilac mist, mauve fog, orchid dense fog, deep purple thick fog), drawn only where the card would say fog (fog forms overnight, rolls in off the coast and burns off by midday), and cloud as a fainter white one, in patches when partly cloudy and nearly solid when overcast. Light rain is a faint wash so the map stays readable; only small heavy cores are strong. It fades out towards its edges, and shows the selected time slot at the moment the raid's weather is read (every 14 in-game minutes), moving on as the clock does. A key in the map's bottom-right corner shows what each colour means.

**Raid playback.** Pick a map and a time and, after half a second, the radar plays that raid's weather through (as long as the map's raid time on your server, in 12 seconds), twice, then rests on the raid's start. A "RAID +25 MIN" readout sits above the key and a faint ring marks the map. Picking another map or time starts it again. Off with the "Play the raid on the radar" setting.

**The forecast**, in a box on top of the game's time bar, drawn like that bar, in the game's own font, label style and weather icons. It shows the selected time slot; pick the other slot and it switches. It is always the same size:

```
 FORECAST FOR THE 14:32 RAID:
 ☁  Overcast                      ☂ Rain starting in ~15 min
    Breezy                        ☀ Rain stopping in ~35 min
```

- The weather the raid starts with and the wind, then up to two changes with how far into the raid they come ("within minutes" for the first couple of minutes).
- Factory, Labs and Labyrinth have no weather of their own, so they get no box.
- Changes too close together to get their own time are folded into one, so a card never lists two changes at the same minute.
- Fog comes in four strengths: mist, fog, dense fog and thick fog. Thick fog is rare (about one autumn raid in a hundred) and hides you from bots as much as it hides them from you. Fog under rain or snow is named ("Rain and dense fog"). "All raid" only when the weather really doesn't change. No temperatures.
- **Blizzards.** In winter, snow in a strong wind is a blizzard (about one winter raid in twenty): the card says so, and the raid gets the game's own blizzard, the driven snow BSG only uses for live events, easing in and out at the minutes the card names. In SPT's Storm season the game keeps its blizzard on, and the card calls the heavier snow a blizzard.
- **Thunderstorms.** Rain (not light rain) in an hour the season makes electric is a thunderstorm: in about one summer raid in ten, one in twenty in spring, a few in early autumn, none in winter (St Petersburg's thunder days). The card says "Thunderstorm" with the game's thunder-cloud icon. In raid, lightning flashes and thunder follows, the sound arriving later the further the strike (a storm 10 km off is heard before it arrives), quieter and duller with distance. The flash lights the whole sky the way lightning does: a brief blue-white glow over the outdoors and the clouds (the game's or CloudSix's), seen through windows and doorways but not inside closed rooms, bright at night and faint at midday; the thunder plays through the game's outdoor-rain sound, which the game muffles by where you are: full outside, muffled in a basement with windows, nothing in a sealed bunker. The thunder is real recordings ([credits](THUNDER_CREDITS.md)).
- Icons are the game's own weather icons, plus glyphs drawn in the same style for what the game has no icon for: light snow, snow, heavy snow, blizzard, mist, dense fog, thick fog, and the night skies (the game names night icons but doesn't ship them).

**Weather patterns.** The region is always in one weather pattern, in 8-in-game-hour steps, and patterns follow on from each other the way real weather does: fine weather tends to last (a spell is about a day of in-game time, around three real hours), and when it breaks it goes the usual way, cloud thickening, then a front or steady rain, showers behind it, then clearing. Weather already in the sky carries over, so patterns hand over gradually.

**Climate.** Tarkov's Norvinsk region lies in northwest Russia, between St Petersburg and the Estonian border, so the weather is modelled on St Petersburg's (Pulkovo), at the head of the Gulf of Finland: clear days are rare all year, summer has the most sun and afternoon thunderstorms, autumn into winter is grey, wet and foggy, and spring is the driest season. The season your SPT server is in picks the climate:

| Pattern | What it brings | Summer | Autumn | Late autumn | Winter | Early spring | Spring | Storm |
|---|---|---|---|---|---|---|---|---|
| Fair weather | Sun and fair-weather cloud; in summer an afternoon shower now and then | 56% | 24% | 10% | 14% | 37% | 53% | 26% |
| Overcast | Grey and dry | 21% | 47% | 62% | 57% | 40% | 26% | 29% |
| Showers | Scattered showers, sometimes a front; in summer mostly in the afternoon | 12% | 10% | 7% | 7% | 7% | 8% | 24% |
| Prolonged rain | A wide, slow rain (snow in winter) shield for 8+ hours | 3% | 16% | 20% | 20% | 12% | 5% | 12% |
| Coastal fog | A fog bank along the coast over Lighthouse and Shoreline | 3% | 3% | 1% | 1% | 4% | 6% | 1% |
| Storm front | A fast squall line with heavy cores, then showers | 6% | 1% | 0% | 0% | 0% | 1% | 8% |

Over a 40-minute raid that comes to rain (or snow) at some point in 20% of summer raids, 28% autumn, 35% late autumn, 34% winter, 23% early spring, 17% spring and 38% in the Storm season. Fog forms on calm nights, more readily after rain and most in autumn, and burns off by late morning; the wind is lighter in fine weather and summer, stronger with fronts and in autumn and winter.

Sources: [climates.com's St Petersburg guide](https://climates.com/ASIA/PDF/RUS06EUR.pdf) (monthly blue sky, dry, snowy, thunder and foggy days) and [Climate of Saint Petersburg](https://en.wikipedia.org/wiki/Climate_of_Saint_Petersburg) (1991-2020 sunshine).

**Raid times.** Every server sets its own raid times (SVM and similar mods change them), so the mod reads each map's raid time from your server: the forecast covers your whole raid, the radar plays your raid's length, and the raid's weather runs past its end with time to spare.

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

- **Winter rain as snow.** The game draws falling snow in Winter and the Storm season (its code says so); to see in game. **Fog strengths** (0.0045 / 0.0065 / 0.01 / 0.016) and the **blizzard** are new and unseen in game; the Fog thickness setting tunes the fog.
- **Wording cut-offs.** How much rain counts as "light rain" and so on needs tuning against screenshots.
- **Other weather mods** (SVM, Realism seasons) may change the weather after the mod sets it.
- **Fika co-op** is untested.

## Verifying accuracy

With "Log weather details" on, `BepInEx/LogOutput.log` gets the forecast for each slot, a line when you confirm, and at raid start the weather the raid was given with the forecast for that start. A few raids across both slots, waiting for a change the card named, should show any mismatch.
