# Forecaster -- working notes for Claude

Weather radar over SPT's location map, a forecast box on top of the time bar, and raid weather that matches
both. Each map has its own weather, read from one storm field. Two halves: a BepInEx client plugin
(`src/Forecaster`, net472) and an SPT 4.1 server mod (`src/Forecaster.Server`, net10.0). Repo
https://github.com/JoelHauser/SPTForecaster (public; the repo kept the old name). README.md is the
player-facing summary; the design detail is here. Called SPTForecaster up to 0.2.2; renamed in 1.0.0.

## Status

1.0.0 released on GitHub 2026-10-30 (tag v1.0.0 on `release-0.2.0`, Latest). PR #1 (release-0.2.0 -> main) is
open: `main` is still 0.1.0 until Joel merges it. Not on the Forge yet (Joel uploads; the Forge is now
sp-mod.com). Earlier development notes (0.0.x-0.3.0 test logs) are in this file's git history.

Not yet seen in game: near vs far thunder, no flash underground, seasons other than autumn, blizzards,
fog strengths, Fika. Seen working: radar, playback, forecast box, slot memory, thunder indoors with reverb,
lightning with and without TraceSix.

## Building

`scripts\pack.ps1 -SPTPath H:\SPT4.1.X [-Install]` builds both halves and writes `dist\Forecaster-<v>.zip`:
`BepInEx/plugins/Forecaster/Forecaster.dll`, `BepInEx/plugins/Forecaster/thunder/*.wav` and
`SPT_Runtime/user/mods/Forecaster/Forecaster.Server.dll`. Nothing else goes in the zip (no README).
The version is in four places (both csproj, `ForecasterPlugin.PluginVersion`, `ModMetadata.VersionText`);
the script refuses to pack if they differ. Install with the game and server closed (DLLs are locked).

The client build checks for `Assembly-CSharp.dll.spt-bak` (only an SPT-patched install builds a working DLL;
`-p:CompileCheckOnly=true` skips the check for a compile test). **Work machine (no patched install):** build
the client against the patched mirror used for PlateDurabilityUI and the server against another install:
`pack.ps1 -SPTPath <mirror> -SptPluginsPath <install>\BepInEx\plugins\spt -ServerPath <install>`. Then
`ilspycmd -il` the client DLL: no `_E…`/`_F…` names (the source names no obfuscated type).

## Names

GUID `com.mybutthasarash.forecaster` (client and server). Route `/forecaster/field`. Config file
`BepInEx/config/com.mybutthasarash.forecaster.cfg`. The old GUID `com.mybutthasarash.sptforecaster` appears
only in the two "old SPTForecaster still installed" warnings (`ForecasterPlugin.Start`, server `Startup`)
and `WeatherSeed.OldFilePath`, which carries `weather-seed.json` over from `user/mods/SPTForecaster`.

## How it works

- **Tarkov seconds.** The clock everything uses: real Unix seconds x acceleration (weather.json, 7) + 3 h.
  Its time of day is the game's Tarkov clock (server `WeatherHelper.GetInRaidTime`). Right slot = +12 h.
- **Server** (`WeatherSeed`, `ForecastRouter`, `Startup`): one random seed, saved to `weather-seed.json`
  next to the DLL. The route returns version, seed, season (SPT's Season enum; 2 winter, 6 storm) and
  acceleration. Start-up writes one line straight to Spectre's AnsiConsole (ASCII only: the console font
  has no block characters): `[Forecaster] <v> loaded, weather seed ready, <season>`. The client
  (`Schedule.Fetch`, refetched every minute) warns once if the server's version differs from its own.
- **Patterns:** a Markov chain over 8-hour periods (fair, grey = "Overcast", unsettled, longrain, coastfog,
  storms), per SPT season climate modelled on St Petersburg (stay odds, weights, cloud/wind offsets, shower
  heat, fog, thunder odds). Deterministic per period: run forward from `ChainFrom` with checkpoints every
  1024 periods. Showers peak in the afternoon; fog forms on calm nights, more after a wet period.
  Measured rain/snow at some point in a 40-min raid: summer 20 %, autumn 28 %, late autumn 35 %, winter
  34 %, early spring 23 %, spring 17 %, storm 38 %.
