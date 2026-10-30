using System;
using System.Collections.Generic;

namespace Forecaster
{
    /// <summary>
    /// The weather as a field over the region, from one seed. Ported from docs/radar-prototype.html; the
    /// two should stay in step.
    ///
    /// Coordinates are the 600 x 400 frame of the location map: x = IconX * 0.6, y = (1000 - IconY) * 0.4
    /// (y down), the same fractions of the map the game anchors its location buttons at. Time is Tarkov
    /// seconds (Schedule.TarkovNow).
    ///
    /// Every 8 in-game hours the region rolls a pattern (fair, unsettled, prolonged rain, coastal fog,
    /// storm front) that decides what forms. Rain cells and fog banks drift with a slowly turning wind and
    /// grow and fade; fronts and squall lines are long curved bands; noise roughens edges and breaks rain
    /// into cells. A location's weather is the field sampled at its button every 14 in-game minutes.
    ///
    /// Thread-safe: the radar renders it on a worker thread while the UI samples it.
    /// </summary>
    internal static class Field
    {
        public const double W = 600, H = 400;
        public const long Step = 14 * 60;
        private const long PeriodSeconds = 8 * 3600;
        private const double CoastX = 88, CoastY = 215;
        private const double Speed = W * 1.1 / (9 * 3600);

        public static readonly double[] RainSteps = { 0.12, 0.3, 0.55, 0.8 };
        /// <summary>
        /// Fog has four strengths the card names (<see cref="FogNames"/>). <see cref="FogValues"/> are what the
        /// raid gets (ScaterringFogDensity): SPT's presets stop at 0.006, the game's own scales go far higher,
        /// and bots' sight is cut more and more from 0.004 up to 0.01 (LookSensor), so dense and thick fog matter.
        /// </summary>
        public static readonly double[] FogSteps = { 0.3, 0.45, 0.65, 0.85 };
        public static readonly double[] FogValues = { 0.0013, 0.0045, 0.0065, 0.01, 0.016 };
        public static readonly string[] FogNames = { "", "Mist", "Fog", "Dense fog", "Thick fog" };
        private const double Margin = 0.03;

        private sealed class Pattern
        {
            public string Name;
            public double Showers, Fronts, Fog, Clouds, Wind;
            public bool Shield, Squall, Coast;
        }

        private static readonly string[] PatternKeys = { "fair", "grey", "unsettled", "longrain", "coastfog", "storms" };

        private static readonly Dictionary<string, Pattern> Patterns = new Dictionary<string, Pattern>
        {
            ["fair"] = new Pattern { Name = "Fair weather", Showers = 0.05, Fronts = 0, Fog = 1, Clouds = -1.05, Wind = -0.15 },
            ["grey"] = new Pattern { Name = "Overcast", Showers = 0, Fronts = 0, Fog = 0.6, Clouds = 0.65, Wind = 0 },
            ["unsettled"] = new Pattern { Name = "Showers", Showers = 0.6, Fronts = 0.05, Fog = 0.4, Clouds = 0.1, Wind = 0.15 },
            ["longrain"] = new Pattern { Name = "Prolonged rain", Showers = 0.15, Fronts = 0, Fog = 0.5, Clouds = 0.75, Wind = 0.05, Shield = true },
            ["coastfog"] = new Pattern { Name = "Coastal fog", Showers = 0, Fronts = 0, Fog = 1, Clouds = -0.5, Wind = -0.2, Coast = true },
            ["storms"] = new Pattern { Name = "Storm front", Showers = 0.35, Fronts = 0, Fog = 0, Clouds = 0.45, Wind = 0.35, Squall = true },
        };

        /// <summary>
        /// What each pattern can turn into, and how readily, before the season's weighting (rows and columns in
        /// <see cref="PatternKeys"/> order): fine weather clouds over, fogs up along the coast or a front comes
        /// through; cloud brings steady rain or clears; rain and fronts leave showers behind; showers clear.
        /// </summary>
        private static readonly double[][] Next =
        {
            //               fair grey unsettled longrain coastfog storms
            new double[] {   0,   3,   0.5,      0,       1.5,     1 },   // fair
            new double[] {   2,   0,   1,        2,       0,       0 },   // grey
            new double[] {   2,   1,   0,        0,       0,       0.3 }, // unsettled
            new double[] {   0,   1.5, 2,        0,       0,       0 },   // longrain
            new double[] {   2,   1,   0,        0,       0,       0 },   // coastfog
            new double[] {   1.5, 0.5, 2,        0,       0,       0 },   // storms
        };

        private sealed class Climate
        {
            /// <summary>How strongly each pattern is favoured when the weather changes, and how likely each is to last another period.</summary>
            public double[] Weight, Stay;

            /// <summary>
            /// How much afternoon heating drives showers (0 none, 1 full summer), how readily fog forms, and how
            /// much cloudier and windier than the patterns' own levels the season runs.
            /// </summary>
            public double Heat, Fog, Clouds, Wind;

            /// <summary>How likely an hour of heavy rain is to be a thunderstorm (St Petersburg's thunder days: summer, a little in spring and early autumn).</summary>
            public double Thunder;
        }

