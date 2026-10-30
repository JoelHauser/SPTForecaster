# SPTForecaster

A weather forecast and live weather radar for SPT's map select screen. Each map gets its own weather, and what the forecast says is what you get in the raid.

**Status: 0.3.0.** Requires SPT 4.1. The radar, forecast, lightning and thunder have been tested in game. Seasons, fog strengths and blizzards have not been tested in game yet.

## What it does

- **Weather radar.** A radar over the map shows rain, snow, fog and cloud moving across the region with the wind, mostly from the west and south-west.
- **Raid playback.** Pick a map and a time, and the radar plays out the weather for the length of that raid.
- **Forecast.** A box above the time bar shows the weather when the raid starts and the wind, with the radar's key and the raid playback slider beside it. It uses the game's own fonts and icons.
- **Weather that matches.** The raid gets exactly the weather the forecast showed. Each map has its own weather, and the two time slots differ.
- **Realistic weather.** Modelled on St Petersburg, near where Tarkov is set, and following your server's season. Clear and grey spells last, fronts bring rain, and fog forms on calm nights and burns off by midday. Not every raid has rain or fog.
- **New weather in raid:**
  - **Four fog strengths:** mist, fog, dense fog and thick fog.
  - **Winter blizzards:** the game's own driven snow.
  - **Thunderstorms:** lightning flashes, then thunder. The thunder arrives later the further away the strike is. It's muffled indoors and silent in sealed places like the Reserve bunker.
- **Follows your raid times.** It reads each map's raid length from your server.
- **Stays the same across restarts.** The server saves the weather seed, so every player on the same server sees the same weather.

Factory, Labs and Labyrinth have no forecast.

## Install

Download `SPTForecaster-<version>.zip` from [Releases](https://github.com/JoelHauser/SPTForecaster/releases). Extract it into your SPT folder, the one with `EscapeFromTarkov.exe`. It adds a client plugin, a server mod and the thunder sounds, and both halves are needed.

## Settings (F12)

- **Forecast:** show the forecast, make raid weather follow it, and set fog thickness.
- **Radar:** show the radar and its key, set opacity, and turn raid playback on or off.
- **Storms:** turn lightning and thunder on or off, and set lightning brightness and thunder volume.
- **Debug** (advanced settings only): log the weather, force a weather pattern, preview hours ahead, force a thunderstorm, and strike lightning, a flash or thunder on demand.

## Works with

- [CloudSix](https://github.com/matsixx/CloudSix): its clouds follow the forecast and light up with lightning.
- TraceSix and FogSix: lightning works with TraceSix's lighting and stays out of FogSix's fog shadows.
- Return Of Thunderstorms: its storms, wind and flickering lamps follow the forecast's thunderstorms, and the thunder is the forecast's.
- [TimeWeatherChanger](https://gitlab.com/flir063-spt/timeweatherchanger): its settings take over while switched on, and the forecast's weather comes back when you switch them off.
- [Deploy Screen Overhaul](https://github.com/JoelHauser/LoadingRaid): it lights your PMC to match the raid's weather.
- [TushonkaTerritories](https://github.com/minesettimi/TushonkaTerritories): its faction map shows through under the radar.

Fika co-op is untested.

## Credits

Thunder sounds are real recordings from Wikimedia Commons under CC BY / CC BY-SA or in the public domain. See [THUNDER_CREDITS.md](THUNDER_CREDITS.md).
