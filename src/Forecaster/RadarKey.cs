using System;
using EFT.UI.Matchmaker;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Forecaster
{
    /// <summary>
    /// The radar's key, in the forecast box's right half under the playback slider (Joel's layout,
    /// 2026-10-29). With no box (no map picked, Labs, Factory) it waits, hidden, on the radar's bottom-right
    /// corner. In the "SELECT TIME PHASE:" label's style:
    ///
    ///   RAIN ● ● ● ●    FOG ● ● ● ●    CLOUD ● ●
    ///
    /// Rain's dots are the radar's colours for light rain, rain, heavy rain and the heaviest cores; it
    /// reads SNOW, in the snow colours, in winter. Fog: mist, fog, dense fog, thick fog. Cloud: partly cloudy, overcast.
    /// Only weather the game has; the colours come from RadarRenderer, so the key can't drift from the radar.
    ///
    /// Above it, a readout as wide as the key ("RAID +25 MIN" over a thin progress bar) follows the radar's
    /// playback (RadarLayer.PlayProgress) and reads "RAID START" between plays. It used to ring the selected map too, but
    /// the ring sat beside the map's name plate, not around it, and looked out of place (Joel, 2026-10-29).
    /// </summary>
    internal sealed class RadarKey : MonoBehaviour
    {
        private const string Name = "ForecasterRadarKey";
        private const float Margin = 12f;

        /// <summary>The time bar's colour as drawn, as the forecast box uses.</summary>
        private static readonly Color Fill = new Color(0.051f, 0.059f, 0.059f, 0.9f);

        private static Sprite _dot;

        private TextMeshProUGUI _fallingLabel;
        private readonly Image[] _fallingDots = new Image[4];
        private bool? _winter;

        private CanvasGroup _group;

        private CanvasGroup _readout;
        private TextMeshProUGUI _readoutText;
        private RectTransform _readoutFill;
        private int _lastMinute = -1;

        private RectTransform _radarRect, _keyRect, _readoutRect;
        private float _readoutOffset, _readoutWidth;

        // Docked in the forecast box (its right half), or on the radar's corner when there's no box.
        private static RadarKey _live;
        private RectTransform _dock;
        private Image _keyFill, _readoutBack;
        private ContentSizeFitter _keyFitter, _readoutFitter;
        private HorizontalLayoutGroup _keyRow;
        private VerticalLayoutGroup _readoutColumn;
        private RectOffset _keyPadding, _readoutPadding;
        private static readonly RectOffset NoPadding = new RectOffset(0, 0, 0, 0);
        private float _nextPlace;

        /// <summary>Put the key on the radar (once), which covers exactly the buttons container.</summary>
        public static void Attach(Transform radar, MatchMakerSelectionLocationScreen screen)
        {
            try
            {
                // Once per radar (the strip may be docked in the forecast box, so not found under the radar).
                if (radar == null || (_live != null && _live._radarRect == radar) || radar.Find(Name) != null) return;

                var style = Style(screen);
                if (style == null)
                {
                    ForecasterPlugin.Log.LogWarning("Radar key: no text on the location screen to take a style from.");
                    return;
                }

                var size = Mathf.Clamp(style.fontSize, 11f, 22f);
                var go = new GameObject(Name, typeof(RectTransform));
                go.transform.SetParent(radar, false);
                go.AddComponent<LayoutElement>().ignoreLayout = true;

                // The map's bottom-right corner, in from the edge.
                var rt = (RectTransform)go.transform;
                rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(1f, 0f);
                rt.anchoredPosition = new Vector2(-Margin, Margin);

                var fill = go.AddComponent<Image>();
                fill.color = Fill;
                fill.raycastTarget = false;
                var row = go.AddComponent<HorizontalLayoutGroup>();
                var pad = Mathf.RoundToInt(size * 0.6f);
                row.padding = new RectOffset(pad, pad, Mathf.RoundToInt(pad * 0.6f), Mathf.RoundToInt(pad * 0.6f));
                row.spacing = size * 0.35f;
                row.childAlignment = TextAnchor.MiddleLeft;
                row.childControlWidth = row.childControlHeight = true;
                row.childForceExpandWidth = row.childForceExpandHeight = false;
                var fitter = go.AddComponent<ContentSizeFitter>();
                fitter.horizontalFit = fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

                // The component sits on its own object that stays on the radar: the strip itself moves into
                // the forecast box, which hides with it, and a hidden object gets no Update to move back out.
                var driver = new GameObject("ForecasterRadarKeyDriver", typeof(RectTransform));
                driver.transform.SetParent(radar, false);
                driver.AddComponent<LayoutElement>().ignoreLayout = true;
                var key = driver.AddComponent<RadarKey>();
                _live = key;
                key._keyFill = fill;
                key._keyFitter = fitter;
                key._keyRow = row;
                key._keyPadding = row.padding;
                key._group = go.AddComponent<CanvasGroup>();
                key._group.blocksRaycasts = false;
                key._group.interactable = false;
                key._fallingLabel = Label(go.transform, style, size, "RAIN");
                for (var i = 0; i < key._fallingDots.Length; i++) key._fallingDots[i] = Dot(go.transform, size, Color.white);

                Gap(go.transform, size);
                Label(go.transform, style, size, "FOG");
                foreach (var colour in RadarRenderer.FogColours) Dot(go.transform, size, Shade(colour, 1));

                Gap(go.transform, size);
                Label(go.transform, style, size, "CLOUD");
                Dot(go.transform, size, Shade(RadarRenderer.CloudColour, 0.55));
                Dot(go.transform, size, Shade(RadarRenderer.CloudColour, 1));

                key.Apply();
                key._radarRect = (RectTransform)radar;
                key._keyRect = rt;
                key.AddPlayback(radar, style, size, pad, rt);
                if (ForecasterPlugin.VerboseLog.Value) ForecasterPlugin.Log.LogInfo($"Radar key added, text '{style.name}' at {size:0.#}.");
            }
            catch (Exception ex)
            {
                ForecasterPlugin.Log.LogError("Could not add the radar key: " + ex);
            }
        }

        /// <summary>The playback readout above the key, as wide as it.</summary>
        private void AddPlayback(Transform radar, TextMeshProUGUI style, float size, int pad, RectTransform keyRect)
        {
            LayoutRebuilder.ForceRebuildLayoutImmediate(keyRect);
            var width = keyRect.rect.width > size * 6f ? keyRect.rect.width : size * 12f;
            var keyHeight = size * 1.3f + 2 * Mathf.RoundToInt(pad * 0.6f);

            var go = new GameObject("ForecasterRadarPlayback", typeof(RectTransform));
            go.transform.SetParent(radar, false);
            go.AddComponent<LayoutElement>().ignoreLayout = true;
            var rt = (RectTransform)go.transform;
            rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(1f, 0f);
            _readoutOffset = keyHeight + 4f;
            _readoutRect = rt;
            rt.anchoredPosition = new Vector2(-Margin, Margin + _readoutOffset);
            rt.sizeDelta = new Vector2(width, 0f);
            _readoutWidth = width;
            var fill = go.AddComponent<Image>();
            fill.color = Fill;
            fill.raycastTarget = false;
            _readoutBack = fill;
            var column = go.AddComponent<VerticalLayoutGroup>();
            column.padding = new RectOffset(pad, pad, Mathf.RoundToInt(pad * 0.6f), Mathf.RoundToInt(pad * 0.8f));
            column.spacing = size * 0.3f;
            column.childControlWidth = column.childControlHeight = true;
            column.childForceExpandWidth = true;
            column.childForceExpandHeight = false;
            _readoutColumn = column;
            _readoutPadding = column.padding;
            _readoutFitter = go.AddComponent<ContentSizeFitter>();
            _readoutFitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            _readout = go.AddComponent<CanvasGroup>();
            _readout.blocksRaycasts = false;
            _readout.interactable = false;
            _readout.alpha = 0f;

            _readoutText = Label(go.transform, style, size, "RAID +0 MIN");

            var bar = new GameObject("Bar", typeof(RectTransform));
            bar.transform.SetParent(go.transform, false);
            var track = bar.AddComponent<Image>();
            track.color = new Color(style.color.r, style.color.g, style.color.b, 0.18f);
            track.raycastTarget = false;
            var barLayout = bar.AddComponent<LayoutElement>();
            barLayout.minHeight = barLayout.preferredHeight = 3f;
            var done = new GameObject("Done", typeof(RectTransform));
            done.transform.SetParent(bar.transform, false);
            var doneImage = done.AddComponent<Image>();
            doneImage.color = style.color;
            doneImage.raycastTarget = false;
            _readoutFill = (RectTransform)done.transform;
            _readoutFill.anchorMin = Vector2.zero;
            _readoutFill.anchorMax = new Vector2(0f, 1f);
            _readoutFill.offsetMin = _readoutFill.offsetMax = Vector2.zero;
        }

        private void Update()
        {
            if (Schedule.Winter != _winter) Apply();
            if (Time.unscaledTime >= _nextPlace)
            {
                _nextPlace = Time.unscaledTime + 0.2f;
                Place();
            }

            // Only in the forecast box: with no map picked (or Labs, Factory) the key on its own over the
            // time bar was out of place (Joel, 2026-10-29).
            _group.alpha = _dock != null && ForecasterPlugin.ShowRadar.Value && ForecasterPlugin.ShowRadarKey.Value ? 1f : 0f;

            if (_readout == null) return;
            var progress = ForecasterPlugin.ShowRadar.Value ? RadarLayer.PlayProgress : null;
            // The slider always shows in the box (at the raid's start between plays), never on the map.
            var shown = _dock != null && ForecasterPlugin.ShowRadar.Value;
            _readout.alpha = shown ? 1f : 0f;
            if (shown)
            {
                var minute = progress.HasValue ? Mathf.FloorToInt(progress.Value * RadarLayer.RaidMinutes) : -2;
                if (minute != _lastMinute)
                {
                    _lastMinute = minute;
                    _readoutText.text = minute < 0 ? "RAID START" : "RAID +" + minute + " MIN";
                }

                _readoutFill.anchorMax = new Vector2(progress ?? 0f, 1f);
            }
        }

        /// <summary>Follow the forecast box: into it when it shows, back onto the radar when it goes.</summary>
        private void Place()
        {
            if (_radarRect == null || _keyRect == null) return;
            var slot = ForecastPanel.KeySlot();
            if (slot != _dock) Dock(slot);
        }

        private void OnDestroy()
        {
            if (_live == this) _live = null;
        }

        /// <summary>The forecast box is going away: take the slider and key back out of it first.</summary>
        public static void Undock(RectTransform slot)
        {
            if (_live != null && slot != null && _live._dock == slot) _live.Dock(null);
        }

        /// <summary>
        /// Into the forecast box's right half (slider over key, no backgrounds of their own, the box's layout
        /// places them), or back onto the radar's corner (null).
        /// </summary>
        private void Dock(RectTransform slot)
        {
            _dock = slot;
            var docked = slot != null;
            var parent = docked ? (Transform)slot : _radarRect;
            _readoutRect.SetParent(parent, false);
            _keyRect.SetParent(parent, false);
            foreach (var rt in new[] { _readoutRect, _keyRect })
            {
                rt.GetComponent<LayoutElement>().ignoreLayout = !docked;
                rt.localScale = Vector3.one;
                if (!docked)
                {
                    rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(1f, 0f);
                }
            }

            _keyFitter.enabled = _readoutFitter.enabled = !docked;
            _keyFill.color = _readoutBack.color = docked ? Color.clear : Fill;
            _keyRow.padding = docked ? NoPadding : _keyPadding;
            _readoutColumn.padding = docked ? NoPadding : _readoutPadding;
            if (!docked)
            {
                _keyRect.anchoredPosition = new Vector2(-Margin, Margin);
                _readoutRect.anchoredPosition = new Vector2(-Margin, Margin + _readoutOffset);
                _readoutRect.sizeDelta = new Vector2(_readoutWidth, 0f);
            }

            _lastMinute = -1;
            if (ForecasterPlugin.VerboseLog.Value) ForecasterPlugin.Log.LogInfo(docked ? "Radar key: in the forecast box." : "Radar key: on the map corner.");
        }

        /// <summary>Rain or snow, as the radar draws it now.</summary>
        private void Apply()
        {
            _winter = Schedule.Winter;
            _fallingLabel.text = Schedule.Winter ? "SNOW" : "RAIN";
            var colours = RadarRenderer.Colours(Schedule.Winter);
            for (var i = 0; i < _fallingDots.Length; i++) _fallingDots[i].color = Shade(colours[i], 1);
        }

        /// <summary>
        /// The conditions panel's small caps label if it's there; else the screen's smallest text, which is
        /// that kind of label too.
        /// </summary>
        private static TextMeshProUGUI Style(MatchMakerSelectionLocationScreen screen)
        {
            var panel = screen.GetComponentInChildren<LocationConditionsPanel>(true);
            var label = panel != null ? ForecastView.FindLabel(panel) : null;
            if (label != null) return label;

            TextMeshProUGUI best = null;
            foreach (var text in screen.GetComponentsInChildren<TextMeshProUGUI>(true))
            {
                if (text.fontSize >= 8f && (best == null || text.fontSize < best.fontSize)) best = text;
            }

            return best;
        }

        private static TextMeshProUGUI Label(Transform parent, TextMeshProUGUI style, float size, string text)
        {
            var go = new GameObject("Label", typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var label = go.AddComponent<TextMeshProUGUI>();
            label.font = style.font;
            label.fontSharedMaterial = style.fontSharedMaterial;
            label.fontSize = size;
            label.color = style.color;
            label.characterSpacing = style.characterSpacing;
            label.enableWordWrapping = false;
            label.alignment = TextAlignmentOptions.MidlineLeft;
            label.raycastTarget = false;
            label.text = text;
            go.AddComponent<LayoutElement>().minHeight = size * 1.3f;
            return label;
        }

        private static Image Dot(Transform parent, float size, Color colour)
        {
            var go = new GameObject("Dot", typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var dot = go.AddComponent<Image>();
            dot.sprite = DotSprite();
            dot.color = colour;
            dot.raycastTarget = false;
            var layout = go.AddComponent<LayoutElement>();
            layout.minWidth = layout.preferredWidth = layout.minHeight = layout.preferredHeight = Mathf.Round(size * 0.8f);
            return dot;
        }

        private static void Gap(Transform parent, float size)
        {
            var go = new GameObject("Gap", typeof(RectTransform));
            go.transform.SetParent(parent, false);
            go.AddComponent<LayoutElement>().minWidth = size * 0.9f;
        }

        /// <summary>
        /// A radar colour as a solid dot. The radar draws it as a wash over the map, so full strength would
        /// overstate it; <paramref name="strength"/> blends it towards the key's dark background.
        /// </summary>
        private static Color Shade(double[] rgb, double strength)
        {
            var s = (float)strength;
            return new Color(
                Mathf.Lerp(Fill.r, (float)rgb[0] / 255f, s),
                Mathf.Lerp(Fill.g, (float)rgb[1] / 255f, s),
                Mathf.Lerp(Fill.b, (float)rgb[2] / 255f, s),
                1f);
        }

        /// <summary>A white antialiased disc, made once.</summary>
        private static Sprite DotSprite()
        {
            if (_dot != null) return _dot;
            const int n = 64;
            var texture = new Texture2D(n, n, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
            var pixels = new Color32[n * n];
            for (var j = 0; j < n; j++)
            {
                for (var i = 0; i < n; i++)
                {
                    float dx = i + 0.5f - n / 2f, dy = j + 0.5f - n / 2f;
                    var a = Mathf.Clamp01(n / 2f - 1f - Mathf.Sqrt(dx * dx + dy * dy) + 0.5f);
                    pixels[j * n + i] = new Color32(255, 255, 255, (byte)(a * 255));
                }
            }

            texture.SetPixels32(pixels);
            texture.Apply(false);
            _dot = Sprite.Create(texture, new Rect(0, 0, n, n), new Vector2(0.5f, 0.5f), 100f);
            return _dot;
        }
    }
}