        // Modelled on St Petersburg (Pulkovo), at the head of the Gulf of Finland, the real climate nearest the
        // game's Norvinsk region: clear days are rare all year; summer has the most sun and afternoon
        // thunderstorms; autumn into winter is grey, wet and foggy, winter snowing on about half its days;
        // spring is the driest season. Monthly figures from climates.com's St Petersburg guide (blue sky, dry,
        // snowy, thunder and foggy days) and Wikipedia's Climate of Saint Petersburg (1991-2020 sunshine).
        // Tuned with docs/radar-prototype.html's model (2026-10-29); measured over 40-minute raids, rain or
        // snow in summer 20 %, autumn 28 %, late autumn 35 %, winter 34 %, early spring 23 %, spring 17 %.
        private static readonly Dictionary<string, Climate> Climates = new Dictionary<string, Climate>
        {
            //                               fair grey unsettled longrain coastfog storms
            ["summer"] = new Climate { Weight = new[] { 2.2, 1.3, 1.7, 0.7, 0.6, 2.2 }, Stay = new[] { 0.7, 0.45, 0.3, 0.35, 0.3, 0.05 }, Heat = 1, Fog = 0.5, Clouds = 0.15, Wind = -0.05, Thunder = 0.7 },
            ["autumn"] = new Climate { Weight = new[] { 1.2, 2.4, 1.2, 1.8, 1.3, 0.4 }, Stay = new[] { 0.6, 0.65, 0.3, 0.5, 0.45, 0.05 }, Heat = 0.2, Fog = 2.1, Clouds = 0.02, Wind = 0.05, Thunder = 0.15 },
            ["lateautumn"] = new Climate { Weight = new[] { 0.7, 3, 0.8, 2, 0.8, 0.2 }, Stay = new[] { 0.5, 0.75, 0.3, 0.55, 0.4, 0.05 }, Heat = 0, Fog = 2.3, Clouds = 0.12, Wind = 0.08, Thunder = 0 },
            ["winter"] = new Climate { Weight = new[] { 1, 3, 0.9, 2.4, 0.4, 0.7 }, Stay = new[] { 0.6, 0.72, 0.35, 0.55, 0.3, 0.05 }, Heat = 0, Fog = 1.9, Clouds = 0.02, Wind = 0.05, Thunder = 0 },
            ["earlyspring"] = new Climate { Weight = new[] { 1.8, 2, 1, 1.5, 1, 0.2 }, Stay = new[] { 0.65, 0.6, 0.3, 0.45, 0.4, 0.05 }, Heat = 0.2, Fog = 1.2, Clouds = 0.1, Wind = 0, Thunder = 0.05 },
            ["spring"] = new Climate { Weight = new[] { 2, 1.6, 1.3, 0.8, 1.4, 0.5 }, Stay = new[] { 0.7, 0.5, 0.3, 0.4, 0.4, 0.05 }, Heat = 0.6, Fog = 0.9, Clouds = 0.15, Wind = 0, Thunder = 0.4 },
            // SPT's Storm season is drawn by the game as a blizzard (driven snow), so: wintry, wet, windy, no afternoon showers.
            ["storm"] = new Climate { Weight = new[] { 0.8, 1.4, 1.8, 1.5, 0.3, 2.5 }, Stay = new[] { 0.55, 0.45, 0.4, 0.45, 0.3, 0.15 }, Heat = 0, Fog = 0.5, Clouds = 0.1, Wind = 0.15, Thunder = 0 },
        };

        /// <summary>SPT's seasons in its Season enum's order: SUMMER, AUTUMN, WINTER, SPRING, AUTUMN_LATE, SPRING_EARLY, STORM.</summary>
        private static readonly string[] SeasonClimates = { "summer", "autumn", "winter", "spring", "lateautumn", "earlyspring", "storm" };

        private static Climate CurrentClimate => Climates[_season >= 0 && _season < SeasonClimates.Length ? SeasonClimates[_season] : "summer"];

        /// <summary>
        /// The pattern chain runs forward period by period from <see cref="ChainFrom"/> (2022 on the Tarkov
        /// clock), each step drawn from that period's own seeded numbers, so any period always comes out the
        /// same. Checkpoints every <see cref="Mark"/> periods keep it cheap; a new seed or season starts afresh.
        /// </summary>
        private const long ChainFrom = 400000;
        private const int Mark = 1024;
        private static readonly List<int> ChainMarks = new List<int>();

        private sealed class Kind
        {
            public double Life0, Life1, SpeedFactor, Width0, Width1, Length0, Length1, Peak0, Peak1, Trail, Curve;
            public bool Plateau, IsFog;
        }

