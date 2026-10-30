using System;
using System.Collections.Generic;
using EFT.Weather;

namespace Forecaster
{
    internal sealed class ForecastLine
    {
        public string Text;

        /// <summary>The game's own icon for the line, or None for no icon.</summary>
        public EWeatherType Icon = EWeatherType.None;

        /// <summary>One of our glyphs (<see cref="Glyphs"/>) for weather the game has no icon for; wins over Icon.</summary>
        public string Glyph;

        public bool Faint;
    }

    internal sealed class ForecastResult
    {
        public ForecastLine Headline;

        /// <summary>The wind at the start ("Calm", "Breezy", "Strong wind"), or null.</summary>
        public string Wind;

        /// <summary>Up to two changes during the raid, with the minute they happen at.</summary>
        public readonly List<ForecastLine> Changes = new List<ForecastLine>();

        /// <summary>"More changes after ~45 min" when more follow than fit, or null.</summary>
        public ForecastLine More;

        /// <summary>One line of plain text, for the log and for comparing forecast with raid.</summary>
        public string Summary
        {
            get
            {
                var parts = new List<string> { Headline.Text };
                if (Changes.Count == 0 && Wind != null) parts.Add(Lower(Wind));
                foreach (var l in Changes) parts.Add(Lower(l.Text));
                if (More != null) parts.Add(Lower(More.Text));
                return string.Join(", ", parts.ToArray());
            }
        }

        internal static string Lower(string s) => string.IsNullOrEmpty(s) ? s : char.ToLowerInvariant(s[0]) + s.Substring(1);
    }

    /// <summary>
    /// Turns the schedule into the forecast words. Rules (settled in the design):
    /// headline, then at most two changes with the raid minute they happen at; "within minutes" for a
    /// change in the first couple of minutes; a last line when more changes follow than fit; "all raid"
    /// only when nothing changes; fog under rain or snow is named; no temperatures.
    /// </summary>
    internal static class Forecast
    {
        private sealed class Reading
        {
            public string Label;
            public int Level;
            public bool Wet;
            public bool Foggy;
            public int FogLevel;
            public bool Blizzard;
            public bool Thunder;
            public bool ClearSky;
            public string Wind;
            public EWeatherType Icon;
            public string Glyph;
        }

        private sealed class Segment
        {
            public Reading Reading;
            public double FromMinute;
            public long At;
        }

        private static Reading Read(Block b, bool night)
        {
            var r = new Reading();
            var sky = b.Clouds <= -0.8 ? "Clear" : b.Clouds <= 0.4 ? "Partly cloudy" : b.Clouds < 1.1 ? "Overcast" : "Stormy skies";
            r.ClearSky = b.Clouds <= 0.4;
            r.FogLevel = b.FogLevel;
            r.Foggy = b.FogLevel > 0;
            var fog = Field.FogNames[b.FogLevel];
            var level = (int)Math.Round(b.Rain);
            r.Level = level;
            r.Blizzard = b.Blizzard;
            r.Thunder = b.Thunder && level >= 2;

            if (level >= 2)
            {
                r.Wet = true;
                r.Label = b.Blizzard ? "Blizzard"
                    : r.Thunder ? "Thunderstorm"
                    : Schedule.Winter ? (level == 2 ? "Light snow" : level == 3 ? "Snow" : "Heavy snow")
                    : (level == 2 ? "Light rain" : level == 3 ? "Rain" : "Heavy rain");
                if (r.Foggy) r.Label += " and " + ForecastResult.Lower(fog);
            }
            else
            {
                r.Label = r.Foggy ? fog : sky;
            }

            r.Wind = b.WindSpeed <= 1 ? "Calm" : b.WindSpeed < 3 ? "Breezy" : "Strong wind";
            r.Icon = Icon(b, night);
            r.Glyph = Glyph(b, r, night);
            return r;
        }

