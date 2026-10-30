# Forecaster -- working notes for Claude

Weather radar over SPT's location map, a forecast box on top of the time bar, and raid weather that matches
both. Each map has its own weather, read from one storm field. Two halves: a BepInEx client plugin
(`src/Forecaster`, net472) and an SPT 4.1 server mod (`src/Forecaster.Server`, net10.0). Repo
https://github.com/JoelHauser/SPTForecaster (public). README.md is a short user-facing summary; the design detail is here. Browser prototype the client's
weather model is ported from: docs/radar-prototype.html (keep the two in step). **Out of step since
0.2.2:** the C# field draws each cell's texture and ragged edge in the cell's own moving frame
(`Field.CellValue`, steady-z noise, `TextureLife` 2 h) and drifts the cloud pattern with the wind
(`Field.CloudNoise`); the prototype still draws them in the map's frame.

## NEXT: the home test of 0.2.2

0.2.2 (2026-10-29, home PC, installed in H:\SPT4.1.X, not run in game) answers Joel's first
look at 0.2.1:
- Radar key was hidden behind the forecast box (bottom-right corner). `RadarKey.Place` now lifts the key
  (and the playback readout) to just above whatever covers the corner (time panel, forecast box), right-
  aligned with it, checked every 0.2 s.
- Playback "dancing in place / mirrored": the rain/fog texture was drawn in the map's frame with an eased
  z blend, so it boiled and swelled back while the storm outline slid. Now it rides with each cell and
  changes at an even rate; card and raid read the same field, so their weather changes for a given seed.
  Harness (scratchpad): render 9 -> 12 ms per frame.
- Playback hitches: the radar now has its own nested Canvas (key, readout, ring inside it) so per-frame
  changes no longer rebuild the location screen's canvas. Unverified that this was the cause: the verbose
  log now writes `Radar playback: N frames, render X ms, upload Y ms` after each run.
- Slow start: wait 0.5 -> 0.15 s, first play's opening hold 1 -> 0.2 s.
- Thunder silent in game although clips loaded and strikes fired (log: group 'PrecipitationAmbientOut').
  Cause unknown; the thunder source now copies the game's outdoor rain source's settings (spatialize,
  blend, bypasses, priority), and the verbose log writes one `Thunder: '<clip>' ...` line per play with
  everything that decides audibility, or why it was skipped. Read that line first if still silent.
- Lightning "from one direction, source visible": the flash's direct light was the moon light at the
  moon's angle, and CloudSix's clouds were lit through their moon light. The light now turns towards
  straight down for the flash (by the flash's share of its intensity; restored afterwards, as TOD below
  shadow quality 3 sets the direction only about once a second) and CloudSix's clouds get an even glow
  (ambient colour) instead.

- Storms "only come from the south" (Joel, same day, on the installed 0.2.2): the steering heading was one
  angle per seed swinging +-0.9 rad over ~11 days (seed 638965958: always moving N/NW). Now `Field.Steering`
  gives each 12 in-game hour spell a heading drawn from a St Petersburg climatology (W 30, SW 25, NW 18,
  S 9, N 6, SE 5, E 4, NE 3) times a per-pattern bias (`SteerBias`), turning to the next over the spell's
  last 3 h. Harness over 60 days, Joel's seed: W 27 %, SW 24 %, NW 19 %, S 10 %, N 7 %, E/NE/SE 3-4 %.
  Installed 2026-10-29 together with the items below.