        private static readonly Dictionary<string, Kind> Kinds = new Dictionary<string, Kind>
        {
            ["shower"] = new Kind { Life0 = 2, Life1 = 5, SpeedFactor = 1, Width0 = 22, Width1 = 50, Length0 = 1.2, Length1 = 2.2, Peak0 = 0.35, Peak1 = 1, Trail = 1.6 },
            ["front"] = new Kind { Life0 = 5, Life1 = 9, SpeedFactor = 0.9, Width0 = 28, Width1 = 48, Length0 = 260, Length1 = 480, Peak0 = 0.5, Peak1 = 0.9, Trail = 2.4, Curve = 0.0012 },
            ["squall"] = new Kind { Life0 = 7, Life1 = 10, SpeedFactor = 1.35, Width0 = 20, Width1 = 32, Length0 = 420, Length1 = 650, Peak0 = 0.85, Peak1 = 1, Trail = 2.8, Curve = 0.0016, Plateau = true },
            ["shield"] = new Kind { Life0 = 11, Life1 = 11, SpeedFactor = 0.25, Width0 = 150, Width1 = 210, Length0 = 620, Length1 = 820, Peak0 = 0.36, Peak1 = 0.5, Trail = 1.4, Curve = 0.0003, Plateau = true },
            ["fog"] = new Kind { Life0 = 4, Life1 = 9, SpeedFactor = 0.25, Width0 = 80, Width1 = 130, Length0 = 140, Length1 = 260, Peak0 = 0.6, Peak1 = 1, Trail = 1.2, IsFog = true },
            ["coastfog"] = new Kind { Life0 = 10, Life1 = 10, SpeedFactor = 0.02, Width0 = 48, Width1 = 66, Length0 = 440, Length1 = 500, Peak0 = 0.85, Peak1 = 1, Trail = 1.3, Plateau = true, IsFog = true },
        };

        internal sealed class Cell
        {
            public bool Fog, Plateau, Coast;
            public double Birth, Life, X0, Y0, Vx, Vy, Ang, Width, Length, Peak, Trail, Curve;

            /// <summary>Where in the noise this cell's texture comes from, so no two cells look alike.</summary>
            public double Nx, Ny;
        }

        private static readonly object Gate = new object();
        private static readonly Dictionary<long, List<Cell>> CellCache = new Dictionary<long, List<Cell>>();
        private static readonly Dictionary<long, string> PatternCache = new Dictionary<long, string>();
        private static int _seed;
        private static int _season;

        /// <summary>Bumped with every cache clear, so a render that started before it can't store old cells after it.</summary>
        private static int _generation;
        private static string _forcedPattern;

        /// <summary>
        /// Debug: one pattern for every period ("fair", "unsettled", "longrain", "coastfog", "storms"), or
        /// null for the seeded ones. Storms are generated from the pattern, so changing it drops them.
        /// </summary>
        public static string ForcedPattern
        {
            get => _forcedPattern;
            set
            {
                lock (Gate)
                {
                    if (value == _forcedPattern) return;
                    _forcedPattern = value;
                    _generation++;
                    CellCache.Clear();
                    SteerCache.Clear();
                }
            }
        }

        /// <summary>The seed from the server; a new seed clears everything derived from the old one.</summary>
        public static int Seed
        {
            get => _seed;
            set
            {
                lock (Gate)
                {
                    if (value == _seed) return;
                    _seed = value;
                    _generation++;
                    CellCache.Clear();
                    PatternCache.Clear();
                    SteerCache.Clear();
                    ChainMarks.Clear();
                }
            }
        }

        /// <summary>The server's season (SPT's Season enum); it shapes the weather, so a new one clears everything too.</summary>
        public static int Season
        {
            get => _season;
            set
            {
                lock (Gate)
                {
                    if (value == _season) return;
                    _season = value;
                    _generation++;
                    CellCache.Clear();
                    PatternCache.Clear();
                    SteerCache.Clear();
                    ChainMarks.Clear();
                }
            }
        }

        // ---- deterministic randomness ----------------------------------------------------------

        private static int Mix(int a, int b)
        {
            unchecked
            {
                var h = (uint)(a ^ (int)0x9e3779b9) * 0x85ebca6bu ^ (uint)(b + 0x7f4a7c15) * 0xc2b2ae35u;
                return (int)(h ^ (h >> 13));
            }
        }

        private sealed class Rng
        {
            private int _s;
            public Rng(int seed) { _s = seed; }

            public double Next()
            {
                unchecked
                {
                    _s += 0x6D2B79F5;
                    var t = (uint)_s;
                    t = (t ^ (t >> 15)) * (t | 1u);
                    t = (t + (t ^ (t >> 7)) * (t | 61u)) ^ t;
                    return (t ^ (t >> 14)) / 4294967296.0;
                }
            }

            public double Range(double a, double b) => a + (b - a) * Next();
        }

        private static double Smoothstep(double a, double b, double v)
        {
            var x = Math.Min(1, Math.Max(0, (v - a) / (b - a)));
            return x * x * (3 - 2 * x);
        }

        // ---- wind and patterns -------------------------------------------------------------------

        /// <summary>
        /// Direction storms drift (radians, frame axes) and strength 0..1: calmer in fine weather, stronger
        /// with fronts and showers.
        /// </summary>
        public static void Wind(double t, out double dir, out double strength)
        {
            var s = (Seed % 997 + 997) % 997 / 997.0 * Math.PI * 2;
            dir = Steering(t) + 0.15 * Math.Sin(t / 20000 + s);
            strength = Math.Max(0, Math.Min(1, 0.45 + 0.4 * Math.Sin(t / 47000 + s * 1.7) + PatternBlend(t, p => p.Wind) + CurrentClimate.Wind));
        }

        private const double SpellSeconds = 12 * 3600, TurnSeconds = 3 * 3600;
        private static readonly Dictionary<long, double> SteerCache = new Dictionary<long, double>();

