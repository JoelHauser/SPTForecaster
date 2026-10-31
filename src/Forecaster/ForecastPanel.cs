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

namespace Forecaster
{
    /// <summary>
    /// Lives on the location screen's LocationConditionsPanel and keeps the selected time slot's forecast
    /// in a box sitting on top of the panel's time bar: the left toggle (_amTimeToggle) is now, the right
    /// one (_pmTimeToggle) 12 in-game hours on. Picking the other toggle switches the box (and the radar).
    /// </summary>
    internal sealed class ForecastPanel : MonoBehaviour
    {
        private const float RefreshInterval = 1f;
        private static readonly List<ForecastPanel> Live = new List<ForecastPanel>();

        private LocationConditionsPanel _panel;
        private LocationSettings.Location _location;
        private ForecastView _view;
        private float _nextRefresh;
        private bool _hidRightToggle;
        private bool _hooked;
        private string _lastLogged;
        private Sprite _gameIcon;
        private bool _gameIconShown;
        private bool _iconMatched;

        // The time bar as the game laid it out, kept so it can be put back (see CompactBar).
        private bool _barCaptured;
        private bool _barCompact;
        private RectTransform _phaseContainer;
        private Vector2 _phaseContainerOffsetMax;
        private RectTransform[] _phaseToggles;
        private Vector2[] _phaseTogglePositions;
        private GameObject _weatherCaption;
        private bool _weatherCaptionShown;
        private CanvasGroup[] _slotGroups;
        private bool _centreDue;
        private bool _centred;
        private bool _renderHooked;
        private int _hiddenFrames;

        public static void Attach(LocationConditionsPanel panel, RaidSettings raidSettings)
        {
            var fp = panel.GetComponent<ForecastPanel>();
            if (fp == null) fp = panel.gameObject.AddComponent<ForecastPanel>();
            fp._panel = panel;
            fp._location = raidSettings?.SelectedLocation;
            // The game's "current weather conditions" icon as Set just drew it (SPT's one menu weather).
            fp._gameIcon = panel._weatherIcon != null ? panel._weatherIcon.sprite : null;
            fp._gameIconShown = panel._weatherIcon != null && panel._weatherIcon.gameObject.activeSelf;
            fp._iconMatched = false;
            Maps.Remember(fp._location);
            fp.HookToggles();
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

        /// <summary>The showing forecast box's slot for the radar's slider and key, or null.</summary>
        public static RectTransform KeySlot()
        {
            foreach (var fp in Live)
            {
                var slot = fp != null ? fp._view?.KeySlot : null;
                if (slot != null) return slot;
            }

            return null;
        }

        private void OnEnable() => Live.Add(this);

        private void OnDestroy() => UnhookRender();

        private void OnDisable()
        {
            Live.Remove(this);
            UnhookRender();
            // The panel closes for Labs (no time slots): let the radar fall back to the remembered slot
            // instead of keeping the time this panel last gave it, which goes stale.
            RadarLayer.TargetTime = null;
        }

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

        /// <summary>Switch the box the moment the other slot is picked, not on the next once-a-second refresh.</summary>
        private void HookToggles()
        {
            if (_hooked) return;
            _hooked = true;
            if (_panel._amTimeToggle != null) _panel._amTimeToggle.onValueChanged.AddListener(OnToggle);
            if (_panel._pmTimeToggle != null) _panel._pmTimeToggle.onValueChanged.AddListener(OnToggle);
        }

        private void OnToggle(bool on)
        {
            if (on) Refresh();
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
                var now = Schedule.ScreenNow;
                var rightSelected = _panel._pmTimeToggle != null && _panel._pmTimeToggle.isOn && _panel._pmTimeToggle.gameObject.activeInHierarchy;
                var start = rightSelected ? now + Schedule.HalfDaySeconds : now;
                var raidMinutes = _location != null && _location.EscapeTimeLimit > 0 ? _location.EscapeTimeLimit : 40;
                // The radar plays the raid through only on maps with weather of their own.
                var weather = _location != null && !Maps.NoForecast(_location.Id) && Maps.Position(_location.Id, out _, out _);
                RadarLayer.SetTarget(start, raidMinutes, _location?.Id, rightSelected, weather);

                EnsureView(rebuildLayout);
                if (_view == null) return;
                // The bar loses its weather caption and icon on every map while the forecast is on (Factory's
                // too, so the bar looks the same everywhere); only switching the forecast off brings them back.
                if (ForecasterPlugin.ShowForecast.Value) CompactBar();
                else VanillaBar();

                // No box at all on maps without weather of their own (Factory, Labs, Labyrinth...).
                if (!ForecasterPlugin.ShowForecast.Value || !weather)
                {
                    _view.Hide();
                    if (!ForecasterPlugin.ShowForecast.Value && weather && ForecasterPlugin.ApplyWeather.Value && Schedule.Ready()) MatchGameIcon(Build(start, raidMinutes));
                    else RestoreGameIcon();
                    return;
                }

                var heading = "Forecast for the " + Maps.Clock(start) + " raid:";
                if (!Schedule.Ready())
                {
                    _view.ShowMessage(heading, "Waiting for the server's weather");
                    return;
                }

                var forecast = Build(start, raidMinutes);
                _view.Show(forecast, heading);

                // Once per change of map, slot or wording; the slot's clock alone ticks on every minute.
                var key = _location.Id + (rightSelected ? " right " : " left ") + forecast.Summary;
                if (ForecasterPlugin.VerboseLog.Value && _lastLogged != key)
                {
                    _lastLogged = key;
                    ForecasterPlugin.Log.LogInfo($"Forecast {_location.Id} {Maps.Clock(start)} slot: {forecast.Summary}");
                }
            }
            catch (Exception ex)
            {
                ForecasterPlugin.Log.LogError("Forecast refresh failed: " + ex);
                _nextRefresh = Time.unscaledTime + 10f;
            }
        }

