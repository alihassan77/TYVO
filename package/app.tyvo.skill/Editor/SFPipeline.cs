using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.TextCore;
using UnityEngine.TextCore.LowLevel;
using Object = UnityEngine.Object;

namespace Mindstorm.Tyvo.Editor
{
    [Serializable]
    public class SFGenerateRequest
    {
        public string font;                       // Assets/... .ttf / .otf
        public string style;                      // style id, name or json path
        public string name;                       // font asset name (default "<Font> - <Style>")
        public string outputFolder;               // default Assets/StylizedFonts/<Font>
        public int padding;                       // 0 = automatic (style required padding)
        public int atlasSize = 1024;
        public int sampling;                      // 0 = automatic (largest size that fits)
        public string charset = "extended-ascii"; // extended-ascii | ascii | numbers | custom
        public string characters;                 // used when charset = custom
        public bool reuse = true;                 // return an existing asset with the same font + look
        public string mode = "auto";              // job mode for Photoshop: auto | build-psd
    }

    [Serializable]
    public class SFGenerateResult
    {
        public bool reused;
        public string reusedReason;
        public string fontAsset;
        public string style;
        public string signature;
        public string rawPng;
        public string job;
        public string stylizedPng;
        public string psd;
        public string resultJson;
        public int samplingPointSize;
        public int padding;
        public int requiredPadding;
        public float capHeightPx;                 // height of "H" in the atlas at the sampling size
        public float unitScale;                   // atlas px per style unit (1 for px styles, capHeight/100 for cap styles)
        public float letterSpacing;               // baked extra advance (atlas px)
        public int atlasSize;
        public int glyphCount;
        public string missingInFont;
        public string didNotFit;
        public List<string> warnings = new List<string>();
    }

    /// <summary>Metadata stored in the font asset's .meta (importer userData) so assets can be found and reused.</summary>
    [Serializable]
    public class SFMeta
    {
        public string tool = "tyvo";
        public string fontGuid;
        public string style;
        public string signature;
        public bool shared;
        public int padding;
        public int atlasSize;
        public int sampling;
        public float unitScale = 1f;
        public string charset;
        public string stylizedPng;

        public static SFMeta Read(string assetPath)
        {
            var importer = AssetImporter.GetAtPath(assetPath);
            if (importer == null || string.IsNullOrEmpty(importer.userData) || !(importer.userData.Contains("\"tool\":\"tyvo\"") || importer.userData.Contains("stylized-tmp-font"))) return null;
            try { return JsonUtility.FromJson<SFMeta>(importer.userData); } catch { return null; }
        }

        public void Write(string assetPath)
        {
            var importer = AssetImporter.GetAtPath(assetPath);
            if (importer == null) return;
            importer.userData = JsonUtility.ToJson(this);
            importer.SaveAndReimport();
        }
    }

    public static class SFPipeline
    {
        // ------------------------------------------------------------------ charsets
        public static uint[] Charset(string charset, string custom)
        {
            var list = new List<uint>();
            void Range(uint a, uint b) { for (uint u = a; u <= b; u++) list.Add(u); }
            switch ((charset ?? "extended-ascii").ToLowerInvariant())
            {
                case "ascii": Range(32, 126); break;
                case "numbers": foreach (char c in "0123456789 +-.,:/%xX") list.Add(c); break;
                case "custom":
                    foreach (char c in custom ?? "") if (!char.IsSurrogate(c)) list.Add(c);
                    if (!list.Contains(32)) list.Add(32);
                    break;
                default: // TMP Font Asset Creator "Extended ASCII"
                    Range(32, 126); Range(160, 255); Range(8192, 8303); list.Add(8364); list.Add(8482); list.Add(9633);
                    break;
            }
            return list.Distinct().ToArray();
        }

