# TYVO – Team Guide

*Custom font maker by Ali*

Make styled TextMeshPro fonts (outline, 3D extrude, gradient, shadow) from any .ttf/.otf, with Photoshop doing the styling and Unity checking the result.

## What you need
- Unity 6 project (TextMeshPro / uGUI 2.0)
- Photoshop 2024 or newer
- Claude Code (optional – everything also works from the Unity menu)

Python 3 and the Unity package are set up for you (see below).

## Install (once per computer)
1. Copy the `TYVO` folder anywhere (e.g. from Box) or clone https://github.com/alihassan77/TYVO.
2. Add the skill to Claude Code (needs Python; if you don't have it, run step 1 of the next section first):
   ```
   python TYVO/scripts/install_skill.py
   ```
3. Restart Claude Code.

## Setup (automatic)
With Claude you don't need to do anything: the first time you ask for a font in a project, Claude runs the setup check. It skips whatever is already in place and only installs what is missing:
1. **Python 3**: installed with winget (Windows) or Homebrew (macOS) only if not found.
2. **Unity package**: copied into the project's `Packages/` only if missing or out of date, plus TMP Essential Resources if missing.

To run it yourself:
```
powershell -NoProfile -ExecutionPolicy Bypass -File TYVO/scripts/bootstrap.ps1 -Project "<path to Unity project>"
```
(macOS: `sh TYVO/scripts/bootstrap.sh "<path to Unity project>"`)

## Make a font

**From an image (best)** – give Claude a screenshot of the styled text + the .ttf/.otf:
> Make this title style with this font *(attach image + font)*

TYVO measures the colours, outlines and extrude from the image, builds the font, and shows a side-by-side with a match score. Then just say what to change ("outline thinner", "more yellow").

**With Claude** – or describe it, e.g.:
> Make a Candy Pop font from Assets/Fonts/MyFont.ttf

> Create the Golden Victory style for Olive Days

> I need a white button font with a dark green outline

Claude picks the style, builds it, checks it and shows you a test image.

**Without Claude** – in Unity: **Tools › TYVO**
1. Drop in the font, pick a style.
2. Click **Run all**.
3. Read the report. **PASS** = done.

Result: `Assets/StylizedFonts/<Font>/<Font> - <Style>.asset` → assign it to your TMP text.

## Which style?

| Style | Use for | How to colour it |
|---|---|---|
| **Candy Pop** | Button labels (PLAY, NEXT…) | One font for **every** button. Set the text colour on the TMP component; the outline turns into a darker shade of the button automatically. |
| **Golden Victory** | Headlines / rewards (GREAT JOB!) | Fixed look. Keep the text colour white. |
| Purple NameTag, Gold Bevel 3D, Shared Dark Stroke | Examples | – |

**Rule: don't make a new font per button colour.** If it is "plain body + darker outline", use Candy Pop (shared). The tool also refuses to make a duplicate of a font + style that already exists – it hands you the existing one.

## If the report says FAIL
| Message | Do this |
|---|---|
| *glyphs touch the edge… regenerate with padding N* | Rebuild with that padding (Claude does it if you ask) |
| *characters do not fit* | Use atlas size 2048 or fewer characters |
| *material / importer* problems | Click **Apply stylized atlas** |
| *missing in font* | The .ttf doesn't have those letters – pick another font or add them with the glyph editor |

## Hand-made tweaks in Photoshop
**Build layered PSD** → edit `StylizedFontsWork/<name>/<name>.psd` (don't resize the canvas, stay inside each letter's area) → **Finalize edited PSD** → **Apply** → **Check**.

## New style
Copy a JSON from `package/app.tyvo.skill/Styles~/`, change colours/sizes, save it in `Assets/StylizedFonts/Styles/` of your project. Details: `reference/styles.md`.
Photoshop version of all styles: `Styles~/asl/stylized-tmp-font-styles.asl` (Styles panel › Import Styles).

## Don't
- Don't resize or move anything in the atlas PNG.
- Don't set the font asset back to Dynamic.
- Don't compress the atlas texture.
- Don't commit `StylizedFontsWork/` if you don't need the PSDs (it's working files only).
