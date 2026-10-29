using System;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.UI;

namespace SPTForecaster
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
        private const float PlaySeconds = 12f, HoldSeconds = 1f, FadeSeconds = 0.5f, WaitSeconds = 0.5f;
        private const int Plays = 2;
        private const float FrameSeconds = 1f / 30f;

        /// <summary>The Tarkov second to show: the selected slot's raid start (set by the forecast panel).</summary>
        public static double? TargetTime;

        /// <summary>The selected raid's length in raid minutes and its location, for playback.</summary>
        public static int RaidMinutes = 40;
        public static string PlayLocation { get; private set; }

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
        private Task<Color32[]> _pending;
        private float _nextRedraw;
        private double _lastTime = double.NaN;
        private double _pendingTime;
        private float _lastOpacity = -1f;
        private bool _lastWinter;

        private float _queuedAt = -1f, _playStart = -1f, _restFrom = -1f, _nextFrame;
        private bool _playing;
        private float _scrub, _alpha = 1f;

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
            PlayLocation = play ? locationId : null;
            if (_live != null) _live.Queue(play);
        }

        private void Queue(bool play)
        {
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
                _playStart = now;
            }

            if (_playing && (TargetTime == null || !ForecasterPlugin.PlayRadar.Value)) Queue(false);

            _alpha = 1f;
            if (_playing)
            {
                const float cycle = HoldSeconds + PlaySeconds + HoldSeconds + FadeSeconds;
                var c = now - _playStart;
                var n = (int)(c / cycle);
                var w = c - n * cycle;
                if (n >= Plays)
                {
                    _playing = false;
                    _scrub = 0f;
                    _restFrom = now;
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
                var existing = parent.Find("SPTForecasterRadar");
                if (existing != null)
                {
                    _live = existing.GetComponent<RadarLayer>();
                    if (_live != null) _live.Restart();
                    return;
                }

                var go = new GameObject("SPTForecasterRadar", typeof(RectTransform));
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
                var t = ShownTime;
                if (_pending != null && _pending.IsCompleted)
                {
                    var task = _pending;
                    _pending = null;
                    // A picture drawn for a time the screen no longer shows (the slot or the screen changed
                    // while it rendered, or it was started before the forecast said which slot) is dropped.
                    if (Math.Abs(_pendingTime - t) > 60 * Schedule.Acceleration) _lastTime = double.NaN;
                    else if (task.Status == TaskStatus.RanToCompletion) Upload(task.Result);
                    else ForecasterPlugin.Log.LogError("Radar render failed: " + task.Exception?.GetBaseException());
                }

                if (!ForecasterPlugin.ShowRadar.Value)
                {
                    _image.enabled = false;
                    return;
                }

                var opacity = ForecasterPlugin.RadarOpacity.Value;
                var changed = Math.Abs(t - _lastTime) > 60 * Schedule.Acceleration || opacity != _lastOpacity || Schedule.Winter != _lastWinter;
                // While playing, a new frame as soon as the last is up (at most 30 a second).
                if (_playing && t != _lastTime && now >= _nextFrame) changed = true;
                if (_pending != null || (now < _nextRedraw && !changed)) return;
                if (!Schedule.Ready()) return;

                _nextRedraw = now + RedrawSeconds;
                _nextFrame = now + FrameSeconds;
                _lastTime = t;
                _pendingTime = t;
                _lastOpacity = opacity;
                _lastWinter = Schedule.Winter;
                var winter = Schedule.Winter;
                _pending = Task.Run(() => RadarRenderer.Render(t, opacity, winter));
            }
            catch (Exception ex)
            {
                ForecasterPlugin.Log.LogError("Radar update failed: " + ex);
                _nextRedraw = Time.unscaledTime + 30f;
                Queue(false);
            }
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

            _texture.SetPixels32(pixels);
            _texture.Apply(false);
            _image.enabled = ForecasterPlugin.ShowRadar.Value;
        }

        private void OnDestroy()
        {
            if (_texture != null) Destroy(_texture);
            if (_live == this) _live = null;
        }
    }
}