        // ------------------------------------------------------------------ reuse
        public static string FindExisting(string fontGuid, string signature, string charset, int padding)
        {
            foreach (string guid in AssetDatabase.FindAssets("t:TMP_FontAsset"))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                var meta = SFMeta.Read(path);
                if (meta == null) continue;
                if (meta.fontGuid == fontGuid && meta.signature == signature && meta.charset == charset && (padding <= 0 || meta.padding == padding))
                    return path;
            }
            return null;
        }

        // ------------------------------------------------------------------ generate
        public static SFGenerateResult Generate(SFGenerateRequest req)
        {
            var result = new SFGenerateResult();
            var style = SFStyles.Find(req.style) ?? throw new Exception($"Style '{req.style}' not found. Known: {string.Join(", ", SFStyles.All().Select(s => s.id))}");
            var font = AssetDatabase.LoadAssetAtPath<Font>(req.font) ?? throw new Exception($"Font not found at '{req.font}'. Use an Assets/... path to a .ttf/.otf.");
            if (Shader.Find(SFPaths.CustomAtlasShader) == null) throw new Exception("TMP shader 'Bitmap Custom Atlas' missing. Run the 'setup' command (imports TMP Essential Resources).");

            string fontGuid = AssetDatabase.AssetPathToGUID(req.font);
            string charsetKey = req.charset == "custom" ? "custom:" + (req.characters ?? "").GetHashCode().ToString("x8") : (req.charset ?? "extended-ascii");
            result.style = style.id;
            result.signature = style.Signature();
            result.atlasSize = req.atlasSize;

            string fontName = font.name;
            string assetName = string.IsNullOrEmpty(req.name) ? $"{fontName} - {style.name}" : req.name;
            string outDir = string.IsNullOrEmpty(req.outputFolder) ? $"{SFPaths.DefaultOutputRoot}/{SFPaths.Sanitize(fontName)}" : req.outputFolder.TrimEnd('/');
            string assetPath = $"{outDir}/{SFPaths.Sanitize(assetName)}.asset";

            if (req.reuse)
            {
                string existing = FindExisting(fontGuid, result.signature, charsetKey, req.padding);
                if (existing != null)
                {
                    var meta = SFMeta.Read(existing);
                    result.reused = true;
                    result.fontAsset = existing;
                    result.stylizedPng = meta?.stylizedPng;
                    result.padding = meta?.padding ?? 0;
                    result.samplingPointSize = meta?.sampling ?? 0;
                    result.reusedReason = $"'{existing}' already has font '{fontName}' with style '{style.id}' (same look). " +
                                          (style.IsShared ? "Shared style: set the text color on the TMP component; the stroke takes the button color." : "Assign it / its material instead of making a new one.");
                    return result;
                }
            }

            // Make sure the font's data is readable by the font engine.
            if (AssetImporter.GetAtPath(req.font) is TrueTypeFontImporter ttf && !ttf.includeFontData)
            {
                ttf.includeFontData = true;
                ttf.SaveAndReimport();
            }

            // Which characters exist in the font at all.
            uint[] wanted = Charset(req.charset, req.characters);
            FontEngine.InitializeFontEngine();
            if (FontEngine.LoadFontFace(font, 90) != FontEngineError.Success) throw new Exception("Font engine could not load " + req.font);
            var supported = new List<uint>();
            var missingList = new List<uint>();
            foreach (uint u in wanted)
            {
                if (FontEngine.TryGetGlyphWithUnicodeValue(u, GlyphLoadFlags.LOAD_NO_BITMAP, out Glyph g) && g != null && g.index != 0) supported.Add(u);
                else missingList.Add(u);
            }
            if (missingList.Count > 0)
            {
                // Printable ones first (those matter); invisible spacing/format characters (U+2000-U+206F) are only counted.
                var visible = missingList.Where(u => u < 0x2000 || u > 0x206F).Select(u => $"U+{u:X4} '{(char)u}'").ToList();
                int hidden = missingList.Count - visible.Count;
                result.missingInFont = $"{missingList.Count} missing" +
                                       (visible.Count > 0 ? ": " + string.Join(", ", visible) : "") +
                                       (hidden > 0 ? $" ({hidden} are spacing/format characters U+2000-U+206F)" : "");
            }
            if (supported.Count == 0) throw new Exception("None of the requested characters exist in this font.");
            uint[] chars = supported.ToArray();

            // Cap height per point: lets "cap" styles keep the same proportions on any font and sampling size.
            float capPerPt = 0.7f;
            if (FontEngine.TryGetGlyphWithUnicodeValue('H', GlyphLoadFlags.LOAD_NO_BITMAP, out Glyph hGlyph) && hGlyph != null && hGlyph.metrics.height > 0)
                capPerPt = hGlyph.metrics.height / 90f;
            else if (FontEngine.GetFaceInfo().capLine > 0)
                capPerPt = FontEngine.GetFaceInfo().capLine / 90f;
            int PaddingFor(int pt) => req.padding > 0 ? req.padding : style.RequiredPadding(style.UnitScale(capPerPt * pt));

            // Sampling point size: fixed, or the largest that fits the atlas.
            int sampling = req.sampling;
            if (sampling <= 0)
            {
                int lo = 8, hi = 512, best = -1;
                while (lo <= hi)
                {
                    int mid = (lo + hi) / 2;
                    var probe = Create(font, mid, PaddingFor(mid), req.atlasSize);
                    bool fits = probe != null && probe.TryAddCharacters(chars, out uint[] _);
                    Destroy(probe);
                    if (fits) { best = mid; lo = mid + 1; } else hi = mid - 1;
                }
                if (best < 0) throw new Exception($"Characters do not fit a {req.atlasSize} atlas even at 8 pt. Use a bigger atlas or fewer characters.");
                sampling = best;
            }
            result.samplingPointSize = sampling;
            result.capHeightPx = capPerPt * sampling;
            result.unitScale = style.UnitScale(result.capHeightPx);
            int required = style.RequiredPadding(result.unitScale);
            int padding = PaddingFor(sampling);
            result.requiredPadding = required;
            result.padding = padding;
            if (padding < required) result.warnings.Add($"Padding {padding} is below what the style needs ({required}); effects will be clipped.");

            var fa = Create(font, sampling, padding, req.atlasSize) ?? throw new Exception("TMP could not create the font asset.");
            if (!fa.TryAddCharacters(chars, out uint[] notAdded) && notAdded != null && notAdded.Length > 0)
            {
                string lost = new string(notAdded.Where(u => u < 0xFFFF).Select(u => (char)u).ToArray());
                Destroy(fa);
                throw new Exception($"{notAdded.Length} characters do not fit a {req.atlasSize} atlas at {sampling} pt: {lost}. " +
                                    "Use a bigger --atlas-size, a smaller --sampling, or sampling 0 (auto).");
            }
            fa.atlasPopulationMode = AtlasPopulationMode.Static;
            fa.name = assetName;

            // Baked effects sit around each glyph but TMP advances by the bare shape, so a later glyph's outline
            // paints over the previous glyph. Widen every advance (half each side) so outlines meet instead.
            float spacing = style.LetterSpacingPx(result.unitScale);
            result.letterSpacing = spacing;
            if (spacing > 0)
            {
                foreach (var glyph in fa.glyphTable)
                {
                    var m = glyph.metrics;
                    m.horizontalAdvance += spacing;
                    m.horizontalBearingX += spacing * 0.5f;
                    glyph.metrics = m;
                }
            }

            var shader = Shader.Find(SFPaths.CustomAtlasShader);
            var mat = new Material(shader) { name = assetName + " Material" };
            mat.SetTexture("_MainTex", fa.atlasTextures[0]);
            mat.SetFloat("_Padding", padding);
            mat.SetColor("_FaceColor", Color.white);
            Object.DestroyImmediate(fa.material);
            fa.material = mat;
            fa.atlasTextures[0].name = assetName + " Atlas (raw)";

            SFPaths.EnsureAssetFolder(outDir);
            fa = SaveFontAsset(fa, assetPath);

            // Raw atlas (white glyphs, coverage in alpha) + job for Photoshop.
            string work = SFPaths.WorkFolder(assetName);
            string safe = SFPaths.Sanitize(assetName);
            result.fontAsset = assetPath;
            result.rawPng = $"{work}/{safe}_raw.png";
            result.job = $"{work}/{safe}.job.json";
            result.psd = $"{work}/{safe}.psd";
            result.resultJson = $"{work}/{safe}.result.json";
            result.stylizedPng = $"{outDir}/{safe} Atlas.png";
            result.glyphCount = fa.glyphTable.Count;

            WriteRawAtlas(fa, result.rawPng);
            WriteJob(fa, style, result, req.mode);

            new SFMeta
            {
                fontGuid = fontGuid, style = style.id, signature = result.signature, shared = style.IsShared,
                padding = padding, atlasSize = req.atlasSize, sampling = sampling, unitScale = result.unitScale, charset = charsetKey, stylizedPng = result.stylizedPng
            }.Write(assetPath);

            AssetDatabase.SaveAssets();
            return result;
        }

