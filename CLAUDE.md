# SPTForecaster -- working notes for Claude

Weather radar over SPT's location map, a forecast box on top of the time bar, and raid weather that matches
both. Each map has its own weather, read from one storm field. Two halves: a BepInEx client plugin
(`src/SPTForecaster`, net472) and an SPT 4.1 server mod (`src/SPTForecaster.Server`, net10.0). Repo
https://github.com/JoelHauser/SPTForecaster (public). Design: README.md. Browser prototype the client's
weather model is ported from: docs/radar-prototype.html (keep the two in step).

## NEXT: the home test of 0.2.0

0.2.0 (2026-10-29, built on the work machine against the patched mirror) adds, all unseen in game: radar
colours matching the card, radar key, raid playback, season-aware St Petersburg weather (Markov patterns),
four fog strengths with their own colours and the Fog thickness setting, blizzards (the game's own storm
snow), glyphs (snow, blizzard, fog strengths, night skies), thunderstorms with lightning and thunder,
raid weather covering each server's raid time and a day either side, TimeWeatherChanger compatibility.
The test list below (items 10-16 are new) is the next step.

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
- **Patterns** (2026-10-29, Joel: "doesn't always need to be rain or fog just for the sake of weather";
  seasons should shape it): a Markov chain over 8-hour periods (fair, grey = "Overcast", unsettled =
  "Showers", longrain, coastfog, storms). Each step: stay with the climate's `Stay` odds, else pick from
  `Next[k]` × climate `Weight`; drawn from Rng(Mix(seed ^ 0x5bd1e995, p)), run forward from period
  `ChainFrom` (400000) with checkpoints every 1024, so any period is deterministic (first lookup 1-6 ms).
  Climates per SPT Season (all seven), modelled on St Petersburg (Joel: "model the weather off somewhere in
  Russia"): climates.com guide monthly blue sky/dry/snowy/thunder/foggy days, Wikipedia sunshine. Each
  climate also offsets pattern clouds and wind. Showers × `ShowerHour` (afternoon
  peak with the climate's `Heat`); fog cells × climate fog × 1.6 after a wet period × calm (none in wind);
  wind strength gets the pattern's `Wind` bias. Measured over 40-min raids (prototype harness, 6 seeds):
  rain/snow at some point summer 20 %, autumn 28 %, late autumn 35 %, winter 34 %, early spring 23 %, spring 17 %, storm 38 % (before: 52 % in every season). Field.cs and
  the prototype's model give identical output (console harness with stubs vs node, all 7 seasons, 5,306 lines).
  `Field.Season` is set from the server's season in `Schedule.Fetch`; a change clears every cache.
  Debug "Force weather pattern" gained Overcast.
- **Fog strengths and blizzard** (2026-10-29, unseen in game): `Field.FogSteps` 0.3/0.45/0.65/0.85 →
  `FogValues` 0.0013/0.0045/0.0065/0.01/0.016, `FogNames` Mist/Fog/Dense fog/Thick fog; `Block.FogLevel`.
  Raid fog scaled by config `Forecast / Fog thickness` above 0.0013 (`SetWeatherNodesPatch.ThickenFog`).
  Radar fog four opacities, key four dots. `Block.Blizzard` = snow (Rain ≥ 3) and (wind ≥ 3 or Storm
  season); winter storms weight 0.7. Measured: blizzard in 5 % of winter raids, 21 % storm season;
  thick fog ~0.8 % of autumn raids. `BlizzardDriver` (on WeatherController's GameObject, Winter only)
  eases `RainController._snowFlakes.StormFactor` 0..1 over 40 real s centred on block boundaries and sets
  `_snowWetRenderer.StormEnabled` above 0.5; the Storm season is left to the game.
- **Glyphs** (`Glyphs.cs`, PNGs in src/SPTForecaster/Glyphs embedded as SPTForecaster.Glyphs.<Name>.png,
  drawn by tools/glyphs.py): LightSnow, Snow, HeavySnow, Blizzard, Mist, DenseFog, ThickFog, ClearNight,
  PartlyCloudNight, ClearFogNight. Style measured from the game's sprites (extracted with UnityPy from
  resources.assets for comparison only, never shipped): colour 125,123,111, ~3.3 px strokes at 48 px,
  cropped tight. The game ships no night sprites (ClearNight etc. returned null before: blank icon at
  night). `ForecastLine.Glyph` wins over `Icon`; plain fog keeps the game's Fog icon.
- **Thunderstorms, lightning, thunder** (2026-10-29, unseen in game): `Block.Thunder` = not snow, Rain ≥ 3 and
  `Field.Electric(t)` (per hour, Rng(Mix(seed ^ 0x27d4eb2d, hour)) < climate `Thunder`, ×3 in storm fronts;
  summer 0.7, spring 0.4, autumn 0.15, early spring 0.05). Measured: thunder in 9.5 % of summer raids,
  5 % spring, 3 % autumn. Card "Thunderstorm" + EWeatherType.ThunderCloud. `Field.ThunderDistance` searches
  rings out to 250 units (1 unit = 40 m) for rain ≥ RainSteps[1] in an electric hour. `Thunder` (component on
  WeatherController's GameObject): spans per block within the raid ± 1 h; strike rate 1/22 s overhead to
  1/60 s at 10 km; flash = pulses added to TOD light in `ThunderLightPatch` (postfix TOD_Sky.LateUpdate,
  also shadowStrength 1 so walls block it fully); thunder after distance/343 s, volume and low-pass by
  distance, through the outdoor `PrecipitationAmbientBlender._outputMixerGroup` (the game muffles it by room
  and portals), silent in rooms with OutdoorAmbientVolume ~0. TimeWeatherChanger's thunder slider drives an
  overhead storm while its override is on. Clips: assets/thunder (12, 4 near / 8 far), cut by
  tools/thunder_clips.py from Commons recordings (THUNDER_CREDITS.md; CC BY / CC BY-SA), loaded with
  UnityWebRequestMultimedia from BepInEx/plugins/SPTForecaster/thunder. Config section Storms.
  Unchecked in game: flash look (day vs night), mixer group muffling, bunker silence, CPU. Not synced in Fika.
- **Raid lengths differ per server** (Joel, 2026-10-29): `Maps.RaidMinutes(id)` (EscapeTimeLimit, learnt with
  the icons). The card and playback use the panel's location; `SetWeatherNodesPatch` builds nodes to
  max(16 Tarkov h, (raid min × 1.5 + 30) real min), 400 real min when unknown. Before, a fixed 16 h
  (~137 raid min) ran out on long-raid servers and the curve would PingPong backwards.
- **Field** (`Field.cs`, ported from the prototype): the period's pattern; per in-game hour, cells from a
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
- **Radar matches the card** (2026-10-29, unseen in game): rain colours step exactly at `Field.RainSteps`
  (one colour per card level: green light, yellow rain, orange heavy, magenta cores; snow pale to deep
  blue), fog only from `Field.FogSteps` (a veil per strength, see Fog strengths; a sandy grey read as cloud, Joel 2026-10-29), nothing drawn below either. Before,
  the radar painted from 0.06 and fog from 0.15 while the card said dry/clear up to 0.12/0.38. The radar's
  time is floored to the `Field.Step` the card's opening block reads (`RadarLayer.BlockStart`), so it
  moves in 14-in-game-minute steps. Left: the card's hysteresis (±0.03) and Catmull-Rom smoothing.
- **Radar key** (`RadarKey`, unseen in game): child of the radar, bottom-right corner (no map button
  there), time-bar fill, "SELECT TIME PHASE:" label style; `RAIN/SNOW ●●●● FOG ●● CLOUD ●●`, colours
  read from RadarRenderer. Config `Radar / Show radar key`.
- **Raid playback** (2026-10-29, unseen in game; tuned in docs/radar-prototype.html, which Joel liked): the
  panel's `Refresh` calls `RadarLayer.SetTarget(start, EscapeTimeLimit, id, right, weather)`; a new
  map+slot key queues playback after 0.5 s (same key: nothing). Two plays, each: 1 s hold on the start,
  12 s through the raid, 1 s hold on the end, 0.5 s fade out; then rests on the start, fading in. Frames
  render back to back at the true time (smooth, at most 30/s); the RawImage colour carries the fade.
  `RadarRenderer` reuses its buffers (the returned array too: upload before the next render). `RadarKey`
  adds the readout ("RAID +N MIN", progress bar, as wide as the key, just above it) and a ring at the
  map's IconX/IconY, both only while `PlayProgress` is set. Config `Radar / Play the raid on the radar`.
  Check in game: smoothness/CPU while playing, the ring's size and centring on the button, readout width.
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

## The game's weather code (read 2026-10-29, decompiled patched 4.1 Assembly-CSharp)

- **Rain**: `WeatherCurve` keys `InverseLerp(1, 5, node.Rain)` (1 dry, 2 = 0.25, 3 = 0.5, 4 = 0.75, 5 = 1);
  `RainRandomness` is not used for intensity. Wind likewise `InverseLerp(1, 5, Wind)`, rate-limited.
  Lying water builds up from rain and temperature over the nodes (so nodes from 3 h before the raid matter).
- **Snow**: only `RainController`'s winter states draw snowflakes: Winter, and the Storm season
  (`StateWinterStorm`: `SnowFlakes.StormFactor = 1`, sideways `StormSideSpeed`, a blizzard). Late
  autumn and early spring use the summer-type (rain) states. Hence `Schedule.Winter` = season 2 or 6.
  SPT's weather.json never uses season 6 by default (its "STORM" date entry has seasonType 4).
- **SPT's season dates** (weather.json): summer 23 Jun-15 Oct, autumn 15 Oct-1 Nov, late autumn
  1 Nov-13 Dec, winter 13 Dec-9 Jan, early spring 9 Jan-20 Mar, spring 20 Mar-23 Jun.
- **Fog**: `ScaterringFogDensity`. SPT's presets top out at 0.006; the game's own scales go far higher
  (`EFogType` Faint 0.015 / Fog 0.035 / Heavy 0.075; random test nodes up to 0.03; Halloween 0.018).
  Bots' sight (`LookSensor.WeatherVisibilityK`) scales the fog penalty over 0.004..0.01
  (`NormalizeFogValueToActualMinMax`), so mist / fog / dense / thick (0.0045 / 0.0065 / 0.01 / 0.016) give about 8 % / 42 % / 100 % / 100 % of it.
  `GetWeatherTypeByNode` calls anything over 0.004 foggy, 0.1 and up "Fog".
- **Lightning/thunder**: `WeatherCurve.LightningThunderProbability = InverseLerp(0.5, 1, Cloudiness)` is
  computed (and in `WeatherDebug`) but nothing reads it; no lightning or thunder code in any game DLL.
- **Halloween ritual weather**: `WeatherEventController` + `HalloweenEventController` (globals
  `EventSettings.EventActive`): forces 21:40, clouds 1.3, fog 0.018 via `SetWeatherForce`, separate
  from the nodes we set.
- **Offline weather menu**: `RaidSettingsWindow` still has cloud/rain/wind/fog/time-flow dropdowns
  (`TimeAndWeatherSettings`), but only `EftNetworkGame.ModifyWeather` applies them; local raids
  (`BaseLocalGame.InitWeather`) only read the random-weather toggle, so in SPT they do nothing.

## Compatibility

**TimeWeatherChanger** (gitlab.com/flir063-spt/timeweatherchanger, `com.samswat.timeweatherchanger` 2.8.0,
read 2026-10-29): weather through `WeatherController.WeatherDebug` (Enabled + values; the controller's
`WeatherCurve` returns the debug curve while Enabled, our nodes otherwise; its fog slider goes to 0.35, its
"Thunder probability" sets the unused LightningThunderProbability). Time through
`GameDateTime.ResetForce` (and the sky clock), up to 23 h either way; "Raid defaults" can apply both at raid
start (after our nodes). So: nodes now span a Tarkov day either side of the raid (`DayMargin`) so a moved
clock never runs off the curve (PingPong backwards); `BlizzardDriver` follows the raid clock and targets 0
while `WeatherDebug.Enabled`. Its Fika addon syncs its own state only; ours is seeded, so peers agree anyway.

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

1. Server console: `[SPTForecaster] 0.2.0 loaded, weather seed ready.` BepInEx log:
   `SPTForecaster 0.2.0 loaded.`, `Thunder: 4 near and 8 far clips loaded.`, `Radar attached under '...' at sibling N, size WxH`, no "Could not ...".
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
7. Radar playback: pick a map and slot; it plays twice and rests on the start? Smooth, no menu hitch?
   Readout above the key, ring on the right button? Picking another slot restarts it; Labs/Factory don't play.
8. Restart the server: same radar and forecasts (weather-seed.json reloaded).
9. Right slot raid: clock is the +12 h one and the weather is the right card's.
10. Radar key bottom-right (clear of the game's UI?), fog shades told apart from cloud, dots match the radar.
11. Glyphs: night forecasts show a moon (were blank before); snow/fog/blizzard glyphs look like the game's icons.
12. Fog: dense and thick fog in raid look right? Tune with `Forecast / Fog thickness`; card words unchanged.
13. Blizzard (set weather.json `overrideSeason` to 2 and force Storm front or wait): card "Blizzard", in raid
    driven snow easing in around the named minute; log `Blizzard: N spell(s)`.
14. Thunderstorm (summer, force Storm front): card "Thunderstorm" with the thunder-cloud icon; in raid the
    flash outdoors and through windows but not through walls, thunder after the flash, quieter far off.
    Log `Thunder plays through mixer group '...'`. Reserve bunker: silent. Shoreline resort basement: muffled.
15. TimeWeatherChanger: its weather overrides ours and hands back; moving the time keeps the weather sane;
    its thunder slider makes lightning.
16. CPU/hitches while thunder and playback run.
