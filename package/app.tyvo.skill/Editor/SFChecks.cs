using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TMPro;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Mindstorm.Tyvo.Editor
{
    [Serializable]
    public class SFCheckReport
    {
        public bool passed;
        public string fontAsset;
        public string stylizedPng;
        public int padding;
        public int usedPadding;           // farthest painted pixel from any glyph edge
        public int recommendedPadding;    // set when effects are clipped
        public int bleedPixels;           // painted pixels outside every padded glyph rect
        public List<string> clippedGlyphs = new List<string>();
        public List<string> lostGlyphs = new List<string>();
        public List<string> errors = new List<string>();
        public List<string> warnings = new List<string>();
        public List<string> info = new List<string>();
    }

    /// <summary>All the checks the manual flow relied on people to remember.</summary>
    public static class SFChecks
    {
        const byte PaintedAlpha = 8;    // alpha above this counts as painted
        const byte EdgeAlpha = 24;      // alpha above this on the padded border = clipped effect

        public static SFCheckReport Run(string fontAssetPath)
        {
            var rep = new SFCheckReport { fontAsset = fontAssetPath };
            var fa = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(fontAssetPath);
            if (fa == null) { rep.errors.Add("Font asset not found: " + fontAssetPath); return rep; }
            var meta = SFMeta.Read(fontAssetPath);
            var style = meta != null ? SFStyles.Find(meta.style) : null;
            rep.padding = fa.atlasPadding;

            // ---- font asset
            if (fa.atlasPopulationMode != AtlasPopulationMode.Static)
                rep.errors.Add("Font asset is Dynamic. New characters would be added to the atlas and not exist in the stylized texture. Set Atlas Population Mode to Static.");
            if (fa.isMultiAtlasTexturesEnabled) rep.warnings.Add("Multi Atlas Textures is on; stylized fonts support one atlas only.");
            if (fa.atlasRenderMode.ToString().Contains("SDF"))
                rep.errors.Add($"Render mode is {fa.atlasRenderMode}. Stylized atlases need a bitmap mode (SMOOTH / RASTER).");
            float unitScale = meta != null && meta.unitScale > 0 ? meta.unitScale : 1f;
            if (style != null && fa.atlasPadding < style.RequiredPadding(unitScale))
                rep.warnings.Add($"Padding {fa.atlasPadding} is below the style's computed need of {style.RequiredPadding(unitScale)} px.");

            // ---- material
            var mat = fa.material;
            Texture2D atlasTex = null;
            if (mat == null) rep.errors.Add("Font asset has no material.");
            else
            {
                if (mat.shader == null || mat.shader.name != SFPaths.CustomAtlasShader)
                    rep.errors.Add($"Material shader is '{mat.shader?.name}', expected '{SFPaths.CustomAtlasShader}'.");
                if (mat.HasProperty("_Padding") && Mathf.Abs(mat.GetFloat("_Padding") - fa.atlasPadding) > 0.01f)
                    rep.errors.Add($"Material Padding is {mat.GetFloat("_Padding")} but the atlas was generated with {fa.atlasPadding}. They must match (less clips effects, more shows neighbour glyphs).");
                if (mat.HasProperty("_FaceColor") && mat.GetColor("_FaceColor") != Color.white)
                    rep.warnings.Add("Material Face Color is not white; it tints every text using this font.");
                atlasTex = mat.HasProperty("_MainTex") ? mat.GetTexture("_MainTex") as Texture2D : null;
                if (atlasTex == null) rep.errors.Add("Material has no Font Atlas texture.");
                else if (fa.atlasTextures != null && fa.atlasTextures.Contains(atlasTex))
                    rep.errors.Add("Material still uses the raw (unstylized) atlas. Apply the stylized PNG.");
            }

            // ---- stylized texture import settings
            string pngPath = atlasTex != null ? AssetDatabase.GetAssetPath(atlasTex) : meta?.stylizedPng;
            rep.stylizedPng = pngPath;
            if (!string.IsNullOrEmpty(pngPath) && AssetImporter.GetAtPath(pngPath) is TextureImporter ti)
            {
                if (ti.textureCompression != TextureImporterCompression.Uncompressed) rep.warnings.Add("Stylized atlas is compressed. Compression smears glyph edges and bleeds neighbours; use Uncompressed.");
                foreach (string p in new[] { "Android", "iPhone" })
                {
                    var s = ti.GetPlatformTextureSettings(p);
                    if (s.overridden && s.format != TextureImporterFormat.RGBA32) rep.warnings.Add($"{p} override uses {s.format}; use RGBA32 or remove the override.");
                }
                if (ti.mipmapEnabled) rep.warnings.Add("Mipmaps are on for the stylized atlas; turn them off (they blend neighbouring glyphs).");
                if (ti.npotScale != TextureImporterNPOTScale.None) rep.warnings.Add("Non-power-of-2 scaling is on; it moves glyph UVs. Set it to None.");
                if (ti.maxTextureSize < Mathf.Max(fa.atlasWidth, fa.atlasHeight)) rep.errors.Add($"Max Size {ti.maxTextureSize} is smaller than the atlas; it gets downscaled.");
                if (!ti.alphaIsTransparency) rep.warnings.Add("Alpha Is Transparency is off; edges may show dark/white halos.");
            }

            // ---- pixels
            if (!string.IsNullOrEmpty(pngPath) && File.Exists(SFPaths.Abs(pngPath)))
                AnalysePixels(fa, SFPaths.Abs(pngPath), style, unitScale, rep);
            else if (atlasTex != null)
                rep.warnings.Add("Stylized PNG file not found on disk; pixel checks skipped.");

            rep.passed = rep.errors.Count == 0;
            return rep;
        }

        static void AnalysePixels(TMP_FontAsset fa, string pngFile, SFStyle style, float unitScale, SFCheckReport rep)
        {
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            try
            {
                tex.LoadImage(File.ReadAllBytes(pngFile));
                int w = tex.width, h = tex.height;
                if (w != fa.atlasWidth || h != fa.atlasHeight)
                {
                    rep.errors.Add($"Stylized PNG is {w}x{h}; atlas is {fa.atlasWidth}x{fa.atlasHeight}. The canvas must not be resized or cropped.");
                    return;
                }
                var px = tex.GetPixels32(); // row 0 = bottom, same as TMP glyph rects
                int pad = fa.atlasPadding;

                var owner = new short[w * h];
                for (int i = 0; i < owner.Length; i++) owner[i] = -1;
                var glyphs = fa.glyphTable.Where(g => g.glyphRect.width > 0 && g.glyphRect.height > 0).ToList();
                var names = new Dictionary<uint, string>();
                foreach (var c in fa.characterTable)
                    if (!names.ContainsKey(c.glyphIndex)) names[c.glyphIndex] = c.unicode < 0xFFFF ? ((char)c.unicode).ToString() : $"U+{c.unicode:X4}";

                for (short gi = 0; gi < glyphs.Count; gi++)
                {
                    var r = glyphs[gi].glyphRect;
                    for (int y = Mathf.Max(0, r.y - pad); y < Mathf.Min(h, r.y + r.height + pad); y++)
                    for (int x = Mathf.Max(0, r.x - pad); x < Mathf.Min(w, r.x + r.width + pad); x++)
                        owner[y * w + x] = gi;
                }

                // Bleed: painted pixels that belong to no glyph cell.
                int bleed = 0;
                for (int i = 0; i < px.Length; i++) if (px[i].a > PaintedAlpha && owner[i] < 0) bleed++;
                rep.bleedPixels = bleed;
                if (bleed > 0) rep.errors.Add($"{bleed} painted pixels sit outside every glyph cell (effects spilled past the padding or the canvas moved). They will not render and may show on neighbours.");

                int usedMax = 0;
                bool sharedStyle = style != null && style.IsShared;
                int nonWhiteBody = 0, bodySamples = 0, coloredStroke = 0, strokeSamples = 0;
                for (int gi = 0; gi < glyphs.Count; gi++)
                {
                    var r = glyphs[gi].glyphRect;
                    string label = names.TryGetValue(glyphs[gi].index, out var n) ? n : "#" + glyphs[gi].index;
                    int x0 = Mathf.Max(0, r.x - pad), x1 = Mathf.Min(w, r.x + r.width + pad) - 1;
                    int y0 = Mathf.Max(0, r.y - pad), y1 = Mathf.Min(h, r.y + r.height + pad) - 1;

                    bool edge = false, any = false;
                    int used = 0;
                    for (int y = y0; y <= y1; y++)
                    for (int x = x0; x <= x1; x++)
                    {
                        var c = px[y * w + x];
                        if (c.a <= PaintedAlpha) continue;
                        any = true;
                        if ((x == x0 || x == x1 || y == y0 || y == y1) && c.a > EdgeAlpha) edge = true;
                        int dx = x < r.x ? r.x - x : (x >= r.x + r.width ? x - (r.x + r.width - 1) : 0);
                        int dy = y < r.y ? r.y - y : (y >= r.y + r.height ? y - (r.y + r.height - 1) : 0);
                        used = Mathf.Max(used, Mathf.Max(dx, dy));

                        if (sharedStyle)
                        {
                            bool inside = dx == 0 && dy == 0 && c.a >= 250;
                            if (inside) { bodySamples++; if (Mathf.Min(c.r, Mathf.Min(c.g, c.b)) < 235) nonWhiteBody++; }
                            else if (dx > 0 || dy > 0) { strokeSamples++; if (Mathf.Max(c.r, Mathf.Max(c.g, c.b)) > 40) coloredStroke++; }
                        }
                    }
                    usedMax = Mathf.Max(usedMax, used);
                    if (!any) rep.lostGlyphs.Add(label);
                    else if (edge) rep.clippedGlyphs.Add(label);
                }
                rep.usedPadding = usedMax;

                if (rep.lostGlyphs.Count > 0)
                    rep.errors.Add($"{rep.lostGlyphs.Count} glyphs are empty in the stylized PNG: {Short(rep.lostGlyphs)}");
                if (rep.clippedGlyphs.Count > 0)
                {
                    // Clipped pixels can't be measured, so take the larger of "a bit more than now" and the style's computed need.
                    rep.recommendedPadding = Mathf.Max(Mathf.Max(pad + 2, usedMax + 3), style != null ? style.RequiredPadding(unitScale) : 0);
                    rep.errors.Add($"{rep.clippedGlyphs.Count} glyphs touch the edge of their cell, so the effect is cut off: {Short(rep.clippedGlyphs)}. Regenerate with padding {rep.recommendedPadding}.");
                }
                else rep.info.Add($"Effects use {usedMax}px of the {pad}px padding.");

                if (sharedStyle)
                {
                    if (bodySamples > 0 && nonWhiteBody > bodySamples * 0.02f)
                        rep.errors.Add($"Shared style: {nonWhiteBody * 100 / bodySamples}% of body pixels are not white, so the TMP text color cannot tint them.");
                    if (strokeSamples > 0 && coloredStroke > strokeSamples * 0.02f)
                        rep.errors.Add($"Shared style: {coloredStroke * 100 / strokeSamples}% of stroke pixels are not black, so they will not take the button color.");
                }
            }
            finally
            {
                Object.DestroyImmediate(tex);
            }
        }

        static string Short(List<string> items) => string.Join(" ", items.Take(30)) + (items.Count > 30 ? $" (+{items.Count - 30} more)" : "");

        public static string Format(SFCheckReport rep)
        {
            var lines = new List<string> { $"{(rep.passed ? "PASS" : "FAIL")}  {rep.fontAsset}" };
            lines.AddRange(rep.errors.Select(e => "  ERROR  " + e));
            lines.AddRange(rep.warnings.Select(e => "  WARN   " + e));
            lines.AddRange(rep.info.Select(e => "  INFO   " + e));
            return string.Join("\n", lines);
        }
    }
}