        // Where weather comes from around St Petersburg, and how often (frame axes: x east, y south; the
        // angle is the way it moves, so "from the west" moves east, 0). Mostly Atlantic lows off the Baltic
        // from the west and south-west, north-west behind cold fronts, now and then south (warm air ahead of
        // a low) or east (a blocking high). Per pattern, a multiplier on each.
        //                                         from:  W      SW     NW     S      N      SE     E      NE
        private static readonly double[] SteerAngles = { 0, -0.785, 0.785, -1.571, 1.571, -2.356, 3.142, 2.356 };
        private static readonly double[] SteerWeights = { 30, 25, 18, 9, 6, 5, 4, 3 };
        private static readonly Dictionary<string, double[]> SteerBias = new Dictionary<string, double[]>
        {
            ["fair"] = new double[] { 0.8, 0.7, 1.2, 0.8, 1.8, 1.2, 2.2, 1.8 },
            ["grey"] = new double[] { 1, 1.2, 0.8, 1.2, 0.6, 1.2, 1.2, 0.8 },
            ["unsettled"] = new double[] { 1.3, 0.8, 1.8, 0.6, 1, 0.5, 0.4, 0.6 },
            ["longrain"] = new double[] { 1.3, 1.6, 0.6, 1.4, 0.3, 1.2, 0.5, 0.3 },
            ["coastfog"] = new double[] { 1.8, 1.4, 1, 0.6, 0.6, 0.4, 0.3, 0.4 },
            ["storms"] = new double[] { 1.4, 1.8, 0.8, 1.4, 0.3, 0.8, 0.3, 0.3 },
        };

        /// <summary>
        /// The way weather moves at t. Each twelve-hour spell gets its own heading, drawn from the region's
        /// usual directions for the spell's weather pattern, and the last three hours turn to the next one.
        /// Before, one heading per seed swung only about 50 degrees either way over days, so a seed could
        /// send every storm in from the south for good (seen 2026-10-29).
        /// </summary>
        private static double Steering(double t)
        {
            var k = (long)Math.Floor(t / SpellSeconds);
            var a = SpellHeading(k);
            var f = Smoothstep(SpellSeconds - TurnSeconds, SpellSeconds, t - k * SpellSeconds);
            if (f <= 0) return a;
            return a + Math.IEEERemainder(SpellHeading(k + 1) - a, Math.PI * 2) * f;
        }

        private static double SpellHeading(long k)
        {
            lock (Gate)
            {
                if (SteerCache.TryGetValue(k, out var cached)) return cached;
                var key = PatternOfPeriod((long)Math.Floor((k + 0.5) * SpellSeconds / PeriodSeconds));
                var bias = SteerBias.TryGetValue(key, out var b) ? b : null;
                var weights = new double[SteerWeights.Length];
                for (var i = 0; i < weights.Length; i++) weights[i] = SteerWeights[i] * (bias != null ? bias[i] : 1);
                var r = new Rng(Mix(Seed ^ 0x68e31da4, (int)k));
                var heading = SteerAngles[PickIndex(r.Next(), weights)] + (r.Next() - 0.5) * 0.6;
                if (SteerCache.Count > 4000) SteerCache.Clear();
                SteerCache[k] = heading;
                return heading;
            }
        }

        private static string PatternOfPeriod(long p)
        {
            lock (Gate)
            {
                if (_forcedPattern != null) return _forcedPattern;
                if (PatternCache.TryGetValue(p, out var key)) return key;

                var climate = CurrentClimate;
                var q = Math.Max(0, p - ChainFrom);
                var m = (int)(q / Mark);
                while (ChainMarks.Count <= m)
                {
                    var n = ChainMarks.Count;
                    if (n == 0)
                    {
                        ChainMarks.Add(PickIndex(new Rng(Mix(Seed, 0x2545f491)).Next(), climate.Weight));
                        continue;
                    }

                    var c = ChainMarks[n - 1];
                    for (long i = (long)(n - 1) * Mark; i < (long)n * Mark; i++) c = StepPattern(climate, c, i + ChainFrom);
                    ChainMarks.Add(c);
                }

                var k = ChainMarks[m];
                for (var i = (long)m * Mark; i < q; i++) k = StepPattern(climate, k, i + ChainFrom);

                if (PatternCache.Count > 4000) PatternCache.Clear();
                key = PatternKeys[k];
                PatternCache[p] = key;
                return key;
            }
        }

        /// <summary>The next period's pattern: the same again (the season's stay odds), or one of what it can turn into.</summary>
        private static int StepPattern(Climate climate, int k, long p)
        {
            var r = new Rng(Mix(Seed ^ 0x5bd1e995, (int)p));
            if (r.Next() < climate.Stay[k]) return k;
            var weights = new double[PatternKeys.Length];
            for (var n = 0; n < weights.Length; n++) weights[n] = Next[k][n] * climate.Weight[n];
            return PickIndex(r.Next(), weights);
        }

        private static int PickIndex(double x, double[] weights)
        {
            var total = 0.0;
            foreach (var w in weights) total += w;
            x *= total;
            for (var i = 0; i < weights.Length; i++)
            {
                x -= weights[i];
                if (x < 0) return i;
            }

            return weights.Length - 1;
        }