        static TMP_FontAsset Create(Font font, int sampling, int padding, int atlasSize)
        {
            return TMP_FontAsset.CreateFontAsset(font, sampling, padding, GlyphRenderMode.SMOOTH, atlasSize, atlasSize, AtlasPopulationMode.Dynamic, false);
        }

        static void Destroy(TMP_FontAsset fa)
        {
            if (fa == null) return;
            if (fa.atlasTextures != null) foreach (var t in fa.atlasTextures) if (t != null) Object.DestroyImmediate(t);
            if (fa.material != null) Object.DestroyImmediate(fa.material);
            Object.DestroyImmediate(fa);
        }

        /// <summary>Creates the asset, or updates an existing one in place so GUIDs (and prefab references) stay valid.</summary>
        static TMP_FontAsset SaveFontAsset(TMP_FontAsset fa, string assetPath)
        {
            var existing = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(assetPath);
            var tex = fa.atlasTextures[0];
            var mat = fa.material;
            if (existing == null)
            {
                AssetDatabase.CreateAsset(fa, assetPath);
                AssetDatabase.AddObjectToAsset(tex, fa);
                AssetDatabase.AddObjectToAsset(mat, fa);
                SetClearDynamicData(fa);
                EditorUtility.SetDirty(fa);
                AssetDatabase.SaveAssets();
                return fa;
            }

            var subAssets = AssetDatabase.LoadAllAssetRepresentationsAtPath(assetPath);
            var exTex = subAssets.OfType<Texture2D>().FirstOrDefault();
            var exMat = subAssets.OfType<Material>().FirstOrDefault();
            if (exTex != null) { EditorUtility.CopySerialized(tex, exTex); Object.DestroyImmediate(tex); }
            else { exTex = tex; AssetDatabase.AddObjectToAsset(exTex, existing); }
            if (exMat != null)
            {
                if (exMat.shader == null || exMat.shader.name != SFPaths.CustomAtlasShader) exMat.shader = mat.shader;
                exMat.SetFloat("_Padding", mat.GetFloat("_Padding"));
                Object.DestroyImmediate(mat);
            }
            else { exMat = mat; AssetDatabase.AddObjectToAsset(exMat, existing); }

            string keepName = existing.name;
            EditorUtility.CopySerialized(fa, existing);
            existing.name = keepName;
            existing.atlasTextures = new[] { exTex };
            existing.material = exMat;
            existing.ReadFontAssetDefinition();
            Object.DestroyImmediate(fa);
            SetClearDynamicData(existing);
            EditorUtility.SetDirty(exTex);
            EditorUtility.SetDirty(exMat);
            EditorUtility.SetDirty(existing);
            AssetDatabase.SaveAssets();
            return existing;
        }

