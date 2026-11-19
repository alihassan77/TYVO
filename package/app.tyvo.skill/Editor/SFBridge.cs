using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Mindstorm.Tyvo.Editor
{
    [Serializable]
    public class SFCommand
    {
        public string id;
        public string command;            // ping setup refresh styles generate photoshop apply check render accept run analyze compare
        public string fontAsset;
        public string png;
        public string job;
        public string script;             // photoshop: stylize_atlas.jsx (default) | finalize_psd.jsx
        public string image;              // analyze / compare: reference image (absolute path)
        public string text;               // compare: the text shown in the reference
        public string styleId;            // analyze: id/name for the draft style
        public string styleName;
        public bool save;                 // analyze: write the draft into Assets/StylizedFonts/Styles
        public SFGenerateRequest generate;
        public SFRenderRequest render;
    }

    [Serializable]
    public class SFPing
    {
        public string unity;
        public string project;
        public string packageRoot;
        public bool tmpShaders;
        public bool tmpSettings;
        public string platform;
    }

    [Serializable]
    public class SFStyleInfo { public string id, name, track, path, description; public int requiredPadding; }

    [Serializable]
    public class SFStyleList { public List<SFStyleInfo> styles = new List<SFStyleInfo>(); }

    [Serializable]
    public class SFRunResult
    {
        public SFGenerateResult generate;
        public string photoshop;          // raw JSON written by the .jsx
        public SFPipeline.SFApplyResult apply;
        public SFRenderResult render;
        public string summary;
    }

    /// <summary>
    /// File bridge so Claude Code (or any script) can drive the open Unity editor.
    /// Write  &lt;project&gt;/Temp/TYVO/bridge/cmd-&lt;id&gt;.json  and wait for  res-&lt;id&gt;.json .
    /// Response: {"id": "...", "ok": true|false, "error": "...", "result": {...}}
    /// </summary>
    [InitializeOnLoad]
    public static class SFBridge
    {
        public static readonly string Folder = SFPaths.ProjectRoot + "/Temp/TYVO/bridge";
        static double s_Next;
        static readonly Dictionary<string, Func<(bool done, bool ok, string error, string json)>> s_Pending = new Dictionary<string, Func<(bool, bool, string, string)>>();

        static SFBridge()
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(Folder + "/alive.txt", DateTime.Now.ToString("o"));
            EditorApplication.update += Tick;
        }

        static void Tick()
        {
            if (EditorApplication.timeSinceStartup < s_Next) return;
            s_Next = EditorApplication.timeSinceStartup + 0.3;

            foreach (var id in s_Pending.Keys.ToList())
            {
                (bool done, bool ok, string error, string json) state;
                try { state = s_Pending[id](); }
                catch (Exception ex) { state = (true, false, ex.Message, null); }
                if (!state.done) continue;
                s_Pending.Remove(id);
                Respond(id, state.ok, state.error, state.json);
            }

            if (EditorApplication.isCompiling || EditorApplication.isUpdating) return;
            if (!Directory.Exists(Folder)) return;
            foreach (string file in Directory.GetFiles(Folder, "cmd-*.json").OrderBy(f => f))
            {
                string id = Path.GetFileNameWithoutExtension(file).Substring(4);
                SFCommand cmd;
                try { cmd = JsonUtility.FromJson<SFCommand>(File.ReadAllText(file)); }
                catch (IOException) { continue; } // still being written
                catch (Exception ex) { File.Delete(file); Respond(id, false, "Bad command JSON: " + ex.Message, null); continue; }
                File.Delete(file);
                if (string.IsNullOrEmpty(cmd.id)) cmd.id = id;
                Execute(cmd);
            }
        }

        static void Execute(SFCommand cmd)
        {
            try
            {
                switch (cmd.command)
                {
                    case "ping": Respond(cmd.id, true, null, JsonUtility.ToJson(Ping())); break;
                    case "setup": Respond(cmd.id, true, null, JsonUtility.ToJson(Setup())); break;
                    case "refresh": AssetDatabase.Refresh(); Respond(cmd.id, true, null, "{}"); break;
                    case "styles": Respond(cmd.id, true, null, JsonUtility.ToJson(Styles())); break;
                    case "generate": Respond(cmd.id, true, null, JsonUtility.ToJson(SFPipeline.Generate(cmd.generate))); break;
                    case "apply": Respond(cmd.id, true, null, JsonUtility.ToJson(SFPipeline.Apply(cmd.fontAsset, cmd.png))); break;
                    case "check": Respond(cmd.id, true, null, JsonUtility.ToJson(SFChecks.Run(cmd.fontAsset))); break;
                    case "render": Respond(cmd.id, true, null, JsonUtility.ToJson(SFRender.Render(cmd.render))); break;
                    case "accept": Respond(cmd.id, true, null, "{\"baseline\":\"" + SFRender.AcceptBaseline(cmd.fontAsset).Replace("\\", "/") + "\"}"); break;
                    case "analyze": Respond(cmd.id, true, null, JsonUtility.ToJson(AnalyzeCmd(cmd))); break;
                    case "compare": Respond(cmd.id, true, null, JsonUtility.ToJson(SFCompare.Run(cmd.fontAsset, cmd.image, cmd.text))); break;
                    case "photoshop": StartPhotoshop(cmd); break;
                    case "run": StartRun(cmd); break;
                    default: Respond(cmd.id, false, "Unknown command: " + cmd.command, null); break;
                }
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
                Respond(cmd.id, false, ex.Message, null);
            }
        }

        // ------------------------------------------------------------------ commands
        public static SFPing Ping() => new SFPing
        {
            unity = Application.unityVersion,
            project = SFPaths.ProjectRoot,
            packageRoot = SFPaths.PackageRoot,
            tmpShaders = Shader.Find(SFPaths.CustomAtlasShader) != null,
            tmpSettings = Resources.Load("TMP Settings") != null,
            platform = Application.platform.ToString()
        };

        [Serializable] public class SFSetupResult { public bool alreadyInstalled; public string imported; }

        /// <summary>Imports TMP Essential Resources (shaders + settings) when missing.</summary>
        public static SFSetupResult Setup()
        {
            if (Shader.Find(SFPaths.CustomAtlasShader) != null && Resources.Load("TMP Settings") != null)
                return new SFSetupResult { alreadyInstalled = true };
            var ugui = UnityEditor.PackageManager.PackageInfo.FindForAssetPath("Packages/com.unity.ugui/package.json");
            if (ugui == null) throw new Exception("com.unity.ugui is not installed. Add \"com.unity.ugui\": \"2.0.0\" to Packages/manifest.json.");
            string pkg = ugui.resolvedPath + "/Package Resources/TMP Essential Resources.unitypackage";
            if (!File.Exists(pkg)) throw new Exception("TMP Essential Resources package not found at " + pkg);
            AssetDatabase.ImportPackage(pkg, false);
            return new SFSetupResult { imported = pkg.Replace('\\', '/') };
        }

        public static SFStyleList Styles()
        {
            var list = new SFStyleList();
            foreach (var s in SFStyles.All())
                list.styles.Add(new SFStyleInfo { id = s.id, name = s.name, track = s.track, path = s.path, description = s.description, requiredPadding = s.RequiredPadding() });
            return list;
        }

        static SFAnalyzeResult AnalyzeCmd(SFCommand cmd)
        {
            string id = string.IsNullOrEmpty(cmd.styleId) ? SFPaths.Sanitize(Path.GetFileNameWithoutExtension(cmd.image)).ToLowerInvariant().Replace(' ', '-') : cmd.styleId;
            string name = string.IsNullOrEmpty(cmd.styleName) ? id : cmd.styleName;
            string work = SFPaths.WorkFolder("_analyze");
            var res = SFAnalyze.Analyze(cmd.image, $"{work}/{id}_debug.png");
            string json = SFAnalyze.DraftStyleJson(res, id, name);
            string path = cmd.save ? Path.GetFullPath(SFStyles.ProjectStylesFolder + "/" + id + ".json") : $"{work}/{id}.json";
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, json);
            res.draftStyle = path.Replace('\\', '/');
            if (cmd.save) AssetDatabase.Refresh();
            return res;
        }

        static void StartPhotoshop(SFCommand cmd)
        {
            if (string.IsNullOrEmpty(cmd.job) || !File.Exists(cmd.job)) throw new Exception("Job file not found: " + cmd.job);
            string resultJson = ReadJobField(cmd.job, "result");
            var run = SFPhotoshop.Start(string.IsNullOrEmpty(cmd.script) ? "stylize_atlas.jsx" : cmd.script, cmd.job, resultJson);
            s_Pending[cmd.id] = () =>
            {
                if (!run.IsDone) return (false, false, null, null);
                string res = run.ReadResult();
                if (res == null) return (true, false, "Photoshop finished without a result file. Log:\n" + run.log, null);
                AssetDatabase.Refresh();
                bool ok = res.Contains("\"ok\":true");
                return (true, ok, ok ? null : "Photoshop script reported an error (see result).", res);
            };
        }

        /// <summary>generate -> Photoshop -> apply -> check -> render, in one call.</summary>
        static void StartRun(SFCommand cmd)
        {
            var result = new SFRunResult { generate = SFPipeline.Generate(cmd.generate) };
            var render = cmd.render ?? new SFRenderRequest();

            if (result.generate.reused)
            {
                render.fontAsset = result.generate.fontAsset;
                result.render = SFRender.Render(render);
                result.apply = new SFPipeline.SFApplyResult { fontAsset = result.generate.fontAsset, check = SFChecks.Run(result.generate.fontAsset) };
                result.summary = "Reused existing font asset. " + result.generate.reusedReason;
                Respond(cmd.id, true, null, JsonUtility.ToJson(result));
                return;
            }

            if (cmd.generate.mode == "build-psd")
            {
                var psdRun = SFPhotoshop.Start("stylize_atlas.jsx", result.generate.job, result.generate.resultJson);
                s_Pending[cmd.id] = () =>
                {
                    if (!psdRun.IsDone) return (false, false, null, null);
                    result.photoshop = psdRun.ReadResult() ?? psdRun.log;
                    result.summary = "Layered PSD ready for manual edits: " + result.generate.psd + ". Then run photoshop with script finalize_psd.jsx, then apply.";
                    return (true, result.photoshop.Contains("\"ok\":true"), null, JsonUtility.ToJson(result));
                };
                return;
            }

            var run = SFPhotoshop.Start("stylize_atlas.jsx", result.generate.job, result.generate.resultJson);
            s_Pending[cmd.id] = () =>
            {
                if (!run.IsDone) return (false, false, null, null);
                result.photoshop = run.ReadResult() ?? ("{\"ok\":false,\"log\":\"" + run.log.Replace("\\", "/").Replace("\"", "'").Replace("\n", " | ") + "\"}");
                if (!result.photoshop.Contains("\"ok\":true"))
                    return (true, false, "Photoshop step failed.", JsonUtility.ToJson(result));
                AssetDatabase.Refresh();
                result.apply = SFPipeline.Apply(result.generate.fontAsset, result.generate.stylizedPng);
                render.fontAsset = result.generate.fontAsset;
                result.render = SFRender.Render(render);
                var check = result.apply.check;
                result.summary = (check.passed ? "PASS" : "FAIL") + $": {check.errors.Count} errors, {check.warnings.Count} warnings. Test render: {result.render.output}";
                return (true, true, null, JsonUtility.ToJson(result));
            };
        }

        static string ReadJobField(string jobPath, string field)
        {
            string text = File.ReadAllText(jobPath);
            string key = "\"" + field + "\":";
            int i = text.IndexOf(key, StringComparison.Ordinal);
            if (i < 0) return null;
            int a = text.IndexOf('"', i + key.Length);
            int b = text.IndexOf('"', a + 1);
            return text.Substring(a + 1, b - a - 1).Replace("\\\\", "\\");
        }

        static void Respond(string id, bool ok, string error, string json)
        {
            Directory.CreateDirectory(Folder);
            string body = "{\"id\":\"" + id + "\",\"ok\":" + (ok ? "true" : "false") +
                          ",\"error\":" + (error == null ? "null" : "\"" + error.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "") + "\"") +
                          ",\"result\":" + (string.IsNullOrEmpty(json) ? "null" : json) + "}";
            string tmp = Folder + "/res-" + id + ".tmp";
            File.WriteAllText(tmp, body);
            string final = Folder + "/res-" + id + ".json";
            if (File.Exists(final)) File.Delete(final);
            File.Move(tmp, final);
        }
    }
}
