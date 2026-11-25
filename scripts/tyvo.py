#!/usr/bin/env python3
"""TYVO - Custom font maker by Ali.
Command line client for the Unity bridge (stdlib only, Windows + macOS).

Talks to the open Unity editor through files in <project>/Temp/TYVO/bridge/.
The Unity package app.tyvo.skill must be installed (see `install`).

Examples
  python tyvo.py --project "F:/Unity Projects/Testing" ensure     (one-time setup; skips what is already done)
  python tyvo.py --project "F:/Unity Projects/Testing" install    (force a reinstall of the package)
  python tyvo.py --project ... ping
  python tyvo.py --project ... setup
  python tyvo.py --project ... styles
  python tyvo.py --project ... run --font "Assets/Fonts/Arial Black.ttf" --style purple-nametag
  python tyvo.py --project ... generate --font ... --style ... [--padding 12] [--mode build-psd]
  python tyvo.py --project ... photoshop --job <job.json> [--script finalize_psd.jsx]
  python tyvo.py --project ... apply --font-asset "Assets/StylizedFonts/.../X.asset"
  python tyvo.py --project ... check --font-asset ...
  python tyvo.py --project ... render --font-asset ... [--text "PLAY"] [--bg "#3DBE4E" --bg "#8E8E93"]
  python tyvo.py --project ... accept --font-asset ...
  python tyvo.py --project ... analyze --image ref.png [--style-id my-title --style-name "My Title"] [--save]
  python tyvo.py --project ... compare --font-asset ... --image ref.png --text "Hexa cluB"

If Unity is not running on the project, TYVO opens it (matching editor from Unity Hub) and waits.
  python tyvo.py asl --styles a.json b.json --output styles.asl      (Photoshop only, no Unity)
"""
import argparse
import json
import os
import platform
import shutil
import subprocess
import sys
import tempfile
import time
import uuid

SKILL_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
PACKAGE_NAME = "app.tyvo.skill"
PACKAGE_SRC = os.path.join(SKILL_ROOT, "package", PACKAGE_NAME)


def bridge_dir(project):
    return os.path.join(project, "Temp", "TYVO", "bridge")


def focus_unity(project):
    """Bring the Unity editor for this project to the front so it refreshes/compiles (Windows/macOS)."""
    try:
        if platform.system() == "Windows":
            # Alt tap + SetForegroundWindow: plain AppActivate is often blocked by Windows' foreground lock.
            ps = (
                "Add-Type 'using System; using System.Runtime.InteropServices; public class TyvoW { "
                "[DllImport(\"user32.dll\")] public static extern bool SetForegroundWindow(IntPtr h); "
                "[DllImport(\"user32.dll\")] public static extern bool ShowWindow(IntPtr h, int c); "
                "[DllImport(\"user32.dll\")] public static extern void keybd_event(byte k, byte s, uint f, UIntPtr e); }'; "
                "$ids = Get-CimInstance Win32_Process -Filter \"Name='Unity.exe'\" | "
                "Where-Object { $_.CommandLine -match [regex]::Escape('%s') -and $_.CommandLine -notmatch 'batchMode' } | ForEach-Object { $_.ProcessId }; "
                "$p = Get-Process -Id $ids -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1; "
                "if ($p) { [TyvoW]::keybd_event(0x12,0,0,[UIntPtr]::Zero); [TyvoW]::keybd_event(0x12,0,2,[UIntPtr]::Zero); "
                "[void][TyvoW]::ShowWindow($p.MainWindowHandle, 9); [void][TyvoW]::SetForegroundWindow($p.MainWindowHandle) }"
            ) % project.replace("/", "\\")
            subprocess.run(["powershell", "-NoProfile", "-Command", ps], capture_output=True, timeout=20)
        elif platform.system() == "Darwin":
            subprocess.run(["osascript", "-e", 'tell application "Unity" to activate'], capture_output=True, timeout=20)
    except Exception:
        pass


def _norm(p):
    return os.path.normcase(os.path.abspath(p)).replace(os.sep, "/").rstrip("/")