        /// <summary>The weather pattern at t, and when it ends (runs of the same pattern merged).</summary>
        public static string PatternAt(double t, out double end)
        {
            var p = (long)Math.Floor(t / PeriodSeconds);
            var key = PatternOfPeriod(p);
            var b = p;
            while (b < p + 6 && PatternOfPeriod(b + 1) == key) b++;
            end = (b + 1) * PeriodSeconds;
            return Patterns[key].Name;
        }

        /// <summary>A pattern's value (cloud, wind) eased from the last pattern's to this one's over the period's first hour.</summary>
        private static double PatternBlend(double t, Func<Pattern, double> value)
        {
            var p = (long)Math.Floor(t / PeriodSeconds);
            var cur = value(Patterns[PatternOfPeriod(p)]);
            var prev = value(Patterns[PatternOfPeriod(p - 1)]);
            return prev + (cur - prev) * Smoothstep(0, 3600, t - p * PeriodSeconds);
        }

        private static double PatternClouds(double t) => PatternBlend(t, p => p.Clouds) + CurrentClimate.Clouds;

        /// <summary>
        /// How much the hour drives showers: with the season's heating, mostly in the afternoon and rarely at
        /// night; without it, the same all day. About 1 averaged over a day either way.
        /// </summary>
        private static double ShowerHour(int hourOfDay, double heat)
        {
            var d = (hourOfDay + 0.5 - 15) / 3.2;
            return 1 - heat + heat * (0.25 + 2.6 * Math.Exp(-d * d));
        }

        // ---- storms ----------------------------------------------------------------------------

        private static Cell MakeCell(Rng r, double t0, string kindName, bool atStart)
        {
            var k = Kinds[kindName];
            // Pattern systems are born exactly at their hour, which is when ActiveCells starts looking at them.
            var birth = atStart ? t0 : t0 + r.Next() * 3600;
            var life = r.Range(k.Life0, k.Life1) * 3600;
            Wind(birth, out var dir, out _);
            var speed = k.SpeedFactor * (0.75 + 0.5 * r.Next()) * Speed;
            var width = r.Range(k.Width0, k.Width1);
            var length = kindName == "shower" ? width * r.Range(k.Length0, k.Length1) : r.Range(k.Length0, k.Length1);
            var ang = kindName == "shower" ? dir + (r.Next() - 0.5) * 1.4 : dir;
            var c = new Cell
            {
                Fog = k.IsFog, Plateau = k.Plateau, Birth = birth, Life = life,
                Vx = Math.Cos(dir) * speed, Vy = Math.Sin(dir) * speed, Ang = ang,
                Width = width, Length = length, Peak = r.Range(k.Peak0, k.Peak1), Trail = k.Trail,
                Curve = k.Curve * (r.Next() < 0.5 ? -1 : 1),
                Nx = (uint)Mix((int)(birth / 60), 0x3c6ef372) / 4294967296.0 * 512,
                Ny = (uint)Mix((int)(birth / 60), 0x1b873593) / 4294967296.0 * 512,
            };

            if (kindName == "coastfog")
            {
                // Lies along the coast (north-south), drifting only a little on or off shore. CellValue
                // moves it with the hour and roughs up its landward edge.
                c.Ang = 0; c.Vx = (r.Next() - 0.5) * 0.0006; c.Vy = 0; c.Coast = true;
                c.X0 = CoastX + (r.Next() - 0.5) * 24; c.Y0 = CoastY + (r.Next() - 0.5) * 40;
            }
            else if (kindName == "shield")
            {
                c.X0 = W / 2 - Math.Cos(dir) * 120 + (r.Next() - 0.5) * 120;
                c.Y0 = H / 2 - Math.Sin(dir) * 120 + (r.Next() - 0.5) * 80;
            }
            else
            {
                // Start upwind and spread across the wind, so most cells cross part of the region.
                var travel = speed * life * (kindName == "squall" || kindName == "front" ? 0.5 : 0.3 + 0.4 * r.Next());
                var side = (r.Next() - 0.5) * 520;
                c.X0 = W / 2 - Math.Cos(dir) * travel - Math.Sin(dir) * side;
                c.Y0 = H / 2 - Math.Sin(dir) * travel + Math.Cos(dir) * side;
            }

            return c;
        }

