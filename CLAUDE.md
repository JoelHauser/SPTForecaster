# SPTForecaster -- working notes for Claude

Weather radar over SPT's location map, a forecast above each time slot, and raid weather that matches
both. Each map has its own weather, read from one storm field. Two halves: a BepInEx client plugin
(`src/SPTForecaster`, net472) and an SPT 4.1 server mod (`src/SPTForecaster.Server`, net10.0). Repo
https://github.com/JoelHauser/SPTForecaster (public). Design: README.md. Browser prototype the client's
weather model is ported from: docs/radar-prototype.html (keep the two in step).

## NEXT: first in-game test of 0.0.2

0.0.2 is the radar build (0.0.1 was a single shared schedule and is superseded). **Never run in game.**
The server half was run on the work machine (SPT 4.1, C:\HUH): it loads, saves weather-seed.json and the
route answers. The client's weather model (Field, RadarRenderer, Forecast) was run outside the game in a
console harness with stubbed game types: radar frames 3-11 ms on .NET 10 (Mono will be slower; it runs
on a worker thread), 1,920 cards with no repeated change time and the raid's read always equal to the
card, and rendered frames matched the prototype. The UI in game is unseen. See "What the first test has
to look at".

## Building

`scripts\pack.ps1 -SPTPath <install> [-Install]` builds both halves and writes `dist\SPTForecaster-<v>.zip`
(only `BepInEx/plugins/SPTForecaster.dll` and `SPT_Runtime/user/mods/SPTForecaster/SPTForecaster.Server.dll`).
The version lives in several places (both csproj, PluginVersion, ModMetadata, the router's `version`,
Startup's log line); the script checks the first four agree. On the home machine:
`scripts\pack.ps1 -SPTPath H:\SPT4.1.X -Install`.

**Work machine (no patched install):** build the client against the patched mirror used for
PlateDurabilityUI (SPT's HDiffPatch delta applied to a copy of Assembly-CSharp; steps in that repo's
CLAUDE.md) and the server against C:\HUH:
`pack.ps1 -SPTPath <mirror> -SptPluginsPath C:\HUH\BepInEx\plugins\spt -ServerPath C:\HUH`.
Then `ilspycmd -il` the client DLL: no `_E…`/`_F…` names. The source names no obfuscated type.

## How it works (game code read 2026-10-27 from ilspycmd -p of the patched 4.1 Assembly-CSharp)

- **Tarkov seconds.** The clock everything uses: real Unix seconds × acceleration (weather.json, 7) + 3 h.
  Its time of day is the game's Tarkov clock (server `WeatherHelper.GetInRaidTime`). Right slot = +12 h.
- **Server** (`WeatherSeed`, `ForecastRouter`): one random seed, saved to `weather-seed.json` next to the
  DLL. Route `/sptforecaster/field` returns version, seed, season (server enum; 2 = winter) and
  acceleration. Vanilla's own weather routes are untouched.
- **Field** (`Field.cs`, ported from the prototype): every 8 in-game hours a pattern (fair, unsettled,
  prolonged rain, coastal fog, storm front) from a hash of seed and period; per in-game hour, cells from a
  hash of seed and hour (showers, fronts, squall lines, rain shields, fog banks, coastal fog), each an
  anisotropic, bowed Gaussian band drifting with a seeded wind, with a plateau or sine envelope; 3-D
  value noise warps coordinates and breaks rain into cells. Frame: 600 x 400, x = IconX·0.6,
  y = (1000 − IconY)·0.4. `Field.Blocks(x, y, from, to)` samples a spot every 14 in-game minutes into the
  game's value ranges (clouds −1..1.2, rain level 1-5 + intensity, fog 0.0013/0.0045/0.0065, wind 0-4,
  direction 1-8), with hysteresis on rain and fog levels. Caches are locked: the radar renders on a worker.
- **Map positions** (`Maps.cs`): IconX/IconY per location id, learnt from `session.LocationSettings` when
  the location screen opens (modded maps included), with SPT 4.1 base.json defaults. The game anchors
  each `LocationButton` at `(IconX/1000, IconY/1000)` of `_locationButtonsContainer` (`LocationButton.Show`).
- **Radar** (`RadarLayer`, `RadarRenderer`): postfix on `MatchMakerSelectionLocationScreen.Show(IEftSession,
  RaidSettings, MatchmakerPlayersController)` adds a RawImage with the buttons container's RectTransform,
  same parent, sibling index just below the buttons and `_locationPathsContainer`. Rendered with
  `Task.Run` (field on a 121 x 81 grid, Catmull-Rom to 480 x 320, stepped colour bands with antialiased
  edges, rain/snow palettes, fog veil, opacity), uploaded via SetPixels32 when done; redraws every 5 s or
  when the selected slot/opacity/season changes. Shows `RadarLayer.TargetTime`, set by the forecast panel
  to the selected slot's start.