def unity_running(project):
    """True when a (non-batchmode) Unity editor has this project open."""
    target = _norm(project)
    try:
        if platform.system() == "Windows":
            ps = ("Get-CimInstance Win32_Process -Filter \"Name='Unity.exe'\" | "
                  "Where-Object { $_.CommandLine -notmatch 'batchMode' } | ForEach-Object { $_.CommandLine }")
            out = subprocess.run(["powershell", "-NoProfile", "-Command", ps], capture_output=True, text=True, timeout=30).stdout
        else:
            out = subprocess.run(["ps", "-axo", "command"], capture_output=True, text=True, timeout=30).stdout
        for line in out.splitlines():
            if "batchmode" in line.lower():
                continue
            if target in os.path.normcase(line).replace(os.sep, "/"):
                return True
    except Exception:
        pass
    return False


def unity_editor_for(project):
    """Path of the Unity editor matching ProjectSettings/ProjectVersion.txt (Unity Hub install locations)."""
    version = None
    with open(os.path.join(project, "ProjectSettings", "ProjectVersion.txt"), encoding="utf-8") as f:
        for line in f:
            if line.startswith("m_EditorVersion:"):
                version = line.split(":", 1)[1].strip()
    if not version:
        raise SystemExit("Cannot read the Unity version from ProjectSettings/ProjectVersion.txt")
    roots = []
    if platform.system() == "Windows":
        roots.append(os.path.join(os.environ.get("ProgramFiles", "C:/Program Files"), "Unity", "Hub", "Editor"))
        sec = os.path.join(os.environ.get("APPDATA", ""), "UnityHub", "secondaryInstallPath.json")
        if os.path.exists(sec):
            try:
                with open(sec, encoding="utf-8") as f:
                    v = json.load(f)
                if v:
                    roots.append(v)
            except Exception:
                pass
        rel = os.path.join("Editor", "Unity.exe")
    else:
        roots.append("/Applications/Unity/Hub/Editor")
        rel = os.path.join("Unity.app", "Contents", "MacOS", "Unity")
    for r in roots:
        exe = os.path.join(r, version, rel)
        if os.path.exists(exe):
            return exe
    raise SystemExit("Unity %s is not installed via Unity Hub (looked in: %s). Install it or open the project manually." % (version, ", ".join(roots)))


def ensure_unity(project, wait=900):
    """Open the project in Unity if it isn't open, then wait until the TYVO bridge reports alive."""
    if unity_running(project):
        return False
    exe = unity_editor_for(project)
    alive = os.path.join(bridge_dir(project), "alive.txt")
    started = time.time()
    print("TYVO: opening %s in Unity..." % project, file=sys.stderr)
    flags = 0x00000008 if platform.system() == "Windows" else 0  # DETACHED_PROCESS
    subprocess.Popen([exe, "-projectPath", project], creationflags=flags, close_fds=True)
    while time.time() - started < wait:
        if os.path.exists(alive) and os.path.getmtime(alive) >= started - 5:
            time.sleep(5)  # let the first domain reload settle
            return True
        time.sleep(3)
    raise SystemExit("Unity did not finish opening within %ds. Is the TYVO package installed (`tyvo.py install`)?" % wait)


def call(project, payload, timeout=900):
    ensure_unity(project)
    folder = bridge_dir(project)
    if not os.path.isdir(folder):
        raise SystemExit(
            "No Unity bridge in %s.\nOpen the project in Unity and install the package (`tyvo.py install`), then focus the Unity window once so it compiles." % project
        )
    cid = time.strftime("%H%M%S") + "-" + uuid.uuid4().hex[:6]
    payload = dict(payload, id=cid)
    tmp = os.path.join(folder, "cmd-%s.tmp" % cid)
    with open(tmp, "w", encoding="utf-8") as f:
        json.dump(payload, f)
    os.replace(tmp, os.path.join(folder, "cmd-%s.json" % cid))

    res = os.path.join(folder, "res-%s.json" % cid)
    start = time.time()
    focused = False
    while time.time() - start < timeout:
        if os.path.exists(res):
            time.sleep(0.05)
            with open(res, encoding="utf-8") as f:
                data = json.load(f)
            os.remove(res)
            return data
        if not focused and time.time() - start > 10 and os.path.exists(os.path.join(folder, "cmd-%s.json" % cid)):
            focus_unity(project)  # command not picked up yet: Unity may be throttled in the background
            focused = True
        time.sleep(0.25)
    raise SystemExit("Timed out after %ds waiting for Unity (command '%s'). Is the editor open on %s and not stuck in a dialog?" % (timeout, payload.get("command"), project))


