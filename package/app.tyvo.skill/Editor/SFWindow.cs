using System.IO;
using System.Linq;
using TMPro;
using UnityEditor;
using UnityEngine;

namespace Mindstorm.Tyvo.Editor
{
    /// <summary>Artist UI: Tools > TYVO. Same steps the bridge exposes.</summary>
    public class SFWindow : EditorWindow
    {
        Font m_Font;
        int m_StyleIndex;
        int m_Padding;
        int m_AtlasSize = 1024;
        int m_Charset;
        string m_Custom = "";
        TMP_FontAsset m_Asset;
        SFGenerateResult m_Last;
        SFPhotoshopRun m_Run;
        string m_RunLabel;
        string m_Report = "";
        Vector2 m_Scroll;

        static readonly string[] Charsets = { "extended-ascii", "ascii", "numbers", "custom" };
        static readonly int[] Sizes = { 256, 512, 1024, 2048, 4096 };

        [MenuItem("Tools/TYVO")]
        public static void Open() => GetWindow<SFWindow>("TYVO");

        void OnEnable() => EditorApplication.update += Poll;
        void OnDisable() => EditorApplication.update -= Poll;

        void OnGUI()
        {
            var styles = SFStyles.All();
            var names = styles.Select(s => $"{s.name}  [{s.track}]").ToArray();
            m_Scroll = EditorGUILayout.BeginScrollView(m_Scroll);

            EditorGUILayout.LabelField("TYVO", new GUIStyle(EditorStyles.boldLabel) { fontSize = 18 }, GUILayout.Height(24));
            EditorGUILayout.LabelField("Custom font maker by Ali", EditorStyles.miniLabel);
            EditorGUILayout.Space();

            EditorGUILayout.LabelField("1. Source", EditorStyles.boldLabel);
            m_Font = (Font)EditorGUILayout.ObjectField("Font (.ttf / .otf)", m_Font, typeof(Font), false);
            if (styles.Count == 0) { EditorGUILayout.HelpBox("No style presets found.", MessageType.Error); EditorGUILayout.EndScrollView(); return; }
            m_StyleIndex = Mathf.Clamp(EditorGUILayout.Popup("Style", m_StyleIndex, names), 0, styles.Count - 1);
            var style = styles[m_StyleIndex];
            if (!string.IsNullOrEmpty(style.description)) EditorGUILayout.HelpBox(style.description, MessageType.None);
            m_Padding = EditorGUILayout.IntField(new GUIContent("Padding (0 = auto)", $"Style needs {style.RequiredPadding()} px"), m_Padding);
            int si = System.Array.IndexOf(Sizes, m_AtlasSize);
            m_AtlasSize = Sizes[EditorGUILayout.Popup("Atlas size", si < 0 ? 2 : si, Sizes.Select(s => s.ToString()).ToArray())];
            m_Charset = EditorGUILayout.Popup("Characters", m_Charset, Charsets);
            if (Charsets[m_Charset] == "custom") m_Custom = EditorGUILayout.TextField("Custom characters", m_Custom);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("2. Build", EditorStyles.boldLabel);
            using (new EditorGUI.DisabledScope(m_Font == null || m_Run != null))
            {
                if (GUILayout.Button("Generate font asset + raw atlas")) Generate(style, "auto");
                if (GUILayout.Button("Run all: generate, Photoshop, apply, check, render")) RunAll(style);
            }
            using (new EditorGUI.DisabledScope(m_Last == null || m_Last.reused || m_Run != null))
            {
                EditorGUILayout.BeginHorizontal();
                if (GUILayout.Button("Stylize in Photoshop")) StartPs("stylize_atlas.jsx", "Stylizing", false);
                if (GUILayout.Button("Build layered PSD (edit by hand)")) { RewriteJobMode("build-psd"); StartPs("stylize_atlas.jsx", "Building PSD", false); }
                if (GUILayout.Button("Finalize edited PSD")) StartPs("finalize_psd.jsx", "Finalizing PSD", false);
                EditorGUILayout.EndHorizontal();
            }
            if (m_Run != null) EditorGUILayout.HelpBox($"{m_RunLabel} in Photoshop... ({(int)(System.DateTime.Now - m_Run.started).TotalSeconds}s)", MessageType.Info);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("3. Apply & test", EditorStyles.boldLabel);
            m_Asset = (TMP_FontAsset)EditorGUILayout.ObjectField("Font asset", m_Asset, typeof(TMP_FontAsset), false);
            using (new EditorGUI.DisabledScope(m_Asset == null))
            {
                EditorGUILayout.BeginHorizontal();
                if (GUILayout.Button("Apply stylized atlas")) Report(SFChecks.Format(SFPipeline.Apply(AssetDatabase.GetAssetPath(m_Asset)).check));
                if (GUILayout.Button("Check")) Report(SFChecks.Format(SFChecks.Run(AssetDatabase.GetAssetPath(m_Asset))));
                if (GUILayout.Button("Render test sheet")) RenderTest();
                if (GUILayout.Button("Accept as baseline")) Report("Baseline: " + SFRender.AcceptBaseline(AssetDatabase.GetAssetPath(m_Asset)));
                EditorGUILayout.EndHorizontal();
            }

            if (!string.IsNullOrEmpty(m_Report))
            {
                EditorGUILayout.Space();
                EditorGUILayout.TextArea(m_Report, GUILayout.MinHeight(140));
            }
            EditorGUILayout.EndScrollView();
        }