        private static List<Cell> CellsForHour(long h)
        {
            int generation;
            lock (Gate)
            {
                if (CellCache.TryGetValue(h, out var cached)) return cached;
                generation = _generation;
            }

            var r = new Rng(Mix(Seed, (int)h));
            var outList = new List<Cell>();
            var hourOfDay = (int)(((h % 24) + 24) % 24);
            var t0 = h * 3600.0;
            var period = (long)Math.Floor((t0 + 1800) / PeriodSeconds);
            var pat = Patterns[PatternOfPeriod(period)];
            var climate = CurrentClimate;
            var periodStart = h * 3600 % PeriodSeconds == 0;
            var showers = pat.Showers * ShowerHour(hourOfDay, climate.Heat);
            if (r.Next() < showers) outList.Add(MakeCell(r, t0, "shower", false));
            if (r.Next() < showers * 0.4) outList.Add(MakeCell(r, t0, "shower", false));
            if (r.Next() < pat.Fronts) outList.Add(MakeCell(r, t0, "front", false));
            if (periodStart && pat.Shield) outList.Add(MakeCell(r, t0, "shield", true));
            if (periodStart && pat.Squall) outList.Add(MakeCell(r, t0, "squall", true));
            if (periodStart && pat.Coast) outList.Add(MakeCell(r, t0, "coastfog", true));
            // Fog forms on calm nights, more readily when the ground is still wet from rain, and not in a wind.
            var prev = PatternOfPeriod((long)Math.Floor(t0 / PeriodSeconds) - 1);
            var wet = prev == "longrain" || prev == "unsettled" || prev == "storms" ? 1.6 : 1;
            Wind(t0, out _, out var strength);
            var calm = 1 - Smoothstep(0.35, 0.7, strength);
            var fogChance = (hourOfDay >= 1 && hourOfDay <= 8 ? 0.3 : 0.04) * pat.Fog * climate.Fog * wet * calm;
            if (r.Next() < fogChance) outList.Add(MakeCell(r, t0, "fog", false));

            lock (Gate)
            {
                // Made from a seed, season or forced pattern that has changed since (the radar renders on a worker).
                if (generation != _generation) return outList;
                if (CellCache.Count > 4000) CellCache.Clear();
                CellCache[h] = outList;
            }

            return outList;
        }

        public static List<Cell> ActiveCells(double t)
        {
            var h = (long)Math.Floor(t / 3600);
            var list = new List<Cell>();
            for (var k = h - 12; k <= h; k++)
            {
                foreach (var c in CellsForHour(k))
                {
                    if (t >= c.Birth && t <= c.Birth + c.Life) list.Add(c);
                }
            }

            return list;
        }

        // ---- noise -------------------------------------------------------------------------------

        private static double Lattice(int x, int y, int z) => (uint)Mix(Mix(Mix(Seed, x), y), z) / 4294967296.0;

        /// <summary>
        /// Value noise. With <paramref name="steadyZ"/> it changes along z at an even rate: the usual eased
        /// blend between z slices stops at every slice and starts again, which, played fast, reads as the
        /// weather swelling and shrinking back in place.
        /// </summary>
        private static double VNoise(double x, double y, double z, bool steadyZ = false)
        {
            var xi = (int)Math.Floor(x); var yi = (int)Math.Floor(y); var zi = (int)Math.Floor(z);
            double xf = x - xi, yf = y - yi, zf = z - zi;
            double u = xf * xf * (3 - 2 * xf), v = yf * yf * (3 - 2 * yf), w = steadyZ ? zf : zf * zf * (3 - 2 * zf);
            double L(double a, double b, double f) => a + (b - a) * f;
            return L(
                L(L(Lattice(xi, yi, zi), Lattice(xi + 1, yi, zi), u), L(Lattice(xi, yi + 1, zi), Lattice(xi + 1, yi + 1, zi), u), v),
                L(L(Lattice(xi, yi, zi + 1), Lattice(xi + 1, yi, zi + 1), u), L(Lattice(xi, yi + 1, zi + 1), Lattice(xi + 1, yi + 1, zi + 1), u), v), w);
        }

        private static double Fbm(double x, double y, double z, bool steadyZ = false)
        {
            double a = 0.5, sum = 0, norm = 0;
            for (var o = 0; o < 4; o++)
            {
                sum += a * VNoise(x, y, z, steadyZ); norm += a;
                x *= 2.03; y *= 2.03; z *= 1.6; a *= 0.5;
            }

            return sum / norm;
        }

        // ---- sampling ------------------------------------------------------------------------------

        /// <summary>How long (Tarkov seconds) a storm's inner texture takes to turn into another.</summary>
        private const double TextureLife = 7200;

        /// <summary>
        /// A cell's strength at a point, texture included. Everything is worked out in the cell's own frame,
        /// relative to its centre as it drifts with the wind, so the rain inside a storm travels with it the
        /// way radar echoes do and only slowly changes shape. (Texture drawn in the map's frame seethed in
        /// place while the storm's outline slid over it: "dancing" on playback, 2026-10-29.)
        /// </summary>
        private static double CellValue(Cell c, double x, double y, double t)
        {
            var age = (t - c.Birth) / c.Life;
            if (age < 0 || age > 1) return 0;
            var env = c.Plateau ? Smoothstep(0, 0.12, age) * (1 - Smoothstep(0.85, 1, age)) : Math.Pow(Math.Sin(Math.PI * age), 0.7);
            var dx = x - (c.X0 + c.Vx * (t - c.Birth));
            var dy = y - (c.Y0 + c.Vy * (t - c.Birth));
            if (c.Coast)
            {
                // A marine layer comes ashore overnight and pulls back offshore as the day warms, and its
                // edge is ragged, tongues of fog reaching inland with clear gaps between, never a ruler line.
                dx -= 20 * (FogHour(t) - 0.5);
                dx += 40 * (VNoise(y / 38 + 11.3, t / 5400, 3.7) - 0.5);
            }

            // Ragged edges: the point is pushed about by noise that rides with the cell.
            var z = (t - c.Birth) / TextureLife;
            var ox = 18 * (2 * VNoise(dx / 70 + c.Nx, dy / 70 + c.Ny, z, true) - 1);
            var oy = 18 * (2 * VNoise(dx / 70 + c.Nx + 31.7, dy / 70 + c.Ny + 17.3, z, true) - 1);
            dx += ox; dy += oy;

            double ca = Math.Cos(c.Ang), sa = Math.Sin(c.Ang);
            var along = dx * ca + dy * sa;
            var across = -dx * sa + dy * ca;
            along -= c.Curve * across * across;              // bows the band
            var w = along > 0 ? c.Width : c.Width * c.Trail;  // sharp leading edge, rain trailing behind
            var v = c.Peak * env * Math.Exp(-1.5 * (along * along / (w * w) + across * across / (c.Length * c.Length)));
            if (v < 0.002) return 0;

            // Fog in banks and thinner patches; rain broken into cells with heavier cores.
            if (c.Fog) return Math.Min(1, v * (0.3 + 1.1 * Fbm(dx / 48 + c.Nx + 5.1, dy / 48 + c.Ny + 9.3, z * 0.5, true)));
            var tex = Fbm(dx / 32 + c.Nx, dy / 32 + c.Ny, z, true);
            return Math.Min(1, v * Math.Max(0, 0.05 + 1.55 * tex * tex + 0.25 * tex));
        }