OLD_PACKAGE_NAMES = ("com.mindstorm.stylized-tmp-font", "com.mindstorm.tyvo")


def package_status(project):
    """Why the project's copy of the Unity package needs (re)installing, or "" when it is current.
    Compares every file of the skill's package byte for byte; extra files Unity adds (.meta) are ignored."""
    for old_name in OLD_PACKAGE_NAMES:
        if os.path.isdir(os.path.join(project, "Packages", old_name)):
            return "old package name %s present" % old_name
    dst = os.path.join(project, "Packages", PACKAGE_NAME)
    if not os.path.isdir(dst):
        return "not installed"
    for root, _, files in os.walk(PACKAGE_SRC):
        for name in files:
            src = os.path.join(root, name)
            rel = os.path.relpath(src, PACKAGE_SRC)
            other = os.path.join(dst, rel)
            if not os.path.isfile(other):
                return "missing " + rel.replace(os.sep, "/")
            if os.path.getsize(src) != os.path.getsize(other):
                return "out of date (%s)" % rel.replace(os.sep, "/")
            with open(src, "rb") as f1, open(other, "rb") as f2:
                if f1.read() != f2.read():
                    return "out of date (%s)" % rel.replace(os.sep, "/")
    try:
        with open(os.path.join(project, "Packages", "manifest.json"), encoding="utf-8") as f:
            if "com.unity.ugui" not in json.load(f).get("dependencies", {}):
                return "com.unity.ugui missing from manifest"
    except (OSError, ValueError):
        return "cannot read Packages/manifest.json"
    return ""


def ensure_installed(project):
    """Install the package only when it is missing or out of date. Returns the install result, or None if skipped."""
    reason = package_status(project)
    if not reason:
        return None
    print("TYVO: Unity package %s, installing..." % reason, file=sys.stderr)
    result = install(project)
    result["reason"] = reason
    return result


def install(project):
    if not os.path.isdir(os.path.join(project, "Assets")):
        raise SystemExit("Not a Unity project: " + project)
    for old_name in OLD_PACKAGE_NAMES:
        old = os.path.join(project, "Packages", old_name)
        if os.path.isdir(old):
            shutil.rmtree(old)  # earlier package names: would define the same classes twice
    dst = os.path.join(project, "Packages", PACKAGE_NAME)
    if os.path.isdir(dst):
        shutil.rmtree(dst)
    shutil.copytree(PACKAGE_SRC, dst)
    manifest_path = os.path.join(project, "Packages", "manifest.json")
    with open(manifest_path, encoding="utf-8") as f:
        manifest = json.load(f)
    deps = manifest.setdefault("dependencies", {})
    changed = False
    if "com.unity.ugui" not in deps:
        deps["com.unity.ugui"] = "2.0.0"
        changed = True
    if changed:
        with open(manifest_path, "w", encoding="utf-8") as f:
            json.dump(manifest, f, indent=2)
            f.write("\n")
    os.makedirs(bridge_dir(project), exist_ok=True)
    result = {"installed": dst.replace("\\", "/"), "manifestChanged": changed, "unityReloaded": False}
    # If the editor is open, make it import + recompile now and wait for the bridge to come back,
    # so the next command runs the new code (not the old assembly).
    if unity_running(project):
        alive = os.path.join(bridge_dir(project), "alive.txt")
        started = time.time()
        focus_unity(project)
        while time.time() - started < 240:
            if os.path.exists(alive) and os.path.getmtime(alive) > started:
                time.sleep(3)
                result["unityReloaded"] = True
                break
            if time.time() - started > 30 and int(time.time() - started) % 30 < 1:
                focus_unity(project)
            time.sleep(1)
        if not result["unityReloaded"]:
            result["warning"] = "Unity did not reload within 4 minutes: check the Console for compile errors."
    return result


