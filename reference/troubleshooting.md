# Troubleshooting

| Symptom | Cause | Fix |
|---|---|---|
| `No Unity bridge in ...` | Package not installed or never compiled | `tyvo.py install`, open the project in Unity, focus the window once |
| Command times out | Unity in background / modal dialog / compiling | `tyvo.py` focuses Unity after 10 s; check for an open dialog in Unity; check `%LOCALAPPDATA%/Unity/Editor/Editor.log` (macOS `~/Library/Logs/Unity/Editor.log`) for `error CS` |
| `ping` shows `tmpShaders: false` | TMP Essential Resources not imported | `tyvo.py setup`, wait ~20 s, `ping` again |
| `Photoshop finished without a result file` | Photoshop busy for >4 min, or a Photoshop dialog is open | Close dialogs in Photoshop, retry. Windows uses COM `Photoshop.Application` (latest installed version registers it) |
| `The command "X" is not currently available` from Photoshop | The user is in a modal state (text editing, dialog, Free Transform) | Ask the user to press Enter/Esc in Photoshop and retry |
| Check: glyphs touch the edge / bleed pixels | Padding too small for the effect, or PSD content moved | `generate --padding <recommendedPadding> --no-reuse`, re-run Photoshop, `apply` |
| Check: glyphs are empty | Layer hidden/deleted in manual PSD | Fix the PSD, `finalize_psd.jsx`, `apply` |
| Text shows slivers of other letters | Material `_Padding` larger than atlas padding, or texture compressed/mipmapped | `apply` (resets importer and material) |
| Text blurry when big | Bitmap atlas sampled at N pt (see `samplingPointSize`) | Keep on-screen size near the sampling size, or use a 2048 atlas |
| New characters missing at runtime | Font asset Dynamic | `apply`/`generate` sets Static; add characters with `--charset custom --characters "..."` and `--no-reuse` |
| `missingInFont` lists printable characters | TTF lacks those glyphs | Use the Glyph Editor flow (Tophix) to add them, or a different font |
| Track B stroke too faint on light buttons | Alpha too low for light backgrounds | Copy the style, raise stroke `opacity` (55–65), `run` with the new style id |
| .asl import fails in Photoshop | Unsupported effect field combination | Remove the last added effect field, rebuild; report which effect |
| `analyze`: low confidence / wrong bands | Busy background, JPG artefacts, letters overlapping, tiny image | Crop to the text, use PNG, upscale small images 2x; fix the draft JSON by hand using the debug image |
| `analyze`: capital height looks wrong | Mostly lowercase or very short text | Use a reference with capitals, or set sizes by hand |
| `compare`: score low but image looks right | Different text/case than the reference, or a different font | Pass the exact text in the same case; judge by `compare.png` |
| Command runs old code after editing the package | Unity had not recompiled yet | `install` now waits for Unity to reload; if it warns, check the Console for compile errors |

