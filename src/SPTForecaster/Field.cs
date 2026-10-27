using System;
using System.Collections.Generic;

namespace SPTForecaster
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
        private static readonly double[] FogSteps = { 0.38, 0.67 };
        private static readonly double[] FogValues = { 0.0013, 0.0045, 0.0065 };
        private const double Margin = 0.03;

        private sealed class Pattern
        {
            public string Name;
            public int Weight;
            public double Showers, Fronts, Fog, Clouds;
            public bool Shield, Squall, Coast;
        }

        private static readonly string[] PatternKeys = { "fair", "unsettled", "longrain", "coastfog", "storms" };

        private static readonly Dictionary<string, Pattern> Patterns = new Dictionary<string, Pattern>
        {
            ["fair"] = new Pattern { Name = "Fair weather", Weight = 30, Showers = 0.06, Fronts = 0, Fog = 0.5, Clouds = -0.75 },
            ["unsettled"] = new Pattern { Name = "Unsettled", Weight = 28, Showers = 0.85, Fronts = 0.14, Fog = 1, Clouds = 0.1 },
            ["longrain"] = new Pattern { Name = "Prolonged rain", Weight = 14, Showers = 0.35, Fronts = 0, Fog = 1, Clouds = 0.7, Shield = true },
            ["coastfog"] = new Pattern { Name = "Coastal fog", Weight = 14, Showers = 0.12, Fronts = 0, Fog = 1, Clouds = -0.3, Coast = true },
            ["storms"] = new Pattern { Name = "Storm front", Weight = 10, Showers = 0.55, Fronts = 0, Fog = 0.3, Clouds = 0.45, Squall = true },
        };

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
            public bool Fog, Plateau;
            public double Birth, Life, X0, Y0, Vx, Vy, Ang, Width, Length, Peak, Trail, Curve;
        }

        private static readonly object Gate = new object();
        private static readonly Dictionary<long, List<Cell>> CellCache = new Dictionary<long, List<Cell>>();
        private static readonly Dictionary<long, string> PatternCache = new Dictionary<long, string>();
        private static int _seed;

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
                    CellCache.Clear();
                    PatternCache.Clear();
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

        /// <summary>Direction storms drift (radians, frame axes) and strength 0..1.</summary>
        public static void Wind(double t, out double dir, out double strength)
        {
            var s = (Seed % 997 + 997) % 997 / 997.0 * Math.PI * 2;
            dir = s + 0.9 * Math.Sin(t / 150000 + s);
            strength = 0.5 + 0.5 * Math.Sin(t / 47000 + s * 1.7);
        }

        private static string PatternOfPeriod(long p)
        {
            lock (Gate)
            {
                if (PatternCache.TryGetValue(p, out var key)) return key;
                var r = new Rng(Mix(Seed ^ 0x5bd1e995, (int)p));
                var total = 0;
                foreach (var k in PatternKeys) total += Patterns[k].Weight;
                var x = r.Next() * total;
                key = PatternKeys[PatternKeys.Length - 1];
                foreach (var k in PatternKeys)
                {
                    x -= Patterns[k].Weight;
                    if (x < 0) { key = k; break; }
                }

                PatternCache[p] = key;
                return key;
            }
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

        private static double PatternClouds(double t)
        {
            var p = (long)Math.Floor(t / PeriodSeconds);
            var cur = Patterns[PatternOfPeriod(p)].Clouds;
            var prev = Patterns[PatternOfPeriod(p - 1)].Clouds;
            return prev + (cur - prev) * Smoothstep(0, 3600, t - p * PeriodSeconds);
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
            };

            if (kindName == "coastfog")
            {
                // Lies along the coast (north-south), drifting only a little on or off shore.
                c.Ang = 0; c.Vx = (r.Next() - 0.5) * 0.0006; c.Vy = 0;
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
            lock (Gate)
            {
                if (CellCache.TryGetValue(h, out var cached)) return cached;
            }

            var r = new Rng(Mix(Seed, (int)h));
            var outList = new List<Cell>();
            var hourOfDay = (int)(((h % 24) + 24) % 24);
            var t0 = h * 3600.0;
            var pat = Patterns[PatternOfPeriod((long)Math.Floor((t0 + 1800) / PeriodSeconds))];
            var periodStart = h * 3600 % PeriodSeconds == 0;
            if (r.Next() < pat.Showers) outList.Add(MakeCell(r, t0, "shower", false));
            if (r.Next() < pat.Showers * 0.4) outList.Add(MakeCell(r, t0, "shower", false));
            if (r.Next() < pat.Fronts) outList.Add(MakeCell(r, t0, "front", false));
            if (periodStart && pat.Shield) outList.Add(MakeCell(r, t0, "shield", true));
            if (periodStart && pat.Squall) outList.Add(MakeCell(r, t0, "squall", true));
            if (periodStart && pat.Coast) outList.Add(MakeCell(r, t0, "coastfog", true));
            if (r.Next() < (hourOfDay >= 1 && hourOfDay <= 8 ? 0.3 : 0.05) * pat.Fog) outList.Add(MakeCell(r, t0, "fog", false));

            lock (Gate)
            {
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

        private static double VNoise(double x, double y, double z)
        {
            var xi = (int)Math.Floor(x); var yi = (int)Math.Floor(y); var zi = (int)Math.Floor(z);
            double xf = x - xi, yf = y - yi, zf = z - zi;
            double u = xf * xf * (3 - 2 * xf), v = yf * yf * (3 - 2 * yf), w = zf * zf * (3 - 2 * zf);
            double L(double a, double b, double f) => a + (b - a) * f;
            return L(
                L(L(Lattice(xi, yi, zi), Lattice(xi + 1, yi, zi), u), L(Lattice(xi, yi + 1, zi), Lattice(xi + 1, yi + 1, zi), u), v),
                L(L(Lattice(xi, yi, zi + 1), Lattice(xi + 1, yi, zi + 1), u), L(Lattice(xi, yi + 1, zi + 1), Lattice(xi + 1, yi + 1, zi + 1), u), v), w);
        }

        private static double Fbm(double x, double y, double z)
        {
            double a = 0.5, sum = 0, norm = 0;
            for (var o = 0; o < 4; o++)
            {
                sum += a * VNoise(x, y, z); norm += a;
                x *= 2.03; y *= 2.03; z *= 1.6; a *= 0.5;
            }

            return sum / norm;
        }

        // ---- sampling ------------------------------------------------------------------------------

        private static double CellValue(Cell c, double x, double y, double t)
        {
            var age = (t - c.Birth) / c.Life;
            if (age < 0 || age > 1) return 0;
            var env = c.Plateau ? Smoothstep(0, 0.12, age) * (1 - Smoothstep(0.85, 1, age)) : Math.Pow(Math.Sin(Math.PI * age), 0.7);
            var dx = x - (c.X0 + c.Vx * (t - c.Birth));
            var dy = y - (c.Y0 + c.Vy * (t - c.Birth));
            double ca = Math.Cos(c.Ang), sa = Math.Sin(c.Ang);
            var along = dx * ca + dy * sa;
            var across = -dx * sa + dy * ca;
            along -= c.Curve * across * across;              // bows the band
            var w = along > 0 ? c.Width : c.Width * c.Trail;  // sharp leading edge, rain trailing behind
            return c.Peak * env * Math.Exp(-1.5 * (along * along / (w * w) + across * across / (c.Length * c.Length)));
        }

        /// <summary>Rain and fog strength (0..1) at a point of the frame.</summary>
        public static void At(List<Cell> cells, double x, double y, double t, out double rain, out double fog)
        {
            var z = t / 1800;
            var wx = x + 36 * (VNoise(x / 70, y / 70, z) - 0.5);
            var wy = y + 36 * (VNoise(x / 70 + 31.7, y / 70 + 17.3, z) - 0.5);
            double dry = 1, clear = 1;
            foreach (var c in cells)
            {
                var v = CellValue(c, wx, wy, t);
                if (c.Fog) clear *= 1 - v; else dry *= 1 - v;
            }

            rain = 1 - dry;
            fog = 1 - clear;
            if (rain > 0.01)
            {
                var tex = Fbm(wx / 32, wy / 32, z * 0.8);
                rain = Math.Min(1, rain * Math.Max(0, 0.05 + 1.55 * tex * tex + 0.25 * tex));
            }

            if (fog > 0.01) fog = Math.Min(1, fog * (0.75 + 0.4 * VNoise(wx / 90 + 5.1, wy / 90 + 9.3, z * 0.5)));
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
                var baseClouds = Math.Max(-1, Math.Min(1, PatternClouds(t) + 0.25 * Math.Sin(t / 9000.0 + x / 90)));
                var clouds = rl > 0 ? Math.Min(1.2, 0.85 + 0.4 * rain) : Math.Min(1, baseClouds + 1.6 * rain);
                if (t + Step <= from) continue; // warm-up samples only seed the hysteresis

                result.Add(new Block
                {
                    Start = t,
                    End = t + Step,
                    Clouds = Math.Round(clouds, 2),
                    Rain = rl.Value > 0 ? rl.Value + 1 : 1,
                    RainIntensity = Math.Round(rain, 2),
                    Fog = FogValues[fl.Value],
                    WindSpeed = Math.Min(4, Math.Round(strength * 3 + (rain > 0.5 ? 1 : 0))),
                    WindDirection = DirectionIndex(dir),
                    WindGustiness = Math.Round(strength, 2),
                    Temperature = Schedule.Winter ? (Schedule.IsNight(t) ? -9 : -3) : (Schedule.IsNight(t) ? 9 : 17),
                    Pressure = 760,
                });
            }

            return result;
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
