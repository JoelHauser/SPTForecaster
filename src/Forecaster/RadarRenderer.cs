using System;
using UnityEngine;

namespace Forecaster
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

        // One colour per level the forecast card names, changing exactly where the card's level changes
        // (Field.RainSteps), so the colour over a map's button is the word on its card: light rain, rain,
        // heavy rain, and the heaviest cores (still "heavy rain" on the card). [r, g, b, opacity]
        private static readonly double[][] RainColours =
        {
            new double[] { 86, 190, 92, 0.2 }, new double[] { 236, 208, 58, 0.32 },
            new double[] { 232, 104, 40, 0.44 }, new double[] { 196, 28, 92, 0.56 },
        };

        private static readonly double[][] SnowColours =
        {
            new double[] { 168, 206, 246, 0.2 }, new double[] { 98, 150, 236, 0.32 },
            new double[] { 58, 92, 222, 0.44 }, new double[] { 70, 40, 176, 0.56 },
        };

        // Fog, at the strengths the card calls fog and dense fog (Field.FogSteps): mauve, so it can't be
        // mistaken for cloud (white), rain (green to magenta) or snow (blues). A sandy grey read as cloud.
        // Each strength its own shade, deeper and stronger as it thickens, the way the rain colours climb:
        // mist a pale lilac, fog mauve, dense fog orchid, thick fog deep purple. [r, g, b] and opacity.
        internal static readonly double[][] FogColours =
        {
            new double[] { 210, 200, 228 }, new double[] { 200, 150, 225 }, new double[] { 172, 100, 212 }, new double[] { 128, 56, 178 },
        };

        internal static readonly double[] FogOpacity = { 0.12, 0.2, 0.28, 0.36 }; // mist, fog, dense, thick

        /// <summary>The fog veil's colour and opacity: a step at each strength the card names (Field.FogSteps).</summary>
        private static float FogShade(float f, out float r, out float g, out float b)
        {
            double a = 0, cr = FogColours[0][0], cg = FogColours[0][1], cb = FogColours[0][2];
            double prev = 0;
            for (var i = 0; i < FogOpacity.Length; i++)
            {
                var s = Step(f, Field.FogSteps[i], 0.012);
                a += (FogOpacity[i] - prev) * s;
                prev = FogOpacity[i];
                cr += (FogColours[i][0] - cr) * s; cg += (FogColours[i][1] - cg) * s; cb += (FogColours[i][2] - cb) * s;
            }

            r = (float)cr; g = (float)cg; b = (float)cb;
            return (float)a;
        }
        internal static readonly double[] CloudColour = { 214, 218, 216 };

        /// <summary>The colours the key shows for rain (or snow), lightest level first.</summary>
        internal static double[][] Colours(bool winter) => winter ? SnowColours : RainColours;

        /// <summary>Cloud at its thickest: fainter than fog, so the map stays readable under overcast.</summary>
        private const float CloudAlpha = 0.14f;

        // Nothing is drawn below the first rain and fog levels: the card calls those dry and clear.
        private static readonly float MinRain = (float)Field.RainSteps[0] - 0.01f;
        private static readonly float MinFog = (float)Field.FogSteps[0] - 0.013f;

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
            var bands = Colours(winter);
            var lut = new float[1024 * 4];
            const double aa = 0.009;
            for (var v = 0; v < 1024; v++)
            {
                double x = v / 1023.0, r = bands[0][0], g = bands[0][1], b = bands[0][2], a = 0;
                for (var k = 0; k < bands.Length; k++)
                {
                    var s = Step(x, Field.RainSteps[k], aa);
                    r += (bands[k][0] - r) * s; g += (bands[k][1] - g) * s; b += (bands[k][2] - b) * s; a += (bands[k][3] - a) * s;
                }

                lut[v * 4] = (float)r; lut[v * 4 + 1] = (float)g; lut[v * 4 + 2] = (float)b; lut[v * 4 + 3] = (float)a;
            }

            if (winter) _snowLut = lut; else _rainLut = lut;
            return lut;
        }

        /// <summary>0 below <paramref name="edge"/>, 1 above, with an antialiased step <paramref name="aa"/> either side.</summary>
        private static double Step(double x, double edge, double aa)
        {
            var s = Math.Min(1, Math.Max(0, (x - (edge - aa)) / (2 * aa)));
            return s * s * (3 - 2 * s);
        }

        // Working buffers, reused: playback renders up to 30 frames a second, and fresh ~1 MB of arrays per
        // frame would keep the garbage collector (and so the menu) busy.
        private static readonly object Gate = new object();
        private static readonly float[] Rain = new float[GridW * GridH], Fog = new float[GridW * GridH], Cloud = new float[GridW * GridH];
        private static readonly float[] WideRain = new float[GridH * Width], WideFog = new float[GridH * Width], WideCloud = new float[GridH * Width];
        /// <summary>
        /// The radar at Tarkov second <paramref name="t"/> into <paramref name="into"/> (Width x Height, every
        /// pixel written), bottom row first (Unity's texture order). The caller owns the buffers, so playback
        /// can keep several frames rendered ahead.
        /// </summary>
        public static Color32[] Render(double t, float opacity, bool winter, Color32[] into)
        {
            lock (Gate) return RenderLocked(t, opacity, winter, into);
        }

        private static Color32[] RenderLocked(double t, float opacity, bool winter, Color32[] into)
        {
            var cells = Field.ActiveCells(t);
            float[] rain = Rain, fog = Fog, cloud = Cloud;
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
            float[] wideRain = WideRain, wideFog = WideFog, wideCloud = WideCloud;
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
            var pixels = into;
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

                    if (r < MinRain && f < MinFog && c < 0.03f)
                    {
                        pixels[row + i] = default;
                        continue;
                    }

                    r = r < 0 ? 0 : r > 1 ? 1 : r;
                    f = f < 0 ? 0 : f > 1 ? 1 : f;
                    c = c < 0 ? 0 : c > 1 ? 1 : c;
                    var li = (int)Math.Round(r * 1023) * 4;
                    var ra = lut[li + 3];
                    var fo = FogShade(f, out var fr, out var fg, out var fb);
                    var fa = fo * (1 - ra);
                    var ca = c * CloudAlpha * (1 - ra) * (1 - fo / (float)FogOpacity[FogOpacity.Length - 1]);
                    var a = (ra + fa + ca) * opacity * Edge[j * Width + i];
                    if (a < 0.01f)
                    {
                        pixels[row + i] = default;
                        continue;
                    }

                    var sum = ra + fa + ca;
                    pixels[row + i] = new Color32(
                        (byte)((lut[li] * ra + fr * fa + CloudColour[0] * ca) / sum),
                        (byte)((lut[li + 1] * ra + fg * fa + CloudColour[1] * ca) / sum),
                        (byte)((lut[li + 2] * ra + fb * fa + CloudColour[2] * ca) / sum),
                        (byte)(Math.Min(1f, a) * 255));
                }
            }

            return pixels;
        }
    }
}