- **Field** (`Field.cs`, ported from docs/radar-prototype.html): per in-game hour, cells from a hash of seed
  and hour (showers, fronts, squall lines, rain shields, fog banks, coastal fog), each a bowed Gaussian band
  drifting with the wind, textured by noise in the cell's own moving frame (`CellValue`; the prototype still
  draws texture in the map's frame). Storm headings: each 12 in-game hour spell draws one from a St
  Petersburg climatology (W 30, SW 25, NW 18, S 9, N 6, SE 5, E 4, NE 3, x a per-pattern bias), turning over
  the last 3 h. Frame 600 x 400: x = IconX*0.6, y = (1000 - IconY)*0.4. `Field.Blocks(x, y, from, to)`
  samples a spot every `Step` (14 in-game minutes) into the game's ranges (clouds -1..1.2, rain level 1-5 +
  intensity, fog, wind 0-4, direction 1-8) with hysteresis on rain and fog levels. Caches are locked and
  carry a generation (the radar renders on a worker; a seed/season/forced-pattern change mid-render isn't cached).
- **Fog strengths:** `FogSteps` 0.3/0.45/0.65/0.85 -> `FogValues` 0.0013 (clear)/0.0045/0.0065/0.01/0.016,
  Mist/Fog/Dense fog/Thick fog. `Forecast / Fog thickness` scales raid fog above clear (`ThickenFog`).
- **Blizzard:** `Block.Blizzard` = snow (Rain >= 3) and (wind >= 3 or Storm season). `BlizzardDriver` (Winter
  only) eases `RainController._snowFlakes.StormFactor` over 40 real s centred on block boundaries and sets
  `_snowWetRenderer.StormEnabled` above 0.5; the Storm season is left to the game.
- **Map positions** (`Maps.cs`): IconX/IconY and raid length (EscapeTimeLimit) per location id, learnt from
  the session when the location screen opens (modded maps too), SPT 4.1 base.json defaults. No forecast on
  Factory, Labs, Labyrinth, Icebreaker (which also plays only its pinned hour, 23:00).
- **Raid weather** (`SetWeatherNodesPatch`, prefix on `WeatherController.SetWeatherNodes`): replaces the nodes
  with `Field.Blocks` at the map's spot, laid on the raid clock (`GameWorld.GameDateTime`) so the raid's
  start is the forecast's start: two nodes per block, blends centred on boundaries, at least CloudSix's
  cloud transition time (clouds averaged over neighbours; rain and fog exact). Spans a Tarkov day either
  side plus max(16 h, raid x 1.5 + 30 min) so a moved clock never runs off the curve. Slot = whichever of
  now / now + 12 h has the raid clock's time of day.
- **Lock:** prefixes on `MatchMakerSelectionLocationScreen.CG_Awake` (Next) and `CG_Awake2` (Ready) record
  map, slot and Tarkov second; used if same map and slot and under 30 real min old.
- **Slot memory** (`SlotMemory`): Labs/Labyrinth reset the slot to the left one; the last slot picked on a
  map with toggles is put back when such a map is selected again.
- **Forecast box** (`ForecastPanel` drives, `ForecastView` draws): on `LocationConditionsPanel`, only when
  `Set` gets `takeFromCurrent` (the location screen; not the accept screen or invite window). A second copy
  of the time bar on top of it, as wide: heading "FORECAST FOR THE hh:mm RAID:" in the "SELECT TIME
  PHASE:" label's style; left half big icon, headline, wind; right half (`RadarKeySlot`, own Canvas) the
  radar's playback slider and key. No box on no-forecast maps. Icons: the game's `UI/WeatherIcons` by
  `GetWeatherTypeByNode` (thunder -> ThunderCloud), or our glyphs (`Glyphs.cs`, embedded PNGs drawn by
  tools/glyphs.py) for snow, blizzard, mist, dense/thick fog and night skies (the game ships no night
  sprites). `ForecastResult.Changes` is still computed, for the verbose log.
- **Radar** (`RadarLayer`, `RadarRenderer`): a RawImage on its own Canvas, same rect as the location buttons'
  container, just below the buttons and route lines. Rendered on a worker (121 x 81 grid, Catmull-Rom to
  480 x 320, one colour per card level, fog veils, cloud, wobbly superellipse edge fade), redrawn every 5 s
  at rest, on the block the card opens with. **Playback:** a new map/slot plays the raid twice (12 s each,
  holds and fades) on a fixed 24 fps timeline, rendered up to 10 frames ahead into pooled buffers; the
  verbose log writes `Radar playback: N frames shown (L late), render..., upload...`.
- **Radar key** (`RadarKey`, on `ForecasterRadarKeyDriver` under the radar so it keeps updating): docked
  into the box's right half while the box shows (`RAID START`/`RAID +N MIN` over a progress bar, then
  `RAIN/SNOW ●●●● FOG ●●●● CLOUD ●●` in the radar's colours); hidden (alpha 0) on the radar when there is no box.
- **Thunderstorms:** `Block.Thunder` = not snow, Rain >= 3 and `Field.Electric(t)` (per hour from the seed,
  climate thunder odds x3 in storm fronts). `Field.ThunderDistance` searches rings to 500 units for rain >=
  `RainSteps[1]` (`MetresPerUnit` 20, so 10 km earshot). `Thunder` (on WeatherController's GameObject):
  strikes 1/22 s overhead to 1/60 s at 10 km; strikes land from the storm's near edge into it (exp, mean
  1.2 km; overhead 200 m-3 km, mostly close). Thunder after distance/343 s, volume (300/m)^0.9, low-pass
  600 + 17400 e^(-m/1200), near clips under 1.5 km, no clip twice running.
- **Thunder audio:** 29 WAV clips (10 near, 19 far) read by `Thunder.Wav` on a worker (Unity's loaders gave
  0 s clips in EFT) and rebuilt from kept samples after every audio reset (entering a raid empties
  code-made clips). Each strike is two scheduled copies, equal-power crossfaded by inside/outside (0.4 s):
  outdoor rain mixer group, and indoor rain group with its own low-pass and AudioReverbFilter scaled by
  openness (`AmbientSoundBlender._currentOutdoorFilterValue`, 1 at a window). Sealed underground rooms
  (openness 0, `LightningFlash.Underground`) hear nothing. Clips: assets/thunder, cut by
  tools/thunder_clips.py, de-rained by tools/derain.py, credits in THUNDER_CREDITS.md.
- **Lightning** (`LightningFlash`, in `Camera.onPreCull`): pulses (`Thunder.Flash`) x time of day (1 night,
  0.25 noon) x `Lightning brightness`. Sky colours always flash; CloudSix's clouds via `CloudSixFlashPatch`.
  Without TraceSix: outdoor ambient SH plus the TOD light (intensity, colour and direction towards straight
  down, each tracked and restored). With TraceSix's lighting on (`1. General / Enabled`, read live): the
  TOD light and SH are left alone (it flips its sun-shadow mode on any TOD light change) and our own
  shadowed directional light (`ForecasterLightning`) is aimed per strike as high as a 2 km cloud base over
  the strike (22-80 degrees) so it reaches into rooms. Lights and ambient fade by openness only underground
  (camera > 1.5 m under the terrain).
- **Debug** (section Debug, all marked IsAdvanced, so F12 shows them only with advanced settings): verbose
  log (off by default), force pattern, preview hours ahead, force thunderstorm (Overhead/3 km/8 km), strike/
  flash/thunder now at a set distance, strike key.
- **ForecasterApi.RaidWeather(locationId)** (public, for other mods by reflection): the opening WeatherNode
  for the last confirmed slot on that map, or null.

## The game's weather code (read 2026-10-29, decompiled patched 4.1 Assembly-CSharp)

- **Rain**: `WeatherCurve` keys `InverseLerp(1, 5, node.Rain)` (1 dry, 2 = 0.25, 3 = 0.5, 4 = 0.75, 5 = 1);
  `RainRandomness` is not used for intensity. Wind likewise `InverseLerp(1, 5, Wind)`, rate-limited.
  Lying water builds up from rain and temperature over the nodes (so nodes from before the raid matter).
- **Snow**: only `RainController`'s winter states draw snowflakes: Winter, and the Storm season
  (`StateWinterStorm`: `SnowFlakes.StormFactor = 1`, sideways `StormSideSpeed`, a blizzard). Late
  autumn and early spring use the summer-type (rain) states. Hence `Schedule.Winter` = season 2 or 6.
  SPT's weather.json never uses season 6 by default (its "STORM" date entry has seasonType 4).
- **SPT's season dates** (weather.json): summer 23 Jun-15 Oct, autumn 15 Oct-1 Nov, late autumn
  1 Nov-13 Dec, winter 13 Dec-9 Jan, early spring 9 Jan-20 Mar, spring 20 Mar-23 Jun.
- **Fog**: `ScaterringFogDensity`. SPT's presets top out at 0.006; the game's own scales go far higher
  (`EFogType` Faint 0.015 / Fog 0.035 / Heavy 0.075; random test nodes up to 0.03; Halloween 0.018).
  Bots' sight (`LookSensor.WeatherVisibilityK`) scales the fog penalty over 0.004..0.01
  (`NormalizeFogValueToActualMinMax`), so mist / fog / dense / thick give about 8 % / 42 % / 100 % / 100 % of it.
  `GetWeatherTypeByNode` calls anything over 0.004 foggy, 0.1 and up "Fog".
- **Lightning/thunder**: `WeatherCurve.LightningThunderProbability = InverseLerp(0.5, 1, Cloudiness)` is
  computed (and in `WeatherDebug`) but nothing reads it; no lightning or thunder code in any game DLL.
- **TOD light**: TOD sets intensity (and below shadow quality 3, direction) only every `Light.UpdateInterval`
  (1/0.5/0.1/0 s by shadow quality), so anything written to it must be tracked and put back.
- **Halloween ritual weather**: `WeatherEventController` + `HalloweenEventController` force 21:40, clouds 1.3,
  fog 0.018 via `SetWeatherForce`, separate from the nodes we set.
- **Offline weather menu**: `RaidSettingsWindow`'s weather dropdowns only apply in `EftNetworkGame.ModifyWeather`;
  local raids only read the random-weather toggle, so in SPT they do nothing.

## Compatibility

**Time & Weather Changer NG** (sp-mod.com/mod/2120, `com.samswat.timeweatherchanger` 2.8.0, read 2026-10-29):
weather through `WeatherController.WeatherDebug` (Enabled + values; the curve returns the debug curve while
Enabled, our nodes otherwise; its "Thunder probability" drives our overhead storm). Time through
`GameDateTime.ResetForce`, up to 23 h either way, hence the day of nodes either side. `BlizzardDriver` targets 0
while `WeatherDebug.Enabled`.

**CloudSix** (sp-mod.com/mod/2567, `com.matsix.cloudsix` 4.0.1): its clouds read `WeatherCurve.Cloudiness`
and `.Wind` every frame, so they follow our nodes. It slew-limits coverage by `Weather / Min Cloud Transition
Time (sec)` (default 200 s); `SetWeatherNodesPatch.BlendRealSeconds` blends at least that long.

**TraceSix** (sp-mod.com/mod/3221, `com.matsix.tracesix`): GI volumes take only the game's sun and lamps;
any TOD light change at night flipped its RayTracedWorld sun-shadow mode (40k meshes). See Lightning above.
In daytime RayTracedWorld mode it strips meshes from shadow casting, so our light is unshadowed then.

**FogSix** (sp-mod.com/mod/3110, `com.matsix.fogsix` 1.0.1): picks the brightest shadowed directional light
as its sun at scene load and 3, 11, 26 s after; `FogSixSunPatch` (postfix on `FogShadows.FindSun`) skips our light.

**Return Of Thunderstorms** (sp-mod.com/mod/3236, `com.killdg.returnofthunderstorms` 1.2.1, read 2026-10-30):
`ReturnOfThunderstormsCompat`, all by name (a missing target is skipped and named in the log): its
`StormtideSound.Apply` skips sources named `Thunder.SourceName`; in a forecast raid its `RainEpisode.Tick` =
forecast storm within 2 km; its heavy rain (`HeavyRain.Prefix`), recording (`StormtideRecordedRain.Tick`,
kept running silent) and ducking (`WorldAudioDucking.Tick`) wait for the storm overhead; its
`StormtideThunder.Tick` is held while ours runs, and each of our strikes calls its lamp flicker. Its release
zip nests everything under `INSTALL/`.

**Tushonka Territories** (sp-mod.com/mod/2942, `com.minesettimi.territories`): adds a faction-colour image as
the first child of `Content/Map/Image`; the radar sits above it, translucent, and never touches button colours.

**Deploy Screen Overhaul** (sp-mod.com/mod/3047, Joel's, H:\SPTMods\LoadingRaid): lighting the PMC from
`ForecasterApi.RaidWeather` exists only uncommitted in its working copy (new GUID first, then the old one);
public DSO 1.1.0 never had it and uses the menu's weather. Joel chose not to ship a DSO update for now.

Other mods that set raid weather conflict; `Raid weather follows the forecast` off hands the weather back.