def ensure(project, timeout=900):
    """One-time project setup, skipping every step that is already done:
    package (install only if missing/out of date), Unity open + bridge alive, TMP Essential Resources."""
    if not os.path.isdir(os.path.join(project, "Assets")):
        raise SystemExit("Not a Unity project: " + project)
    installed = ensure_installed(project)
    result = {"package": "installed (%s)" % installed["reason"] if installed else "already up to date",
              "tmpEssentials": "already imported", "ready": False}
    if installed and installed.get("warning"):
        result["warning"] = installed["warning"]
    ping = call(project, {"command": "ping"}, timeout).get("result") or {}
    if not (ping.get("tmpShaders") and ping.get("tmpSettings")):
        call(project, {"command": "setup"}, timeout)
        result["tmpEssentials"] = "imported"
        for _ in range(6):  # the import finishes after a domain reload
            time.sleep(10)
            ping = call(project, {"command": "ping"}, timeout).get("result") or {}
            if ping.get("tmpShaders") and ping.get("tmpSettings"):
                break
    result["ready"] = bool(ping.get("tmpShaders") and ping.get("tmpSettings"))
    result["unity"] = ping.get("unity")
    if not result["ready"]:
        result["warning"] = "TMP Essential Resources still missing: in Unity use Window > TextMeshPro > Import TMP Essential Resources."
    return result