        SFGenerateRequest Request(SFStyle style, string mode) => new SFGenerateRequest
        {
            font = AssetDatabase.GetAssetPath(m_Font), style = style.id, padding = m_Padding, atlasSize = m_AtlasSize,
            charset = Charsets[m_Charset], characters = m_Custom, mode = mode
        };

        void Generate(SFStyle style, string mode)
        {
            m_Last = SFPipeline.Generate(Request(style, mode));
            m_Asset = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(m_Last.fontAsset);
            Report(m_Last.reused
                ? "REUSED  " + m_Last.reusedReason
                : $"Generated {m_Last.fontAsset}\n  sampling {m_Last.samplingPointSize}pt, padding {m_Last.padding} (style needs {m_Last.requiredPadding}), {m_Last.glyphCount} glyphs\n" +
                  (string.IsNullOrEmpty(m_Last.missingInFont) ? "" : "  Missing in font: " + m_Last.missingInFont + "\n") +
                  string.Join("\n", m_Last.warnings.Select(w => "  WARN " + w)));
            EditorGUIUtility.PingObject(m_Asset);
        }

        bool m_ApplyAfterRun;

        void RunAll(SFStyle style)
        {
            Generate(style, "auto");
            if (!m_Last.reused) StartPs("stylize_atlas.jsx", "Stylizing", true);
        }

        void StartPs(string script, string label, bool applyAfter)
        {
            m_Run = SFPhotoshop.Start(script, m_Last.job, m_Last.resultJson);
            m_RunLabel = label;
            m_ApplyAfterRun = applyAfter;
        }

        void RewriteJobMode(string mode)
        {
            string text = File.ReadAllText(m_Last.job);
            text = System.Text.RegularExpressions.Regex.Replace(text, "\"mode\": \"[^\"]*\"", $"\"mode\": \"{mode}\"");
            File.WriteAllText(m_Last.job, text);
        }

        void Poll()
        {
            if (m_Run == null || !m_Run.IsDone) { if (m_Run != null) Repaint(); return; }
            string res = m_Run.ReadResult() ?? m_Run.log;
            m_Run = null;
            RewriteJobMode("auto");
            AssetDatabase.Refresh();
            if (m_ApplyAfterRun && res.Contains("\"ok\":true"))
            {
                var apply = SFPipeline.Apply(m_Last.fontAsset, m_Last.stylizedPng);
                Report("Photoshop: " + res + "\n\n" + SFChecks.Format(apply.check));
                RenderTest();
            }
            else Report("Photoshop: " + res);
            Repaint();
        }

        void RenderTest()
        {
            var r = SFRender.Render(new SFRenderRequest { fontAsset = AssetDatabase.GetAssetPath(m_Asset) });
            Report(m_Report + $"\nTest sheet: {r.output}" + (r.diffPercent >= 0 ? $"  (changed vs baseline: {r.diffPercent:0.00}%)" : ""));
            EditorUtility.RevealInFinder(r.output);
        }

        void Report(string text) { m_Report = text; Debug.Log("[TYVO] " + text); Repaint(); }
    }
}
