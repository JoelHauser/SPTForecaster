# SPTForecaster -- working notes for Claude

Weather forecast above each time slot on SPT's location screen, and raid weather that matches it.
Two halves: a BepInEx client plugin (`src/SPTForecaster`, net472) and an SPT 4.1 server mod
(`src/SPTForecaster.Server`, net10.0). Repo https://github.com/JoelHauser/SPTForecaster (public).
Design and decisions: README.md. Browser preview: docs/forecast-preview.html.

## NEXT: first in-game test of 0.0.2

The 0.0.2 zip is attached to its GitHub pre-release (0.0.1 is superseded: 0.0.2 adds the CloudSix-aware
blend). **Never run in game.** The server half was run on the work machine (SPT 4.1, C:\HUH): it
loads, builds 24 blocks, saves schedule.json, and the route answers. Client untested. See "What the
first test has to look at".

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
  raid's start lands at the raid clock's current tick; two nodes per block (start + half a blend,
  end − half a blend) so a block holds steady and the change to the next is a linear blend centred on
  the boundary (half done at the minute the card names). Blend = 120 real s, or CloudSix's transition
  time + 10 s if larger (see Compatibility). Slot found by matching the raid clock's time of day to now
  or now+12 h. InitWeather also calls SetWeatherNodes (random test nodes); the patch replaces both calls.
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

## Compatibility

**CloudSix** (github.com/matsixx/CloudSix, `com.matsix.cloudsix` 4.0.1, read 2026-10-27): replaces the
game's clouds with volumetric ones. Patches `CloudLayerRenderer.RenderClouds`, `TOD_Sky.LateUpdate`,
`EnvironmentManager.Update`, `PrismEffects.HandleExposure`, `BloodOnScreen.Start` and a postfix on
`WeatherController.LateUpdate` that reads `WeatherCurve.Cloudiness` and `.Wind` every frame, so its
clouds follow our nodes with no extra work. It slew-limits coverage: a full clear<->overcast swing takes
at least config `Weather / Min Cloud Transition Time (sec)` (default 200 s). `SetWeatherNodesPatch`
reads that entry through `Chainloader.PluginInfos[...].Instance.Config.TryGetEntry` and blends at
least that long, so its clouds don't trail a forecast change. Its per-raid cloud heights are rolled
from the curve's cloudiness at raid start (after our nodes are in). Its Fika sync shares cloud motion
from the host; the weather itself still comes from each peer's nodes (Fika untested here).

**TushonkaTerritories** (github.com/minesettimi/TushonkaTerritories, `com.minesettimi.territories`):
no overlap with the forecast. It never touches weather, time, `LocationConditionsPanel` or Next/Ready;
its server only reads `EscapeTimeLimit` for spawns. It adds a faction-colour Voronoi image as the first
child of `Content/Map/Image` and recolours `LocationButton`s, which matters only for the radar idea
(radar-prototype branch): a radar layer must sit above that image and below the buttons, translucent.

## What the first test has to look at

1. Server console: `[SPTForecaster] 0.0.2 loaded, N weather blocks ready.` BepInEx log:
   `SPTForecaster 0.0.2 loaded.` and no "Could not ..." errors.
2. Location screen: a forecast above both time toggles. **Placement is unverified** (prefab never seen):
   overlapping something? Use F12 Layout offsets and note the values that look right.
3. Icons present and matching the words? Font right? Selected slot brighter?
4. Factory, Labs, Labyrinth: no forecast. Icebreaker (if installed): one slot at 23:00.
5. Log lines `Forecast <map> <hh:mm> slot: ...` then, after Next, `Confirmed ...`, then at raid load
   `Raid weather for <map>: ... locked at confirm ...`. In raid, does the weather match the card? Wait
   for a forecast change ("Rain from ~15 min") and check it lands then.
6. Restart the server, back to the location screen: same forecast (schedule.json reloaded).
7. Right slot raid: clock is the +12 h one and the weather is the right card's.
