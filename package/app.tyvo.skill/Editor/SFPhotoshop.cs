using System;
using System.Diagnostics;
using System.IO;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Mindstorm.Tyvo.Editor
{
    /// <summary>
    /// Runs the bundled .jsx scripts in Photoshop. Windows: COM automation through PowerShell. macOS: osascript.
    /// Photoshop must be installed; it is started if not running. The call is asynchronous (poll IsDone).
    /// </summary>
    public class SFPhotoshopRun
    {
        public Process process;
        public string resultJson;
        public string log = "";
        public DateTime started = DateTime.Now;

        public bool IsDone => process == null || process.HasExited;

        public string ReadResult()
        {
            if (!string.IsNullOrEmpty(resultJson) && File.Exists(resultJson)) return File.ReadAllText(resultJson);
            return null;
        }
    }

    public static class SFPhotoshop
    {
        /// <param name="script">stylize_atlas.jsx | finalize_psd.jsx | build_asl.jsx</param>
        /// <param name="jobVar">SF_JOB (atlas scripts) or SF_ASL_JOB (build_asl.jsx)</param>
        public static SFPhotoshopRun Start(string script, string jobPath, string resultJson, string jobVar = "SF_JOB")
        {
            string jsx = SFPaths.PhotoshopFolder + "/" + script;
            if (!File.Exists(jsx)) throw new Exception("Photoshop script missing: " + jsx);
            if (!string.IsNullOrEmpty(resultJson) && File.Exists(resultJson)) File.Delete(resultJson);

            string js = $"var {jobVar} = '{Esc(jobPath)}'; $.evalFile('{Esc(jsx)}');";
            var psi = Application.platform == RuntimePlatform.WindowsEditor ? Windows(js) : Mac(js);
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;

            var run = new SFPhotoshopRun { resultJson = resultJson };
            run.process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            run.process.OutputDataReceived += (_, e) => { if (e.Data != null) run.log += e.Data + "\n"; };
            run.process.ErrorDataReceived += (_, e) => { if (e.Data != null) run.log += e.Data + "\n"; };
            run.process.Start();
            run.process.BeginOutputReadLine();
            run.process.BeginErrorReadLine();
            return run;
        }

        static string Esc(string s) => s.Replace('\\', '/').Replace("'", "\\'");

        static ProcessStartInfo Windows(string js)
        {
            // Photoshop answers RPC_E_SERVERCALL_RETRYLATER while busy (user interacting, long op); retry.
            string ps1 = Path.Combine(Path.GetTempPath(), "tyvo_photoshop_run.ps1");
            File.WriteAllText(ps1,
                "$ErrorActionPreference = 'Stop'\n" +
                "$js = @'\n" + js + "\n'@\n" +
                "$ps = New-Object -ComObject Photoshop.Application\n" +
                "for ($i = 0; $i -lt 120; $i++) {\n" +
                "  try { $r = $ps.DoJavaScript($js); Write-Output $r; exit 0 }\n" +
                "  catch { if (\"$_\" -match 'busy|RETRYLATER|0x8001010A') { Start-Sleep -Seconds 2 } else { Write-Error \"$_\"; exit 1 } }\n" +
                "}\n" +
                "Write-Error 'Photoshop stayed busy for 4 minutes.'; exit 2\n");
            return new ProcessStartInfo("powershell.exe", $"-NoProfile -ExecutionPolicy Bypass -File \"{ps1}\"");
        }

        static ProcessStartInfo Mac(string js)
        {
            string scpt = Path.Combine(Path.GetTempPath(), "tyvo_photoshop_run.applescript");
            File.WriteAllText(scpt, "tell application id \"com.adobe.Photoshop\"\n  do javascript \"" + js.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"\nend tell\n");
            return new ProcessStartInfo("/usr/bin/osascript", $"\"{scpt}\"");
        }
    }
}
