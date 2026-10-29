using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using Audio.AmbientSubsystem;
using Audio.Extensions;
using Audio.SpatialSystem;
using Comfort.Common;
using EFT;
using EFT.Weather;
using UnityEngine;
using UnityEngine.Audio;
using UnityEngine.Networking;
using Random = UnityEngine.Random;

namespace SPTForecaster
{
    /// <summary>
    /// Lightning and thunder in a raid, while a thunderstorm is over the map or near it (the game has none:
    /// it computes a LightningThunderProbability and never uses it).
    ///
    /// Strikes come at random, more often the nearer the storm (Field.ThunderDistance, worked out per weather
    /// block when the raid's weather is set). Each is a flash, then its thunder after the sound has travelled
    /// (distance / 343 m/s), quieter and more muffled the further away.
    ///
    /// - The flash (LightningFlash) lights the sky, the outdoor ambient, the clouds and, more weakly, the
    ///   sun/moon light for a few flickers: a blue-white glow outdoors and through windows, not in rooms.
    /// - The thunder plays through the same mixer group as the game's outdoor rain, which the game turns
    ///   down and muffles by the room the player is in and the windows and doors to the outside (its
    ///   portal system): full outdoors, muffled in a basement with windows, silent in a sealed bunker. A
    ///   room whose outdoor sound the game sets to nothing gets no thunder at all, as a backstop.
    ///
    /// While the game's weather debug override is on (TimeWeatherChanger's panel), its "Thunder probability"
    /// drives the storm instead, overhead, so that slider finally does something.
    /// </summary>
    internal sealed class Thunder : MonoBehaviour
    {
        private const float SpeedOfSound = 343f;

        /// <summary>Strikes per real second with the storm overhead, and at the edge of hearing.</summary>
        private const float RateOverhead = 1f / 22f, RateFar = 1f / 60f;

        private static readonly List<AudioClip> Near = new List<AudioClip>(), Far = new List<AudioClip>();
        private static bool _loading;

        /// <summary>Per weather block on the raid clock: from tick, to tick, storm distance (frame units).</summary>
        private static readonly List<double[]> Spans = new List<double[]>();

        /// <summary>This frame's lightning: 0 none, about 1 a strike overhead (drawn by LightningFlash).</summary>
        public static float Flash;

        private readonly List<AudioSource> _sources = new List<AudioSource>();
        private AudioMixerGroup _group;
        private bool _groupLooked;
        private float _nextCheck;
        private readonly List<float[]> _pulses = new List<float[]>(); // start time, length, strength

        // ---- clips -----------------------------------------------------------------------------------

        /// <summary>Loads the thunder clips (BepInEx/plugins/SPTForecaster/thunder/*.ogg) once, in the background.</summary>
        public static void LoadClips(MonoBehaviour host, string pluginDir)
        {
            if (_loading) return;
            _loading = true;
            host.StartCoroutine(LoadAll(Path.Combine(Path.Combine(pluginDir, "SPTForecaster"), "thunder")));
        }

        private static IEnumerator LoadAll(string dir)
        {
            if (!Directory.Exists(dir))
            {
                ForecasterPlugin.Log.LogWarning("No thunder sounds in " + dir + "; lightning will be silent.");
                yield break;
            }

            foreach (var file in Directory.GetFiles(dir, "*.ogg"))
            {
                var url = "file:///" + file.Replace('\\', '/');
                using (var request = UnityWebRequestMultimedia.GetAudioClip(url, AudioType.OGGVORBIS))
                {
                    ((DownloadHandlerAudioClip)request.downloadHandler).streamAudio = false;
                    yield return request.SendWebRequest();
                    if (request.result != UnityWebRequest.Result.Success)
                    {
                        ForecasterPlugin.Log.LogWarning("Could not load " + Path.GetFileName(file) + ": " + request.error);
                        continue;
                    }

                    var clip = DownloadHandlerAudioClip.GetContent(request);
                    clip.name = Path.GetFileNameWithoutExtension(file);
                    (clip.name.Contains("_near_") ? Near : Far).Add(clip);
                }
            }

            ForecasterPlugin.Log.LogInfo($"Thunder: {Near.Count} near and {Far.Count} far clips loaded.");
        }

        // ---- the raid's storms -----------------------------------------------------------------------

        /// <summary>Called with the raid's blocks when its weather nodes are set.</summary>
        public static void Set(List<Block> blocks, double start, long raidTicks, double x, double y, int raidMinutes)
        {
            Spans.Clear();
            Flash = 0;
            // The component goes on every raid (it does nothing without a storm), so the debug strikes always work.
            if (ForecasterPlugin.Lightning.Value) AddSpans(blocks, start, raidTicks, x, y, raidMinutes);

            var weather = WeatherController.Instance;
            if (weather == null) return;
            if (weather.GetComponent<Thunder>() == null) weather.gameObject.AddComponent<Thunder>();
            LightningFlash.Hook();
            if (ForecasterPlugin.VerboseLog.Value)
            {
                ForecasterPlugin.Log.LogInfo($"Thunder: storms within earshot in {Spans.Count} of this raid's weather blocks.");
            }
        }

