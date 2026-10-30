using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.UI;

namespace Forecaster
{
    /// <summary>
    /// The radar on the location screen: a RawImage the size of the location buttons' container, placed
    /// just below the buttons (and the route lines) in the same parent. The game anchors each button at
    /// (IconX / 1000, IconY / 1000) of that container, and the field uses the same fractions, so the radar
    /// lines up with the buttons at any resolution. TushonkaTerritories' faction map sits lower, in the
    /// map image, so it shows through underneath.
    ///
    /// The picture is rendered on a worker thread and uploaded when ready, so the menu never stutters.
    /// It shows the selected time slot's weather and redraws every few seconds as the clock moves.
    ///
    /// Playback: when a map and slot are picked, the radar plays that raid through (its own raid length)
    /// twice and then rests on the raid's start. One play holds on the start, runs the raid in
    /// <see cref="PlaySeconds"/>, holds on the end and fades out; the next play, or the rest, fades back in.
    /// Frames are rendered one after another at the true in-game time, so storms slide rather than jump.
    /// Timings were picked in docs/radar-prototype.html (2026-10-29).
    /// </summary>
    internal sealed class RadarLayer : MonoBehaviour
    {
        private const float RedrawSeconds = 5f;
        // A short wait, so clicking through maps doesn't restart playback on every map passed (0.5 s plus the
        // full opening hold read as a slow start, 2026-10-29); the first play skips most of its opening hold.
        private const float PlaySeconds = 12f, HoldSeconds = 1f, FadeSeconds = 0.5f, WaitSeconds = 0.15f, FirstHoldSeconds = 0.2f;
        private const int Plays = 2;

        /// <summary>
        /// Playback runs on a fixed timeline of <see cref="PlayFps"/> frames a second, rendered up to
        /// <see cref="Ahead"/> frames before they're due and shown exactly on time. Rendering each frame only
        /// once the last was up (0.2.1) showed the storms in uneven steps, a frame that took longer (new
        /// cells, a busy thread pool) holding the picture and then jumping ("hitches and stutters", 2026-10-29).
        /// </summary>
        private const int PlayFps = 24, Ahead = 10;
        private const int PlayFrames = (int)(PlaySeconds * PlayFps);   // steps per play; frames 0..PlayFrames
        private const int PerPlay = PlayFrames + 1;

        private sealed class Frame
        {
            public int Index, Generation;
            public double Time;
            public bool Rest;
            public Color32[] Pixels;
            public double RenderMs;
        }

        /// <summary>The Tarkov second to show: the selected slot's raid start (set by the forecast panel).</summary>
        public static double? TargetTime;

        /// <summary>The selected raid's length in raid minutes, for playback.</summary>
        public static int RaidMinutes = 40;

        /// <summary>Where playback is in the raid, 0..1, or null when the radar rests on the start.</summary>
        public static float? PlayProgress { get; private set; }

        private static string _slotKey;

        /// <summary>
        /// What to show when the forecast panel isn't up (no map selected yet, or Labs): the slot the player
        /// last chose, at the same "now" the panel uses, so selecting a map doesn't make the radar jump.
        /// </summary>
        private static double RestTime => BlockStart(TargetTime ?? Schedule.ScreenNow + (SlotMemory.RightSlot ? Schedule.HalfDaySeconds : 0));

        private double ShownTime => RestTime + _scrub * RaidMinutes * 60.0 * Schedule.Acceleration;

        private static RadarLayer _live;

        private RawImage _image;
        private Texture2D _texture;
        private Task<Frame> _pending;
        private float _nextRedraw;
        private double _lastTime = double.NaN;
        private float _lastOpacity = -1f;
        private bool _lastWinter;

        private float _queuedAt = -1f, _playStart = -1f, _restFrom = -1f;
        private bool _playing;
        private int _play;
        private float _scrub, _alpha = 1f;

        // Playback frames: buffers to render into, frames rendered and waiting, the next index to render, and
        // a generation that drops frames rendered for a slot or start the screen no longer shows.
        private readonly Stack<Color32[]> _free = new Stack<Color32[]>();
        private readonly Queue<Frame> _ready = new Queue<Frame>();
        private int _nextIndex, _generation;