        /// <summary>
        /// With the forecast on, the time bar's own "current weather conditions" caption and icon say the
        /// same thing again (or, from SPT's one menu weather, something else), so they go, and the time slots move
        /// to the middle of the bar. The game places them by hand (TimePhaseContainer is inset 159 px from the
        /// right for the weather, the slots sit at x 15 and 211), so the container is widened to the whole bar and
        /// the slots shifted together; the "SELECT TIME PHASE:" caption stays put, in line with the box's heading.
        /// Everything is put back as it was when the forecast is switched off (VanillaBar).
        ///
        /// The centring is measured from the drawn checkbox and clock, which exist only once Unity has laid the
        /// UI out, so it runs in Canvas.willRenderCanvases (after layout, before drawing). Until the first
        /// measurement lands the slots are invisible, so they never show at the game's spot and then jump.
        /// </summary>
        private void CompactBar()
        {
            if (!CaptureBar()) return;
            _barCompact = true;
            if (_weatherCaption != null && _weatherCaption.activeSelf) _weatherCaption.SetActive(false);
            if (_panel._weatherIcon != null && _panel._weatherIcon.gameObject.activeSelf) _panel._weatherIcon.gameObject.SetActive(false);

            _phaseContainer.offsetMax = new Vector2(0f, _phaseContainerOffsetMax.y);
            if (!_centred) SetSlotsVisible(false);
            _centreDue = true;
            if (!_renderHooked)
            {
                Canvas.willRenderCanvases += OnWillRenderCanvases;
                _renderHooked = true;
            }
        }

        private void OnWillRenderCanvases()
        {
            if (this == null || !_barCompact)
            {
                UnhookRender();
                return;
            }

            if (!_centreDue || !_panel.gameObject.activeInHierarchy) return;
            var shift = CentreSlots();
            if (float.IsNaN(shift)) return; // nothing drawn yet; the next frame tries again

            // While hidden, show the slots only once a measurement finds them already centred: on the first frame
            // the clock is still plain text (narrower than the game's <mspace> digits a frame later, ~6 px off),
            // so the first move is followed by a second; capped, so they always show.
            if (!_centred && Mathf.Abs(shift) >= 0.01f && ++_hiddenFrames < 30) return;
            _centreDue = false;
            if (!_centred)
            {
                _centred = true;
                _hiddenFrames = 0;
                SetSlotsVisible(true);
            }
        }

        private void UnhookRender()
        {
            if (!_renderHooked) return;
            Canvas.willRenderCanvases -= OnWillRenderCanvases;
            _renderHooked = false;
        }

        /// <summary>
        /// Centred on what is drawn, not on the toggles' layout boxes (their rects don't match the checkbox plus
        /// clock): from the first checkbox's left edge to the last clock's rendered right edge, against the middle
        /// of the whole bar. Returns the shift applied, or NaN if the slots aren't drawn yet.
        /// </summary>
        private float CentreSlots()
        {
            float left = float.MaxValue, right = float.MinValue;
            for (var i = 0; i < _phaseToggles.Length; i++)
            {
                if (!_phaseToggles[i].gameObject.activeSelf) continue; // Icebreaker hides the right slot
                if (!DrawnSpan(i == 0 ? _panel._amTimeToggle : _panel._pmTimeToggle, out var l, out var r)) return float.NaN;
                left = Mathf.Min(left, l);
                right = Mathf.Max(right, r);
            }

            if (left > right) return float.NaN;
            var bar = (RectTransform)_panel.transform;
            var middle = _phaseContainer.InverseTransformPoint(bar.TransformPoint(bar.rect.center)).x;
            var shift = middle - (left + right) / 2f;
            if (Mathf.Abs(shift) >= 0.01f)
            {
                for (var i = 0; i < _phaseToggles.Length; i++) _phaseToggles[i].anchoredPosition += new Vector2(shift, 0f);
            }

            return shift;
        }

        /// <summary>Hides the time slots (our own CanvasGroup on each; a toggle that already has one is left alone).</summary>
        private void SetSlotsVisible(bool visible)
        {
            if (_slotGroups == null)
            {
                _slotGroups = new CanvasGroup[_phaseToggles.Length];
                for (var i = 0; i < _phaseToggles.Length; i++)
                {
                    var go = _phaseToggles[i].gameObject;
                    if (go.GetComponent<CanvasGroup>() == null) _slotGroups[i] = go.AddComponent<CanvasGroup>();
                }
            }

            foreach (var group in _slotGroups)
            {
                if (group != null) group.alpha = visible ? 1f : 0f;
            }
        }

