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
    /// </summary>
    internal sealed class RadarLayer : MonoBehaviour
    {
        private const float RedrawSeconds = 5f;

        /// <summary>The Tarkov second to show: the selected slot's raid start (set by the forecast panel).</summary>
        public static double? TargetTime;

        private static RadarLayer _live;

        private RawImage _image;
        private Texture2D _texture;
        private Task<Color32[]> _pending;
        private float _nextRedraw;
        private double _lastTime = double.NaN;
        private float _lastOpacity = -1f;
        private bool _lastWinter;

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

        private void Update()
        {
            try
            {
                if (_pending != null && _pending.IsCompleted)
                {
                    var task = _pending;
                    _pending = null;
                    if (task.Status == TaskStatus.RanToCompletion) Upload(task.Result);
                    else ForecasterPlugin.Log.LogError("Radar render failed: " + task.Exception?.GetBaseException());
                }

                if (!ForecasterPlugin.ShowRadar.Value)
                {
                    _image.enabled = false;
                    return;
                }

                var t = TargetTime ?? Schedule.TarkovNow;
                var opacity = ForecasterPlugin.RadarOpacity.Value;
                var changed = Math.Abs(t - _lastTime) > 60 * Schedule.Acceleration || opacity != _lastOpacity || Schedule.Winter != _lastWinter;
                if (_pending != null || (Time.unscaledTime < _nextRedraw && !changed)) return;
                if (!Schedule.Ready()) return;

                _nextRedraw = Time.unscaledTime + RedrawSeconds;
                _lastTime = t;
                _lastOpacity = opacity;
                _lastWinter = Schedule.Winter;
                var winter = Schedule.Winter;
                _pending = Task.Run(() => RadarRenderer.Render(t, opacity, winter));
            }
            catch (Exception ex)
            {
                ForecasterPlugin.Log.LogError("Radar update failed: " + ex);
                _nextRedraw = Time.unscaledTime + 30f;
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