        /// <summary>
        /// Our own glyph where the game has no icon: snow and blizzard (the game only has rain icons), each fog
        /// strength (the game has one fog icon), and night skies (the game names night icons it doesn't ship).
        /// Null where the game's own icon says it.
        /// </summary>
        private static string Glyph(Block b, Reading r, bool night)
        {
            if (r.Blizzard) return "Blizzard";
            if (r.Wet && Schedule.Winter) return r.Level == 2 ? "LightSnow" : r.Level == 3 ? "Snow" : "HeavySnow";
            if (!r.Wet && r.Foggy) return r.FogLevel == 1 ? "Mist" : r.FogLevel == 2 ? null : r.FogLevel == 3 ? "DenseFog" : "ThickFog";
            switch (r.Icon)
            {
                case EWeatherType.ClearNight: return "ClearNight";
                case EWeatherType.PartlyCloudNight: return "PartlyCloudNight";
                case EWeatherType.ClearFogNight: return "ClearFogNight";
                default: return null;
            }
        }

        /// <summary>The game's own classification, so the icon is exactly the one vanilla would show for this weather.</summary>
        internal static EWeatherType Icon(Block b, bool night)
        {
            var node = ToNode(b, 0);
            // The game's rule calls rain 1-3 "light rain"; a block the words call dry must not get a rain icon.
            if (b.Rain < 2) node.Rain = 0;
            // A thunderstorm is the game's thunder cloud (the one icon of its with lightning).
            if (b.Thunder && b.Rain >= 2) return EWeatherType.ThunderCloud;
            // Plain fog (no rain) is the game's fog icon; mist, dense and thick fog get glyphs of their own.
            if (b.Rain < 2 && b.FogLevel == 2) return EWeatherType.Fog;
            var type = node.GetWeatherTypeByNode();
            if (!night) return type;
            switch (type)
            {
                case EWeatherType.ClearDay: return EWeatherType.ClearNight;
                case EWeatherType.PartlyCloudDay: return EWeatherType.PartlyCloudNight;
                case EWeatherType.ClearFogDay: return EWeatherType.ClearFogNight;
                default: return type;
            }
        }

        internal static WeatherNode ToNode(Block b, long ticks)
        {
            return new WeatherNode
            {
                Time = ticks,
                Cloudness = (float)b.Clouds,
                Wind = (float)b.WindSpeed,
                WindDirection = b.WindDirection,
                Turbulence = (float)b.WindGustiness,
                Rain = (float)b.Rain,
                RainRandomness = (float)b.RainIntensity,
                ScaterringFogDensity = (float)b.Fog,
                Temperature = (float)b.Temperature,
                AtmospherePressure = (float)b.Pressure,
            };
        }

        /// <summary>The blocks a raid starting at Tarkov second <paramref name="start"/> passes through, merged where they read the same.</summary>
        private static List<Segment> Segments(List<Block> blocks, double start, int raidMinutes)
        {
            var end = start + raidMinutes * 60 * Schedule.Acceleration;
            var segments = new List<Segment>();
            foreach (var b in blocks)
            {
                if (b.End <= start || b.Start >= end) continue;
                var from = Math.Max(0, (b.Start - start) / 60 / Schedule.Acceleration);
                var at = Math.Max(b.Start, (long)start);
                var reading = Read(b, Schedule.IsNight(at));
                var last = segments.Count > 0 ? segments[segments.Count - 1] : null;
                if (last != null && last.Reading.Label == reading.Label) continue;
                segments.Add(new Segment { Reading = reading, FromMinute = from, At = at });
            }

            return segments;
        }