        static void SetClearDynamicData(TMP_FontAsset fa)
        {
            var so = new SerializedObject(fa);
            var p = so.FindProperty("m_ClearDynamicDataOnBuild");
            if (p != null) { p.boolValue = false; so.ApplyModifiedPropertiesWithoutUndo(); }
        }

        static void WriteRawAtlas(TMP_FontAsset fa, string pngPath)
        {
            var tex = fa.atlasTextures[0];
            int w = tex.width, h = tex.height;
            var pixels = new Color32[w * h];
            if (tex.format == TextureFormat.Alpha8)
            {
                byte[] alpha = tex.GetRawTextureData();
                for (int i = 0; i < pixels.Length && i < alpha.Length; i++) pixels[i] = new Color32(255, 255, 255, alpha[i]);
            }
            else
            {
                var src = tex.GetPixels32();
                for (int i = 0; i < pixels.Length; i++) pixels[i] = new Color32(255, 255, 255, src[i].a);
            }
            var outTex = new Texture2D(w, h, TextureFormat.RGBA32, false);
            outTex.SetPixels32(pixels);
            outTex.Apply();
            File.WriteAllBytes(pngPath, outTex.EncodeToPNG());
            Object.DestroyImmediate(outTex);
        }

        /// <summary>Padded glyph rectangles in top-left pixel coordinates (Photoshop space).</summary>
        public static List<(string name, int l, int t, int r, int b)> PaddedRectsTopLeft(TMP_FontAsset fa)
        {
            int w = fa.atlasWidth, h = fa.atlasHeight, pad = fa.atlasPadding;
            var names = new Dictionary<uint, string>();
            foreach (var c in fa.characterTable)
                if (!names.ContainsKey(c.glyphIndex)) names[c.glyphIndex] = $"U+{c.unicode:X4} {(c.unicode < 0xFFFF && !char.IsControl((char)c.unicode) ? ((char)c.unicode).ToString() : "")}".Trim();
            var list = new List<(string, int, int, int, int)>();
            foreach (var g in fa.glyphTable)
            {
                var r = g.glyphRect;
                if (r.width <= 0 || r.height <= 0) continue;
                int l = Mathf.Clamp(r.x - pad, 0, w);
                int rr = Mathf.Clamp(r.x + r.width + pad, 0, w);
                int t = Mathf.Clamp(h - (r.y + r.height) - pad, 0, h);
                int b = Mathf.Clamp(h - r.y + pad, 0, h);
                list.Add((names.TryGetValue(g.index, out var n) ? n : "glyph " + g.index, l, t, rr, b));
            }
            return list;
        }

