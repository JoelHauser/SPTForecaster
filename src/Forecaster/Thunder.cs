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
using HarmonyLib;
using UnityEngine;
using UnityEngine.Audio;
using Random = UnityEngine.Random;

namespace Forecaster
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
    /// - The thunder plays through the game's outdoor rain mixer group, which the game turns down and
    ///   muffles by the windows and doors to the outside (its portal system), and, low and muffled, through
    ///   its indoor rain group, which carries it through the walls once the outdoor group has gone quiet.
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
        private AudioMixerGroup _group, _indoorGroup;
        private AudioClip _lastClip;
        private AudioSource _template;
        private bool _groupLooked;
        private float _nextCheck;
        private readonly List<float[]> _pulses = new List<float[]>(); // start time, length, strength

        // ---- clips -----------------------------------------------------------------------------------

        /// <summary>Loads the thunder clips (thunder/*.wav next to the plugin, in BepInEx/plugins/Forecaster) once, in the background.</summary>
        public static void LoadClips(MonoBehaviour host, string pluginDir)
        {
            if (_loading) return;
            _loading = true;
            host.StartCoroutine(LoadAll(Path.Combine(pluginDir, "thunder")));
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
                    if (size < 0) break;
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

        /// <summary>The thunder sources' name (Return Of Thunderstorms' rain volume control skips it).</summary>
        internal const string SourceName = "ForecasterThunder";

        /// <summary>This raid runs on the forecast's weather (set with its nodes, cleared when the raid ends).</summary>
        internal static bool ForecastRaid;

        /// <summary>A thunderstorm over the player now, by the forecast (or the debug storm).</summary>
        internal static bool StormOverhead()
        {
            var d = StormDistance(out _);
            return d != null && d.Value <= 0;
        }

        /// <summary>A thunderstorm within <paramref name="metres"/> of the player now (its near edge).</summary>
        internal static bool StormWithin(double metres)
        {
            var d = StormDistance(out _);
            return d != null && d.Value * Field.MetresPerUnit <= metres;
        }

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
            // Only with the component on: its OnDestroy is what clears it.
            ForecastRaid = true;
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
            // Last raid's blender is gone.
            _blender = null;
            _blenderLooked = false;
            // A raid: the audio has usually been reset on the way in, which empties the clips.
            EnsureClips();
        }

        private void Update()
        {
            try
            {
                Flash = FlashNow(Time.unscaledTime);
                DebugStrikes();
                UpdateIndoorCopies();
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
        private static int _distanceFrame = -1;
        private static double? _distance;
        private static float _strength;

        private static double? StormDistance(out float strength)
        {
            // Several of Return Of Thunderstorms' hooks ask every frame; it only changes between blocks.
            if (_distanceFrame != Time.frameCount)
            {
                _distanceFrame = Time.frameCount;
                _distance = StormDistanceNow(out _strength);
            }

            strength = _strength;
            return _distance;
        }

        private static double? StormDistanceNow(out float strength)
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
            // Overhead strikes land from a couple of hundred metres to a few km, mostly close (spread evenly,
            // most were 1-3 km and overhead sounded like the rest); farther storms, around their distance.
            var r = Random.value;
            // A storm's distance is its near edge (Field.ThunderDistance), and it strikes all through itself:
            // now and then just in front, mostly a few km into it. 0.2.2's +-1.5 km put a whole raid's strikes
            // at 1.8-4.4 km, about 6 dB apart, and they all sounded the same (Joel's log, 2026-10-30).
            var edge = (float)(units * Field.MetresPerUnit);
            var metres = units <= 0
                ? 200f + 2800f * r * r
                : Mathf.Clamp(edge * Random.Range(0.7f, 1f) - Mathf.Log(Mathf.Max(1e-4f, Random.value)) * 1200f, 300f, 14000f);

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
            LightningFlash.Aim(metres);
            ReturnOfThunderstormsCompat.Strike();

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
            var pool = metres < 1500f && Near.Count > 0 ? Near : Far.Count > 0 ? Far : Near;
            if (pool.Count == 0)
            {
                if (ForecasterPlugin.VerboseLog.Value) ForecasterPlugin.Log.LogInfo("Thunder: no clips loaded, nothing to play.");
                yield break;
            }

            var group = MixerGroup();
            // Never the same clip twice running.
            var clip = pool[Random.Range(0, pool.Count)];
            if (clip == _lastClip && pool.Count > 1) clip = pool[(pool.IndexOf(clip) + Random.Range(1, pool.Count)) % pool.Count];
            _lastClip = clip;
            // The clips are all loudness-matched, so distance is all in here. 0.2.2 went from 1.0 overhead to
            // 0.98 at 2 km and filtered nothing out below about 13 kHz there: every strike sounded the same
            // (Joel, 2026-10-29). Now it falls about 4.5 dB per doubling past 300 m (a bit gentler than open
            // air, which would leave far storms inaudible over the rain): 2 km about -12 dB, 8 km about -21 dB.
            // 0.9: about 5.5 dB per doubling, nearly open air's 6: 1 km 0.34, 2 km 0.18, 4 km 0.10, 8 km 0.05.
            var volume = Mathf.Pow(300f / Mathf.Max(metres, 300f), 0.9f) * ForecasterPlugin.ThunderVolume.Value;
            // Far thunder rolls slower and lower.
            var pitch = Random.Range(0.94f, 1.05f) * Mathf.Lerp(1f, 0.9f, Mathf.Clamp01(metres / 8000f));
            // Air takes the top off distant thunder: a crack close by, about 8 kHz at 1 km, 4 kHz at 2 km,
            // a low rumble under 1 kHz by 5 km.
            var cutoff = 600f + 17400f * Mathf.Exp(-metres / 1200f);

            // Two copies, started together, only one of them heard at a time. The outdoor one goes through the
            // outdoor rain group, which the game turns down to nearly nothing a few steps inside (rain stops
            // there; thunder doesn't), so on its own it could only be heard outside (Joel, 2026-10-29). The
            // indoor one goes through the indoor rain group: muffled, ringing in the room, louder and clearer by
            // a window. Both used to play at once, and the same sound through two effect chains, a hair apart,
            // came out phasey and garbled, with a room's reverb outside too (Joel, 2026-10-30). Now the player's
            // side of the doorway has its copy and the other is silent, crossfaded as they cross mid-rumble.
            var open = Openness(out var inside);
            // A sealed room underground (a bunker the game lets no outdoor sound into) hears nothing.
            if (inside && open <= 0.01f && LightningFlash.Underground())
            {
                if (ForecasterPlugin.VerboseLog.Value) ForecasterPlugin.Log.LogInfo($"Thunder: '{clip.name}' at {metres:0} m skipped, sealed underground.");
                yield break;
            }

            var start = AudioSettings.dspTime + 0.05;
            var mix = inside ? 1f : 0f;
            var outdoors = Play(clip, group, volume * OutdoorGain(mix), pitch, cutoff, start, false);
            var indoors = Play(clip, _indoorGroup, volume * IndoorShare(open) * IndoorGain(mix), pitch, IndoorCutoff(cutoff, open), start, true);
            Reverb(indoors.GetComponent<AudioReverbFilter>(), open);
            _rumbles.Add(new Pair { Outdoor = outdoors, Indoor = indoors, Volume = volume, Cutoff = cutoff, Open = open, ReverbOpen = open, Mix = mix });

            if (ForecasterPlugin.VerboseLog.Value)
            {
                // Everything that decides whether it is heard, for when it isn't (0.2.1 was silent in game).
                ForecasterPlugin.Log.LogInfo($"Thunder: '{clip.name}' ({clip.length:0.0} s, {clip.loadState}) at {metres:0} m, volume {volume:0.00}, low-pass {cutoff:0} Hz{(outdoors.bypassEffects ? " (bypassed!)" : "")}, " +
                    $"group '{(group != null ? group.name : "none")}', {(inside ? $"inside (openness {open:0.00}), indoor copy {indoors.volume:0.00}" : "outside")} via '{(_indoorGroup != null ? _indoorGroup.name : "none")}'" +
                    $", spatialize {outdoors.spatialize}, blend {outdoors.spatialBlend:0.#}, priority {outdoors.priority}, listener volume {AudioListener.volume:0.00}{(AudioListener.pause ? " (paused)" : "")}.");
            }
        }

        private AudioSource Play(AudioClip clip, AudioMixerGroup group, float volume, float pitch, float cutoff, double start, bool reverb)
        {
            var source = FreeSource();
            source.clip = clip;
            source.outputAudioMixerGroup = group;
            source.volume = volume;
            source.pitch = pitch;
            source.GetComponent<AudioLowPassFilter>().cutoffFrequency = cutoff;
            source.GetComponent<AudioReverbFilter>().enabled = reverb;
            source.PlayScheduled(start);
            return source;
        }

        /// <summary>
        /// Inside, the thunder rings in the room it comes into, as it does in real life (Joel, 2026-10-29): by
        /// a window mostly the thunder itself and a short room, deep inside more of the room than the thunder,
        /// longer and darker (walls and floors soak up the highs). The indoor group carries none of the
        /// game's room reverb, so this is the copy's own.
        /// </summary>
        private static void Reverb(AudioReverbFilter reverb, float open)
        {
            reverb.reverbPreset = AudioReverbPreset.User;
            reverb.dryLevel = Mathf.Lerp(-150f, 0f, open);         // mB (-500 deep inside drowned the thunder)
            reverb.room = Mathf.Lerp(-300f, -1400f, open);
            reverb.roomHF = -1200f;
            reverb.roomLF = 0f;
            reverb.decayTime = Mathf.Lerp(1.9f, 1.0f, open);       // s
            reverb.decayHFRatio = 0.45f;
            reverb.reflectionsLevel = Mathf.Lerp(-200f, -1200f, open);
            reverb.reflectionsDelay = 0.012f;
            reverb.reverbLevel = Mathf.Lerp(400f, -600f, open);
            reverb.reverbDelay = 0.03f;
            reverb.diffusion = 100f;
            reverb.density = 100f;
            reverb.hfReference = 4000f;
        }

        /// <summary>One strike's two copies; Mix 0 is all outdoor, 1 all indoor.</summary>
        private sealed class Pair
        {
            public AudioSource Outdoor, Indoor;
            public float Volume, Cutoff, Open, ReverbOpen, Mix;
        }

        private readonly List<Pair> _rumbles = new List<Pair>();

        // Equal-power crossfade, so the doorway doesn't dip.
        private static float OutdoorGain(float mix) => Mathf.Cos(mix * Mathf.PI / 2f);
        private static float IndoorGain(float mix) => Mathf.Sin(mix * Mathf.PI / 2f);

        // Indoors, thunder comes in through the windows (often broken) and doorways: nearly all of it beside
        // one, a muffled rumble through the walls deep inside (Joel, 2026-10-29: hear it more inside,
        // especially with windows). The first version was a 450 Hz rumble at under half volume everywhere.
        // Deep inside it was half volume, and with the reverb's dry cut and a 300 Hz filter a far strike came to
        // about 2%: silent far from a window (Joel, 2026-10-30). Thunder carries into buildings.
        private static float IndoorShare(float open) => Mathf.Lerp(0.85f, 1f, open);
        // The walls take the top off on top of the air's, so a close crack still comes through brighter than a
        // far rumble: deep inside about 2.1 kHz overhead and 500 Hz at 6 km, at a window what the air leaves.
        // 0.2.2's indoor copy was cut to 500 Hz whatever the distance, so inside only the volume changed.
        private static float IndoorCutoff(float cutoff, float open) => Mathf.Lerp(420f + 0.1f * cutoff, cutoff, open * open);

        /// <summary>
        /// The copies follow the player as they play: through doorways (a 0.4 s crossfade) and, inside,
        /// between windows and the middle of rooms.
        /// </summary>
        private void UpdateIndoorCopies()
        {
            if (_rumbles.Count == 0) return;
            var open = Openness(out var inside);
            var dt = Time.unscaledDeltaTime;
            for (var i = _rumbles.Count - 1; i >= 0; i--)
            {
                var c = _rumbles[i];
                if (c.Outdoor == null || c.Indoor == null || (!c.Outdoor.isPlaying && !c.Indoor.isPlaying)) { _rumbles.RemoveAt(i); continue; }
                c.Mix = Mathf.MoveTowards(c.Mix, inside ? 1f : 0f, dt / 0.4f);
                // Openness eases too, so a step past a window doesn't jump.
                var o = c.Open = Mathf.MoveTowards(c.Open, open, dt * 2f);
                c.Outdoor.volume = c.Volume * OutdoorGain(c.Mix);
                c.Indoor.volume = c.Volume * IndoorShare(o) * IndoorGain(c.Mix);
                c.Indoor.GetComponent<AudioLowPassFilter>().cutoffFrequency = IndoorCutoff(c.Cutoff, o);
                // The reverb only on real changes: setting it every frame can click.
                if (Mathf.Abs(o - c.ReverbOpen) > 0.05f || (o != c.ReverbOpen && o == open))
                {
                    c.ReverbOpen = o;
                    Reverb(c.Indoor.GetComponent<AudioReverbFilter>(), o);
                }
            }
        }

        private static AmbientSoundBlender _blender;
        private static bool _blenderLooked;
        // Looked up on first use, inside the try below: a renamed field then falls back instead of failing the
        // type's static set-up (which would stop the whole plugin at start-up).
        private static AccessTools.FieldRef<AmbientSoundBlender, float> _outdoorFilter;

        /// <summary>
        /// How open to the outside the player's spot is, 0-1: 1 outdoors and right at a window or doorway,
        /// falling with distance from the nearest one, down to the room's own outdoor level deep inside. It's
        /// the game's own measure, the one it muffles the outdoor sound by (AmbientSoundBlender's outdoor filter
        /// value, from its portal search).
        /// </summary>
        internal static float Openness(out bool inside)
        {
            var spatial = MonoBehaviourSingleton<SpatialAudioSystem>.Instance;
            var room = spatial != null ? spatial.ListenerCurrentRoom : null;
            inside = room != null && room.IsValid && !room.Type.IsOutdoor();
            if (!inside) return 1f;

            if (!_blenderLooked || (_blender == null && Time.frameCount % 600 == 0))
            {
                _blenderLooked = true;
                _blender = FindObjectOfType<AmbientSoundBlender>();
            }

            if (_blender != null)
            {
                try
                {
                    if (_outdoorFilter == null) _outdoorFilter = AccessTools.FieldRefAccess<AmbientSoundBlender, float>("_currentOutdoorFilterValue");
                    return Mathf.Clamp01(_outdoorFilter(_blender));
                }
                catch
                {
                    _blender = null;
                }
            }

            var outdoor = room.RoomAmbientData != null ? room.RoomAmbientData.OutdoorAmbientVolume : 0.3f;
            return Mathf.Clamp01(outdoor);
        }

        private AudioSource FreeSource()
        {
            foreach (var s in _sources)
            {
                if (!s.isPlaying) return s;
            }

            var go = new GameObject(SourceName);
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
                // Not bypassEffects: the distance low-pass below is a source effect.
                source.bypassListenerEffects = _template.bypassListenerEffects;
                source.bypassReverbZones = _template.bypassReverbZones;
                source.reverbZoneMix = _template.reverbZoneMix;
                source.ignoreListenerPause = _template.ignoreListenerPause;
                source.ignoreListenerVolume = _template.ignoreListenerVolume;
                source.dopplerLevel = 0f;
                source.priority = Mathf.Max(0, _template.priority - 8);
            }

            // In this order: muffled by the walls first, then ringing in the room.
            go.AddComponent<AudioLowPassFilter>();
            go.AddComponent<AudioReverbFilter>().enabled = false;
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
                if (blender._outputMixerGroup == null) continue;
                if (blender._environment == EnvironmentType.Outdoor && _group == null)
                {
                    _group = blender._outputMixerGroup;
                    _template = blender._precipitationSource;
                }
                else if (blender._environment == EnvironmentType.Indoor && _indoorGroup == null)
                {
                    _indoorGroup = blender._outputMixerGroup;
                }
            }

            if (_group == null && MonoBehaviourSingleton<BetterAudio>.Instantiated) _group = MonoBehaviourSingleton<BetterAudio>.Instance.AmbientOutMixer;
            // Never no group: a source without one skips the game's mixer (its volume sliders, pause). Only one
            // copy is heard at a time, so sharing the outdoor group is fine.
            if (_indoorGroup == null) _indoorGroup = _group;
            if (ForecasterPlugin.VerboseLog.Value)
            {
                ForecasterPlugin.Log.LogInfo("Thunder plays through mixer group '" + (_group != null ? _group.name : "none") + "', indoors '" + (_indoorGroup != null ? _indoorGroup.name : "none") + "'.");
            }
            return _group;
        }

        private void OnDestroy()
        {
            if (_instance == this) _instance = null;
            ForecastRaid = false;
            Flash = 0;
            LightningFlash.Unhook();
        }
    }
}
