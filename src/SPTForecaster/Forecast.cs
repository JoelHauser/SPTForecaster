using System;
using System.Collections.Generic;
using EFT.Weather;

namespace SPTForecaster
{
    internal sealed class ForecastLine
    {
        public string Text;

        /// <summary>The game's own icon for the line, or None for no icon.</summary>
        public EWeatherType Icon = EWeatherType.None;

        public bool Faint;
    }

    internal sealed class ForecastResult
    {
        public ForecastLine Headline;
        public readonly List<ForecastLine> Lines = new List<ForecastLine>();

        /// <summary>One line of plain text, for the log and for comparing forecast with raid.</summary>
        public string Summary
        {
            get
            {
                var parts = new List<string> { Headline.Text };
                foreach (var l in Lines) parts.Add(Lower(l.Text));
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
            public bool Wet;
            public bool Foggy;
            public bool ClearSky;
            public string Wind;
            public EWeatherType Icon;
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
            r.Foggy = b.Fog >= 0.004;
            var fog = b.Fog >= 0.006 ? "Dense fog" : "Fog";
            var level = (int)Math.Round(b.Rain);

            if (level >= 2)
            {
                r.Wet = true;
                r.Label = Schedule.Winter
                    ? (level == 2 ? "Light snow" : level == 3 ? "Snow" : "Heavy snow")
                    : (level == 2 ? "Light rain" : level == 3 ? "Rain" : "Heavy rain");
                if (r.Foggy) r.Label += " and " + ForecastResult.Lower(fog);
            }
            else
            {
                r.Label = r.Foggy ? fog : sky;
            }

            r.Wind = b.WindSpeed <= 1 ? "Calm" : b.WindSpeed < 3 ? "Breezy" : "Strong wind";
            r.Icon = Icon(b, night);
            return r;
        }

        /// <summary>The game's own classification, so the icon is exactly the one vanilla would show for this weather.</summary>
        internal static EWeatherType Icon(Block b, bool night)
        {
            var node = ToNode(b, 0);
            // The game's rule calls rain 1-3 "light rain"; a block the words call dry must not get a rain icon.
            if (b.Rain < 2) node.Rain = 0;
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
            if (segments.Count == 1)
            {
                result.Headline = new ForecastLine { Text = first.Label + " all raid", Icon = first.Icon };
                result.Lines.Add(new ForecastLine { Text = first.Wind });
                return result;
            }

            result.Headline = new ForecastLine { Text = first.Label, Icon = first.Icon };
            for (var k = 1; k < segments.Count && k <= 2; k++)
            {
                var phrase = Change(segments[k - 1].Reading, segments[k].Reading, segments[k].FromMinute);
                result.Lines.Add(new ForecastLine { Text = char.ToUpperInvariant(phrase[0]) + phrase.Substring(1), Icon = segments[k].Reading.Icon });
            }

            if (segments.Count > 3)
            {
                result.Lines.Add(new ForecastLine { Text = "More changes after ~" + Round5(segments[3].FromMinute) + " min", Faint = true });
            }

            return result;
        }

        private static string Change(Reading prev, Reading cur, double minute)
        {
            string from, by, at;
            if (minute < 2.5)
            {
                from = by = at = "within minutes";
            }
            else
            {
                var mm = "~" + Round5(minute) + " min";
                from = "from " + mm;
                by = "by " + mm;
                at = mm;
            }

            if (cur.Wet && !prev.Wet) return ForecastResult.Lower(cur.Label) + " " + from;
            if (prev.Wet && !cur.Wet) return (Schedule.Winter ? "snow stops " : cur.ClearSky ? "clearing " : "dry ") + by;
            if (cur.Foggy && !prev.Foggy) return "fog rolling in " + at;
            if (prev.Foggy && !cur.Foggy) return "fog lifting " + at;
            return ForecastResult.Lower(cur.Label) + " " + from;
        }

        private static int Round5(double minute) => Math.Max(5, (int)Math.Round(minute / 5) * 5);
    }
}