        // Playback timings, for the verbose log.
        private int _frames, _late;
        private double _renderMs, _uploadMs, _worstUploadMs, _worstRenderMs;
        private readonly System.Diagnostics.Stopwatch _clock = new System.Diagnostics.Stopwatch();

        /// <summary>
        /// The forecast panel's slot, every refresh. A different map or slot (re)starts playback after a short
        /// wait, so clicking through maps doesn't keep restarting it; the same one again changes nothing.
        /// </summary>
        public static void SetTarget(double start, int raidMinutes, string locationId, bool rightSlot, bool play)
        {
            TargetTime = start;
            RaidMinutes = raidMinutes > 0 ? raidMinutes : 40;
            var key = locationId + (rightSlot ? " right" : " left");
            if (key == _slotKey) return;
            _slotKey = key;
            if (_live != null) _live.Queue(play);
        }

        private void Queue(bool play)
        {
            LogPlayback();
            _generation++;
            DropReady();
            _nextIndex = 0;
            _frames = _late = 0;
            _renderMs = _uploadMs = _worstUploadMs = _worstRenderMs = 0;
            _playing = false;
            _scrub = 0f;
            _restFrom = -1f;
            _alpha = 1f;
            PlayProgress = null;
            _queuedAt = play && ForecasterPlugin.PlayRadar.Value ? Time.unscaledTime + WaitSeconds : -1f;
            _nextRedraw = 0;
        }

        /// <summary>Advances playback: the position in the raid and how visible the picture is.</summary>
        private void Advance(float now)
        {
            if (_queuedAt >= 0f && now >= _queuedAt)
            {
                _queuedAt = -1f;
                _playing = true;
                _playStart = now - (HoldSeconds - FirstHoldSeconds);
            }

            if (_playing && (TargetTime == null || !ForecasterPlugin.PlayRadar.Value)) Queue(false);

            _alpha = 1f;
            if (_playing)
            {
                const float cycle = HoldSeconds + PlaySeconds + HoldSeconds + FadeSeconds;
                var c = now - _playStart;
                var n = (int)(c / cycle);
                var w = c - n * cycle;
                _play = n;
                if (n >= Plays)
                {
                    LogPlayback();
                    _playing = false;
                    _scrub = 0f;
                    _restFrom = now;
                    _generation++;
                    DropReady();
                    _lastTime = double.NaN; // draw the rest picture again
                }
                else if (w < HoldSeconds)
                {
                    _scrub = 0f;
                    if (n > 0 && w < FadeSeconds) _alpha = w / FadeSeconds;
                }
                else if (w < HoldSeconds + PlaySeconds) _scrub = (w - HoldSeconds) / PlaySeconds;
                else if (w < HoldSeconds + PlaySeconds + HoldSeconds) _scrub = 1f;
                else
                {
                    _scrub = 1f;
                    _alpha = 1f - (w - HoldSeconds - PlaySeconds - HoldSeconds) / FadeSeconds;
                }
            }

            if (!_playing && _restFrom >= 0f)
            {
                _alpha = Mathf.Clamp01((now - _restFrom) / FadeSeconds);
                if (now - _restFrom >= FadeSeconds) _restFrom = -1f;
            }

            PlayProgress = _playing ? _scrub : (float?)null;
        }

        /// <summary>
        /// The card and the raid read the weather once per <see cref="Field.Step"/> (14 in-game minutes, two
        /// real ones), at the step's start, and hold it for the step; the radar rests on that same moment, so
        /// what is over a map's button is exactly what its card opens with.
        /// </summary>
        private static double BlockStart(double t) => Math.Floor(t / Field.Step) * Field.Step;