        static void WriteJob(TMP_FontAsset fa, SFStyle style, SFGenerateResult r, string mode)
        {
            string Q(string s) => "\"" + (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
            var sb = new StringBuilder();
            sb.Append("{\n");
            sb.Append("  \"raw\": ").Append(Q(r.rawPng)).Append(",\n");
            sb.Append("  \"output\": ").Append(Q(SFPaths.Abs(r.stylizedPng))).Append(",\n");
            sb.Append("  \"psd\": ").Append(Q(r.psd)).Append(",\n");
            sb.Append("  \"result\": ").Append(Q(r.resultJson)).Append(",\n");
            sb.Append("  \"mode\": ").Append(Q(string.IsNullOrEmpty(mode) ? "auto" : mode)).Append(",\n");
            sb.Append("  \"fontAsset\": ").Append(Q(r.fontAsset)).Append(",\n");
            sb.Append("  \"unitScale\": ").Append(r.unitScale.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(",\n");
            sb.Append("  \"width\": ").Append(fa.atlasWidth).Append(", \"height\": ").Append(fa.atlasHeight).Append(", \"padding\": ").Append(fa.atlasPadding).Append(",\n");
            sb.Append("  \"glyphs\": [\n");
            var rects = PaddedRectsTopLeft(fa);
            for (int i = 0; i < rects.Count; i++)
            {
                var g = rects[i];
                sb.Append("    {\"name\": ").Append(Q(g.name)).Append($", \"l\": {g.l}, \"t\": {g.t}, \"r\": {g.r}, \"b\": {g.b}}}");
                sb.Append(i < rects.Count - 1 ? ",\n" : "\n");
            }
            sb.Append("  ],\n");
            sb.Append("  \"style\": ").Append(style.json.Trim()).Append("\n}\n");
            File.WriteAllText(r.job, sb.ToString());
        }

        // ------------------------------------------------------------------ apply
        [Serializable]
        public class SFApplyResult
        {
            public string fontAsset;
            public string material;
            public string stylizedPng;
            public SFCheckReport check;
        }

        public static SFApplyResult Apply(string fontAssetPath, string stylizedPng = null)
        {
            var fa = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(fontAssetPath) ?? throw new Exception("Font asset not found: " + fontAssetPath);
            var meta = SFMeta.Read(fontAssetPath);
            if (string.IsNullOrEmpty(stylizedPng)) stylizedPng = meta?.stylizedPng;
            if (string.IsNullOrEmpty(stylizedPng)) throw new Exception("No stylized PNG given and none recorded for this font asset.");
            stylizedPng = SFPaths.ToAssetPath(stylizedPng);
            if (!File.Exists(SFPaths.Abs(stylizedPng))) throw new Exception("Stylized PNG not found: " + stylizedPng);

            AssetDatabase.ImportAsset(stylizedPng, ImportAssetOptions.ForceSynchronousImport);
            ConfigureAtlasImporter(stylizedPng, Mathf.Max(fa.atlasWidth, fa.atlasHeight));
            var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(stylizedPng);

            var mat = fa.material;
            var shader = Shader.Find(SFPaths.CustomAtlasShader) ?? throw new Exception("Bitmap Custom Atlas shader missing (run setup).");
            if (mat.shader != shader) mat.shader = shader;
            mat.SetTexture("_MainTex", tex);
            mat.SetFloat("_Padding", fa.atlasPadding);
            mat.SetColor("_FaceColor", Color.white);
            EditorUtility.SetDirty(mat);

            if (meta != null && meta.stylizedPng != stylizedPng) { meta.stylizedPng = stylizedPng; meta.Write(fontAssetPath); }
            AssetDatabase.SaveAssets();

            return new SFApplyResult
            {
                fontAsset = fontAssetPath,
                material = mat.name,
                stylizedPng = stylizedPng,
                check = SFChecks.Run(fontAssetPath)
            };
        }

        public static void ConfigureAtlasImporter(string pngAssetPath, int size)
        {
            if (!(AssetImporter.GetAtPath(pngAssetPath) is TextureImporter ti)) return;
            ti.textureType = TextureImporterType.Default;
            ti.sRGBTexture = true;
            ti.alphaSource = TextureImporterAlphaSource.FromInput;
            ti.alphaIsTransparency = true;
            ti.mipmapEnabled = false;
            ti.npotScale = TextureImporterNPOTScale.None;
            ti.wrapMode = TextureWrapMode.Clamp;
            ti.filterMode = FilterMode.Bilinear;
            ti.textureCompression = TextureImporterCompression.Uncompressed;
            ti.maxTextureSize = Mathf.Max(2048, Mathf.NextPowerOfTwo(size));
            ti.isReadable = false;
            foreach (string platform in new[] { "Android", "iPhone", "Standalone", "WebGL" })
            {
                var s = ti.GetPlatformTextureSettings(platform);
                if (s.overridden) { s.overridden = false; ti.SetPlatformTextureSettings(s); }
            }
            ti.SaveAndReimport();
        }
    }
}
