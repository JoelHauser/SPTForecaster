# SPTForecaster -- working notes for Claude

Weather radar over SPT's location map, a forecast box on top of the time bar, and raid weather that matches
both. Each map has its own weather, read from one storm field. Two halves: a BepInEx client plugin
(`src/SPTForecaster`, net472) and an SPT 4.1 server mod (`src/SPTForecaster.Server`, net10.0). Repo
https://github.com/JoelHauser/SPTForecaster (public). Design: README.md. Browser prototype the client's
weather model is ported from: docs/radar-prototype.html (keep the two in step).

## NEXT: in-raid weather in 0.1.0

0.1.0 (released 2026-10-27, v0.1.0) is the first release from the home PC (H:\SPT4.1.X). The location
screen was worked over in game the same day: forecast box, clouds and fog on the radar, edge fade, slot
memory across Labs/Factory, start-up radar time, debug settings (all confirmed by Joel). **In-raid
weather following the forecast has never been seen**: no `Raid weather for ...` log line yet. The next
test is a raid: does the weather match the card, and does a named change happen around its minute?
(Set the debug settings back to Off / 0 first.) Earlier: the server half ran on the work machine; the
weather model ran in a console harness (radar frames 3-11 ms, cards consistent with raid reads).

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
- **Forecast UI** (`ForecastPanel` drives, `ForecastView` draws): component on `LocationConditionsPanel`
  (fields `_amTimeToggle` CURR, `_pmTimeToggle` PAST, `_currentPhaseTime`/`_nextPhaseTime` clocks,
  `_weatherIcon`). Postfix on `Set` attaches, only when `takeFromCurrent` is true (the location screen;
  the accept screen and raid invite window pass false). Postfix on `UpdateConditions` (every frame)
  refreshes once a second; toggle `onValueChanged` refreshes at once. **One box for the selected slot**,
  on top of the time bar and as wide: child of the bar's background image (biggest panel Image that isn't
  the weather icon or in a toggle; the panel if none or if it masks), `LayoutElement.ignoreLayout`, pivot
  at its bottom so it grows upward (gap and text size are constants in ForecastView; the Layout config section was removed on request). Always the same size, and drawn as a
  second copy of the time bar: the bar's colour (13, 15, 15 as drawn) with the bar's own striped sprite
  over it, no borders. A small caps heading ("FORECAST FOR THE hh:mm RAID:") in the "SELECT TIME PHASE:"
  label's style, then one row: big icon, bold headline and the wind under it on the left; up to two
  changes on the right, or "No change expected". Icon slots keep their place when a sprite is missing, so
  the change lines stay aligned. Text
  takes the clock's font and colour and the "SELECT TIME PHASE:" label's style and inset (smallest
  non-clock TMP, leftmost on a tie); every text row has a fixed height and autosizes to 80 % before an
  ellipsis. Icons: the game's `UI/WeatherIcons/<EWeatherType>` via `ResourcesCache.Pop`, chosen by
  `WeatherNode.GetWeatherTypeByNode()` (night variants at night; dry blocks forced to rain 0). Change
  wording is "<what> <-ing> in ~N min" ("Rain starting", "Skies clearing", "Fog arriving"; "in", a countdown from the raid start, not "at", as Joel asked); steps too
  short for their own ~5 min fold into the next change. No box at all on no-forecast maps (Joel: "remove the raid forecast from factory"). The
  verbose log names what the box found (`Forecast box on '...'`).
  History (all seen in game 2026-10-27): 0.0.2's per-toggle text overflowed into the other slot and the
  "SELECT TIME PHASE:" label; a regional pattern line ("Coastal fog until ~16:00" on Customs) read as
  that map's weather; "Clear from ~30 min" read as bad English; a two-column text box changed size with
  the weather and "did not look polished"; a framed box with a coloured raid timeline and a "LATER" footer
  (this map's weather after the raid) was "better" but the bar read as unintuitive and "Later" made no
  sense, so both went (the other time slot already shows the next half-day).
- **Radar edges** (`RadarRenderer.EdgeMask`): the radar fades out over a superellipse (power 4) from r 0.72
  to 0.98, with the fade line wobbled by three sines of the angle, so it never shows a straight side. A
  hard edge at the buttons container read as the map being cut off; an 8 % straight fade still showed as
  vertical lines left and right under wide rain or fog (both seen 2026-10-27). Lighthouse (u -0.76) stays
  about 94 % visible; Icebreaker, further out, fades more.
- **Fog follows the day** (`Field.FogHour`): every fog value is scaled from 1 (evening to mid-morning) to
  0.12 (afternoon), and broken into banks by Fbm. Coastal fog cells (`Cell.Coast`) sit 10 px further
  inland at night than by day, and their landward edge is warped by noise along the coast. Before this
  the coastal fog was a smooth straight north-south stripe that stayed all pattern long ("always there,
  doesn't look right", 2026-10-27). Contact sheets of both, rendered with a harness that compiles Field.cs
  and RadarRenderer.cs against small stubs, showed the stripe gone and the fog off by 11:00.
- **Clouds on the radar** (`Field.CloudCover`): the radar drew only rain and fog, so a "Partly cloudy"
  card had nothing on the map to match (2026-10-27). Cloudiness now has one formula (`Field.SkyClouds`,
  used by `Blocks` and the radar); the radar turns it into a share of sky covered and draws that as soft
  broken cloud (Fbm at /60, smoothstep half-width 0.28), a white veil at most 0.14 alpha under the rain
  and fog. Partly cloudy is patches with gaps, overcast nearly solid, clear empty.
- **Duplicate changes:** cloud wobbling across the partly cloudy/overcast line, once the short wobbles
  were folded away, left two identical steps ("Clouds breaking up in ~35 min", then again in ~45). A
  step reading the same as the step before is now dropped; if none are left the headline says "all raid".
- **Slot memory** (`SlotMemory`, `SlotChangedPatch`, `LocationSelectedPatch`): vanilla's `SelectedLocation`
  setter forces `_selectedDateTime` to CURR for laboratory and factory4_day (PAST for factory4_night), and
  `UpdateSelectedDateTime` forces CURR for anything not in `AvailableMaps` (Labyrinth, modded maps);
  nothing restores it, so right slot -> Labs -> Customs landed on the left slot and the forecast jumped
  (Joel, 2026-10-27). A postfix on `UpdateSelectedTime` remembers the slot while an ordinary map (in
  AvailableMaps, not Labs/Factory/one-slot) is selected; a prefix on `OnLocationSelected` writes it back
  into `_selectedDateTime` and `_raidSettings.SelectedDateTime` before an ordinary map is selected.
  Joel then saw Factory do the same (2026-10-27); the game code read statically doesn't show how, so a
  postfix on `OnLocationSelected` also flips the chosen toggle on if the slot still differs, and the
  verbose log writes `Slot: ...` lines for every record/restore/flip. Unverified in game.
  Then: right slot picked *on Factory* -> any map came back left. Cause was ours: Factory was excluded
  from recording, so the older left choice was "restored". Factory now records and restores like any map
  with the toggles (only Labs, one-slot and non-AvailableMaps maps are excluded). Confirmed fixed in game
  by Joel 2026-10-27.
- **Debug settings:** `Force weather pattern` sets `Field.ForcedPattern` (every period returns that key, cell
  cache cleared; radar, forecast and raid all follow). `Preview hours ahead` (0-48) adds to `now` in
  `ForecastPanel.Refresh` only, so the location screen's radar and box move ahead; raids keep real time
  (WeatherLock and SetWeatherNodesPatch read the unshifted clock).
- **Screen opening:** `ShowInternal` calls `OnLocationSelected` (so `Set`, so our attach) *before*
  `ShowGameObject`, so the toggles read inactive (left slot) and the bar lookups (active-only) can miss.
  The `Show` postfix now calls `ForecastPanel.RefreshAll()` once the screen is up. The radar hides its
  picture on every reopen (`Restart`) and drops any render finished for a time the screen no longer
  shows (`_pendingTime`), so no old picture shows. Joel saw "the previous forecast" over the map after a
  relaunch until he clicked a map (2026-10-27). That alone did not fix it: the log showed no map is
  selected when the screen opens after a relaunch (the box only appears on the first click), and the
  radar's fallback with no panel was plain `TarkovNow`, ignoring the slot and the preview offset; on
  later visits `TargetTime` was the stale absolute time from the last visit. Now the Show postfix clears
  `TargetTime`, the panel clears it on `OnDisable` (Labs), and the radar's fallback is
  `Schedule.ScreenNow` (TarkovNow + preview) plus 12 h if `SlotMemory.RightSlot`. Unverified in game.
- **Maps without a forecast:** factory4_day/night, laboratory, Labyrinth, icebreaker, hideout. Icebreaker:
  right toggle hidden, clock text forced to 23:00:00 every frame. The radar still shows over the region.

## Compatibility

**Deploy Screen Overhaul** (github.com/JoelHauser/LoadingRaid, Joel's): lights the PMC on the accept/deploy
screens from the raid's weather, formerly the session's WeatherNode, which is not the raid's weather with
this mod. `ForecasterApi.RaidWeather(locationId)` (public, by reflection) returns the opening WeatherNode
for the last confirmed slot on that map (clouds smoothed as the raid's are) or null; LoadingRaid's
`GameTypes.ForecasterRaidWeather` calls it first (uncommitted there too, 2026-10-27). The forecast box was
also landing on the accept screen's conditions panel; now location screen only.

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

1. Server console: `[SPTForecaster] 0.1.0 loaded, weather seed ready.` BepInEx log:
   `SPTForecaster 0.1.0 loaded.`, `Radar attached under '...' at sibling N, size WxH`, no "Could not ...".
2. Radar on the map: lined up with the buttons? Above the map art, below the buttons and route lines?
   Light rain see-through? With TushonkaTerritories, its colours visible underneath? F12 opacity works?
   Any hitch when it redraws (every 5 s)?
3. Forecast box on top of the time bar: same background and width as the bar, heading in the "SELECT
   TIME PHASE:" style, nothing overlapping? Switching toggles switches the box at once? Check the log's
   `Forecast box on '...'` line (bar background found? label found? inset sane?).
4. Icons present and matching the words? Change lines aligned? Box the same size on every map? Radar fades out at its edges (no hard line)?
5. Factory, Labs, Labyrinth: no forecast. Icebreaker (if installed): one slot at 23:00.
6. Log lines `Forecast <map> <hh:mm> slot: ...`, after Next `Confirmed ...`, at raid load `Raid weather
   for <map>: ... locked at confirm ...`. In raid, does the weather match the card? Wait for a named change.
7. Restart the server: same radar and forecasts (weather-seed.json reloaded).
8. Right slot raid: clock is the +12 h one and the weather is the right card's.