- **Raid weather.** `LocalGame.vmethod_5` fetches `/client/localGame/weather` and calls
  `WeatherController.SetWeatherNodes`. `WeatherCurve` keys nodes by `WeatherNode.Time` (ticks) and samples
  it with `GameWorld.GameDateTime.Calculate().Ticks`, the raid's in-game clock (`BaseLocalGame.InitWeather`:
  backend clock, +12 h for EDateTime.PAST; PingPong wrap outside the nodes). `SetWeatherNodesPatch`
  replaces the nodes with `Field.Blocks` at the raid map's position (3 h before to 16 h after the start),
  laid so the raid's start lands at the raid clock's current tick: two nodes per block (start + half a
  blend, end − half a blend), blends centred on block boundaries. Clouds averaged over neighbouring
  blocks when CloudSix wants slower changes (rain/fog untouched, so the card stays exact). Slot found by
  matching the raid clock's time of day to now or now+12 h. InitWeather also calls SetWeatherNodes
  (random test nodes); the patch replaces both calls.
- **Lock.** Prefix on `MatchMakerSelectionLocationScreen.CG_Awake` (Next) and `CG_Awake2` (Ready) records
  location, slot and Tarkov second; the raid uses it if same location+slot and under 30 real min old.
- **Forecast UI** (`ForecastPanel`): component on `LocationConditionsPanel` (fields `_amTimeToggle` CURR,
  `_pmTimeToggle` PAST, `_currentPhaseTime`/`_nextPhaseTime` clocks, `_weatherIcon`). Postfix on `Set`
  attaches, postfix on `UpdateConditions` (every frame) refreshes once a second. A view per toggle, child
  of the toggle, `LayoutElement.ignoreLayout`, anchored above it (offsets in config). Font and colour from
  the clock text; icons are the game's `UI/WeatherIcons/<EWeatherType>` via `ResourcesCache.Pop`, chosen
  by `WeatherNode.GetWeatherTypeByNode()` (night variants at night; dry blocks forced to rain 0 for the
  icon). Rows: headline, up to two changes, "More changes after", the pattern line. Steps too short to get
  their own ~5 min are folded into the next change. Unselected slot at 60 % alpha.
- **Maps without a forecast:** factory4_day/night, laboratory, Labyrinth, icebreaker, hideout. Icebreaker:
  right toggle hidden, clock text forced to 23:00:00 every frame. The radar still shows over the region.

## Compatibility

**CloudSix** (github.com/matsixx/CloudSix, `com.matsix.cloudsix` 4.0.1, read 2026-10-27): replaces the
game's clouds with volumetric ones. Patches `CloudLayerRenderer.RenderClouds`, `TOD_Sky.LateUpdate`,
`EnvironmentManager.Update`, `PrismEffects.HandleExposure`, `BloodOnScreen.Start` and a postfix on
`WeatherController.LateUpdate` that reads `WeatherCurve.Cloudiness` and `.Wind` every frame, so its
clouds follow our nodes. It slew-limits coverage: a full clear<->overcast swing takes at least config
`Weather / Min Cloud Transition Time (sec)` (default 200 s). `SetWeatherNodesPatch` reads that entry
through `Chainloader.PluginInfos[...].Instance.Config.TryGetEntry`, blends at least that long and
smooths clouds across blocks. Its per-raid cloud heights are rolled from cloudiness at raid start (after
our nodes are in). Its Fika sync shares cloud motion from the host (Fika untested here).

**TushonkaTerritories** (github.com/minesettimi/TushonkaTerritories, `com.minesettimi.territories`): never
touches weather, time, `LocationConditionsPanel` or Next/Ready; its server only reads `EscapeTimeLimit`.
It adds a faction-colour Voronoi image as the first child of `Content/Map/Image` and recolours
`LocationButton`s. The radar sits above that image (just below the buttons) and is translucent, and never
touches button colours.

## What the first test has to look at

1. Server console: `[SPTForecaster] 0.0.2 loaded, weather seed ready.` BepInEx log:
   `SPTForecaster 0.0.2 loaded.`, `Radar attached under '...' at sibling N, size WxH`, no "Could not ...".
2. Radar on the map: lined up with the buttons? Above the map art, below the buttons and route lines?
   Light rain see-through? With TushonkaTerritories, its colours visible underneath? F12 opacity works?
   Any hitch when it redraws (every 5 s)?
3. Forecast above both time toggles: **placement unverified**; use F12 Layout offsets and note the values.
4. Icons present and matching the words? Pattern line there? Selected slot brighter?
5. Factory, Labs, Labyrinth: no forecast. Icebreaker (if installed): one slot at 23:00.
6. Log lines `Forecast <map> <hh:mm> slot: ...`, after Next `Confirmed ...`, at raid load `Raid weather
   for <map>: ... locked at confirm ...`. In raid, does the weather match the card? Wait for a named change.
7. Restart the server: same radar and forecasts (weather-seed.json reloaded).
8. Right slot raid: clock is the +12 h one and the weather is the right card's.