        public static void Attach(Transform buttons, Transform paths)
        {
            try
            {
                if (buttons == null || buttons.parent == null) return;
                var parent = buttons.parent;
                var existing = parent.Find("ForecasterRadar");
                if (existing != null)
                {
                    _live = existing.GetComponent<RadarLayer>();
                    if (_live != null) _live.Restart();
                    return;
                }

                var go = new GameObject("ForecasterRadar", typeof(RectTransform));
                go.transform.SetParent(parent, false);
                var rt = (RectTransform)go.transform;
                var src = (RectTransform)buttons;
                rt.anchorMin = src.anchorMin;
                rt.anchorMax = src.anchorMax;
                rt.pivot = src.pivot;
                rt.anchoredPosition = src.anchoredPosition;
                rt.sizeDelta = src.sizeDelta;
                rt.localScale = src.localScale;
                rt.localRotation = src.localRotation;

                // Below the buttons and the route lines, above everything earlier (the map art).
                var index = buttons.GetSiblingIndex();
                if (paths != null && paths.parent == parent) index = Math.Min(index, paths.GetSiblingIndex());
                go.transform.SetSiblingIndex(index);

                go.AddComponent<LayoutElement>().ignoreLayout = true;

                // Its own canvas. Playback changes the picture's fade, the readout's text and its bar many
                // times a second, and inside the screen's canvas every change made Unity rebuild the whole
                // location screen's UI (frame-time spikes during playback, 2026-10-29). The key and the
                // readout are children, so they're in here too. Still drawn in its place in the hierarchy.
                go.AddComponent<Canvas>();

                var layer = go.AddComponent<RadarLayer>();
                layer._image = go.AddComponent<RawImage>();
                layer._image.raycastTarget = false;
                layer._image.enabled = false;
                _live = layer;

                if (ForecasterPlugin.VerboseLog.Value)
                {
                    ForecasterPlugin.Log.LogInfo($"Radar attached under '{parent.name}' at sibling {index}, size {src.rect.width:0}x{src.rect.height:0}.");
                }
            }
            catch (Exception ex)
            {
                ForecasterPlugin.Log.LogError("Could not attach the radar: " + ex);
            }
        }

        /// <summary>Redraw now (a setting changed).</summary>
        public static void Invalidate()
        {
            if (_live != null) _live._nextRedraw = 0;
        }

        /// <summary>
        /// The location screen is opening again. Hide the old picture (it can be hours old, from the last
        /// visit or another slot) until one for what the screen now shows has been drawn.
        /// </summary>
        private void Restart()
        {
            _image.enabled = false;
            _lastTime = double.NaN;
            _nextRedraw = 0;
            _slotKey = null; // the slot the screen opens on plays again
            Queue(false);
        }

        private void Update()
        {
            try
            {
                var now = Time.unscaledTime;
                Advance(now);
                _image.color = new Color(1f, 1f, 1f, _alpha);
                Collect();

                if (!ForecasterPlugin.ShowRadar.Value)
                {
                    _image.enabled = false;
                    return;
                }

                if (_playing || _queuedAt >= 0f) PlayFramesNow();
                else RestFrame(now);
            }
            catch (Exception ex)
            {
                ForecasterPlugin.Log.LogError("Radar update failed: " + ex);
                _nextRedraw = Time.unscaledTime + 30f;
                Queue(false);
            }
        }

        /// <summary>A finished render: a playback frame joins the queue; a rest picture is shown if still current.</summary>
        private void Collect()
        {
            if (_pending == null || !_pending.IsCompleted) return;
            var task = _pending;
            _pending = null;
            if (task.Status != TaskStatus.RanToCompletion)
            {
                ForecasterPlugin.Log.LogError("Radar render failed: " + task.Exception?.GetBaseException());
                _lastTime = double.NaN;
                return;
            }

            var frame = task.Result;
            _worstRenderMs = Math.Max(_worstRenderMs, frame.RenderMs);
            if (frame.Generation != _generation)
            {
                _free.Push(frame.Pixels);
                if (frame.Rest) _lastTime = double.NaN;
            }
            else if (frame.Rest)
            {
                // A picture drawn for a time the screen no longer shows (the slot changed while it rendered,
                // or it was started before the forecast said which slot) is dropped.
                if (Math.Abs(frame.Time - ShownTime) > 60 * Schedule.Acceleration) _lastTime = double.NaN;
                else Upload(frame.Pixels);
                _free.Push(frame.Pixels);
            }
            else
            {
                _renderMs += frame.RenderMs;
                _ready.Enqueue(frame);
            }
        }

