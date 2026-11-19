# Style JSON reference

One file = one look. Read by Unity (padding/reuse/checks) and by Photoshop (`sf_common.jsxinc → SF.buildEffects`).

```json
{
  "id": "purple-nametag",            // unique, used on the command line and for reuse matching
  "name": "Purple NameTag",          // used in asset names: "<Font> - <name>"
  "track": "photoshop",              // "photoshop" (full style) | "shared" (white body + black alpha stroke, reusable across buttons)
  "description": "...",
  "padding": 10,                     // minimum padding; the tool raises it to effect reach + 2 px if needed
  "units": "cap",                    // optional: "cap" = size/distance/offset/letterSpacing are % of capital-letter height
                                     // (same look on any font and sampling size). Default "px" = atlas pixels.
  "autoLetterSpacing": true,         // optional: when letterSpacing is 0, bake 0.8 x the sideways outline reach
  "letterSpacing": 10,               // optional: extra advance per glyph (atlas px, or % with units cap) baked into the font asset.
                                     // Use for thick outlines: otherwise the next letter's outline paints over the previous body.
                                     // ~ outline width x 2 keeps coloured outlines apart; outer rims of the same colour may still merge (looks intended).
  "split": false,                    // optional: force one-layer (false) or per-glyph (true). Default: per-glyph only with gradientOverlay
  "shared": { "strokeAlpha": 45, "testBackgrounds": ["#8E8E93", "#3DBE4E"] },   // shared track only; backgrounds used by render
  "layers": [                        // bottom -> top. Each layer is a copy of the glyphs with its own effects.
    {
      "name": "Body",
      "offset": [0, 0],              // px, x right / y down (extrude / 3D layers)
      "opacity": 100,
      "blend": "normal",
      "group": "Outline",            // optional: consecutive layers with the same group are wrapped in one isolated group
      "groupOpacity": 50,            // optional: opacity of that group, applied once to the union (no darker overlaps)
      "effects": [ { "type": "colorOverlay", "color": "#5B3FD0" } ]
    }
  ]
}
```

Each effect type may appear once per layer (Photoshop rule). For a second stroke, add another layer.

| type | fields (defaults) | reach used for padding |
|---|---|---|
| `colorOverlay` | `color`, `opacity` (100), `blend` (normal) | 0 |
| `gradientOverlay` | `stops: [{color, location 0-100, opacity}]`, `angle` (90), `reverse`, `scale` (100), `opacity`, `blend` | 0 (forces per-glyph split) |
| `stroke` | `size` (3), `position` outside/inside/center, `color`, `opacity` (100), `blend` | size (outside), size/2 (center) |
| `dropShadow` | `color` (#000), `opacity` (75), `angle` (90 = light from top, shadow goes down), `distance` (3), `spread` (0 %), `size` (0 blur), `blend` (multiply) | distance + size |
| `innerShadow` | same as dropShadow | 0 |
| `outerGlow` | `color`, `opacity` (75), `size` (5), `spread`, `blend` (screen) | size |
| `innerGlow` | same as outerGlow | 0 |
| `bevel` | `size` (3), `depth` (100), `angle` (90), `altitude` (30), `direction` up/down, `soften`, `highlightColor/Opacity/Blend`, `shadowColor/Opacity/Blend` | 0 (inner bevel) |

Blend names: normal, multiply, screen, overlay, softlight, hardlight, colorburn, colordodge, lineardodge, linearburn, darken, lighten, difference, color, luminosity, hue, saturation.

## Outline + 3D extrude (the common game-button look)

Photoshop casts a drop shadow from the glyph shape, not from its stroke, so a shadow shorter than the stroke disappears under it. Build the extrude as its own layer instead:

```json
{ "name": "Extrude", "offset": [0, 5], "effects": [ {"type": "colorOverlay", "color": "#2C155E"}, {"type": "stroke", "size": 5, "color": "#2C155E"} ] },
{ "name": "Outline",                   "effects": [ {"type": "colorOverlay", "color": "#3B1E7A"}, {"type": "stroke", "size": 5, "color": "#3B1E7A"} ] },
{ "name": "Body", "effects": [ ... ] }
```
Padding reach = offset + stroke (+ 2 px). For the shared track put Extrude + Outline in one `group` with `groupOpacity` 50 and pure black colours (see `candy-pop.json`).

Splitting: only layers with a gradient are built per glyph (others use one layer for the whole atlas). `"split": true` on the style forces every layer per glyph, `false` forces none.

## Copying a Photoshop style into JSON

Open Layer Style on a layer that has the style and transcribe each enabled effect. The UI names map directly: Color Overlay → `colorOverlay`, Stroke → `stroke` (Position, Size, Color, Opacity), Drop Shadow → `dropShadow` (Angle, Distance, Spread, Size, Opacity, Blend Mode). Turn off "Use Global Light" or note the global angle. Then:

```
python scripts/tyvo.py asl --styles my-style.json --output my-style.asl
```
and load the .asl in Photoshop (Styles panel menu → Import Styles) to compare against the original side by side.

## Shared (Track B) rules

- Body must stay pure white (`colorOverlay #FFFFFF`), stroke/shadow pure black with partial `opacity`.
- The shader multiplies atlas × text colour: white body → takes the TMP colour; black stroke stays black and its alpha lets the button colour through, so it reads as a darker shade of the button.
- `check` enforces this (body ≥ 98 % white, stroke ≥ 98 % black).
