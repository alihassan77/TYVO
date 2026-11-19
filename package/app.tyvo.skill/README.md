# TYVO (app.tyvo.skill)

*Custom font maker by Ali*

Stylized TextMeshPro bitmap fonts with Photoshop layer styles.

- **Tools > TYVO**: generate font asset + raw atlas, stylize in Photoshop (auto or via an editable layered PSD), apply with the Bitmap Custom Atlas shader, check padding/bleed, render a test sheet.
- `Photoshop~/`: `stylize_atlas.jsx`, `finalize_psd.jsx`, `split_to_layers.jsx`, `build_asl.jsx` (also usable from File > Scripts > Browse).
- `Styles~/`: style presets (JSON) and a matching `.asl` library. Project presets in `Assets/StylizedFonts/Styles/` override them.
- File bridge in `Temp/TYVO/bridge/` so Claude Code (`tyvo` skill, `scripts/tyvo.py`) can drive the editor.

Requires Unity 6 (com.unity.ugui 2.0 with TMP) and Photoshop 2024+ on Windows or macOS.
