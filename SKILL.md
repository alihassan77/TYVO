---
name: tyvo
description: TYVO, custom font maker by Ali. Create stylized TextMeshPro (TMP) bitmap fonts in Unity from a .ttf/.otf using Photoshop layer styles (fill, stroke, drop shadow, gradient, bevel, extra layers) and the TMP "Bitmap Custom Atlas" shader, then check padding/bleed and render a test sheet. Use whenever someone wants a styled/outlined/shadowed game font, a TMP font with a Photoshop style, a font atlas stroke, "Split to Layers" on a font atlas, a font for buttons, to reuse one font across coloured buttons, or to fix clipped/bleeding stylized TMP text. Also turns a reference image of styled text (a title, a button label) plus a font file into a matching TMP font, and builds Photoshop .asl style libraries.
---

# TYVO – Custom font maker by Ali

Turns the manual 16-step flow (Font Asset Creator → Extract Atlas → Photoshop Split to Layers + layer style → Bitmap Custom Atlas material) into tested commands. Everything goes through `scripts/tyvo.py`, which drives the **open Unity editor** through a file bridge and drives **Photoshop** through COM (Windows) or AppleScript (macOS).

```
<skill>/
  SKILL.md                     this file
  GUIDE.md                     short team guide (point people here)
  scripts/bootstrap.ps1 / .sh  setup check: Python (installs only if missing) + `tyvo.py ensure`
  scripts/tyvo.py              CLI: ensure, install, ping, setup, styles, analyze, generate, photoshop, apply, check, render, compare, accept, run, asl
  scripts/install_skill.py     installs this skill into ~/.claude/skills (copy, or --link)
  package/app.tyvo.skill/    Unity package (copied into the project by `install`)
    Editor/                    generator, apply, checks, test renderer, image analyzer, compare, Photoshop runner, bridge, Tools window
    Photoshop~/                stylize_atlas.jsx, finalize_psd.jsx, split_to_layers.jsx, build_asl.jsx, sf_common.jsxinc
    Styles~/                   style presets (*.json) + asl/stylized-tmp-font-styles.asl
  reference/styles.md          style JSON schema, effect fields, how to copy a Photoshop style into JSON
  reference/troubleshooting.md failures and what to do
```

`TYVO="<python> <skill>/scripts/tyvo.py --project <UnityProjectRoot>"` in the examples below. If Unity is not open on the project, any command opens it (matching editor from Unity Hub) and waits.

## Before anything else: setup check (automatic, every new project or session)

Run the bootstrap once before the first TYVO command. It skips every step that is already done, so it is safe to run on a machine/project that is already set up:
```
# Windows
powershell -NoProfile -ExecutionPolicy Bypass -File <skill>/scripts/bootstrap.ps1 -Project "<UnityProjectRoot>"
# macOS
sh <skill>/scripts/bootstrap.sh "<UnityProjectRoot>"
```
1. **Python 3.8+**: uses the one already installed; only if none is found, installs Python 3.12 (Windows: `winget`, per user, no admin; macOS: Homebrew). The Microsoft Store `python` alias is ignored.
2. **Unity package** (`tyvo.py ensure`): installs `Packages/app.tyvo.skill` only if it is missing or differs from the skill's copy, opens Unity if needed, imports TMP Essential Resources only if missing. Prints `"package": "already up to date"` when nothing was changed.

