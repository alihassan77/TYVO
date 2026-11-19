using System;
using System.Collections.Generic;
using System.IO;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace Mindstorm.Tyvo.Editor
{
    [Serializable]
    public class SFRenderRequest
    {
        public string fontAsset;
        public string text = "PLAY  New Text  Level 12";
        public string[] backgrounds;        // hex; default = style test backgrounds or a neutral set
        public string textColor = "#FFFFFF";
        public float fontSize = 64;
        public bool glyphSheet = true;      // add rows with every character in the font
        public string output;               // absolute png path (default: work folder)
    }

    [Serializable]
    public class SFRenderResult
    {
        public string output;
        public string baseline;
        public float diffPercent = -1;      // vs. previous accepted baseline, -1 when there is none
        public int width;
        public int height;
    }

    /// <summary>Renders a test sheet (text on each button colour + full glyph sheet) without touching the open scene.</summary>
    public static class SFRender
    {
        const int Width = 1400;

        public static SFRenderResult Render(SFRenderRequest req)
        {
            var fa = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(req.fontAsset) ?? throw new Exception("Font asset not found: " + req.fontAsset);
            var meta = SFMeta.Read(req.fontAsset);
            var style = meta != null ? SFStyles.Find(meta.style) : null;
            string[] backgrounds = req.backgrounds != null && req.backgrounds.Length > 0 ? req.backgrounds
                : style?.shared?.testBackgrounds != null && style.shared.testBackgrounds.Length > 0 ? style.shared.testBackgrounds
                : new[] { "#8E8E93", "#3DBE4E", "#2F80ED", "#F2C94C", "#202020" };

            float rowH = req.fontSize * 1.9f;
            var sheetLines = new List<string>();
            if (req.glyphSheet)
            {
                var line = new System.Text.StringBuilder();
                foreach (var c in fa.characterTable)
                {
                    if (c.unicode <= 32 || c.unicode >= 0xFFFF) continue;
                    line.Append((char)c.unicode);
                    if (line.Length >= 32) { sheetLines.Add(line.ToString()); line.Clear(); }
                }
                if (line.Length > 0) sheetLines.Add(line.ToString());
            }
            float sheetFont = req.fontSize * 0.6f;
            float sheetRowH = sheetFont * 1.5f;
            int height = Mathf.CeilToInt(backgrounds.Length * rowH + sheetLines.Count * sheetRowH + 40);

            var scene = EditorSceneManager.NewPreviewScene();
            var rt = new RenderTexture(Width, height, 24, RenderTextureFormat.ARGB32) { antiAliasing = 1 };
            Texture2D shot = null;
            try
            {
                var camGo = new GameObject("SF Camera");
                SceneManager_Move(camGo, scene);
                var cam = camGo.AddComponent<Camera>();
                cam.scene = scene;
                cam.orthographic = true;
                cam.orthographicSize = height * 0.5f;
                cam.transform.position = new Vector3(Width * 0.5f, -height * 0.5f, -100);
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = new Color(0.12f, 0.12f, 0.12f, 1);
                cam.cullingMask = ~0;
                cam.nearClipPlane = 0.1f;
                cam.farClipPlane = 1000;
                cam.targetTexture = rt;

                var canvasGo = new GameObject("SF Canvas", typeof(RectTransform));
                SceneManager_Move(canvasGo, scene);
                var canvas = canvasGo.AddComponent<Canvas>();
                canvas.renderMode = RenderMode.WorldSpace;
                canvas.worldCamera = cam;
                var crt = (RectTransform)canvasGo.transform;
                crt.pivot = new Vector2(0, 1);
                crt.sizeDelta = new Vector2(Width, height);
                crt.position = Vector3.zero;

                var texts = new List<TextMeshProUGUI>();
                float y = 0;
                Color textColor = Parse(req.textColor, Color.white);
                foreach (string hex in backgrounds)
                {
                    var bg = MakeRect("bg " + hex, crt, 0, y, Width, rowH);
                    bg.gameObject.AddComponent<Image>().color = Parse(hex, Color.grey);
                    var label = MakeText(bg, fa, req.text, req.fontSize, textColor);
                    texts.Add(label);
                    y += rowH;
                }
                foreach (string s in sheetLines)
                {
                    var row = MakeRect("sheet", crt, 0, y + 20, Width, sheetRowH);
                    texts.Add(MakeText(row, fa, s, sheetFont, textColor));
                    y += sheetRowH;
                }

                Canvas.ForceUpdateCanvases();
                foreach (var t in texts) t.ForceMeshUpdate(true, true);
                Canvas.ForceUpdateCanvases();
                cam.Render();

                var prev = RenderTexture.active;
                RenderTexture.active = rt;
                shot = new Texture2D(Width, height, TextureFormat.RGBA32, false);
                shot.ReadPixels(new Rect(0, 0, Width, height), 0, 0);
                shot.Apply();
                RenderTexture.active = prev;

                string assetName = Path.GetFileNameWithoutExtension(req.fontAsset);
                string work = SFPaths.WorkFolder(assetName);
                string output = string.IsNullOrEmpty(req.output) ? $"{work}/{SFPaths.Sanitize(assetName)}_test.png" : req.output;
                string baseline = $"{work}/{SFPaths.Sanitize(assetName)}_baseline.png";
                File.WriteAllBytes(output, shot.EncodeToPNG());

                var result = new SFRenderResult { output = output, baseline = baseline, width = Width, height = height };
                if (File.Exists(baseline)) result.diffPercent = Diff(shot, baseline);
                return result;
            }
            finally
            {
                if (shot != null) Object.DestroyImmediate(shot);
                rt.Release();
                Object.DestroyImmediate(rt);
                EditorSceneManager.ClosePreviewScene(scene);
            }
        }

        /// <summary>Marks the latest test render as the accepted look for future comparisons.</summary>
        public static string AcceptBaseline(string fontAssetPath)
        {
            string assetName = Path.GetFileNameWithoutExtension(fontAssetPath);
            string work = SFPaths.WorkFolder(assetName);
            string test = $"{work}/{SFPaths.Sanitize(assetName)}_test.png";
            string baseline = $"{work}/{SFPaths.Sanitize(assetName)}_baseline.png";
            if (!File.Exists(test)) throw new Exception("Render a test sheet first.");
            File.Copy(test, baseline, true);
            return baseline;
        }

        static void SceneManager_Move(GameObject go, UnityEngine.SceneManagement.Scene scene) =>
            UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(go, scene);

        static RectTransform MakeRect(string name, RectTransform parent, float x, float y, float w, float h)
        {
            var go = new GameObject(name, typeof(RectTransform));
            var rt = (RectTransform)go.transform;
            rt.SetParent(parent, false);
            rt.anchorMin = rt.anchorMax = new Vector2(0, 1);
            rt.pivot = new Vector2(0, 1);
            rt.anchoredPosition = new Vector2(x, -y);
            rt.sizeDelta = new Vector2(w, h);
            return rt;
        }

        static TextMeshProUGUI MakeText(RectTransform parent, TMP_FontAsset fa, string text, float size, Color color)
        {
            var rt = MakeRect("text", parent, 24, 0, parent.sizeDelta.x - 48, parent.sizeDelta.y);
            var t = rt.gameObject.AddComponent<TextMeshProUGUI>();
            t.font = fa;
            t.fontSharedMaterial = fa.material;
            t.fontSize = size;
            t.color = color;
            t.alignment = TextAlignmentOptions.MidlineLeft;
            t.textWrappingMode = TextWrappingModes.NoWrap;
            t.text = text;
            return t;
        }

        static Color Parse(string hex, Color fallback) => ColorUtility.TryParseHtmlString(hex, out var c) ? c : fallback;

        static float Diff(Texture2D current, string baselineFile)
        {
            var b = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            try
            {
                b.LoadImage(File.ReadAllBytes(baselineFile));
                if (b.width != current.width || b.height != current.height) return 100f;
                var p0 = current.GetPixels32();
                var p1 = b.GetPixels32();
                long changed = 0;
                for (int i = 0; i < p0.Length; i++)
                {
                    int d = Math.Abs(p0[i].r - p1[i].r) + Math.Abs(p0[i].g - p1[i].g) + Math.Abs(p0[i].b - p1[i].b);
                    if (d > 24) changed++;
                }
                return changed * 100f / p0.Length;
            }
            finally { Object.DestroyImmediate(b); }
        }
    }
}