        private static void AddSpans(List<Block> blocks, double start, long raidTicks, double x, double y, int raidMinutes)
        {
            // The raid and an hour either side; further out only matters if a mod moves the clock that far.
            var from = start - 3600;
            var to = start + (raidMinutes > 0 ? raidMinutes : 60) * 60 * Schedule.Acceleration + 3600;
            foreach (var b in blocks)
            {
                if (b.End <= from || b.Start >= to) continue;
                var d = b.Thunder ? 0 : Field.ThunderDistance(x, y, b.Start + Field.Step / 2.0);
                if (d == null) continue;
                Spans.Add(new[]
                {
                    raidTicks + (b.Start - start) * TimeSpan.TicksPerSecond,
                    raidTicks + (b.End - start) * TimeSpan.TicksPerSecond,
                    d.Value,
                });
            }
        }

        // ---- debug: strikes on demand (config section Debug) ----------------------------------------

        internal enum DebugKind { None, Strike, FlashOnly, ThunderOnly }

        private static DebugKind _request;
        private static Thunder _instance;

        /// <summary>Asks for one strike at the debug distance on the next frame (in raid only).</summary>
        public static void Request(DebugKind kind)
        {
            if (_instance == null)
            {
                // Maps without a forecast (Factory, Labs) or raids without our weather never got the component.
                var weather = WeatherController.Instance;
                if (weather == null || Singleton<GameWorld>.Instance == null)
                {
                    ForecasterPlugin.Log.LogInfo("Debug lightning works in raid only.");
                    return;
                }

                weather.gameObject.AddComponent<Thunder>();
                LightningFlash.Hook();
            }

            _request = kind;
        }

        private void Awake() => _instance = this;

        private void Update()
        {
            try
            {
                Flash = FlashNow(Time.unscaledTime);
                DebugStrikes();
                if (!ForecasterPlugin.Lightning.Value && ForecasterPlugin.ForceStorm.Value == ForecasterPlugin.StormDebug.Off) return;
                var now = Time.unscaledTime;
                if (now < _nextCheck) return;
                _nextCheck = now + 0.5f;

                var distance = StormDistance(out var strength);
                if (distance == null) return;

                // A strike in the next half second, at the rate for this distance.
                var near = 1f - Mathf.Clamp01((float)(distance.Value / Field.ThunderReach));
                var rate = Mathf.Lerp(RateFar, RateOverhead, near) * strength;
                if (Random.value < rate * 0.5f) Strike(distance.Value);
            }
            catch (Exception ex)
            {
                ForecasterPlugin.Log.LogError("Thunder failed, stopping it for this raid: " + ex.Message);
                Spans.Clear();
                Flash = 0;
            }
        }

        /// <summary>Distance (frame units) of the storm now, or null for none; strength scales the strike rate.</summary>
        private static double? StormDistance(out float strength)
        {
            strength = 1f;
            switch (ForecasterPlugin.ForceStorm.Value)
            {
                case ForecasterPlugin.StormDebug.Overhead: return 0;
                case ForecasterPlugin.StormDebug.Near3Km: return 3000.0 / Field.MetresPerUnit;
                case ForecasterPlugin.StormDebug.Far8Km: return 8000.0 / Field.MetresPerUnit;
            }

            var weather = WeatherController.Instance;
            if (weather != null && weather.WeatherDebug != null && weather.WeatherDebug.Enabled)
            {
                // The player's weather (TimeWeatherChanger): its thunder slider is an overhead storm's strength.
                strength = weather.WeatherDebug.LightningThunderProbability;
                return strength > 0.01f ? 0 : (double?)null;
            }

            var world = Singleton<GameWorld>.Instance;
            if (world == null || world.GameDateTime == null) return null;
            var tick = world.GameDateTime.Calculate().Ticks;
            foreach (var s in Spans)
            {
                if (tick >= s[0] && tick < s[1]) return s[2];
            }

            return null;
        }

        private void Strike(double units)
        {
            // Overhead strikes land anywhere from a few hundred metres to a few km; farther storms, around their distance.
            var metres = units <= 0
                ? Random.Range(250f, 3000f)
                : Mathf.Max(400f, (float)(units * Field.MetresPerUnit) + Random.Range(-1500f, 1500f));

            StrikeAt(metres, true, true);
        }

        private void DebugStrikes()
        {
            var kind = _request;
            _request = DebugKind.None;
            if (kind == DebugKind.None && ForecasterPlugin.StrikeKey.Value.IsDown()) kind = DebugKind.Strike;
            if (kind == DebugKind.None) return;

            float metres = ForecasterPlugin.StrikeDistance.Value;
            ForecasterPlugin.Log.LogInfo($"Debug lightning: {kind} at {metres:0} m (thunder after {metres / SpeedOfSound:0.0} s).");
            StrikeAt(metres, kind != DebugKind.ThunderOnly, kind != DebugKind.FlashOnly);
        }