The last line is `PYTHON=<path>`: use that path as `<python>` in `$TYVO` (right after a fresh install, plain `python` may not be on PATH yet in this shell). Tell the user in one line what was installed, or that everything was already set up. If the bootstrap fails (no winget/Homebrew, no Unity editor for the project's version), give the user the error and the manual fix from it; don't work around it.

Every Unity command (`run`, `generate`, `apply`, ...) also checks the package first and reinstalls it only when out of date, so a skill update reaches the project automatically.

## 0. From a reference image + font file (the main use)

The user gives an image of styled text and a .ttf/.otf. Copy both into the project (`Assets/Fonts/`, `StylizedFontsWork/_refs/`), then:

1. **Look at the image yourself first.** Note the layer stack you see (body flat/gradient, outlines, rims, extrude, glow, highlights), the background, and the exact text (case matters: small caps vs lowercase).
2. **Analyze:** `$TYVO analyze --image <ref.png> --style-id <id> --style-name "<Name>" --save`
   Writes a draft `units: "cap"` style to `Assets/StylizedFonts/Styles/<id>.json` and a debug image (`StylizedFontsWork/_analyze/<id>_debug.png`: reference + swatches of body top/mid/bottom, stroke bands, extrude). Read both.
   - `confidence` ≥ 0.8: trust the numbers. 0.5–0.8: check each band against what you saw. < 0.5: fix the draft by hand (crop tighter, PNG not JPG, plain background helps).
   - Things the analyzer cannot see, add by hand: glows, inner highlights/gloss, bevels, textures, per-letter colours. Colours under 3 px wide are approximate.
3. **Build:** `$TYVO run --font <ttf> --style <id> --text "<ref text>" --bg "<ref background>"`.
4. **Compare:** `$TYVO compare --font-asset <asset> --image <ref.png> --text "<ref text, same case>"` → `score` (0–100), `diffs` per band in % of capital height, and `compare.png` (reference on top, TYVO below). Read the image, not only the score.
5. **Tune** the style JSON from the diffs (they say thinner/thicker by N %), rebuild with `--no-reuse`, compare again. Usually 0–2 rounds. Stop at score ≥ 90 and a side-by-side that looks right, then show the user and take feedback in their words.
6. On approval: `$TYVO accept --font-asset <asset>`.

Known limits to tell the user: letters drawn by TMP can't fuse rims as tightly as a single Photoshop text layer (we bake `autoLetterSpacing` so outlines don't paint over neighbours); a different font than the reference changes letter shapes; lowercase vs small caps.

## 1. Decide the track first (do this before generating anything)

Ask/confirm what the text needs to look like, then pick:

| Situation | Track | Style |
|---|---|---|
| Plain body colour + stroke/shadow that is just a **darker shade of the button behind it** (grey btn → dark grey stroke, green btn → dark green stroke) | **B: shared** | `shared-dark-stroke` / `shared-dark-stroke-shadow` (white body, black stroke at ~45 % alpha). ONE font asset for every button. Body colour = TMP text colour. |
| Gradient, bevel, coloured stroke that is not a shade of the background, extrude/extra layers, texture | **A: photoshop** | a `track: "photoshop"` style, e.g. `purple-nametag`, `gold-bevel-3d`, or a new one |
| Needs hand-painted extra layers Photoshop styles can't express | **A + manual** | generate with `--mode build-psd`, artist edits the PSD, then `finalize` |

Rules:
- **Never make a second font asset for the same font + same look.** `generate`/`run` reuse automatically (match on source font GUID + style signature + charset) and return `reused: true` with the existing asset. Pass `--no-reuse` only to rebuild in place (GUIDs are kept, prefabs stay linked).
- Track B works only on flat or near-flat backgrounds. On light buttons (yellow/white) the stroke may be too faint: check the test render; if needed make a copy of the style with higher `opacity` on the stroke (e.g. 60) rather than per-button fonts.
- Track B text colour is set on the TMP component (`color`), never in the material.

## 2. Project setup details

The bootstrap above does all of this. The single steps, for when something fails:
```
$TYVO ensure       # install-if-needed + open Unity + TMP Essential Resources if missing (skips what is done)
$TYVO install      # force a reinstall: copies the package into Packages/, adds com.unity.ugui to the manifest
$TYVO ping         # tmpShaders / tmpSettings must be true
$TYVO setup        # if false: imports TMP Essential Resources, then ping again after ~20 s
$TYVO styles       # lists presets with their computed requiredPadding
```
`tyvo.py` brings the Unity window to the front if a command is not picked up within 10 s.
Photoshop must be installed (2024+). It can be open with the user's own documents: the scripts open/close only their own files.

## 3. Build

Full pipeline (generate → Photoshop → apply → check → render):
```
$TYVO run --font "Assets/Fonts/MyFont.ttf" --style purple-nametag [--padding N] [--atlas-size 1024] [--charset extended-ascii|ascii|numbers|custom --characters "..."] [--text "PLAY"] [--bg "#3DBE4E" --bg "#8E8E93"]
```
Step by step (same thing, useful when something fails):
```
$TYVO generate --font ... --style ...           # font asset (Static, SMOOTH bitmap, auto sampling size) + raw atlas + job.json
$TYVO photoshop --job <job.json>                # stylize_atlas.jsx -> Assets/.../<name> Atlas.png (+ layered PSD in StylizedFontsWork/)
$TYVO apply --font-asset <asset>                # importer (uncompressed, no mips, clamp), shader Bitmap Custom Atlas, _MainTex, _Padding = atlas padding
$TYVO check --font-asset <asset>
$TYVO render --font-asset <asset> [--text ...] [--bg ...]
```
Manual-layers path (artist adds layers):
```
$TYVO generate --font ... --style ... --mode build-psd
$TYVO photoshop --job <job.json>                # writes the layered PSD only
#   artist edits StylizedFontsWork/<name>/<name>.psd (keep canvas size; stay inside each glyph cell)
$TYVO photoshop --job <job.json> --script finalize_psd.jsx
$TYVO apply --font-asset <asset>
```
Outputs: `Assets/StylizedFonts/<Font>/<Font> - <Style>.asset` (+ `... Atlas.png`). Work files (raw atlas, job, PSD, test renders, baseline) go to `<project>/StylizedFontsWork/<name>/` (outside Assets; never shipped).