def run_photoshop(js, timeout=600):
    """Run a JavaScript string in Photoshop (Windows COM / macOS osascript). Returns stdout."""
    if platform.system() == "Windows":
        ps1 = os.path.join(tempfile.gettempdir(), "tyvo_cli_photoshop.ps1")
        with open(ps1, "w", encoding="utf-8") as f:
            f.write("$js = @'\n%s\n'@\n" % js)
            f.write("$ps = New-Object -ComObject Photoshop.Application\n")
            f.write("for ($i=0; $i -lt 120; $i++) { try { $ps.DoJavaScript($js); exit 0 } catch { if (\"$_\" -match 'busy|RETRYLATER') { Start-Sleep 2 } else { Write-Error \"$_\"; exit 1 } } }\nexit 2\n")
        out = subprocess.run(["powershell", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", ps1], capture_output=True, text=True, timeout=timeout)
    else:
        script = 'tell application id "com.adobe.Photoshop" to do javascript "%s"' % js.replace("\\", "\\\\").replace('"', '\\"')
        out = subprocess.run(["osascript", "-e", script], capture_output=True, text=True, timeout=timeout)
    if out.returncode != 0:
        raise SystemExit("Photoshop error: " + (out.stderr or out.stdout))
    return out.stdout.strip()


def build_asl(styles, output):
    job = {"styles": [os.path.abspath(s).replace("\\", "/") for s in styles], "output": os.path.abspath(output).replace("\\", "/")}
    job_path = os.path.join(tempfile.gettempdir(), "sf_asl_job.json")
    with open(job_path, "w", encoding="utf-8") as f:
        json.dump(job, f)
    jsx = os.path.join(PACKAGE_SRC, "Photoshop~", "build_asl.jsx").replace("\\", "/")
    out = run_photoshop("var SF_ASL_JOB = '%s'; $.evalFile('%s');" % (job_path.replace("\\", "/"), jsx))
    return json.loads(out) if out.startswith("{") else {"raw": out}


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--project", help="Unity project root (folder with Assets/)")
    ap.add_argument("--timeout", type=int, default=900)
    sub = ap.add_subparsers(dest="cmd", required=True)
    for name in ("ping", "setup", "refresh", "styles", "install", "ensure"):
        sub.add_parser(name)

    def gen_args(p):
        p.add_argument("--font", required=True)
        p.add_argument("--style", required=True)
        p.add_argument("--name")
        p.add_argument("--output-folder")
        p.add_argument("--padding", type=int, default=0)
        p.add_argument("--atlas-size", type=int, default=1024)
        p.add_argument("--sampling", type=int, default=0)
        p.add_argument("--charset", default="extended-ascii", choices=["extended-ascii", "ascii", "numbers", "custom"])
        p.add_argument("--characters")
        p.add_argument("--no-reuse", action="store_true")
        p.add_argument("--mode", default="auto", choices=["auto", "build-psd"])

    def render_args(p):
        p.add_argument("--text")
        p.add_argument("--bg", action="append")
        p.add_argument("--color", default="#FFFFFF")
        p.add_argument("--font-size", type=float, default=64)

    gen_args(sub.add_parser("generate"))
    r = sub.add_parser("run"); gen_args(r); render_args(r)
    p = sub.add_parser("photoshop"); p.add_argument("--job", required=True); p.add_argument("--script", default="stylize_atlas.jsx")
    for name in ("apply", "check", "accept"):
        p = sub.add_parser(name); p.add_argument("--font-asset", required=True); p.add_argument("--png")
    p = sub.add_parser("render"); p.add_argument("--font-asset", required=True); render_args(p)
    p = sub.add_parser("analyze"); p.add_argument("--image", required=True); p.add_argument("--style-id"); p.add_argument("--style-name"); p.add_argument("--save", action="store_true")
    p = sub.add_parser("compare"); p.add_argument("--font-asset", required=True); p.add_argument("--image", required=True); p.add_argument("--text", default="")
    p = sub.add_parser("asl"); p.add_argument("--styles", nargs="+", required=True); p.add_argument("--output", required=True)
    a = ap.parse_args()

    if a.cmd == "asl":
        print(json.dumps(build_asl(a.styles, a.output), indent=2))
        return
    if not a.project:
        raise SystemExit("--project is required")
    project = os.path.abspath(a.project)
    if a.cmd == "install":
        print(json.dumps(install(project), indent=2))
        return
    if a.cmd == "ensure":
        print(json.dumps(ensure(project, a.timeout), indent=2))
        return
    # Every Unity command makes sure the package is in place first (skipped when already current).
    ensure_installed(project)

    def gen_payload():
        return {"font": a.font, "style": a.style, "name": a.name or "", "outputFolder": a.output_folder or "", "padding": a.padding,
                "atlasSize": a.atlas_size, "sampling": a.sampling, "charset": a.charset, "characters": a.characters or "",
                "reuse": not a.no_reuse, "mode": a.mode}

    def render_payload(font_asset=""):
        d = {"fontAsset": font_asset, "textColor": a.color, "fontSize": a.font_size, "glyphSheet": True, "backgrounds": a.bg or []}
        if a.text:
            d["text"] = a.text
        else:
            d["text"] = "PLAY  New Text  Level 12"
        return d

    payload = {"command": a.cmd}
    if a.cmd == "generate":
        payload["generate"] = gen_payload()
    elif a.cmd == "run":
        payload["generate"] = gen_payload()
        payload["render"] = render_payload()
    elif a.cmd == "photoshop":
        payload["job"] = os.path.abspath(a.job).replace("\\", "/"); payload["script"] = a.script
    elif a.cmd in ("apply", "check", "accept"):
        payload["fontAsset"] = a.font_asset
        if a.png:
            payload["png"] = a.png
    elif a.cmd == "analyze":
        payload.update(image=os.path.abspath(a.image).replace(os.sep, "/"), styleId=a.style_id or "", styleName=a.style_name or "", save=a.save)
    elif a.cmd == "compare":
        payload.update(fontAsset=a.font_asset, image=os.path.abspath(a.image).replace(os.sep, "/"), text=a.text)
    elif a.cmd == "render":
        payload["render"] = render_payload(a.font_asset)

    data = call(project, payload, a.timeout)
    print(json.dumps(data, indent=2))
    sys.exit(0 if data.get("ok") else 1)


if __name__ == "__main__":
    main()