        public static ForecastResult Build(List<Block> blocks, double start, int raidMinutes)
        {
            var result = new ForecastResult();
            var segments = blocks == null ? new List<Segment>() : Segments(blocks, start, raidMinutes);
            if (segments.Count == 0)
            {
                result.Headline = new ForecastLine { Text = "No forecast" };
                return result;
            }

            var first = segments[0].Reading;
            result.Wind = first.Wind;

            // A step that is gone before the card could name a different time for the next one (under 2.5 raid
            // minutes, or rounding to the same ~5 min) is folded into the change after it, so the card reads
            // "Heavy rain in ~5 min" rather than "Rain in ~5 min, Heavy rain in ~5 min". A step that, after
            // that, reads the same as the one before is no change at all: cloud wobbling across the partly
            // cloudy / overcast line used to leave "Clouds breaking up" twice under a partly cloudy headline.
            var steps = new List<Segment> { segments[0] };
            for (var s = 1; s < segments.Count; s++)
            {
                var next = s + 1 < segments.Count ? segments[s + 1] : null;
                if (next != null && (next.FromMinute - segments[s].FromMinute < 2.5 || Slot5(next.FromMinute) == Slot5(segments[s].FromMinute))) continue;
                if (segments[s].Reading.Label == steps[steps.Count - 1].Reading.Label) continue;
                steps.Add(segments[s]);
            }

            if (steps.Count == 1)
            {
                result.Headline = new ForecastLine { Text = first.Label + " all raid", Icon = first.Icon, Glyph = first.Glyph };
                return result;
            }

            result.Headline = new ForecastLine { Text = first.Label, Icon = first.Icon, Glyph = first.Glyph };
            for (var k = 1; k < steps.Count && k <= 2; k++)
            {
                var phrase = Change(steps[k - 1].Reading, steps[k].Reading, steps[k].FromMinute);
                result.Changes.Add(new ForecastLine { Text = char.ToUpperInvariant(phrase[0]) + phrase.Substring(1), Icon = steps[k].Reading.Icon, Glyph = steps[k].Reading.Glyph });
            }

            if (steps.Count > 3)
            {
                result.More = new ForecastLine { Text = "More changes after ~" + Round5(steps[3].FromMinute) + " min", Faint = true };
            }

            return result;
        }

        private static int Slot5(double minute) => minute < 2.5 ? 0 : (int)Math.Round(minute / 5);

        /// <summary>One change and how far into the raid it comes: "Rain starting in ~15 min", "Skies clearing in ~30 min".</summary>
        private static string Change(Reading prev, Reading cur, double minute)
        {
            var when = minute < 2.5 ? "within minutes" : "in ~" + Round5(minute) + " min";
            var falling = Schedule.Winter ? "Snow" : "Rain";

            if (cur.Blizzard && !prev.Blizzard) return (prev.Wet ? "Blizzard blowing up " : "Blizzard arriving ") + when;
            if (prev.Blizzard && !cur.Blizzard) return (cur.Wet ? "Blizzard easing " : "Snow stopping ") + when;
            if (cur.Thunder && !prev.Thunder) return (prev.Wet ? "Thunder starting " : "Thunderstorm arriving ") + when;
            if (prev.Thunder && !cur.Thunder) return (cur.Wet ? "Thunder passing " : "Rain stopping ") + when;
            if (cur.Wet && !prev.Wet) return cur.Label + " starting " + when;
            if (prev.Wet && !cur.Wet) return falling + " stopping " + when;
            if (cur.Wet && cur.Level != prev.Level) return falling + (cur.Level > prev.Level ? " getting heavier " : " easing ") + when;
            if (cur.Foggy && !prev.Foggy) return Field.FogNames[cur.FogLevel] + (cur.FogLevel == 1 ? " forming " : " rolling in ") + when;
            if (prev.Foggy && !cur.Foggy) return (prev.FogLevel == 1 ? "Mist clearing " : "Fog lifting ") + when;
            if (cur.Foggy && prev.Foggy && cur.FogLevel != prev.FogLevel) return (cur.FogLevel > prev.FogLevel ? "Fog thickening " : "Fog thinning ") + when;
            switch (cur.Label)
            {
                case "Clear": return "Skies clearing " + when;
                case "Partly cloudy": return (prev.Label == "Clear" ? "Clouds building " : "Clouds breaking up ") + when;
                case "Overcast": return (prev.Label == "Stormy skies" ? "Storm clouds passing " : "Turning overcast ") + when;
                case "Stormy skies": return "Storm clouds gathering " + when;
                default: return cur.Label + " " + when;
            }
        }

        private static int Round5(double minute) => Math.Max(5, (int)Math.Round(minute / 5) * 5);
    }
}
