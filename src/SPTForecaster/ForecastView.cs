using System;
using EFT.UI.Matchmaker;
using EFT.Utilities;
using EFT.Weather;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace SPTForecaster
{
    /// <summary>
    /// The forecast box. It sits on top of the conditions panel's time bar, exactly as wide, always the same
    /// height, and is drawn as a second copy of that bar:
    ///
    ///   FORECAST FOR THE 20:30 RAID:
    ///   ☀  Clear                         ☁ Clouds building in ~25 min
    ///      Calm                          ☁ Fog arriving in ~70 min
    ///
    /// The bar's own colour and striped image behind it, its "SELECT TIME PHASE:" label style and inset for
    /// the heading, the clock's font and colour for the weather, and the game's weather icons
    /// (UI/WeatherIcons, picked by WeatherNode.GetWeatherTypeByNode). No borders, bands or extra marks.
    /// </summary>
    internal sealed class ForecastView
    {
        private const string RootName = "SPTForecaster";
        private const float FaintAlpha = 0.6f;

        /// <summary>Weather text size as a fraction of the time slot's clock, and the gap above the bar.</summary>
        private const float TextScale = 0.46f;
        private const float Gap = 4f;

        /// <summary>The time bar's colour as drawn (13, 15, 15), so the two read as one set.</summary>
        private static readonly Color Fill = new Color(0.051f, 0.059f, 0.059f, 0.97f);

        private GameObject _root;
        private TextMeshProUGUI _heading;
        private Image _headIcon;
        private TextMeshProUGUI _headText, _wind;
        private readonly Image[] _changeIcons = new Image[2];
        private readonly TextMeshProUGUI[] _changeTexts = new TextMeshProUGUI[2];

        public static ForecastView Create(LocationConditionsPanel panel)
        {
            var clock = panel._currentPhaseTime;
            var bar = FindBar(panel);
            var host = bar != null && !Clips(bar.transform) ? bar.rectTransform : (RectTransform)panel.transform;
            var label = FindLabel(panel);
            var inset = Inset(host, label);
            var small = label != null ? label : clock;
            var smallSize = label != null ? label.fontSize : clock.fontSize * 0.32f;

            var s = clock.fontSize * TextScale;
            var pad = Mathf.RoundToInt(inset);

            var view = new ForecastView();
            var root = new GameObject(RootName, typeof(RectTransform));
            root.transform.SetParent(host, false);
            view._root = root;

            // Ignore any layout the bar sits in; sit on top of it, exactly as wide, growing upward.
            root.AddComponent<LayoutElement>().ignoreLayout = true;
            var rt = (RectTransform)root.transform;
            rt.anchorMin = new Vector2(0f, 1f);
            rt.anchorMax = new Vector2(1f, 1f);
            rt.pivot = new Vector2(0.5f, 0f);
            rt.anchoredPosition = new Vector2(0f, Gap);
            rt.sizeDelta = Vector2.zero;

            var fill = root.AddComponent<Image>();
            fill.raycastTarget = false;
            fill.color = Fill;
            if (bar != null && bar.sprite != null)
            {
                // The bar's own image is its faint diagonal stripes; over the fill it matches the bar.
                var texture = Decoration(root.transform, "Texture", Vector2.zero, Vector2.one, Vector2.zero);
                texture.sprite = bar.sprite;
                texture.type = bar.type;
                texture.material = bar.material;
                texture.color = bar.color;
                texture.fillCenter = bar.fillCenter;
                texture.pixelsPerUnitMultiplier = bar.pixelsPerUnitMultiplier;
            }

            var column = root.AddComponent<VerticalLayoutGroup>();
            column.padding = new RectOffset(pad, pad, Mathf.RoundToInt(pad * 0.85f), Mathf.RoundToInt(pad * 0.85f));
            column.spacing = s * 0.35f;
            column.childAlignment = TextAnchor.UpperLeft;
            column.childControlWidth = true;
            column.childControlHeight = true;
            column.childForceExpandWidth = true;
            column.childForceExpandHeight = false;
            root.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            var group = root.AddComponent<CanvasGroup>();
            group.blocksRaycasts = false;
            group.interactable = false;

            view._heading = Text(root.transform, "Heading", small, smallSize, false, smallSize * 1.3f);
            view._heading.characterSpacing = label != null ? label.characterSpacing : 0f;

            // The opening weather, big, on the left; the changes on the right.
            var rowHeight = s * 1.3f;
            var main = Row(root.transform, "Main", pad, rowHeight * 2f);

            var left = Row(main.transform, "Now", s * 0.55f, rowHeight * 2f);
            Half(left);
            view._headIcon = Icon(left.transform, s * 2.3f, panel._weatherIcon);
            var words = Stack(left.transform, "Words");
            view._headText = Text(words.transform, "Headline", clock, s * 1.4f, true, rowHeight * 1.15f);
            view._wind = Text(words.transform, "Wind", clock, s * 0.9f, false, rowHeight * 0.85f);
            view._wind.alpha = FaintAlpha;

            var right = Stack(main.transform, "Changes");
            Half(right);
            for (var i = 0; i < 2; i++)
            {
                var change = Row(right.transform, "Change" + i, s * 0.45f, rowHeight);
                view._changeIcons[i] = Icon(change.transform, s * 1.2f, panel._weatherIcon);
                view._changeTexts[i] = Text(change.transform, "Text", clock, s, false, rowHeight);
            }

            if (ForecasterPlugin.VerboseLog.Value)
            {
                ForecasterPlugin.Log.LogInfo($"Forecast box on '{host.name}' ({(bar != null ? "bar found" : "no bar found")}), " +
                                             $"label '{(label != null ? label.name : "none")}', inset {inset:0}, text {s:0.#}.");
            }

            return view;
        }

        public void Show(ForecastResult forecast, string heading)
        {
            if (_root == null) return;
            _root.SetActive(true);

            _heading.text = heading.ToUpperInvariant();
            SetIcon(_headIcon, forecast.Headline);
            _headText.text = forecast.Headline?.Text ?? "";
            _wind.text = forecast.Wind ?? "";

            for (var i = 0; i < 2; i++)
            {
                var change = i < forecast.Changes.Count ? forecast.Changes[i] : null;
                if (change == null && i == 0 && forecast.Wind != null)
                {
                    change = new ForecastLine { Text = "No change expected", Faint = true };
                }

                _changeTexts[i].text = change?.Text ?? "";
                _changeTexts[i].alpha = change != null && change.Faint ? FaintAlpha : 1f;
                SetIcon(_changeIcons[i], change);
            }
        }

        /// <summary>The same box with one line in place of the weather (a map without a forecast, say).</summary>
        public void ShowMessage(string heading, string message)
        {
            Show(new ForecastResult { Headline = new ForecastLine { Text = message } }, heading);
        }

        /// <summary>An icon that isn't there still keeps its place, so the lines beside it stay lined up.</summary>
        private static void SetIcon(Image icon, ForecastLine line)
        {
            var type = line?.Icon ?? EWeatherType.None;
            // Our glyph where the game has no icon (snow, blizzard, mist, dense/thick fog, night skies).
            var sprite = line?.Glyph != null ? Glyphs.Get(line.Glyph) : null;
            if (sprite == null && type != EWeatherType.None) sprite = ResourcesCache.Pop<Sprite>("UI/WeatherIcons/" + type);
            icon.sprite = sprite;
            icon.enabled = sprite != null;
        }

        // ---- building blocks -----------------------------------------------------------------------

        private static GameObject Row(Transform parent, string name, float spacing, float height)
        {
            var row = new GameObject(name, typeof(RectTransform));
            row.transform.SetParent(parent, false);
            var layout = row.AddComponent<HorizontalLayoutGroup>();
            layout.childAlignment = TextAnchor.MiddleLeft;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = false;
            layout.childForceExpandHeight = false;
            layout.spacing = spacing;
            var size = row.AddComponent<LayoutElement>();
            size.minHeight = size.preferredHeight = height;
            return row;
        }

        private static GameObject Stack(Transform parent, string name)
        {
            var stack = new GameObject(name, typeof(RectTransform));
            stack.transform.SetParent(parent, false);
            var layout = stack.AddComponent<VerticalLayoutGroup>();
            layout.childAlignment = TextAnchor.MiddleLeft;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;
            var size = stack.AddComponent<LayoutElement>();
            size.minWidth = 0f;
            size.flexibleWidth = 1f;
            return stack;
        }

        /// <summary>Half the row, whatever its content wants.</summary>
        private static void Half(GameObject go)
        {
            var size = go.GetComponent<LayoutElement>() ?? go.AddComponent<LayoutElement>();
            size.minWidth = 0f;
            size.preferredWidth = 0f;
            size.flexibleWidth = 1f;
        }

        private static Image Icon(Transform parent, float size, Image source)
        {
            var go = new GameObject("Icon", typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var icon = go.AddComponent<Image>();
            icon.preserveAspect = true;
            icon.raycastTarget = false;
            if (source != null) icon.color = source.color;
            var layout = go.AddComponent<LayoutElement>();
            layout.preferredWidth = layout.minWidth = size;
            layout.preferredHeight = layout.minHeight = size;
            return icon;
        }

        /// <summary>
        /// A line of text that takes what width its row has left, shrinks a little if that is too little and
        /// then ends in an ellipsis. It keeps its height when empty, so the box never changes size.
        /// </summary>
        private static TextMeshProUGUI Text(Transform parent, string name, TextMeshProUGUI style, float size, bool bold, float height)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var text = go.AddComponent<TextMeshProUGUI>();
            text.font = style.font;
            text.fontSharedMaterial = style.fontSharedMaterial;
            text.fontSize = size;
            text.enableAutoSizing = true;
            text.fontSizeMin = size * 0.8f;
            text.fontSizeMax = size;
            text.color = style.color;
            text.fontStyle = bold ? FontStyles.Bold : FontStyles.Normal;
            text.enableWordWrapping = false;
            text.overflowMode = TextOverflowModes.Ellipsis;
            text.alignment = TextAlignmentOptions.MidlineLeft;
            text.raycastTarget = false;
            var layout = go.AddComponent<LayoutElement>();
            layout.minWidth = 0f;
            layout.flexibleWidth = 1f;
            layout.minHeight = layout.preferredHeight = height;
            return text;
        }

        /// <summary>A plain image placed by anchors and left out of any layout.</summary>
        private static Image Decoration(Transform parent, string name, Vector2 anchorMin, Vector2 anchorMax, Vector2 size)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            go.AddComponent<LayoutElement>().ignoreLayout = true;
            var rt = (RectTransform)go.transform;
            rt.anchorMin = anchorMin;
            rt.anchorMax = anchorMax;
            rt.pivot = new Vector2(0.5f, 1f);
            rt.anchoredPosition = Vector2.zero;
            rt.sizeDelta = size;
            var image = go.AddComponent<Image>();
            image.raycastTarget = false;
            return image;
        }

        // ---- finding the bar's parts ---------------------------------------------------------------

        /// <summary>The time bar's background: the biggest image in the panel that isn't the weather icon or part of a toggle.</summary>
        private static Image FindBar(LocationConditionsPanel panel)
        {
            Image best = null;
            var bestArea = 0f;
            foreach (var image in panel.GetComponentsInChildren<Image>(false))
            {
                if (image == panel._weatherIcon || image.GetComponentInParent<Toggle>() != null || Ours(image.transform)) continue;
                var area = Area(image.rectTransform);
                if (area > bestArea)
                {
                    best = image;
                    bestArea = area;
                }
            }

            return best != null && bestArea >= Area((RectTransform)panel.transform) * 0.5f ? best : null;
        }

        /// <summary>
        /// The panel's small caps label "SELECT TIME PHASE:": its smallest text that isn't a clock, the
        /// leftmost of a tie ("CURRENT WEATHER CONDITIONS:" is the same style, further right).
        /// </summary>
        internal static TextMeshProUGUI FindLabel(LocationConditionsPanel panel)
        {
            TextMeshProUGUI best = null;
            foreach (var text in panel.GetComponentsInChildren<TextMeshProUGUI>(false))
            {
                if (text == panel._currentPhaseTime || text == panel._nextPhaseTime || Ours(text.transform)) continue;
                if (best == null || text.fontSize < best.fontSize - 0.5f
                    || (text.fontSize < best.fontSize + 0.5f && text.rectTransform.position.x < best.rectTransform.position.x))
                {
                    best = text;
                }
            }

            return best;
        }

        /// <summary>How far the label sits in from the bar's left edge, so the box uses the same margin.</summary>
        private static float Inset(RectTransform host, TextMeshProUGUI label)
        {
            if (label == null) return 12f;
            var corners = new Vector3[4];
            label.rectTransform.GetWorldCorners(corners);
            var x = host.InverseTransformPoint(corners[0]).x - host.rect.xMin;
            return x >= 2f && x <= 80f ? x : 12f;
        }

        private static float Area(RectTransform rt)
        {
            var scale = rt.lossyScale;
            return Mathf.Abs(rt.rect.width * scale.x * rt.rect.height * scale.y);
        }

        private static bool Clips(Transform t) => t.GetComponent<Mask>() != null || t.GetComponent<RectMask2D>() != null;

        /// <summary>Part of a forecast box (an old one waiting to be destroyed at the end of the frame, say).</summary>
        private static bool Ours(Transform t)
        {
            for (; t != null; t = t.parent)
            {
                if (t.name == RootName) return true;
            }

            return false;
        }

        public void Hide()
        {
            if (_root != null) _root.SetActive(false);
        }

        public void Destroy()
        {
            if (_root != null) UnityEngine.Object.Destroy(_root);
            _root = null;
        }
    }
}