Padding: `0` = automatic = the style's `requiredPadding` (effect reach + 2 px). Only pass `--padding` to go higher.

## 4. Check and test (always, before saying it is done)

`apply` and `run` include the check; run `check` again after any manual change. Report to the user:
- **errors** must be fixed. Common ones and the fix:
  - *glyphs touch the edge of their cell* → effects clipped → regenerate with `--padding <recommendedPadding> --no-reuse`, re-run Photoshop, apply.
  - *painted pixels outside every glyph cell* (bleed) → same fix; or the PSD canvas/layers were moved.
  - *glyphs are empty* → a layer was hidden/deleted in the PSD.
  - *Material Padding ≠ atlas padding*, *uses the raw atlas*, *Dynamic* → run `apply`.
  - *Shared style: body not white / stroke not black* → the style is not Track-B-safe; fix the style.
- **warnings**: compression/mipmaps/NPOT/platform overrides (apply fixes them), padding below style need.
- **Test render**: open/read the PNG `render.output` and look at it. Rows = text on each test background, then the full glyph sheet. Look for: cut-off strokes/shadows, slivers of other letters above/below glyphs (bleed), faint stroke on light buttons (Track B), missing characters (□).
- `missingInFont` from generate: characters the TTF does not have. Tell the user if any are printable (not the U+2000–206F spacing block); they need the glyph-editor flow or another font.
- When the user approves the look: `$TYVO accept --font-asset <asset>` stores a baseline; later renders report `diffPercent` against it (regression test after regenerating).

## 5. Styles and .asl

- Presets: `package/.../Styles~/*.json` (shared with the team) and `<project>/Assets/StylizedFonts/Styles/*.json` (project-specific; overrides same `id`).
- New look requested → copy the closest preset, edit values (see `reference/styles.md`), save it in the project styles folder, then `run`.
- Matching an existing Photoshop style: ask the user for the layer-style values (or a screenshot of the Layer Style dialog) and transcribe them; colours sampled from screenshots are approximate, say so.
- Build a Photoshop styles library from presets (no Unity needed, Photoshop must be installed):
  ```
  python <skill>/scripts/tyvo.py asl --styles <a.json> <b.json> --output <file.asl>
  ```
  Multi-layer styles become one Photoshop style per layer ("Name - Layer"); layer offsets are not stored in .asl (reported as warnings).

## 6. Standalone Photoshop tools (for artists, no Claude needed)

`File > Scripts > Browse…`:
- `split_to_layers.jsx` – splits the active atlas layer into one layer per glyph in a "Glyphs" group. Uses exact glyph rects when `<name>.job.json` sits next to the PNG, otherwise asks for a gap (default 4 px, like the old script; slower and splits multi-part glyphs such as `"` `%`).
- `stylize_atlas.jsx`, `finalize_psd.jsx` – pick a `*.job.json`.
- `build_asl.jsx` – pick style JSONs, save an .asl.

Unity side for artists: **Tools > TYVO** window has the same buttons.

## 7. Performance notes

- Layers without gradients are applied to the whole atlas as one layer (identical pixels, ~3–6 s). Only layers with a gradient overlay are built per glyph (~2 min for ~210 glyphs) because the gradient must span each glyph, not the atlas. `"split": false` on a style forces single-layer, `"split": true` forces per-glyph.
- Outline + 3D extrude: build the extrude as an offset layer, not a drop shadow (Photoshop shadows ignore the stroke). See `reference/styles.md`. Presets `golden-victory` (Track A) and `candy-pop` (Track B) are working examples.
- Thick outlines (> ~4 px) need `letterSpacing` in the style, or each letter's outline paints over the previous letter in TMP. Check the test render for this: it is the most common "looks wrong" issue. `hexa-club` is an example (outline + white rim + extrude, letterSpacing 10).
- Effect sizes are atlas pixels, so they look thicker when the sampling size drops (big padding or many characters) and thinner on fonts with few glyphs (auto picks a big size). For consistent looks across fonts, pass the same `--sampling`.

See `reference/troubleshooting.md` if a command times out or Photoshop/Unity misbehave.
