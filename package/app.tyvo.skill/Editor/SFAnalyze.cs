using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Mindstorm.Tyvo.Editor
{
    [Serializable]
    public class SFBand
    {
        public string color;     // hex
        public float px;         // width in reference pixels
        public float cap;        // width in % of capital height
        public int samples;
    }

    [Serializable]
    public class SFAnalyzeResult
    {
        public string image;
        public int width, height;
        public string background;
        public float capHeightPx;
        public string bodyTop, bodyMid, bodyBottom;
        public bool bodyGradient;
        public List<SFBand> strokeBands = new List<SFBand>();  // the outline stack used for the style (from the body outward)
        public List<SFBand> sideBands = new List<SFBand>();    // from the body outward, measured sideways
        public List<SFBand> topBands = new List<SFBand>();
        public List<SFBand> bottomBands = new List<SFBand>();
        public float extrudePx, extrudeCap;
        public string extrudeColor;
        public string innerExtrudeColor;                         // body-side band seen only below the letters (body copy shifted down)
        public float innerExtrudePx, innerExtrudeCap;
        public int columnsUsed, rowsUsed;
        public float confidence;
        public List<string> notes = new List<string>();
        public string debugImage;
        public string draftStyle;                                // path of the written draft style json (if requested)
    }

    /// <summary>
    /// Reads a reference title image and estimates its layer stack: body (flat or vertical gradient), concentric
    /// bands around it (outline, rim, ...), and a downward extrude. Output is a draft "cap" style.
    /// Works best on a clean, uncompressed image of the text on a fairly plain background.
    /// </summary>
    public static class SFAnalyze
    {
        const float Edge = 30f;      // colour jump (RGB distance) that starts a new band
        const int MaxBands = 4;

        struct Seg { public int a, b, n; public Vector3 sum; public Color32 first, last; public int Len => b - a; public Vector3 Mean => sum / Mathf.Max(1, n); }

        public static SFAnalyzeResult Analyze(string imagePath, string debugOut = null)
        {
            if (!File.Exists(imagePath)) throw new Exception("Image not found: " + imagePath);
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            try
            {
                if (!tex.LoadImage(File.ReadAllBytes(imagePath))) throw new Exception("Unsupported image (use PNG or JPG): " + imagePath);
                int w = tex.width, h = tex.height;
                var raw = tex.GetPixels32();
                // top-down access
                Color32 P(int x, int y) => raw[(h - 1 - y) * w + x];

                var res = new SFAnalyzeResult { image = imagePath.Replace('\\', '/'), width = w, height = h };
                Vector3 bg = BorderColor(raw, w, h);
                res.background = Hex(bg);

                // ---------- vertical scans: body = a run wrapped by the SAME colour left, right and above
                // (an outline running down a letter's side has body on one side and rim on the other).
                // Plain text without outlines has no such run: fall back to the longest run.
                var vScans = new List<(int x, List<Seg> segs, int body)>();
                var plain = new List<(int x, List<Seg> segs, int body)>();
                for (int x = 0; x < w; x++)
                {
                    var segs = Segments(i => P(x, i), h);
                    int body = WrappedInternal(segs, x, w, P, bg);
                    if (body >= 0) vScans.Add((x, segs, body));
                    int longest = LongestInternal(segs, bg);
                    if (longest >= 0) plain.Add((x, segs, longest));
                }
                if (vScans.Count < 5) { vScans = plain; res.notes.Add("No outlined body found: treated as plain text."); }
                if (vScans.Count == 0) throw new Exception("No text found. Crop the image to the title and make sure it is on a plain background.");
                int maxBody = vScans.Max(s => s.segs[s.body].Len);
                if (maxBody < 6) throw new Exception("No text found. Crop the image to the title and make sure it is on a plain background.");
                var valid = vScans.Where(s => s.segs[s.body].Len >= maxBody * 0.6f).ToList();
                res.columnsUsed = valid.Count;

                // capital height: median of the tallest runs (lowercase x-height pulls a plain median down,
                // ascenders like l/b/d only add a little on top)
                var bodyLens = valid.Select(s => (float)s.segs[s.body].Len).Where(v => v >= maxBody * 0.85f).OrderBy(v => v).ToList();
                res.capHeightPx = bodyLens[bodyLens.Count / 2];
                float cap = Mathf.Max(1, res.capHeightPx);

                // body colour profile (top, middle, bottom of each stem)
                Vector3 top = Vector3.zero, mid = Vector3.zero, bot = Vector3.zero;
                foreach (var s in valid)
                {
                    var seg = s.segs[s.body];
                    top += C(P(s.x, seg.a + Mathf.Max(1, seg.Len / 10)));
                    mid += C(P(s.x, seg.a + seg.Len / 2));
                    bot += C(P(s.x, seg.b - 1 - Mathf.Max(1, seg.Len / 10)));
                }
                top /= valid.Count; mid /= valid.Count; bot /= valid.Count;
                res.bodyTop = Hex(top); res.bodyMid = Hex(mid); res.bodyBottom = Hex(bot);
                res.bodyGradient = Vector3.Distance(top, bot) > 30f;

                var bodyCols = new[] { top, mid, bot };
                var topSeqs = valid.Select(s => Walk(s.segs, s.body, -1, bg, cap, bodyCols)).ToList();
                var botSeqs = valid.Select(s => Walk(s.segs, s.body, +1, bg, cap, bodyCols)).ToList();
                res.topBands = Aggregate(topSeqs, cap, out float topConf);
                res.bottomBands = Aggregate(botSeqs, cap, out float botConf);

                // ---------- horizontal scans through the middle of the text: true side widths
                var bodyTops = valid.Select(s => s.segs[s.body].a).OrderBy(v => v).ToList();
                var bodyBots = valid.Select(s => s.segs[s.body].b).OrderBy(v => v).ToList();
                int textTop = bodyTops[bodyTops.Count / 2], textBot = bodyBots[bodyBots.Count / 2];
                var sideSeqs = new List<List<(Vector3 c, int len)>>();
                int rows = 0;
                for (int y = textTop + (textBot - textTop) * 3 / 10; y < textTop + (textBot - textTop) * 7 / 10; y++)
                {
                    var segs = Segments(i => P(i, y), w);
                    rows++;
                    for (int i = 1; i < segs.Count - 1; i++)
                    {
                        // stems: body-coloured runs, not too wide (stems are thinner than the cap height)
                        if (segs[i].Len < 3 || segs[i].Len > cap * 0.9f) continue;
                        if (Vector3.Distance(segs[i].Mean, mid) > 60f) continue;
                        sideSeqs.Add(Walk(segs, i, -1, bg, cap, bodyCols));
                        sideSeqs.Add(Walk(segs, i, +1, bg, cap, bodyCols));
                    }
                }
                res.rowsUsed = rows;
                res.sideBands = Aggregate(sideSeqs, cap, out float sideConf);

                // ---------- stroke stack: bands above the letters are the cleanest (no extrude, no neighbours)
                res.strokeBands = res.topBands.Count >= res.sideBands.Count || res.sideBands.Count == 0 ? res.topBands : res.sideBands;
                var stack = res.strokeBands;
                float stackPx = stack.Sum(b => b.px);

                // ---------- extrude: what is below the letters that is not in the stroke stack
                var below = new List<SFBand>(res.bottomBands);
                if (below.Count > 0 && stack.Count > 0 && ColorDist(below[0].color, stack[0].color) > 40f)
                {
                    // first band under the body has its own colour: the body is copied downward in that colour
                    res.innerExtrudeColor = below[0].color;
                    res.innerExtrudePx = below[0].px;
                    res.innerExtrudeCap = below[0].cap;
                    below.RemoveAt(0);
                }
                float extra = Mathf.Max(0, Mathf.Round(below.Sum(b => b.px) - stackPx));
                res.extrudePx = res.innerExtrudePx + extra;
                res.extrudeCap = Pct(res.extrudePx, cap);
                if (below.Count > stack.Count)
                    for (int i = 0; i < below.Count; i++)
                        if (i >= stack.Count || ColorDist(below[i].color, stack[i].color) > 40f) { res.extrudeColor = below[i].color; break; }

                res.confidence = Mathf.Round(Mathf.Clamp01((topConf + botConf + sideConf) / 3f * Mathf.Clamp01(valid.Count / 40f)) * 100f) / 100f;
                if (stack.Count == 0) res.notes.Add("No outline bands found (plain text, or the background is too close to the outline colour).");
                if (res.sideBands.Count > 0 && Mathf.Abs(res.sideBands.Sum(b => b.px) - stackPx) > 3)
                    res.notes.Add($"Side bands ({res.sideBands.Sum(b => b.px):0}px) differ from top bands ({stackPx:0}px): letters may touch, or there is a sideways offset. Top bands were used.");
                if (!string.IsNullOrEmpty(res.innerExtrudeColor)) res.notes.Add($"Inner extrude: body copied {res.extrudePx:0}px down in {res.innerExtrudeColor}; the outline wraps around it.");
                if (res.confidence < 0.5f) res.notes.Add("Low confidence: crop tighter, use a PNG (not JPG) and a plain background, or describe the layers.");
                if (Mathf.Max(w, h) < 200) res.notes.Add("Small image: band widths are only accurate to about 1 px.");

                if (!string.IsNullOrEmpty(debugOut)) { WriteDebug(raw, w, h, res, debugOut); res.debugImage = debugOut.Replace('\\', '/'); }
                return res;
            }
            finally { Object.DestroyImmediate(tex); }
        }

        // ------------------------------------------------------------------ draft style
        public static string DraftStyleJson(SFAnalyzeResult a, string id, string name)
        {
            var inv = CultureInfo.InvariantCulture;
            string F(float v) => v.ToString("0.#", inv);
            var sb = new StringBuilder();
            sb.Append("{\n");
            sb.Append($"  \"id\": \"{id}\",\n  \"name\": \"{name}\",\n  \"track\": \"photoshop\",\n");
            sb.Append($"  \"description\": \"Draft from {Path.GetFileName(a.image)} (analyzer confidence {a.confidence:0.00}). Sizes are % of capital height.\",\n");
            sb.Append("  \"units\": \"cap\",\n  \"padding\": 0,\n  \"autoLetterSpacing\": true,\n  \"layers\": [\n");

            var layers = new List<string>();
            float cumulative = 0;
            var stack = new List<(string name, string color, float size, string extrudeColor)>();
            for (int i = 0; i < a.strokeBands.Count; i++)
            {
                cumulative += a.strokeBands[i].cap;
                string label = i == 0 ? "Outline" : (i == 1 ? "Rim" : "Band " + (i + 1));
                string ext = i == 0 && !string.IsNullOrEmpty(a.extrudeColor) ? a.extrudeColor : a.strokeBands[i].color;
                stack.Add((label, a.strokeBands[i].color, cumulative, ext));
            }
            // outermost first (bottom of the Photoshop stack)
            for (int i = stack.Count - 1; i >= 0; i--)
            {
                var s = stack[i];
                if (a.extrudeCap > 0.5f)
                    layers.Add($"    {{ \"name\": \"{s.name} Extrude\", \"offset\": [0, {F(a.extrudeCap)}], \"effects\": [ {{ \"type\": \"colorOverlay\", \"color\": \"{s.extrudeColor}\" }}, {{ \"type\": \"stroke\", \"position\": \"outside\", \"size\": {F(s.size)}, \"color\": \"{s.extrudeColor}\" }} ] }}");
                layers.Add($"    {{ \"name\": \"{s.name}\", \"effects\": [ {{ \"type\": \"colorOverlay\", \"color\": \"{s.color}\" }}, {{ \"type\": \"stroke\", \"position\": \"outside\", \"size\": {F(s.size)}, \"color\": \"{s.color}\" }} ] }}");
            }
            if (stack.Count == 0 && a.extrudeCap > 0.5f)
                layers.Add($"    {{ \"name\": \"Extrude\", \"offset\": [0, {F(a.extrudeCap)}], \"effects\": [ {{ \"type\": \"colorOverlay\", \"color\": \"{a.extrudeColor ?? a.bodyBottom}\" }} ] }}");
            if (!string.IsNullOrEmpty(a.innerExtrudeColor) && a.extrudeCap > 0.5f)
                layers.Add($"    {{ \"name\": \"Body Extrude\", \"offset\": [0, {F(a.extrudeCap)}], \"effects\": [ {{ \"type\": \"colorOverlay\", \"color\": \"{a.innerExtrudeColor}\" }} ] }}");
            string body = a.bodyGradient
                ? $"{{ \"type\": \"gradientOverlay\", \"angle\": 90, \"stops\": [ {{ \"color\": \"{a.bodyBottom}\", \"location\": 0 }}, {{ \"color\": \"{a.bodyMid}\", \"location\": 50 }}, {{ \"color\": \"{a.bodyTop}\", \"location\": 100 }} ] }}"
                : $"{{ \"type\": \"colorOverlay\", \"color\": \"{a.bodyMid}\" }}";
            layers.Add($"    {{ \"name\": \"Body\", \"effects\": [ {body} ] }}");
            sb.Append(string.Join(",\n", layers)).Append("\n  ]\n}\n");
            return sb.ToString();
        }

        // ------------------------------------------------------------------ helpers
        static Vector3 C(Color32 c) => new Vector3(c.r, c.g, c.b);
        static float Pct(float px, float cap) => Mathf.Round(px / cap * 1000f) / 10f;

        static List<Seg> Segments(Func<int, Color32> at, int n)
        {
            var list = new List<Seg>();
            var cur = new Seg { a = 0, b = 0, sum = Vector3.zero, first = at(0) };
            Color32 prev = at(0);
            for (int i = 0; i < n; i++)
            {
                var p = at(i);
                // sharp edge: big jump between neighbours; soft edge (scaled/filtered images): big change over 3 px.
                // A gradient body changes little over 3 px, so it stays one segment.
                bool soft = i - cur.a >= 3 && Vector3.Distance(C(p), C(at(i - 3))) > Edge * 2f;
                if (i > 0 && (Vector3.Distance(C(p), C(prev)) > Edge || soft))
                {
                    cur.b = i; cur.last = prev; list.Add(cur);
                    cur = new Seg { a = i, sum = Vector3.zero, first = p };
                }
                cur.sum += C(p);
                cur.n++;
                prev = p;
            }
            cur.b = n; cur.last = prev; list.Add(cur);
            // anti-aliasing leaves 1 px slivers between bands: give them to the neighbour they look most like
            var merged = new List<Seg>();
            foreach (var s in list)
            {
                if (s.Len <= 1 && merged.Count > 0) { var m = merged[merged.Count - 1]; m.b = s.b; merged[merged.Count - 1] = m; continue; }
                merged.Add(s);
            }
            return merged;
        }

        /// <summary>Longest internal run whose left, right and upper neighbours share one colour that differs from the run.</summary>
        static int WrappedInternal(List<Seg> segs, int x, int w, Func<int, int, Color32> P, Vector3 bg)
        {
            int best = -1, bestLen = 0;
            for (int i = 1; i < segs.Count - 1; i++)
            {
                var s = segs[i];
                if (s.Len < 4 || s.Len <= bestLen || Vector3.Distance(s.Mean, bg) < 25f) continue;
                int up = i - 1;
                while (up > 0 && segs[up].Len < 2) up--;
                Vector3 upC = segs[up].Mean;
                if (Vector3.Distance(upC, bg) < 25f || Vector3.Distance(upC, s.Mean) < Edge) continue;
                int y = (s.a + s.b) / 2;
                Vector3 inside = C(P(x, y));
                Vector3? Side(int dir)
                {
                    for (int xx = x + dir; xx >= 0 && xx < w; xx += dir)
                    {
                        if (Vector3.Distance(C(P(xx, y)), inside) > Edge * 1.5f)
                        {
                            int probe = Mathf.Clamp(xx + dir * 2, 0, w - 1); // step past the anti-aliased edge pixel
                            return C(P(probe, y));
                        }
                    }
                    return null;
                }
                var l = Side(-1); var r = Side(+1);
                if (l == null || r == null) continue;
                if (Vector3.Distance(l.Value, r.Value) < 45f && Vector3.Distance(l.Value, upC) < 45f)
                { best = i; bestLen = s.Len; }
            }
            return best;
        }

        static int LongestInternal(List<Seg> segs, Vector3 bg)
        {
            int best = -1, len = 0;
            for (int i = 1; i < segs.Count - 1; i++)
            {
                if (Vector3.Distance(segs[i].Mean, bg) < 25f) continue;
                if (segs[i].Len > len) { len = segs[i].Len; best = i; }
            }
            return best;
        }

        /// <summary>Bands from the body outward until background, a very wide run, or MaxBands.</summary>
        static List<(Vector3 c, int len)> Walk(List<Seg> segs, int body, int dir, Vector3 bg, float cap, Vector3[] bodyColors = null)
        {
            var bands = new List<(Vector3, int)>();
            for (int i = body + dir; i > 0 && i < segs.Count - 1 && bands.Count < MaxBands; i += dir)
            {
                var s = segs[i];
                if (Vector3.Distance(s.Mean, bg) < 25f || s.Len > cap * 0.5f) break;
                if (bodyColors != null && bodyColors.Any(c => Vector3.Distance(s.Mean, c) < 45f)) break; // reached the next letter
                if (s.Len < 2) continue;
                if (bands.Any(bd => Vector3.Distance(bd.Item1, s.Mean) < 30f)) break; // colour repeats: we left this letter's band stack
                bands.Add((s.Mean, s.Len)); // the merged 1 px edge sliver already accounts for anti-aliasing
            }
            return bands;
        }

        static List<SFBand> Aggregate(List<List<(Vector3 c, int len)>> seqs, float cap, out float confidence)
        {
            confidence = 0;
            var result = new List<SFBand>();
            seqs = seqs.Where(s => s.Count > 0).ToList();
            if (seqs.Count == 0) return result;
            int mode = seqs.GroupBy(s => s.Count).OrderByDescending(g => g.Count()).First().Key;
            var same = seqs.Where(s => s.Count == mode).ToList();
            confidence = (float)same.Count / seqs.Count;
            for (int i = 0; i < mode; i++)
            {
                var widths = same.Select(s => s[i].len).OrderBy(v => v).ToList();
                float med = widths[widths.Count / 2];
                // colour: average of the samples whose width is near the median (skip corners / diagonals)
                var near = same.Where(s => Mathf.Abs(s[i].len - med) <= 1).Select(s => s[i].c).ToList();
                Vector3 c = near.Aggregate(Vector3.zero, (acc, v) => acc + v) / Mathf.Max(1, near.Count);
                result.Add(new SFBand { color = Hex(c), px = med, cap = Pct(med, cap), samples = same.Count });
            }
            return result;
        }

        static Vector3 BorderColor(Color32[] raw, int w, int h)
        {
            var counts = new Dictionary<int, (int n, Vector3 sum)>();
            void Add(Color32 c)
            {
                int key = (c.r / 24) << 16 | (c.g / 24) << 8 | (c.b / 24);
                counts.TryGetValue(key, out var e);
                counts[key] = (e.n + 1, e.sum + C(c));
            }
            for (int x = 0; x < w; x++) { Add(raw[x]); Add(raw[(h - 1) * w + x]); }
            for (int y = 0; y < h; y++) { Add(raw[y * w]); Add(raw[y * w + w - 1]); }
            var top = counts.Values.OrderByDescending(v => v.n).First();
            return top.sum / top.n;
        }

        public static string Hex(Vector3 c) => "#" + ((int)Mathf.Clamp(c.x, 0, 255)).ToString("X2") + ((int)Mathf.Clamp(c.y, 0, 255)).ToString("X2") + ((int)Mathf.Clamp(c.z, 0, 255)).ToString("X2");

        public static float ColorDist(string a, string b)
        {
            if (!ColorUtility.TryParseHtmlString(a, out var ca) || !ColorUtility.TryParseHtmlString(b, out var cb)) return 999f;
            return Vector3.Distance(new Vector3(ca.r, ca.g, ca.b) * 255f, new Vector3(cb.r, cb.g, cb.b) * 255f);
        }

        /// <summary>Reference image with a legend underneath: body top/mid/bottom, side bands, bottom bands, extrude.</summary>
        static void WriteDebug(Color32[] raw, int w, int h, SFAnalyzeResult a, string path)
        {
            int legendH = 90, W = Mathf.Max(w, 520), H = h + legendH;
            var outTex = new Texture2D(W, H, TextureFormat.RGBA32, false);
            var px = new Color32[W * H];
            for (int i = 0; i < px.Length; i++) px[i] = new Color32(30, 30, 30, 255);
            for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) px[(y + legendH) * W + x] = raw[y * w + x];
            void Box(int x0, int y0top, int bw, int bh, string hex)
            {
                if (!ColorUtility.TryParseHtmlString(hex, out var c)) return;
                Color32 cc = c;
                for (int y = 0; y < bh; y++) for (int x = 0; x < bw; x++)
                {
                    int yy = legendH - 1 - (y0top + y), xx = x0 + x;
                    if (yy >= 0 && yy < H && xx < W) px[yy * W + xx] = cc;
                }
            }
            int bx = 8;
            foreach (var hex in new[] { a.bodyTop, a.bodyMid, a.bodyBottom }) { Box(bx, 8, 34, 34, hex); bx += 38; }
            bx += 16;
            foreach (var b in a.strokeBands) { Box(bx, 8, Mathf.Clamp((int)(b.px * 4), 6, 80), 34, b.color); bx += Mathf.Clamp((int)(b.px * 4), 6, 80) + 4; }
            bx += 16;
            foreach (var b in a.bottomBands) { Box(bx, 8, Mathf.Clamp((int)(b.px * 4), 6, 80), 34, b.color); bx += Mathf.Clamp((int)(b.px * 4), 6, 80) + 4; }
            if (!string.IsNullOrEmpty(a.extrudeColor)) { Box(bx + 16, 8, 34, 34, a.extrudeColor); bx += 54; }
            if (!string.IsNullOrEmpty(a.innerExtrudeColor)) Box(bx + 16, 8, 34, 34, a.innerExtrudeColor);
            Box(8, 50, 34, 30, a.background);
            outTex.SetPixels32(px);
            outTex.Apply();
            File.WriteAllBytes(path, outTex.EncodeToPNG());
            Object.DestroyImmediate(outTex);
        }
    }
}
