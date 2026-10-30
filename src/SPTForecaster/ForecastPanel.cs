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

        public static void Attach(LocationConditionsPanel panel, RaidSettings raidSettings)
        {
            var fp = panel.GetComponent<ForecastPanel>();
            if (fp == null) fp = panel.gameObject.AddComponent<ForecastPanel>();
            fp._panel = panel;
            fp._location = raidSettings?.SelectedLocation;
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

        private void OnDisable()
        {
            Live.Remove(this);
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
                // No box at all on maps without weather of their own (Factory, Labs, Labyrinth...).
                if (!ForecasterPlugin.ShowForecast.Value || _location == null || Maps.NoForecast(_location.Id)
                    || !Maps.Position(_location.Id, out _, out _))
                {
                    _view.Hide();
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
