# Forecaster

A weather forecast and a live weather radar for SPT's map select screen. Every map has its own weather, and the raid gets the weather the forecast showed.

**Version 1.1.0** for SPT 4.1. [Download](https://github.com/JoelHauser/SPTForecaster/releases/latest)

## What you get

- **Weather radar.** Rain, snow, fog and cloud drift across the map screen with the wind.
- **Raid playback.** Pick a map and a time, and the radar plays out the weather over the length of that raid.
- **Forecast.** A box above the time bar shows the weather and wind at the start of the raid, with the radar's key and the playback slider beside it. It uses the game's own fonts and weather icons. The time bar below drops its own weather icon, which showed one weather for every map, and centres the time slots.
- **Raid weather that matches.** You get the weather the forecast showed, changing during the raid when the radar showed it changing.
- **New weather in raid:**
  - **Four fog strengths:** mist, fog, dense fog and thick fog.
  - **Winter blizzards:** the game's own driven snow.
  - **Thunderstorms:** lightning lights the sky and the ground, and the thunder arrives later, quieter and duller the further away the strike is. Indoors it rings in the room and is louder near windows. Sealed places like the Reserve bunker hear none of it.

Factory, Labs, Labyrinth and Icebreaker have no forecast and keep SPT's weather.

## How it works

The server mod saves a weather seed. From that seed, your server's season and the in-game clock, the client works out the weather for the whole region: showers, fronts, storm cells and fog banks, moving with the wind. Each map reads the weather at its own spot, so neighbouring maps get similar but not identical weather, and the two time slots differ.

The forecast, the radar and the raid all come from that one calculation, so they can't disagree. When you press Next or Ready, the raid's weather is locked in, so a long load can't move you into different weather.

The weather is modelled on St Petersburg, near where Tarkov is set. Clear and grey spells last a while, fronts bring rain, storms mostly come from the west and south-west, and fog forms on calm nights and burns off by midday. Not every raid has rain or fog.

The seed is saved on the server, so the weather survives restarts and everyone on the same server sees the same weather. The mod also reads each map's raid length from your server, so changed raid times are handled.

## Install

Download `Forecaster-1.1.0.zip` from the [latest release](https://github.com/JoelHauser/SPTForecaster/releases/latest) and extract it into your SPT folder, the one with `EscapeFromTarkov.exe`. Both halves are needed:

- `BepInEx/plugins/Forecaster/`: the client plugin and its thunder sounds
- `SPT_Runtime/user/mods/Forecaster/`: the server mod, which also keeps the weather seed

When the server starts, its console shows `[Forecaster] 1.1.0 loaded, weather seed ready` and the season.

**Updating from SPTForecaster (0.2.2 or earlier):** the mod is now called Forecaster. Install it, start the server once so it can copy your weather seed, then delete `BepInEx/plugins/SPTForecaster.dll`, the `BepInEx/plugins/SPTForecaster` folder and `SPT_Runtime/user/mods/SPTForecaster`. Your F12 settings start fresh.

## Settings (F12)

- **Forecast:** show the forecast, make raid weather follow it, and set fog thickness.
- **Radar:** show the radar and its key, set opacity, and turn raid playback on or off.
- **Storms:** turn lightning and thunder on or off, and set lightning brightness and thunder volume.
- **Debug** (shown with advanced settings): log the weather, force a weather pattern, preview hours ahead, force a thunderstorm, and strike lightning on demand.

## Works with

- [CloudSix](https://sp-mod.com/mod/2567/cloudsix-volumetric-clouds): its clouds follow the forecast and light up with lightning.
- [TraceSix](https://sp-mod.com/mod/3221/tracesix-full-raytracing-and-voxel-gi): lightning uses its own light while TraceSix's lighting is on, so flashes show properly.
- [FogSix](https://sp-mod.com/mod/3110/fogsix): lightning stays out of its fog shadows.
- [Return Of Thunderstorms](https://sp-mod.com/mod/3236/return-of-thunderstorms): its storms, wind and flickering lamps follow the forecast's thunderstorms. Its heavy rain waits until the storm is overhead, and the thunder is the forecast's.
- [Time & Weather Changer NG](https://sp-mod.com/mod/2120/time-weather-changer-ng): while its weather is switched on it takes over, and its thunder slider brings lightning. Switch it off and the forecast's weather comes back. Moving the clock keeps the forecast's weather for the new time.
- [Deploy Screen Overhaul](https://sp-mod.com/mod/3047/deploy-screen-overhaul): it lights your PMC to match the raid's weather.
- [Tushonka Territories](https://sp-mod.com/mod/2942/tushonka-territories): its faction map shows through under the radar.
- [Weighted Season Randomizer](https://sp-mod.com/mod/2479/weighted-season-randomizer): it picks the next season when a raid ends instead of when one loads, so the forecast shows the season you'll play in.
- [Season Rotator](https://sp-mod.com/mod/2314/season-rotator) and other mods that set the season: the forecast follows the season they pick.

**Not compatible:** [Raids Are Infinite](https://sp-mod.com/mod/2903/raids-are-infinite). It runs the raid clock in real time and makes raids last almost forever, so the forecast, the radar and the raid weather no longer line up.

Other mods that set raid weather will fight with this one. To let them handle it, turn off **Raid weather follows the forecast** (the forecast may then be wrong). Fika co-op is untested, but every player on a server gets the same weather seed.

## Credits

Thunder sounds are real recordings from Wikimedia Commons, under CC BY / CC BY-SA or in the public domain. See [THUNDER_CREDITS.md](THUNDER_CREDITS.md).
