# SPTForecaster -- working notes for Claude

Weather forecast above each time slot on SPT's location screen, and raid weather that matches it.
Two halves: a BepInEx client plugin (`src/SPTForecaster`, net472) and an SPT 4.1 server mod
(`src/SPTForecaster.Server`, net10.0). Repo https://github.com/JoelHauser/SPTForecaster (public).
Design and decisions: README.md. Browser preview: docs/forecast-preview.html.

## NEXT: first in-game test of 0.0.1

The 0.0.1 zip is attached to the GitHub pre-release. **Never run in game.** The server half was run
on the work machine (SPT 4.1, C:\HUH): it loads, builds 24 blocks, saves schedule.json, and the route
answers. Client untested. See "What the first test has to look at".

## Building

`scripts\pack.ps1 -SPTPath <install> [-Install]` builds both halves and writes `dist\SPTForecaster-<v>.zip`
(only `BepInEx/plugins/SPTForecaster.dll` and `SPT_Runtime/user/mods/SPTForecaster/SPTForecaster.Server.dll`).
The version lives in four places (both csproj, PluginVersion, ModMetadata); the script refuses to pack if
they disagree. On the home machine: `scripts\pack.ps1 -SPTPath H:\SPT4.1.X -Install`.

**Work machine (no patched install):** build the client against the patched mirror used for
PlateDurabilityUI (SPT's HDiffPatch delta applied to a copy of Assembly-CSharp; steps in that repo's
CLAUDE.md) and the server against C:\HUH:
`pack.ps1 -SPTPath <mirror> -SptPluginsPath C:\HUH\BepInEx\plugins\spt -ServerPath C:\HUH`.
Then `ilspycmd -il` the client DLL: no `_E…`/`_F…` names. The source names no obfuscated type.

## How it works (read 2026-10-27 from ilspycmd -p of the patched 4.1 Assembly-CSharp)

- **Tarkov seconds.** The schedule's clock: real Unix seconds × acceleration (weather.json, 7) + 3 h.
  Its time of day is the game's Tarkov clock (server `WeatherHelper.GetInRaidTime`). Right slot = +12 h.
- **Server** (`ForecastSchedule`): blocks from SPT's own `WeatherGenerator.GenerateWeather` (so
  weather.json edits apply), lengths from weather.json `timePeriod` × acceleration (105/210 in-game
  min). Saved to `schedule.json` next to the DLL, reloaded if contiguous. Route `/sptforecaster/schedule`
  (`ForecastRouter`) returns acceleration, season (server enum; 2 = winter) and the blocks. Vanilla's own
  weather routes are untouched.
- **Raid weather.** `LocalGame.vmethod_5` fetches `/client/localGame/weather` and calls
  `WeatherController.SetWeatherNodes`. `WeatherCurve` keys nodes by `WeatherNode.Time` (ticks, parsed
  from the server's "yyyy-MM-dd HH:mm:ss") and samples it with `GameWorld.GameDateTime.Calculate().Ticks`,
  the raid's in-game clock (`BaseLocalGame.InitWeather`: backend clock, +12 h for EDateTime.PAST;
  PingPong wrap outside the nodes). `SetWeatherNodesPatch` replaces the nodes: the block covering the
  raid's start lands at the raid clock's current tick; two nodes per block (start, end − 14 in-game
  min) so a block holds steady and blends in its last two real minutes. Slot found by matching the raid
  clock's time of day to now or now+12 h. InitWeather also calls SetWeatherNodes (random test nodes);
  the patch replaces both calls.
- **Lock.** Prefix on `MatchMakerSelectionLocationScreen.CG_Awake` (Next) and `CG_Awake2` (Ready) records
  location, slot and Tarkov second; the raid uses it if same location+slot and under 30 real min old.
- **UI** (`ForecastPanel`): component on `LocationConditionsPanel` (fields `_amTimeToggle` CURR,
  `_pmTimeToggle` PAST, `_currentPhaseTime`/`_nextPhaseTime` clocks, `_weatherIcon`). Postfix on `Set`
  attaches, postfix on `UpdateConditions` (every frame) refreshes once a second. A view per toggle,
  child of the toggle, `LayoutElement.ignoreLayout`, anchored above it (offsets in config). Font and
  colour from the clock text; icons are the game's `UI/WeatherIcons/<EWeatherType>` via
  `ResourcesCache.Pop`, chosen by `WeatherNode.GetWeatherTypeByNode()` (day types swapped to night ones
  at night; dry blocks forced to rain 0 for the icon because the game calls rain 1-3 "light rain").
  Unselected slot at 60 % alpha.
- **Maps** (`Maps.cs`): no forecast on factory4_day/night, laboratory, Labyrinth, icebreaker, hideout.
  Icebreaker: right toggle hidden, clock text forced to 23:00:00 every frame.

## What the first test has to look at

1. Server console: `[SPTForecaster] 0.0.1 loaded, N weather blocks ready.` BepInEx log:
   `SPTForecaster 0.0.1 loaded.` and no "Could not ..." errors.
2. Location screen: a forecast above both time toggles. **Placement is unverified** (prefab never seen):
   overlapping something? Use F12 Layout offsets and note the values that look right.
3. Icons present and matching the words? Font right? Selected slot brighter?
4. Factory, Labs, Labyrinth: no forecast. Icebreaker (if installed): one slot at 23:00.
5. Log lines `Forecast <map> <hh:mm> slot: ...` then, after Next, `Confirmed ...`, then at raid load
   `Raid weather for <map>: ... locked at confirm ...`. In raid, does the weather match the card? Wait
   for a forecast change ("Rain from ~15 min") and check it lands then.
6. Restart the server, back to the location screen: same forecast (schedule.json reloaded).
7. Right slot raid: clock is the +12 h one and the weather is the right card's.
