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
        private AudioSource _template;
        private bool _groupLooked;
        private float _nextCheck;
        private readonly List<float[]> _pulses = new List<float[]>(); // start time, length, strength

        // ---- clips -----------------------------------------------------------------------------------

        /// <summary>Loads the thunder clips (BepInEx/plugins/SPTForecaster/thunder/*.wav) once, in the background.</summary>
        public static void LoadClips(MonoBehaviour host, string pluginDir)
        {
            if (_loading) return;
            _loading = true;
            host.StartCoroutine(LoadAll(Path.Combine(Path.Combine(pluginDir, "SPTForecaster"), "thunder")));
        }

        /// <summary>
        /// Reads the WAV files ourselves (on a worker) and builds the clips from their samples. 0.2.1 loaded
        /// Ogg Vorbis through UnityWebRequestMultimedia, and in EFT every clip came back "Loaded" but 0 s long,
        /// so nothing ever played (2026-10-29).
        /// </summary>
        private static IEnumerator LoadAll(string dir)
        {
            if (!Directory.Exists(dir))
            {
                ForecasterPlugin.Log.LogWarning("No thunder sounds in " + dir + "; lightning will be silent.");
                yield break;
            }

            var files = Directory.GetFiles(dir, "*.wav");
            if (files.Length == 0) ForecasterPlugin.Log.LogWarning("No thunder sounds (*.wav) in " + dir + "; lightning will be silent.");
            var read = System.Threading.Tasks.Task.Run(() =>
            {
                var result = new List<Wav>();
                foreach (var file in files)
                {
                    try
                    {
                        result.Add(Wav.Read(file));
                    }
                    catch (Exception ex)
                    {
                        ForecasterPlugin.Log.LogWarning("Could not read " + Path.GetFileName(file) + ": " + ex.Message);
                    }
                }

                return result;
            });

            while (!read.IsCompleted) yield return null;
            if (read.Exception != null)
            {
                ForecasterPlugin.Log.LogError("Thunder sounds failed to load: " + read.Exception.GetBaseException());
                yield break;
            }

            Sounds.Clear();
            Sounds.AddRange(read.Result);
            BuildClips("loaded");
            AudioSettings.OnAudioConfigurationChanged += _ => _audioReset = true;
        }

        /// <summary>The clips' sound, kept so the clips can be made again.</summary>
        private static readonly List<Wav> Sounds = new List<Wav>();
        private static bool _audioReset;

        /// <summary>
        /// (Re)makes every clip from <see cref="Sounds"/>. A clip made in code loses its sound when Unity's
        /// audio is reset, which happens on the way into a raid: 0.2.2's clips were 120 s long at start-up
        /// and "Loaded" but 0 s long at every strike, so nothing played (2026-10-29; 0.2.1's Ogg clips the same).
        /// </summary>
        private static void BuildClips(string why)
        {
            foreach (var c in Near) if (c != null) Destroy(c);
            foreach (var c in Far) if (c != null) Destroy(c);
            Near.Clear();
            Far.Clear();
            var seconds = 0f;
            foreach (var wav in Sounds)
            {
                var frames = wav.Pcm.Length / wav.Channels;
                var clip = AudioClip.Create(wav.Name, frames, wav.Channels, wav.Rate, false);
                var data = new float[wav.Pcm.Length];
                for (var i = 0; i < data.Length; i++) data[i] = wav.Pcm[i] / 32768f;
                clip.SetData(data, 0);
                seconds += clip.length;
                (wav.Name.Contains("_near_") ? Near : Far).Add(clip);
            }

            ForecasterPlugin.Log.LogInfo($"Thunder: {Near.Count} near and {Far.Count} far clips {why} ({seconds:0} s of sound).");
        }

        /// <summary>The clips, made again first if the audio was reset or a clip has lost its sound.</summary>
        private static void EnsureClips()
        {
            var empty = false;
            foreach (var c in Near) empty |= c == null || c.samples <= 0;
            foreach (var c in Far) empty |= c == null || c.samples <= 0;
            if (!_audioReset && !empty) return;
            BuildClips(_audioReset ? "rebuilt after an audio reset" : "rebuilt (they had lost their sound)");
            _audioReset = false;
        }

        /// <summary>A PCM WAV file's samples (16-bit, 24-bit or 32-bit float), kept as 16-bit.</summary>
        private sealed class Wav
        {
            public string Name;
            public int Channels, Rate;
            public short[] Pcm;

            public static Wav Read(string path)
            {
                var b = File.ReadAllBytes(path);
                if (b.Length < 12 || b[0] != 'R' || b[1] != 'I' || b[2] != 'F' || b[3] != 'F' || b[8] != 'W' || b[9] != 'A' || b[10] != 'V' || b[11] != 'E')
                {
                    throw new InvalidDataException("not a WAV file");
                }

                int format = 0, channels = 0, rate = 0, bits = 0, at = 12;
                while (at + 8 <= b.Length)
                {
                    var id = System.Text.Encoding.ASCII.GetString(b, at, 4);
                    var size = BitConverter.ToInt32(b, at + 4);
                    var body = at + 8;
                    if (id == "fmt ")
                    {
                        format = BitConverter.ToUInt16(b, body);
                        channels = BitConverter.ToUInt16(b, body + 2);
                        rate = BitConverter.ToInt32(b, body + 4);
                        bits = BitConverter.ToUInt16(b, body + 14);
                        if (format == 0xFFFE && size >= 26) format = BitConverter.ToUInt16(b, body + 24); // extensible
                    }
                    else if (id == "data")
                    {
                        if (channels <= 0 || rate <= 0) throw new InvalidDataException("data before format");
                        size = Math.Min(size, b.Length - body);
                        var step = bits / 8;
                        var n = size / step;
                        var samples = new short[n - n % channels];
                        for (var i = 0; i < samples.Length; i++)
                        {
                            var p = body + i * step;
                            if (format == 1 && bits == 16) samples[i] = BitConverter.ToInt16(b, p);
                            else if (format == 1 && bits == 24) samples[i] = (short)((b[p + 1] | (sbyte)b[p + 2] << 8));
                            else if (format == 3 && bits == 32) samples[i] = (short)Math.Round(Math.Max(-1f, Math.Min(1f, BitConverter.ToSingle(b, p))) * 32767f);
                            else throw new InvalidDataException($"unsupported WAV format {format}, {bits}-bit");
                        }

                        return new Wav { Name = Path.GetFileNameWithoutExtension(path), Channels = channels, Rate = rate, Pcm = samples };
                    }

                    at = body + size + (size & 1);
                }

                throw new InvalidDataException("no audio data");
            }
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

        private void Awake()
        {
            _instance = this;
            // A raid: the audio has usually been reset on the way in, which empties the clips.
            EnsureClips();
        }

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
            EnsureClips();
            var pool = metres < 2200f && Near.Count > 0 ? Near : Far.Count > 0 ? Far : Near;
            if (pool.Count == 0)
            {
                if (ForecasterPlugin.VerboseLog.Value) ForecasterPlugin.Log.LogInfo("Thunder: no clips loaded, nothing to play.");
                yield break;
            }

            if (!Audible(out var roomVolume))
            {
                if (ForecasterPlugin.VerboseLog.Value) ForecasterPlugin.Log.LogInfo("Thunder: the player's room lets no outdoor sound in, not played.");
                yield break;
            }

            var group = MixerGroup();
            var source = FreeSource();
            source.clip = pool[Random.Range(0, pool.Count)];
            source.outputAudioMixerGroup = group;
            source.volume = Mathf.Clamp01(1.15f - metres / 12000f) * roomVolume * ForecasterPlugin.ThunderVolume.Value;
            source.pitch = Random.Range(0.92f, 1.06f);
            // Air takes the top off distant thunder: full brightness close by, a low rumble by 10 km.
            source.GetComponent<AudioLowPassFilter>().cutoffFrequency = Mathf.Lerp(16000f, 700f, Mathf.Clamp01(metres / 10000f));
            source.Play();
            if (ForecasterPlugin.VerboseLog.Value)
            {
                // Everything that decides whether it is heard, for when it isn't (0.2.1 was silent in game).
                ForecasterPlugin.Log.LogInfo($"Thunder: '{source.clip.name}' ({source.clip.length:0.0} s, {source.clip.loadState}) at {metres:0} m, volume {source.volume:0.00} (room {roomVolume:0.00}), " +
                    $"group '{(group != null ? group.name : "none")}', spatialize {source.spatialize}, blend {source.spatialBlend:0.#}, priority {source.priority}, " +
                    $"playing {source.isPlaying}, listener volume {AudioListener.volume:0.00}{(AudioListener.pause ? " (paused)" : "")}.");
            }
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
            MixerGroup();
            if (_template != null)
            {
                // Set up like the game's own outdoor rain source, which is heard through the same group: a
                // plain 2D source was silent there in 0.2.1, and EFT's audio (Meta XR spatializer, voice
                // limits) may want what the game's sources have.
                source.spatialize = _template.spatialize;
                source.spatializePostEffects = _template.spatializePostEffects;
                source.spatialBlend = _template.spatialBlend;
                source.bypassEffects = _template.bypassEffects;
                source.bypassListenerEffects = _template.bypassListenerEffects;
                source.bypassReverbZones = _template.bypassReverbZones;
                source.reverbZoneMix = _template.reverbZoneMix;
                source.ignoreListenerPause = _template.ignoreListenerPause;
                source.ignoreListenerVolume = _template.ignoreListenerVolume;
                source.dopplerLevel = 0f;
                source.priority = Mathf.Max(0, _template.priority - 8);
            }

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
                    _template = blender._precipitationSource;
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