- Then (Joel's marked-up screenshot): the change lines ("Thunder passing in ~5 min", "Rain stopping in
  ~10 min") are gone from the box; its right half (`RadarKeySlot`, own Canvas) now holds the playback
  slider over the key. `RadarKey.Dock` moves both strips in (no backgrounds or padding, the box's layout
  places them; slider reads "RAID START" between plays) and back to the radar corner when no box shows
  (Labs/Factory/no map). Joel then asked for no key at all without a box (seen over the time bar with no map
  picked): undocked, the strip and slider are hidden (alpha 0); only the ring stays on the map. Built, not
  installed (game running). The RadarKey component lives on `ForecasterRadarKeyDriver` under the radar
  so it keeps updating while the box is hidden. `ForecastResult.Changes` is still computed (log line).
  Installed 2026-10-29, not seen in game.
- **Thunder silence solved (from the 0.2.2 log):** every play logged `(0.0 s, Loaded) ... playing False`:
  UnityWebRequestMultimedia turned the libsndfile Ogg Vorbis clips into empty clips in EFT (the files are
  fine, ffprobe reads 6-17 s each). Clips are now 16-bit WAV (assets/thunder/*.wav, 10.6 MB; the .ogg
  were removed from the repo; tools/thunder_clips.py writes WAV) and `Thunder.Wav` reads them on a worker
  and builds the clips with AudioClip.Create/SetData. Parser checked against all 12 files in a console
  test. Old .ogg files stay in installs (never delete there); the plugin only looks at *.wav.
- Flash peaks lowered (direct 0.5->0.3, ambient 0.45->0.3, sky 0.6->0.45) after "very bright for night".
  The Six mods (AOSix, FogSix, ReflectSix, TraceSix, read 2026-10-29) patch nothing we patch. TraceSix
  feeds the TOD light (colour x intensity, direction) and `_SHAr` into GI volumes it refreshes a slice
  at a time, so a slice refreshed mid-flash can hold the flash's light until its next refresh; FogSix
  tints fog with MoonSkyColor (only during the flash). Not yet known whether Joel's "bright" was the
  flash or the night between flashes.

- Playback still "hitches and stutters" (Joel, after the nested-canvas build; no `Radar playback` line in
  his log: it was only written after both plays, and he moved on first). Cause in the code: each frame
  was rendered only after the last was uploaded, at whatever time it was then, so a slow render (new
  cells, thread pool busy; 12 ms typical, ~50 ms cold) held the picture and then jumped. Now
  `RadarLayer` plays a fixed 24 fps timeline (frames 0..288 per play), renders up to 10 frames ahead into
  pooled buffers (`RadarRenderer.Render(..., into)`), prefills during the start wait, shows the newest
  frame not past due, and skips ahead if it falls behind. Log line now per play stop or interruption:
  `Radar playback: N frames shown (L late), render avg/worst, upload avg/worst`. Built, NOT installed
  (game running). If it still stutters, read that line: late > 0 = rendering can't keep up; big upload
  worst = main-thread texture upload (next step would be LoadRawTextureData or a smaller playback size).

- **Thunder, real cause (WAV build's log):** clips logged "120 s of sound" at start-up but `(0.0 s, Loaded)`
  at every strike. Clips made in code (AudioClip.Create, and the Ogg ones from UnityWebRequest alike) lose
  their sound when Unity's audio is reset, which happens on the way into a raid. `Thunder` now keeps the
  samples (16-bit, ~10 MB), rebuilds every clip on `AudioSettings.OnAudioConfigurationChanged`, at
  raid start (`Awake`) and before any play if a clip has 0 samples, and logs `Thunder: ... clips rebuilt ...`.
  (WAV stays: no harm, and the loader no longer depends on Unity's decoders.)
- **TraceSix:** its log shows every night flash flipping its RayTracedWorld sun shadow mode ("meshes are out
  of the game's sun shadow (40599)" then "sun's rays missing for half a second, giving it back"), because
  the flash raised the TOD light TraceSix treats as the sun. With `com.matsix.tracesix` loaded the flash no
  longer touches the TOD light (ambient x1.4 instead). Joel also saw sky dark but ground lit like day after
  switching to night with the time changer, normal with TraceSix off: not shown to be ours (we don't touch
  time or the light outside flashes); next test is the same with Lightning off / Force thunderstorm Off.
  Installed 2026-10-29 with the playback rework and hidden-key change, not run in game.

- Joel confirmed thunder plays and the TraceSix lighting is fixed (2026-10-29). Then: some clips carried the
  recording's own rain, doubling the game's rain. `tools/derain.py` (numpy only) now runs after
  thunder_clips.py: spectral subtraction of each clip's steady hiss above ~350-900 Hz (x3, at most -40 dB),
  and highs above 1.5-3 kHz faded 35 dB from 1 s after the crack (drop clicks on the mic aren't hiss).
  HF floor fell 7-11 dB with the gentle pass alone; spectrograms (scratchpad) show the crack and rumble
  kept, tail hiss and drop streaks gone. Applied in place to assets/thunder, installed, not heard in game.

- **After the v0.2.2 release:** with TraceSix on, a flash "just makes the map brighter and fades back";
  toggled off, it "looks good" (Joel). TraceSix's GI (config `1. General / Enabled`, flipped by its toggle
  key) replaces the game's sky ambient with light volumes filled over many frames, so the ambient (SH)
  flash got smeared. `LightningFlash` now reads that entry live: TraceSix lighting on -> no SH; the
  ground is lit by our own directional light (`ForecasterLightning`, hard shadows, low-res, aimed
  random bearing per strike via `LightningFlash.Aim`, as high as a 2 km cloud base over the strike:
  overhead ~80 deg, 3 km ~35, far 22; at a fixed 55-78 it only lit window sills, never the room), enabled only while the flash is
  above zero. TraceSix's volumes take only the game's sun and lamps, so it isn't smeared. Toggled off ->
  ambient x1.4 as before. Not TraceSix -> TOD light + SH as before. Caveat: in daytime RayTracedWorld mode
  TraceSix strips the map's meshes from shadow casting, so our light is unshadowed then (flash is weak
  by day). Installed 2026-10-29 (still 0.2.2; dist zip now differs from the GitHub v0.2.2 asset), unseen.

- **Later, 2026-10-29/30 (pushed to release-0.2.0, still versioned 0.2.2, not released):**
  - Selected-map ring on the radar removed (it sat beside the name plate and looked like a stray circle).
  - Thunder distance: strikes spread from the storm's near edge back into it (exponential, mean 1.2 km;
    0.2.2's +-1.5 km put a whole raid at 1.8-4.4 km, all alike), volume ~5.5 dB per doubling past 300 m,
    low-pass `600 + 17400 e^(-m/1200)`, overhead strikes biased close, near clips under 1.5 km, no clip
    twice running. `Field.MetresPerUnit` 40 -> 20 (the map icons put Customs-Woods ~1.5-2 km), rings out
    to 500 units so earshot stays 10 km.
  - Indoors: the game fades the outdoor rain group to near nothing a few steps in (AmbientSoundBlender),
    so each strike is two scheduled copies, outdoor group + indoor rain group (`PrecipitationAmbientIn`),
    equal-power crossfaded by inside/outside (0.4 s); both audible at once was phasey/"garbled". Indoor
    copy follows the game's own openness (`AmbientSoundBlender._currentOutdoorFilterValue`, 1 at a window,
    the room's OutdoorAmbientVolume deep in): volume 0.85-1, cutoff `Lerp(420 + 0.1c, c, open^2)`, and an
    AudioReverbFilter (wetter, longer deep inside; reverb set only on real changes). Joel: reverb "great".
  - Flash: transparent glass takes no shadows, so underground glass caught the flash. The flash's lights and
    ambient now fade by openness only when the camera is >1.5 m under the terrain (`Underground`, needs the
    TerrainModule reference); sky colours and clouds always flash. Fading it in every building hid the
    flash deep inside them.
  - 17 more thunder clips (29: 10 near, 19 far) from 7 more Commons recordings, cut by
    tools/thunder_clips.py (now numbered by STRIKES position, ffmpeg fallback for odd Oggs) + derain.py;
    four more were dropped for leftover rain. THUNDER_CREDITS.md lists them. Zip ~18 MB.
  - FogSix and Return Of Thunderstorms compatibility (see Compatibility).
  None of this has been through a full test raid yet beyond the reverb and the log checks.

- **Release clean-up (2026-10-30), versioned 0.3.0:** the flash's tint on the TOD light is now taken from
  and restored to the game's colour (it lerped from the already-tinted colour each frame and was never put
  back); `Field` caches carry a generation so a worker render started before a seed/season/forced-pattern
  change can't store stale cells; the WAV parser stops on a negative chunk size; the server's version lives
  only in `ModMetadata.VersionText` (router, start-up log and pack.ps1 read it) and the client warns once if
  the server's differs; "Log weather details" defaults off; the key's corner-dodging code (`Place`,
  `ForecastPanel.Covering`) removed, since undocked the key is hidden anyway; UnityWebRequestAudio
  reference dropped. Built to dist, not installed (game was running), not run in game.

## Earlier: the home test of 0.2.1

0.2.1 (2026-10-29) reworks the lightning flash (see Thunderstorms; 0.2.0's flash added to TOD's light
intensity every frame, but TOD only resets it every 0-1 s by shadow quality, so it built up). 0.2.0
(2026-10-29, built on the work machine against the patched mirror) adds, all unseen in game: radar
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

`scripts\pack.ps1 -SPTPath <install> [-Install]` builds both halves and writes `dist\Forecaster-<v>.zip`
(only `BepInEx/plugins/Forecaster.dll` and `SPT_Runtime/user/mods/Forecaster/Forecaster.Server.dll`).
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
  DLL. Route `/forecaster/field` returns version, seed, season (server enum; 2 = winter) and
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
- **Glyphs** (`Glyphs.cs`, PNGs in src/Forecaster/Glyphs embedded as Forecaster.Glyphs.<Name>.png,
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
  1/60 s at 10 km; flash = pulses (`Thunder.Flash`, 1 overhead, 0.5 at 1.8 km), drawn by `LightningFlash` in
  `Camera.onPreCull` (after every LateUpdate): × time of day (1 night .. 0.25 noon, TOD `LerpValue`) × config
  `Lightning brightness`; blue-white; adds to the outdoor ambient SH (`ToDController.SH` → its
  `AmbientLightScript.SetSH`, mostly from above; interiors have their own ambient volumes, so rooms stay dark),
  TOD's sky colour globals (`TOD_MoonSkyColor`/`TOD_SunSkyColor`), and a smaller share to the TOD light's
  intensity and colour (game's shadows and cloud cookie still apply). TOD sets intensity only every
  `Light.UpdateInterval` (ToDController: 1/0.5/0.1/0 s by shadow quality), so the game's value is tracked
  and put back; everything else the game rewrites each frame. Vanilla clouds light from the sun light at
  render time, so they flash; CloudSix's via `CloudSixFlashPatch` (postfix on its
  `CustomCloudController.UpdateMaterial`: `_MoonIntensity`, `_MoonColor`, `_AmbientColor`; enabled at the
  first raid with lightning, when every plugin has loaded). CloudSix's eye adaptation may dim the view a
  little after a big flash. Thunder after distance/343 s, volume and low-pass by
  distance, through the outdoor `PrecipitationAmbientBlender._outputMixerGroup` (the game muffles it by room
  and portals), silent in rooms with OutdoorAmbientVolume ~0. TimeWeatherChanger's thunder slider drives an
  overhead storm while its override is on. Clips: assets/thunder (12, 4 near / 8 far), cut by
  tools/thunder_clips.py from Commons recordings (THUNDER_CREDITS.md; CC BY / CC BY-SA), loaded with
  UnityWebRequestMultimedia from BepInEx/plugins/Forecaster/thunder. Config section Storms.
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
- **Debug lightning** (0.2.1): `Force thunderstorm` (Off/Overhead/Near3Km/Far8Km) overrides `Thunder.StormDistance`
  (rain and sky stay the forecast's; works with Lightning off). `Strike lightning now` / `Flash only now` /
  `Thunder only now` (tick boxes that untick themselves, `ForecasterPlugin.Button`) and `Strike lightning key`
  strike once at `Debug strike distance (m)` (100-12000) via `Thunder.Request` -> `StrikeAt`; thunder only plays
  at once. On maps without our weather (Factory, Labs) the request adds the Thunder component itself; Force
  thunderstorm needs a strike request there first. Log line `Debug lightning: ...`.
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

**FogSix** (`com.matsix.fogsix` 1.0.1, read 2026-10-30): picks the brightest shadowed directional light as its
sun (fog sun shadows) at each scene load and 3, 11, 26 s after. Our flash light could win a scan that
landed mid-flash and stay its sun for the raid; `FogSixSunPatch` (postfix on `FogShadows.FindSun`) skips
it. Its light tracking takes only point/spot lights.

**Return Of Thunderstorms** (github.com/killdg/Return-Of-Thunderstorms, `com.killdg.returnofthunderstorms`
1.2.1, read 2026-10-30): its own storms (rolls each rain period, heavy rain via a ControlRain prefix, a
heavy-rain recording replacing the game's rain, WorldVolume ducking, hurricane wind/debris, night lamp
flicker, own thunder + unshadowed flash). Its `StormtideSound.Apply` scaled every source on a
`PrecipitationAmbient*` group as rain, i.e. our thunder. `ReturnOfThunderstormsCompat` (all by name, each
target checked against its release DLL; a missing one is skipped and named in the log): Apply skips
sources named `Thunder.SourceName`; in a forecast raid (`Thunder.ForecastRaid`) its `RainEpisode.Tick` =
forecast storm within 2 km (`Thunder.StormWithin`); its heavy rain (`HeavyRain.Prefix`), recording
(`StormtideRecordedRain.Tick`, kept running silent because its wind reads the recording's exposure) and
ducking (`WorldAudioDucking.Tick`) wait for the storm overhead; its `StormtideThunder.Tick` is held while
ours runs, and each of our strikes calls its `LightningLightFlicker.Strike` (it flickers only at night,
and finds lamps only during its storm). Its release zip nests everything under `INSTALL/`.

## What the first test has to look at

1. Server console: `[Forecaster] 0.2.1 loaded, weather seed ready.` BepInEx log:
   `Forecaster 0.2.1 loaded.`, `Thunder: 4 near and 8 far clips loaded.`, `Radar attached under '...' at sibling N, size WxH`, no "Could not ...".
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
    flash lights the sky and the outdoors (night: bright but not daylight; noon: faint), shows through
    windows but not in closed rooms, no hard moon-angle shadows, light falls back to normal after each
    flash; clouds flash (with and without CloudSix; log `Lightning lights CloudSix's clouds.`); tune with
    `Storms / Lightning brightness`. Thunder after the flash, quieter far off. Debug: `Force thunderstorm`,
    `Strike lightning now` / `Flash only now` / `Thunder only now` at 300 m, 1.5 km, 5 km, 10 km.
    Log `Thunder plays through mixer group '...'`. Reserve bunker: silent. Shoreline resort basement: muffled.
15. TimeWeatherChanger: its weather overrides ours and hands back; moving the time keeps the weather sane;
    its thunder slider makes lightning.
16. CPU/hitches while thunder and playback run.

## 1.0.0 (2026-10-30)

Renamed SPTForecaster -> Forecaster: GUID `com.mybutthasarash.forecaster`, assemblies/namespaces `Forecaster` and
`Forecaster.Server`, route `/forecaster/field`, client in `BepInEx/plugins/Forecaster/` (thunder/ beside the DLL),
server in `user/mods/Forecaster/` (copies `weather-seed.json` from `user/mods/SPTForecaster` when it has none). Both
halves warn when the old SPTForecaster is still installed. The GitHub repo keeps the name SPTForecaster. Server
start-up is one ASCII line (the console font has no block characters). Deploy Screen Overhaul's public 1.1.0 never
had the Forecaster hookup; Joel chose not to update it.