        /// <summary>A time slot's drawn extent in the phase container's space: checkbox left edge to clock text's right edge.</summary>
        private bool DrawnSpan(Toggle toggle, out float left, out float right)
        {
            left = float.MaxValue;
            right = float.MinValue;
            var corners = new Vector3[4];

            var box = toggle.targetGraphic != null ? toggle.targetGraphic.rectTransform : (RectTransform)toggle.transform;
            box.GetWorldCorners(corners);
            foreach (var c in corners) Extend(_phaseContainer.InverseTransformPoint(c).x, ref left, ref right);

            var clock = toggle.GetComponentInChildren<TextMeshProUGUI>();
            if (clock != null && !string.IsNullOrEmpty(clock.text))
            {
                clock.ForceMeshUpdate();
                var b = clock.textBounds;
                if (b.size.x <= 0f) return false; // not drawn yet; the next frame tries again
                var t = clock.transform;
                Extend(_phaseContainer.InverseTransformPoint(t.TransformPoint(new Vector3(b.min.x, b.center.y, 0f))).x, ref left, ref right);
                Extend(_phaseContainer.InverseTransformPoint(t.TransformPoint(new Vector3(b.max.x, b.center.y, 0f))).x, ref left, ref right);
            }

            return left <= right;
        }

        private static void Extend(float x, ref float left, ref float right)
        {
            left = Mathf.Min(left, x);
            right = Mathf.Max(right, x);
        }

        private void VanillaBar()
        {
            if (!_barCompact) return;
            _barCompact = false;
            _centreDue = false;
            _centred = false;
            _hiddenFrames = 0;
            UnhookRender();
            SetSlotsVisible(true);
            _phaseContainer.offsetMax = _phaseContainerOffsetMax;
            for (var i = 0; i < _phaseToggles.Length; i++) _phaseToggles[i].anchoredPosition = _phaseTogglePositions[i];
            if (_weatherCaption != null) _weatherCaption.SetActive(_weatherCaptionShown);
            if (_panel._weatherIcon != null) _panel._weatherIcon.gameObject.SetActive(_gameIconShown);
        }

        /// <summary>The bar's parts as the game placed them, read once. False if this panel isn't the one we know.</summary>
        private bool CaptureBar()
        {
            if (_barCaptured) return _phaseContainer != null;
            _barCaptured = true;

            var am = _panel._amTimeToggle;
            var pm = _panel._pmTimeToggle;
            if (am == null || pm == null || am.transform.parent != pm.transform.parent) return false;
            _phaseContainer = am.transform.parent as RectTransform;
            if (_phaseContainer == null) return false;
            _phaseContainerOffsetMax = _phaseContainer.offsetMax;
            _phaseToggles = new[] { (RectTransform)am.transform, (RectTransform)pm.transform };
            _phaseTogglePositions = new[] { _phaseToggles[0].anchoredPosition, _phaseToggles[1].anchoredPosition };

            // "CURRENT WEATHER CONDITIONS:" is the icon's sibling "Weather Caption".
            var icon = _panel._weatherIcon != null ? _panel._weatherIcon.transform : null;
            var caption = icon != null && icon.parent != null ? icon.parent.Find("Weather Caption") : null;
            _weatherCaption = caption != null ? caption.gameObject : null;
            _weatherCaptionShown = _weatherCaption != null && _weatherCaption.activeSelf;
            return true;
        }

        /// <summary>
        /// The game's "current weather conditions" icon shows SPT's menu weather, one for every map and slot, so
        /// it could show rain over a clear forecast. While the raid follows the forecast it shows the forecast's
        /// opening weather instead (the box's icon).
        /// </summary>
        private void MatchGameIcon(ForecastResult forecast)
        {
            var icon = _panel._weatherIcon;
            var sprite = ForecastView.IconSprite(forecast?.Headline);
            if (icon == null || sprite == null) return;
            icon.sprite = sprite;
            icon.gameObject.SetActive(true);
            _iconMatched = true;
        }

        private void RestoreGameIcon()
        {
            if (!_iconMatched || _panel._weatherIcon == null) return;
            _panel._weatherIcon.sprite = _gameIcon;
            _panel._weatherIcon.gameObject.SetActive(_gameIconShown);
            _iconMatched = false;
        }

        /// <summary>This location's weather for a raid starting at <paramref name="start"/>: the field read at its button.</summary>
        private ForecastResult Build(double start, int raidMinutes)
        {
            Maps.Position(_location.Id, out var x, out var y);
            var blocks = Field.Blocks(x, y, start - Field.Step, start + (raidMinutes + 5) * 60 * Schedule.Acceleration);
            return Forecast.Build(blocks, start, raidMinutes);
        }

        private void EnsureView(bool rebuild)
        {
            if (_panel._currentPhaseTime == null) return;

            if (rebuild)
            {
                _view?.Destroy();
                _view = null;
            }

            if (_view == null) _view = ForecastView.Create(_panel);
        }
    }
}