        /// <summary>Rain and fog strength (0..1) at a point of the frame.</summary>
        public static void At(List<Cell> cells, double x, double y, double t, out double rain, out double fog)
        {
            double dry = 1, clear = 1;
            foreach (var c in cells)
            {
                var v = CellValue(c, x, y, t);
                if (v <= 0) continue;
                if (c.Fog) clear *= 1 - v; else dry *= 1 - v;
            }

            rain = 1 - dry;
            fog = 1 - clear;

            // Fog mostly burns off by midday.
            if (fog > 0.01) fog *= FogHour(t);
        }

        /// <summary>
        /// Cloudiness on the game's scale (-1 clear .. 1 thundercloud, 1.2 under rain) at frame x: the
        /// pattern's sky, a slow wave across the region, and whatever rain is near. Locations and the radar
        /// both read it here, so the clouds on the map are the clouds in the forecast.
        /// </summary>
        private static double SkyClouds(double x, double t, double rain, bool raining)
        {
            var baseClouds = Math.Max(-1, Math.Min(1, PatternClouds(t) + 0.25 * Math.Sin(t / 9000.0 + x / 90)));
            return raining ? Math.Min(1.2, 0.85 + 0.4 * rain) : Math.Min(1, baseClouds + 1.6 * rain);
        }

        /// <summary>
        /// Cloud on the radar (0..1) at a point: the share of sky the cloudiness covers, drawn as broken
        /// cloud, so a partly cloudy spot has patches with gaps between and an overcast one is nearly solid.
        /// The patches change shape slowly over in-game hours.
        /// </summary>
        public static double CloudCover(double x, double y, double t, double rain)
        {
            var clouds = SkyClouds(x, t, rain, rain >= RainSteps[0]);
            var cover = Math.Max(0, Math.Min(1, (clouds + 0.9) / 1.9));
            var n = (CloudNoise(x, y, t) - 0.25) / 0.5;
            return Smoothstep(1 - cover - 0.28, 1 - cover + 0.28, n);
        }

        private const double CloudWindow = 6 * 3600;

        /// <summary>
        /// The broken-cloud pattern, carried along by the wind like the storms. The wind's direction is held
        /// for each six-hour window (so the drift needs no history), and the last hour and a half of a window
        /// fades into the next window's pattern.
        /// </summary>
        private static double CloudNoise(double x, double y, double t)
        {
            var k = Math.Floor(t / CloudWindow);
            var a = DriftingClouds(x, y, t, k);
            var s = Smoothstep(0.75, 1, (t - k * CloudWindow) / CloudWindow);
            return s <= 0 ? a : a + (DriftingClouds(x, y, t, k + 1) - a) * s;
        }

        private static double DriftingClouds(double x, double y, double t, double window)
        {
            var start = window * CloudWindow;
            Wind(start, out var dir, out _);
            var d = Speed * (t - start - CloudWindow / 2);
            var shift = window % 64 * 7.3;
            return Fbm((x - Math.Cos(dir) * d) / 60 + 3.7 + shift, (y - Math.Sin(dir) * d) / 60 + 1.9 + shift, t / 10800, true);
        }

        /// <summary>
        /// How much fog the hour allows: all of it from evening through mid-morning, a little over an eighth
        /// in the afternoon, when the sun has burnt it off. Every fog the field makes goes through this, so
        /// fog comes and goes with the day instead of sitting over the coast for whole patterns.
        /// </summary>
        private static double FogHour(double t)
        {
            var h = Schedule.Mod(t, Schedule.DaySeconds) / 3600;
            return 1 - 0.88 * Smoothstep(8.5, 12, h) * (1 - Smoothstep(17.5, 21, h));
        }

        private static int Level(double value, double[] steps, int? prev)
        {
            int up = 0, down = 0, plain = 0;
            foreach (var th in steps)
            {
                if (value >= th + Margin) up++;
                if (value >= th - Margin) down++;
                if (value >= th) plain++;
            }

            return prev == null ? plain : Math.Min(Math.Max(prev.Value, up), down);
        }