        /// <summary>
        /// Playback: show the frame due now (the newest rendered one not past it), and keep rendering ahead.
        /// Also runs while playback waits to start, so the first frames are ready when it does.
        /// </summary>
        private void PlayFramesNow()
        {
            var due = _playing ? Math.Min(_play, Plays - 1) * PerPlay + Mathf.RoundToInt(_scrub * PlayFrames) : 0;

            Frame show = null;
            while (_ready.Count > 0 && _ready.Peek().Index <= due)
            {
                if (show != null) _free.Push(show.Pixels);
                show = _ready.Dequeue();
            }

            if (show != null)
            {
                if (show.Index < due) _late++;
                Upload(show.Pixels);
                _free.Push(show.Pixels);
            }

            if (_pending != null || !Schedule.Ready()) return;
            // Fallen behind: skip to what is due rather than draw frames that would never show.
            if (_nextIndex < due) _nextIndex = due;
            if (_nextIndex >= Plays * PerPlay || _nextIndex > due + Ahead) return;

            var index = _nextIndex++;
            var t = RestTime + (index % PerPlay) / (double)PlayFrames * RaidMinutes * 60.0 * Schedule.Acceleration;
            Render(t, index, false);
        }

        /// <summary>At rest: the raid's start, redrawn every few seconds as the clock moves or when a setting changes.</summary>
        private void RestFrame(float now)
        {
            var t = ShownTime;
            var opacity = ForecasterPlugin.RadarOpacity.Value;
            var changed = Math.Abs(t - _lastTime) > 60 * Schedule.Acceleration || opacity != _lastOpacity || Schedule.Winter != _lastWinter;
            if (_pending != null || (now < _nextRedraw && !changed)) return;
            if (!Schedule.Ready()) return;

            _nextRedraw = now + RedrawSeconds;
            _lastTime = t;
            _lastOpacity = opacity;
            _lastWinter = Schedule.Winter;
            Render(t, -1, true);
        }

        private void Render(double t, int index, bool rest)
        {
            var into = _free.Count > 0 ? _free.Pop() : new Color32[RadarRenderer.Width * RadarRenderer.Height];
            var frame = new Frame { Index = index, Generation = _generation, Time = t, Rest = rest, Pixels = into };
            var opacity = ForecasterPlugin.RadarOpacity.Value;
            var winter = Schedule.Winter;
            _pending = Task.Run(() =>
            {
                var started = System.Diagnostics.Stopwatch.GetTimestamp();
                RadarRenderer.Render(frame.Time, opacity, winter, frame.Pixels);
                frame.RenderMs = (System.Diagnostics.Stopwatch.GetTimestamp() - started) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                return frame;
            });
        }

        private void DropReady()
        {
            while (_ready.Count > 0) _free.Push(_ready.Dequeue().Pixels);
        }

        private void LogPlayback()
        {
            if (ForecasterPlugin.VerboseLog.Value && _frames > 0)
            {
                ForecasterPlugin.Log.LogInfo($"Radar playback: {_frames} frames shown ({_late} late), render {_renderMs / _frames:0.0} ms avg / {_worstRenderMs:0.0} ms worst (worker), " +
                                             $"upload {_uploadMs / _frames:0.00} ms avg / {_worstUploadMs:0.00} ms worst (main thread).");
            }

            _frames = _late = 0;
            _renderMs = _uploadMs = _worstUploadMs = _worstRenderMs = 0;
        }

        private void Upload(Color32[] pixels)
        {
            if (_texture == null)
            {
                _texture = new Texture2D(RadarRenderer.Width, RadarRenderer.Height, TextureFormat.RGBA32, false)
                {
                    filterMode = FilterMode.Bilinear,
                    wrapMode = TextureWrapMode.Clamp,
                };
                _image.texture = _texture;
            }

            _clock.Restart();
            _texture.SetPixels32(pixels);
            _texture.Apply(false);
            _image.enabled = ForecasterPlugin.ShowRadar.Value;
            if (_playing)
            {
                var ms = _clock.Elapsed.TotalMilliseconds;
                _frames++;
                _uploadMs += ms;
                _worstUploadMs = Math.Max(_worstUploadMs, ms);
            }
        }

        private void OnDestroy()
        {
            if (_texture != null) Destroy(_texture);
            if (_live == this) _live = null;
        }
    }
}