        private void StrikeAt(float metres, bool flash, bool sound)
        {
            if (sound) StartCoroutine(Rumble(metres, flash ? metres / SpeedOfSound : 0f));
            if (!flash) return;

            // The flash: a few flickers, 1 close by, half at 1.8 km, faint far off.
            var power = 1f / (1f + (metres / 1800f) * (metres / 1800f));
            var t = Time.unscaledTime;
            var flickers = Random.Range(1, 5);
            for (var i = 0; i < flickers; i++)
            {
                var length = Random.Range(0.04f, 0.1f);
                _pulses.Add(new[] { t, length, power * Random.Range(0.5f, 1f) * (i == 0 ? 1f : 0.8f) });
                t += length + Random.Range(0.03f, 0.14f);
            }
        }

        private float FlashNow(float now)
        {
            var total = 0f;
            for (var i = _pulses.Count - 1; i >= 0; i--)
            {
                var p = _pulses[i];
                if (now > p[0] + p[1] + 0.2f) { _pulses.RemoveAt(i); continue; }
                if (now < p[0]) continue;
                // Snaps on, falls off fast, a little afterglow.
                var k = (now - p[0]) / p[1];
                total += p[2] * (k <= 1f ? 1f - 0.5f * k : 0.5f * Mathf.Exp(-(now - p[0] - p[1]) * 18f));
            }

            return Mathf.Min(total, 1.5f);
        }

        private IEnumerator Rumble(float metres, float delay)
        {
            yield return new WaitForSecondsRealtime(delay);
            var pool = metres < 2200f && Near.Count > 0 ? Near : Far.Count > 0 ? Far : Near;
            if (pool.Count == 0) yield break;
            if (!Audible(out var roomVolume)) yield break;

            var source = FreeSource();
            source.clip = pool[Random.Range(0, pool.Count)];
            source.outputAudioMixerGroup = MixerGroup();
            source.volume = Mathf.Clamp01(1.15f - metres / 12000f) * roomVolume * ForecasterPlugin.ThunderVolume.Value;
            source.pitch = Random.Range(0.92f, 1.06f);
            // Air takes the top off distant thunder: full brightness close by, a low rumble by 10 km.
            source.GetComponent<AudioLowPassFilter>().cutoffFrequency = Mathf.Lerp(16000f, 700f, Mathf.Clamp01(metres / 10000f));
            source.Play();
        }

        /// <summary>
        /// Whether thunder can be heard where the player is. The mixer group does the muffling; this only
        /// silences rooms the game lets no outdoor sound into (a sealed bunker).
        /// </summary>
        private static bool Audible(out float roomVolume)
        {
            roomVolume = 1f;
            var spatial = MonoBehaviourSingleton<SpatialAudioSystem>.Instance;
            var room = spatial != null ? spatial.ListenerCurrentRoom : null;
            if (room == null || !room.IsValid || room.Type.IsOutdoor()) return true;
            var outdoor = room.RoomAmbientData != null ? room.RoomAmbientData.OutdoorAmbientVolume : 1f;
            return outdoor > 0.01f;
        }

        private AudioSource FreeSource()
        {
            foreach (var s in _sources)
            {
                if (!s.isPlaying) return s;
            }

            var go = new GameObject("SPTForecasterThunder");
            go.transform.SetParent(transform, false);
            var source = go.AddComponent<AudioSource>();
            source.playOnAwake = false;
            source.spatialBlend = 0f; // thunder fills the sky; the game's mixer handles indoors
            source.priority = 64;
            go.AddComponent<AudioLowPassFilter>();
            _sources.Add(source);
            return source;
        }

        /// <summary>The game's outdoor rain mixer group (muffled indoors by the game), else its outdoor ambience group.</summary>
        private AudioMixerGroup MixerGroup()
        {
            if (_groupLooked) return _group;
            _groupLooked = true;
            foreach (var blender in FindObjectsOfType<PrecipitationAmbientBlender>())
            {
                if (blender._environment == EnvironmentType.Outdoor && blender._outputMixerGroup != null)
                {
                    _group = blender._outputMixerGroup;
                    break;
                }
            }

            if (_group == null && MonoBehaviourSingleton<BetterAudio>.Instantiated) _group = MonoBehaviourSingleton<BetterAudio>.Instance.AmbientOutMixer;
            if (ForecasterPlugin.VerboseLog.Value) ForecasterPlugin.Log.LogInfo("Thunder plays through mixer group '" + (_group != null ? _group.name : "none") + "'.");
            return _group;
        }

        private void OnDestroy()
        {
            if (_instance == this) _instance = null;
            Flash = 0;
            LightningFlash.Unhook();
        }
    }
}
