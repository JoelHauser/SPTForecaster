using System;
using UnityEngine;

namespace SPTForecaster
{
    /// <summary>
    /// Draws the field as weather radar into a pixel buffer. Pure maths, no Unity objects, so it runs on a
    /// worker thread. The weather is sampled on a grid, interpolated (Catmull-Rom) up to the output size,
    /// and coloured per pixel in stepped intensity bands with antialiased edges, the way weather radar
    /// shows rain. Light rain is a faint wash so the map shows through; only the small heavy cores get
    /// strong. Fog is a faint grey veil, and cloud a fainter white one underneath both, broken into
    /// patches when it is only partly cloudy.
    /// </summary>
    internal static class RadarRenderer
    {
        public const int Width = 480, Height = 320;
        private const int GridW = 121, GridH = 81;

        // [from intensity, r, g, b, opacity]
        private static readonly double[][] RainBands =
        {
            new double[] { 0.08, 118, 200, 110, 0.13 }, new double[] { 0.16, 56, 168, 72, 0.18 }, new double[] { 0.26, 28, 128, 52, 0.24 },
            new double[] { 0.38, 232, 214, 64, 0.32 }, new double[] { 0.52, 240, 150, 42, 0.4 }, new double[] { 0.68, 222, 56, 44, 0.48 },
            new double[] { 0.84, 168, 24, 72, 0.56 },
        };

        private static readonly double[][] SnowBands =
        {
            new double[] { 0.08, 150, 186, 232, 0.13 }, new double[] { 0.16, 118, 160, 226, 0.18 }, new double[] { 0.26, 86, 132, 214, 0.24 },
            new double[] { 0.38, 150, 200, 250, 0.32 }, new double[] { 0.52, 196, 224, 255, 0.4 }, new double[] { 0.68, 230, 240, 255, 0.48 },
            new double[] { 0.84, 255, 255, 255, 0.56 },
        };

        /// <summary>Cloud at its thickest: fainter than fog, so the map stays readable under overcast.</summary>
        private const float CloudAlpha = 0.14f;

        private static readonly int[] TapXi, TapYi;
        private static readonly float[] TapXw, TapYw;
        private static readonly float[] Edge;
        private static float[] _rainLut, _snowLut;

        static RadarRenderer()
        {
            Taps(Width, GridW, out TapXi, out TapXw);
            Taps(Height, GridH, out TapYi, out TapYw);
            Edge = EdgeMask(Width, Height);
        }

        /// <summary>
        /// How much of the radar shows at each pixel (row 0 at the top). Wide rain or fog otherwise stops in
        /// a straight line at the side of the buttons' container, which reads as the edge of a box (and an
        /// 8 % straight fade still did). So the radar fades out over a rounded rectangle (a superellipse),
        /// starting well inside the frame, and the line it fades along wobbles, so no edge is ever straight.
        /// </summary>
        private static float[] EdgeMask(int w, int h)
        {
            var mask = new float[w * h];
            for (var j = 0; j < h; j++)
            {
                for (var i = 0; i < w; i++)
                {
                    double u = 2.0 * i / (w - 1) - 1, v = 2.0 * j / (h - 1) - 1;
                    var r = Math.Pow(Math.Pow(Math.Abs(u), 4) + Math.Pow(Math.Abs(v), 4), 0.25);
                    var angle = Math.Atan2(v, u);
                    r += 0.05 * Math.Sin(angle * 5 + 1.3) + 0.035 * Math.Sin(angle * 11 + 0.4) + 0.02 * Math.Sin(angle * 23 + 2.1);
                    var s = Math.Min(1, Math.Max(0, (r - 0.72) / (0.98 - 0.72)));
                    mask[j * w + i] = (float)(1 - s * s * (3 - 2 * s));
                }
            }

            return mask;
        }

        private static void Taps(int n, int size, out int[] idx, out float[] wt)
        {
            idx = new int[n * 4];
            wt = new float[n * 4];
            for (var p = 0; p < n; p++)
            {
                var g = p * (size - 1) / (double)(n - 1);
                var i0 = (int)Math.Floor(g);
                double f = g - i0, f2 = f * f, f3 = f2 * f;
                double[] w = { -0.5 * f3 + f2 - 0.5 * f, 1.5 * f3 - 2.5 * f2 + 1, -1.5 * f3 + 2 * f2 + 0.5 * f, 0.5 * f3 - 0.5 * f2 };
                for (var q = 0; q < 4; q++)
                {
                    idx[p * 4 + q] = Math.Min(size - 1, Math.Max(0, i0 - 1 + q));
                    wt[p * 4 + q] = (float)w[q];
                }
            }
        }

