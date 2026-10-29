using System;
using System.Collections.Generic;
using UnityEngine;

namespace SPTForecaster
{
    /// <summary>
    /// Our own weather glyphs, for weather the game has no icon for: snow, blizzard, mist, dense and thick fog,
    /// and night skies (the game's EWeatherType names ClearNight, PartlyCloudNight and ClearFogNight, but no
    /// sprite ships for them). Drawn by tools/glyphs.py in the look of the game's UI/WeatherIcons (one flat
    /// colour, ~3.3 px strokes at 48 px, cropped tight) and embedded in the DLL; made into sprites once.
    /// </summary>
    internal static class Glyphs
    {
        private static readonly Dictionary<string, Sprite> Cache = new Dictionary<string, Sprite>();

        /// <summary>The glyph's sprite, or null if there is no such glyph (or it couldn't be loaded).</summary>
        public static Sprite Get(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            if (Cache.TryGetValue(name, out var cached)) return cached;

            Sprite sprite = null;
            try
            {
                using (var stream = typeof(Glyphs).Assembly.GetManifestResourceStream("SPTForecaster.Glyphs." + name + ".png"))
                {
                    if (stream != null)
                    {
                        var bytes = new byte[stream.Length];
                        var read = 0;
                        while (read < bytes.Length) read += stream.Read(bytes, read, bytes.Length - read);
                        var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
                        texture.LoadImage(bytes, true);
                        sprite = Sprite.Create(texture, new Rect(0, 0, texture.width, texture.height), new Vector2(0.5f, 0.5f), 100f);
                        sprite.name = "SPTForecaster." + name;
                    }
                    else
                    {
                        ForecasterPlugin.Log.LogWarning("No glyph called " + name + ".");
                    }
                }
            }
            catch (Exception ex)
            {
                ForecasterPlugin.Log.LogError("Could not load the " + name + " glyph: " + ex.Message);
            }

            Cache[name] = sprite;
            return sprite;
        }
    }
}