        /// <summary>
        /// A location's weather from <paramref name="from"/> to <paramref name="to"/>: the field sampled at
        /// its spot every <see cref="Step"/>, in the value ranges the game's weather uses. Levels carry a
        /// little hysteresis so a storm edge drifting over the location doesn't flicker.
        /// </summary>
        public static List<Block> Blocks(double x, double y, double from, double to)
        {
            var result = new List<Block>();
            var t = (long)Math.Floor(from / Step) * Step - 6 * Step;
            int? rl = null, fl = null;
            for (; t < to; t += Step)
            {
                At(ActiveCells(t), x, y, t, out var rain, out var fog);
                Wind(t, out var dir, out var strength);
                rl = Level(rain, RainSteps, rl);
                fl = Level(fog, FogSteps, fl);
                var clouds = SkyClouds(x, t, rain, rl > 0);
                if (t + Step <= from) continue; // warm-up samples only seed the hysteresis

                var windSpeed = Math.Min(4, Math.Round(strength * 3 + (rain > 0.5 ? 1 : 0)));
                var rainLevel = rl.Value > 0 ? rl.Value + 1 : 1;
                result.Add(new Block
                {
                    Start = t,
                    End = t + Step,
                    Clouds = Math.Round(clouds, 2),
                    Rain = rainLevel,
                    RainIntensity = Math.Round(rain, 2),
                    Fog = FogValues[fl.Value],
                    FogLevel = fl.Value,
                    // A blizzard: snow (not just light snow) in a strong wind; the game drives the Storm season's snow anyway.
                    Blizzard = Schedule.Winter && rainLevel >= 3 && (windSpeed >= 3 || _season == 6),
                    Thunder = !Schedule.Winter && rainLevel >= 3 && Electric(t),
                    WindSpeed = windSpeed,
                    WindDirection = DirectionIndex(dir),
                    WindGustiness = Math.Round(strength, 2),
                    Temperature = Schedule.Winter ? (Schedule.IsNight(t) ? -9 : -3) : (Schedule.IsNight(t) ? 9 : 17),
                    Pressure = 760,
                });
            }

            return result;
        }

        /// <summary>
        /// How far (frame units, 600 across the region) the nearest thunderstorm is from (x, y) at t: rain (not
        /// light rain, not snow) in an electric hour, looked for on rings out to <see cref="ThunderReach"/>.
        /// Null when there is none in reach. 0 when it is overhead.
        /// </summary>
        public static double? ThunderDistance(double x, double y, double t)
        {
            if (Schedule.Winter || !Electric(t)) return null;
            var cells = ActiveCells(t);
            At(cells, x, y, t, out var here, out _);
            if (here >= RainSteps[1]) return 0;
            foreach (var r in ThunderRings)
            {
                for (var k = 0; k < 8; k++)
                {
                    var a = k * Math.PI / 4;
                    At(cells, x + r * Math.Cos(a), y + r * Math.Sin(a), t, out var rain, out _);
                    if (rain >= RainSteps[1]) return r;
                }
            }

            return null;
        }

        /// <summary>Rings searched for thunderstorms, frame units; one unit is taken as 20 m, so the last is 10 km.</summary>
        private static readonly double[] ThunderRings = { 15, 30, 50, 75, 105, 140, 185, 250, 330, 420, 500 };

        /// <summary>
        /// 20 m a unit: the maps' icons sit about where the maps do, Customs to Woods some 1.5-2 km, Customs to
        /// Lighthouse 5-6 km. 0.2.2 took 40 m, twice the region's size, so a storm over the next map sounded
        /// 3-4 km off (Joel, 2026-10-30: "maps aren't that large"). Only thunder distances use it.
        /// </summary>
        public const double ThunderReach = 500, MetresPerUnit = 20;

        /// <summary>
        /// Whether heavy rain in the hour of <paramref name="t"/> is a thunderstorm: the season's thunder odds, three
        /// times as high in a storm front, drawn per hour from the seed so everyone sees the same storms.
        /// </summary>
        private static bool Electric(double t)
        {
            var climate = CurrentClimate;
            var odds = PatternOfPeriod((long)Math.Floor(t / PeriodSeconds)) == "storms" ? Math.Min(1, climate.Thunder * 3) : climate.Thunder;
            return new Rng(Mix(Seed ^ 0x27d4eb2d, (int)Math.Floor(t / 3600))).Next() < odds;
        }

        /// <summary>The game's wind direction index (WeatherNode.WindDirections 1..8) closest to an angle.</summary>
        private static int DirectionIndex(double dir)
        {
            double vx = Math.Cos(dir), vy = -Math.Sin(dir); // frame y is down, the game's is up
            int best = 1;
            var bestDot = double.MinValue;
            for (var i = 1; i < EFT.Weather.WeatherNode.WindDirections.Length; i++)
            {
                var d = EFT.Weather.WeatherNode.WindDirections[i];
                var len = Math.Sqrt(d.x * d.x + d.y * d.y);
                var dot = (vx * d.x + vy * d.y) / len;
                if (dot > bestDot) { bestDot = dot; best = i; }
            }

            return best;
        }
    }
}
