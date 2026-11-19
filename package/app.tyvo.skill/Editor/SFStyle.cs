using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace Mindstorm.Tyvo.Editor
{
    /// <summary>One Photoshop layer effect. Unset numbers are -1 (Photoshop defaults apply).</summary>
    [Serializable]
    public class SFEffect
    {
        public string type;              // colorOverlay, gradientOverlay, stroke, dropShadow, innerShadow, outerGlow, innerGlow, bevel
        public bool enabled = true;
        public string color;
        public string blend;
        public string position;          // stroke: outside | inside | center
        public string direction;         // bevel: up | down
        public float opacity = -1;
        public float size = -1;
        public float distance = -1;
        public float spread = -1;
        public float angle = -1;
    }

    [Serializable]
    public class SFStyleLayer
    {
        public string name;
        public float[] offset;
        public float opacity = -1;
        public string blend;
        public string group;             // consecutive layers with the same group share one isolated group
        public float groupOpacity = -1;  // opacity of that group (e.g. 50 for a shared black outline + extrude)
        public SFEffect[] effects;
    }

    [Serializable]
    public class SFShared
    {
        public float strokeAlpha;
        public string[] testBackgrounds;
    }

    /// <summary>A style preset (JSON). Same file is read by the Photoshop scripts.</summary>
    [Serializable]
    public class SFStyle
    {
        public string id;
        public string name;
        public string track;             // "photoshop" (full style) or "shared" (white body + black alpha stroke, reusable)
        public string description;
        public int padding;              // minimum padding in atlas px (0 = computed from the effects)
        public string units;             // "px" (default) or "cap": size, distance, offset and letterSpacing are % of capital-letter height
        public float letterSpacing;      // extra advance per glyph, baked into the font asset so outlines don't paint over the next letter
        public bool autoLetterSpacing;   // when letterSpacing is 0: use 0.8 x the horizontal outline reach
        public SFShared shared;
        public SFStyleLayer[] layers;

        [NonSerialized] public string path;
        [NonSerialized] public string json;

        public bool IsShared => string.Equals(track, "shared", StringComparison.OrdinalIgnoreCase);
        public bool IsCapUnits => string.Equals(units, "cap", StringComparison.OrdinalIgnoreCase);

        /// <summary>Atlas pixels per style unit. capHeightPx = height of "H" in the atlas at the sampling size.</summary>
        public float UnitScale(float capHeightPx) => IsCapUnits ? capHeightPx / 100f : 1f;

        /// <summary>How far (atlas px) the style paints outside the glyph shape.</summary>
        public float EffectExtent(float scale = 1f)
        {
            float max = 0;
            if (layers == null) return 0;
            foreach (var layer in layers)
            {
                float offset = 0;
                if (layer.offset != null && layer.offset.Length >= 2)
                    offset = Mathf.Max(Mathf.Abs(layer.offset[0]), Mathf.Abs(layer.offset[1]));
                // Shadows and outer glows are cast by the stroked shape, so their reach adds on top of the stroke.
                float stroke = 0, cast = 0;
                if (layer.effects != null)
                {
                    foreach (var e in layer.effects)
                    {
                        if (e == null || !e.enabled) continue;
                        if (e.type == "stroke") stroke = Mathf.Max(stroke, Extent(e));
                        else cast = Mathf.Max(cast, Extent(e));
                    }
                }
                max = Mathf.Max(max, (offset + stroke + cast) * scale);
            }
            return max;
        }

        /// <summary>Sideways reach (strokes, glows, horizontal offsets) – what makes neighbouring letters collide.</summary>
        public float HorizontalReach(float scale = 1f)
        {
            float max = 0;
            if (layers == null) return 0;
            foreach (var layer in layers)
            {
                float ox = layer.offset != null && layer.offset.Length >= 1 ? Mathf.Abs(layer.offset[0]) : 0;
                float stroke = 0, glow = 0;
                if (layer.effects != null)
                    foreach (var e in layer.effects)
                    {
                        if (e == null || !e.enabled) continue;
                        if (e.type == "stroke") stroke = Mathf.Max(stroke, Extent(e));
                        else if (e.type == "outerGlow") glow = Mathf.Max(glow, Extent(e));
                    }
                max = Mathf.Max(max, (ox + stroke + glow) * scale);
            }
            return max;
        }

        public float LetterSpacingPx(float scale = 1f)
        {
            if (letterSpacing > 0) return letterSpacing * scale;
            // 0.8 x reach: coloured outlines stay off the neighbour's body while outer rims of the same colour merge (game-title look)
            return autoLetterSpacing ? Mathf.Round(HorizontalReach(scale) * 0.8f) : 0f;
        }

        static float Val(float v, float def) => v < 0 ? def : v;

        static float Extent(SFEffect e)
        {
            switch (e.type)
            {
                case "stroke":
                    float size = Val(e.size, 3);
                    switch (e.position)
                    {
                        case "inside": return 0;
                        case "center": return size * 0.5f;
                        default: return size;
                    }
                case "dropShadow":
                    return Val(e.distance, 3) + Val(e.size, 0);
                case "outerGlow":
                    return Val(e.size, 5);
                case "bevel":
                    return e.direction == "down" ? Val(e.size, 3) : 0;
                default:
                    return 0;
            }
        }

        /// <summary>Padding (atlas px) so nothing is clipped: effect extent + 2 px anti-aliasing margin, never below the style minimum.</summary>
        public int RequiredPadding(float scale = 1f) => Mathf.Max(padding, Mathf.CeilToInt(EffectExtent(scale)) + 2);

        /// <summary>Stable key used to find an existing atlas that already has this exact look.</summary>
        public string Signature()
        {
            string body = JsonUtility.ToJson(new SFStyleSignature { layers = layers });
            return id + ":" + Hash(body);
        }

        [Serializable]
        class SFStyleSignature { public SFStyleLayer[] layers; }

        static string Hash(string s)
        {
            unchecked
            {
                uint h = 2166136261;
                foreach (char c in s) { h ^= c; h *= 16777619; }
                return h.ToString("x8");
            }
        }
    }

    public static class SFStyles
    {
        public const string ProjectStylesFolder = "Assets/StylizedFonts/Styles";

        /// <summary>All styles: project styles first (they override package styles with the same id).</summary>
        public static List<SFStyle> All()
        {
            var result = new List<SFStyle>();
            var seen = new HashSet<string>();
            foreach (string dir in new[] { Path.GetFullPath(ProjectStylesFolder), SFPaths.PackageStylesFolder })
            {
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) continue;
                foreach (string file in Directory.GetFiles(dir, "*.json"))
                {
                    var style = Load(file);
                    if (style == null || string.IsNullOrEmpty(style.id) || !seen.Add(style.id)) continue;
                    result.Add(style);
                }
            }
            return result;
        }

        public static SFStyle Load(string file)
        {
            try
            {
                string text = File.ReadAllText(file);
                var style = JsonUtility.FromJson<SFStyle>(text);
                if (style == null) return null;
                style.path = Path.GetFullPath(file).Replace('\\', '/');
                style.json = text;
                if (string.IsNullOrEmpty(style.name)) style.name = style.id;
                return style;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[TYVO] Could not read style {file}: {ex.Message}");
                return null;
            }
        }

        public static SFStyle Find(string idOrPath)
        {
            if (string.IsNullOrEmpty(idOrPath)) return null;
            if (idOrPath.EndsWith(".json", StringComparison.OrdinalIgnoreCase) && File.Exists(idOrPath)) return Load(idOrPath);
            foreach (var s in All())
                if (string.Equals(s.id, idOrPath, StringComparison.OrdinalIgnoreCase) || string.Equals(s.name, idOrPath, StringComparison.OrdinalIgnoreCase))
                    return s;
            return null;
        }
    }
}
