using System;
using System.Collections.Generic;
using System.IO;
using TMPro;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Mindstorm.Tyvo.Editor
{
    [Serializable]
    public class SFCompareResult
    {
        public string compareImage;          // reference on top, TYVO render below
        public float score;                  // 0-100, higher = closer
        public List<string> diffs = new List<string>();
        public SFAnalyzeResult reference;
        public SFAnalyzeResult render;
    }

    /// <summary>Renders the same text at the reference's capital height and measures both with the analyzer.</summary>
    public static class SFCompare
    {
        public static SFCompareResult Run(string fontAssetPath, string imagePath, string text)
        {
            var fa = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(fontAssetPath) ?? throw new Exception("Font asset not found: " + fontAssetPath);
            string work = SFPaths.WorkFolder(Path.GetFileNameWithoutExtension(fontAssetPath));
            var res = new SFCompareResult();
            res.reference = SFAnalyze.Analyze(imagePath, work + "/compare_reference_debug.png");

            // TMP: rendered capital height = fontSize * capHeight(sampling px) / samplingPointSize
            float capSampling = fa.faceInfo.capLine;
            var h = fa.characterLookupTable != null && fa.characterLookupTable.TryGetValue('H', out var hc) ? hc.glyph : null;
            if (h != null && h.metrics.height > 0) capSampling = h.metrics.height;
            if (capSampling <= 0) capSampling = fa.faceInfo.pointSize * 0.7f;
            float fontSize = res.reference.capHeightPx * fa.faceInfo.pointSize / capSampling;

            string renderPng = work + "/compare_render.png";
            SFRender.Render(new SFRenderRequest
            {
                fontAsset = fontAssetPath, text = string.IsNullOrEmpty(text) ? "Sample" : text,
                backgrounds = new[] { res.reference.background }, fontSize = fontSize, glyphSheet = false, output = renderPng
            });
            res.render = SFAnalyze.Analyze(renderPng, work + "/compare_render_debug.png");

            res.compareImage = work + "/compare.png";
            Stack(imagePath, renderPng, res.compareImage);

            // ---- differences (in % of capital height so image scale doesn't matter)
            float penalty = 0;
            var a = res.reference; var b = res.render;
            int n = Mathf.Max(a.strokeBands.Count, b.strokeBands.Count);
            if (a.strokeBands.Count != b.strokeBands.Count)
            {
                res.diffs.Add($"Band count: reference {a.strokeBands.Count}, render {b.strokeBands.Count} (a layer is missing or extra).");
                penalty += 20;
            }
            for (int i = 0; i < Mathf.Min(a.strokeBands.Count, b.strokeBands.Count); i++)
            {
                var ra = a.strokeBands[i]; var rb = b.strokeBands[i];
                float dw = rb.cap - ra.cap;
                float dc = SFAnalyze.ColorDist(ra.color, rb.color);
                string label = i == 0 ? "Outline" : i == 1 ? "Rim" : "Band " + (i + 1);
                if (Mathf.Abs(dw) >= 1.5f) res.diffs.Add($"{label} width: reference {ra.cap:0.#}% of cap height, render {rb.cap:0.#}% -> {(dw > 0 ? "thinner" : "thicker")} by {Mathf.Abs(dw):0.#}%.");
                if (dc >= 35f) res.diffs.Add($"{label} colour: reference {ra.color}, render {rb.color}.");
                penalty += Mathf.Abs(dw) * 2f + dc / 6f;
            }
            if (!string.IsNullOrEmpty(a.innerExtrudeColor) && SFAnalyze.ColorDist(a.innerExtrudeColor, b.innerExtrudeColor ?? "") >= 35f)
            { res.diffs.Add($"Inner extrude colour: reference {a.innerExtrudeColor}, render {(string.IsNullOrEmpty(b.innerExtrudeColor) ? "none" : b.innerExtrudeColor)}."); penalty += 8; }
            float de = b.extrudeCap - a.extrudeCap;
            if (Mathf.Abs(de) >= 2f) res.diffs.Add($"Extrude: reference {a.extrudeCap:0.#}%, render {b.extrudeCap:0.#}% -> {(de > 0 ? "less" : "more")} by {Mathf.Abs(de):0.#}%.");
            penalty += Mathf.Abs(de) * 1.5f;
            foreach (var (name, ra, rb) in new[] { ("Body top", a.bodyTop, b.bodyTop), ("Body middle", a.bodyMid, b.bodyMid), ("Body bottom", a.bodyBottom, b.bodyBottom) })
            {
                float dc = SFAnalyze.ColorDist(ra, rb);
                if (dc >= 35f) res.diffs.Add($"{name} colour: reference {ra}, render {rb}.");
                penalty += dc / 8f;
            }
            if (a.bodyGradient != b.bodyGradient) { res.diffs.Add(a.bodyGradient ? "Reference body is a gradient, render is flat." : "Reference body is flat, render is a gradient."); penalty += 10; }
            res.score = Mathf.Round(Mathf.Clamp(100f - penalty, 0, 100));
            if (res.diffs.Count == 0) res.diffs.Add("No measurable difference above the thresholds.");
            if (a.confidence < 0.5f) res.diffs.Add($"Reference analysis confidence is low ({a.confidence:0.00}); trust the side-by-side image over the numbers.");
            return res;
        }

        static void Stack(string topPath, string bottomPath, string outPath)
        {
            var t = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            var b = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            try
            {
                t.LoadImage(File.ReadAllBytes(topPath));
                b.LoadImage(File.ReadAllBytes(bottomPath));
                int W = Mathf.Max(t.width, b.width), gap = 6, H = t.height + b.height + gap;
                var o = new Texture2D(W, H, TextureFormat.RGBA32, false);
                var px = new Color32[W * H];
                for (int i = 0; i < px.Length; i++) px[i] = new Color32(255, 0, 255, 255);
                var bp = b.GetPixels32();
                for (int y = 0; y < b.height; y++) for (int x = 0; x < b.width; x++) px[y * W + x] = bp[y * b.width + x];
                var tp = t.GetPixels32();
                for (int y = 0; y < t.height; y++) for (int x = 0; x < t.width; x++) px[(y + b.height + gap) * W + x] = tp[y * t.width + x];
                o.SetPixels32(px); o.Apply();
                File.WriteAllBytes(outPath, o.EncodeToPNG());
                Object.DestroyImmediate(o);
            }
            finally { Object.DestroyImmediate(t); Object.DestroyImmediate(b); }
        }
    }
}
