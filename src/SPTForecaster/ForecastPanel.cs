using System;
using System.Collections.Generic;
using EFT;
using EFT.UI.Matchmaker;
using EFT.Utilities;
using EFT.Weather;
using JsonType;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace SPTForecaster
{
    /// <summary>
    /// Lives on the location screen's LocationConditionsPanel and keeps a forecast above each time
    /// toggle: the left one (_amTimeToggle, now) and the right one (_pmTimeToggle, 12 in-game hours on).
    ///
    /// Everything is built from the panel's own parts so it reads as stock: the clock text's font and
    /// colour, and the game's own weather icons (UI/WeatherIcons, the set the panel's vanilla weather
    /// icon uses), picked by the game's own WeatherNode.GetWeatherTypeByNode.
    /// </summary>
    internal sealed class ForecastPanel : MonoBehaviour
    {
        private const float RefreshInterval = 1f;
        private static readonly List<ForecastPanel> Live = new List<ForecastPanel>();

        private LocationConditionsPanel _panel;
        private LocationSettings.Location _location;
        private ForecastView _left;
        private ForecastView _right;
        private float _nextRefresh;
        private bool _hidRightToggle;
        private readonly string[] _lastLogged = new string[2];

        public static void Attach(LocationConditionsPanel panel, RaidSettings raidSettings)
        {
            var fp = panel.GetComponent<ForecastPanel>() ?? panel.gameObject.AddComponent<ForecastPanel>();
            fp._panel = panel;
            fp._location = raidSettings?.SelectedLocation;
            fp.ApplySlotRules();
            fp.Refresh();
        }

        public static void RefreshAll()
        {
            foreach (var fp in Live)
            {
                if (fp != null) fp.Refresh(rebuildLayout: true);
            }
        }

        private void OnEnable() => Live.Add(this);

        private void OnDisable() => Live.Remove(this);

        /// <summary>Called from the panel's UpdateConditions postfix, every frame the panel is up.</summary>
        public void Tick()
        {
            if (_location != null && Maps.OneSlot(_location.Id) && _panel._currentPhaseTime != null)
            {
                // Vanilla rewrites the clock every frame; Icebreaker plays at its pinned hour, so show that.
                _panel._currentPhaseTime.SetMonospaceText(Maps.IcebreakerTime);
            }

            if (Time.unscaledTime >= _nextRefresh)
            {
                Refresh();
            }
        }

        private void ApplySlotRules()
        {
            var right = _panel._pmTimeToggle;
            if (right == null) return;

            if (_location != null && Maps.OneSlot(_location.Id))
            {
                if (_panel._amTimeToggle != null) _panel._amTimeToggle.isOn = true;
                right.gameObject.SetActive(false);
                _hidRightToggle = true;
            }
            else if (_hidRightToggle)
            {
                right.gameObject.SetActive(true);
                _hidRightToggle = false;
            }
        }

        private void Refresh(bool rebuildLayout = false)
        {
            _nextRefresh = Time.unscaledTime + RefreshInterval;
            try
            {
                var show = ForecasterPlugin.ShowForecast.Value && _location != null && !Maps.NoForecast(_location.Id);
                var blocks = show ? Schedule.Get() : null;
                show &= blocks != null;

                EnsureViews(rebuildLayout);
                if (!show)
                {
                    _left?.Hide();
                    _right?.Hide();
                    return;
                }

                var raidMinutes = _location.EscapeTimeLimit > 0 ? _location.EscapeTimeLimit : 40;
                var now = Schedule.TarkovNow;
                Show(_left, Forecast.Build(blocks, now, raidMinutes), _panel._amTimeToggle, 0, now);
                Show(_right, Forecast.Build(blocks, now + Schedule.HalfDaySeconds, raidMinutes), _panel._pmTimeToggle, 1, now + Schedule.HalfDaySeconds);
            }
            catch (Exception ex)
            {
                ForecasterPlugin.Log.LogError("Forecast refresh failed: " + ex);
                _nextRefresh = Time.unscaledTime + 10f;
            }
        }

        private void Show(ForecastView view, ForecastResult forecast, Toggle toggle, int index, double start)
        {
            if (view == null) return;
            if (toggle != null && !toggle.gameObject.activeInHierarchy)
            {
                view.Hide();
                return;
            }

            view.Show(forecast, toggle == null || toggle.isOn);

            if (ForecasterPlugin.VerboseLog.Value && _lastLogged[index] != forecast.Summary)
            {
                _lastLogged[index] = forecast.Summary;
                ForecasterPlugin.Log.LogInfo($"Forecast {_location.Id} {Maps.Clock(start)} slot: {forecast.Summary}");
            }
        }

        private void EnsureViews(bool rebuild)
        {
            var font = _panel._currentPhaseTime;
            if (font == null) return;

            if (rebuild)
            {
                _left?.Destroy();
                _right?.Destroy();
                _left = _right = null;
            }

            if (_left == null)
            {
                var host = _panel._amTimeToggle != null ? _panel._amTimeToggle.transform : font.transform;
                _left = ForecastView.Create(host, font, _panel._weatherIcon);
            }

            if (_right == null && _panel._pmTimeToggle != null && _panel._nextPhaseTime != null)
            {
                _right = ForecastView.Create(_panel._pmTimeToggle.transform, _panel._nextPhaseTime, _panel._weatherIcon);
            }
        }
    }

    /// <summary>The forecast block for one slot: a headline row and up to three smaller rows, each with an optional icon.</summary>
    internal sealed class ForecastView
    {
        private const int RowCount = 4;

        private GameObject _root;
        private readonly Image[] _icons = new Image[RowCount];
        private readonly TextMeshProUGUI[] _texts = new TextMeshProUGUI[RowCount];
        private CanvasGroup _group;

        public static ForecastView Create(Transform host, TextMeshProUGUI fontSource, Image iconSource)
        {
            var view = new ForecastView();
            var root = new GameObject("SPTForecaster", typeof(RectTransform));
            root.transform.SetParent(host, false);
            view._root = root;

            // Ignore any layout the host toggle sits in; sit just above it, as wide as it.
            root.AddComponent<LayoutElement>().ignoreLayout = true;
            var rt = (RectTransform)root.transform;
            rt.anchorMin = new Vector2(0f, 1f);
            rt.anchorMax = new Vector2(1f, 1f);
            rt.pivot = new Vector2(0.5f, 0f);
            rt.anchoredPosition = new Vector2(ForecasterPlugin.OffsetX.Value, ForecasterPlugin.OffsetY.Value);
            rt.sizeDelta = Vector2.zero;

            var column = root.AddComponent<VerticalLayoutGroup>();
            column.childAlignment = TextAnchor.LowerLeft;
            column.childControlWidth = true;
            column.childControlHeight = true;
            column.childForceExpandWidth = true;
            column.childForceExpandHeight = false;
            column.spacing = 1f;
            root.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            view._group = root.AddComponent<CanvasGroup>();
            view._group.blocksRaycasts = false;
            view._group.interactable = false;

            var scale = ForecasterPlugin.TextScale.Value;
            for (var i = 0; i < RowCount; i++)
            {
                var size = fontSource.fontSize * scale * (i == 0 ? 1.15f : 1f);

                var row = new GameObject("Row" + i, typeof(RectTransform));
                row.transform.SetParent(root.transform, false);
                var line = row.AddComponent<HorizontalLayoutGroup>();
                line.childAlignment = TextAnchor.MiddleLeft;
                line.childControlWidth = true;
                line.childControlHeight = true;
                line.childForceExpandWidth = false;
                line.childForceExpandHeight = false;
                line.spacing = size * 0.35f;

                var iconGo = new GameObject("Icon", typeof(RectTransform));
                iconGo.transform.SetParent(row.transform, false);
                var icon = iconGo.AddComponent<Image>();
                icon.preserveAspect = true;
                icon.raycastTarget = false;
                if (iconSource != null) icon.color = iconSource.color;
                var iconLayout = iconGo.AddComponent<LayoutElement>();
                iconLayout.preferredWidth = iconLayout.minWidth = size * 1.25f;
                iconLayout.preferredHeight = iconLayout.minHeight = size * 1.25f;

                var textGo = new GameObject("Text", typeof(RectTransform));
                textGo.transform.SetParent(row.transform, false);
                var text = textGo.AddComponent<TextMeshProUGUI>();
                text.font = fontSource.font;
                text.fontSharedMaterial = fontSource.fontSharedMaterial;
                text.fontSize = size;
                text.color = fontSource.color;
                text.fontStyle = i == 0 ? FontStyles.Bold : FontStyles.Normal;
                text.enableWordWrapping = false;
                text.overflowMode = TextOverflowModes.Overflow;
                text.alignment = TextAlignmentOptions.MidlineLeft;
                text.raycastTarget = false;

                view._icons[i] = icon;
                view._texts[i] = text;
            }

            return view;
        }

        public void Show(ForecastResult forecast, bool selected)
        {
            if (_root == null) return;
            _root.SetActive(true);
            _group.alpha = selected ? 1f : 0.6f;

            Set(0, forecast.Headline);
            for (var i = 1; i < RowCount; i++)
            {
                Set(i, i - 1 < forecast.Lines.Count ? forecast.Lines[i - 1] : null);
            }
        }

        private void Set(int i, ForecastLine line)
        {
            var row = _texts[i].transform.parent.gameObject;
            if (line == null)
            {
                row.SetActive(false);
                return;
            }

            row.SetActive(true);
            _texts[i].text = line.Text;
            _texts[i].alpha = line.Faint ? 0.6f : 1f;

            var sprite = line.Icon == EWeatherType.None ? null : ResourcesCache.Pop<Sprite>("UI/WeatherIcons/" + line.Icon);
            _icons[i].sprite = sprite;
            _icons[i].enabled = sprite != null;
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