        private static float[] Lut(bool winter)
        {
            if (winter && _snowLut != null) return _snowLut;
            if (!winter && _rainLut != null) return _rainLut;
            var bands = winter ? SnowBands : RainBands;
            var lut = new float[1024 * 4];
            const double aa = 0.009;
            for (var v = 0; v < 1024; v++)
            {
                double x = v / 1023.0, r = bands[0][1], g = bands[0][2], b = bands[0][3], a = 0;
                foreach (var band in bands)
                {
                    var s = Math.Min(1, Math.Max(0, (x - (band[0] - aa)) / (2 * aa)));
                    s = s * s * (3 - 2 * s);
                    r += (band[1] - r) * s; g += (band[2] - g) * s; b += (band[3] - b) * s; a += (band[4] - a) * s;
                }

                lut[v * 4] = (float)r; lut[v * 4 + 1] = (float)g; lut[v * 4 + 2] = (float)b; lut[v * 4 + 3] = (float)a;
            }

            if (winter) _snowLut = lut; else _rainLut = lut;
            return lut;
        }

        /// <summary>The radar at Tarkov second <paramref name="t"/>, bottom row first (Unity's texture order).</summary>
        public static Color32[] Render(double t, float opacity, bool winter)
        {
            var cells = Field.ActiveCells(t);
            var rain = new float[GridW * GridH];
            var fog = new float[GridW * GridH];
            var cloud = new float[GridW * GridH];
            for (int gj = 0, n = 0; gj < GridH; gj++)
            {
                for (var gi = 0; gi < GridW; gi++, n++)
                {
                    double x = gi * Field.W / (GridW - 1), y = gj * Field.H / (GridH - 1);
                    Field.At(cells, x, y, t, out var r, out var f);
                    rain[n] = (float)r; fog[n] = (float)f; cloud[n] = (float)Field.CloudCover(x, y, t, r);
                }
            }

            // Rows first, then columns.
            var wideRain = new float[GridH * Width];
            var wideFog = new float[GridH * Width];
            var wideCloud = new float[GridH * Width];
            for (var j = 0; j < GridH; j++)
            {
                int src = j * GridW, dst = j * Width;
                for (var i = 0; i < Width; i++)
                {
                    float r0 = 0, f0 = 0, c0 = 0;
                    for (var p = 0; p < 4; p++)
                    {
                        var w = TapXw[i * 4 + p];
                        var at = src + TapXi[i * 4 + p];
                        r0 += rain[at] * w; f0 += fog[at] * w; c0 += cloud[at] * w;
                    }

                    wideRain[dst + i] = r0; wideFog[dst + i] = f0; wideCloud[dst + i] = c0;
                }
            }

            var lut = Lut(winter);
            var pixels = new Color32[Width * Height];
            for (var j = 0; j < Height; j++)
            {
                var row = (Height - 1 - j) * Width; // frame y runs down; texture rows run up
                for (var i = 0; i < Width; i++)
                {
                    float r = 0, f = 0, c = 0;
                    for (var q = 0; q < 4; q++)
                    {
                        var wy = TapYw[j * 4 + q];
                        var at = TapYi[j * 4 + q] * Width + i;
                        r += wideRain[at] * wy; f += wideFog[at] * wy; c += wideCloud[at] * wy;
                    }

                    if (r < 0.06f && f < 0.15f && c < 0.03f) continue;
                    r = r < 0 ? 0 : r > 1 ? 1 : r;
                    f = f < 0 ? 0 : f > 1 ? 1 : f;
                    c = c < 0 ? 0 : c > 1 ? 1 : c;
                    var li = (int)Math.Round(r * 1023) * 4;
                    var ra = lut[li + 3];
                    var fs = Math.Min(1f, Math.Max(0f, (f - 0.15f) / 0.45f));
                    var fa = fs * fs * (3 - 2 * fs) * 0.16f * (1 - ra);
                    var ca = c * CloudAlpha * (1 - ra) * (1 - fa / 0.16f);
                    var a = (ra + fa + ca) * opacity * Edge[j * Width + i];
                    if (a < 0.01f) continue;
                    var sum = ra + fa + ca;
                    pixels[row + i] = new Color32(
                        (byte)((lut[li] * ra + 176 * fa + 214 * ca) / sum),
                        (byte)((lut[li + 1] * ra + 176 * fa + 218 * ca) / sum),
                        (byte)((lut[li + 2] * ra + 166 * fa + 216 * ca) / sum),
                        (byte)(Math.Min(1f, a) * 255));
                }
            }

            return pixels;
        }
    }
}
