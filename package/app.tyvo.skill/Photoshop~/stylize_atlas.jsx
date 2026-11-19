// TYVO - apply a style JSON to a raw TMP atlas and export the stylized PNG.
//
// Run automatically (Unity / skill sets SF_JOB before evaluating this file):
//     var SF_JOB = "C:/path/to/job.json"; $.evalFile("C:/.../stylize_atlas.jsx");
// Run manually: File > Scripts > Browse... and pick a *.job.json when asked.
//
// Job JSON (written by the Unity package):
// {
//   "raw": "<abs path of raw atlas png>", "output": "<abs path for stylized png>",
//   "psd": "<abs path for layered psd, optional>", "result": "<abs path for result json>",
//   "mode": "auto" | "build-psd",          // build-psd = stop after layered PSD so an artist can add layers
//   "width": 1024, "height": 1024, "padding": 10,
//   "glyphs": [{ "name": "U+0041 A", "l": 0, "t": 0, "r": 10, "b": 10 }],   // padded rects, top-left origin
//   "style": { ...style json... }
// }
#target photoshop

$.evalFile(File($.fileName).path + "/sf_common.jsxinc");

(function () {
    var jobPath = (typeof SF_JOB !== "undefined" && SF_JOB) ? SF_JOB : null;
    if (!jobPath) {
        var picked = File.openDialog("Select a TYVO job (*.job.json)", "*.json");
        if (!picked) return;
        jobPath = picked.fsName;
    }

    var job = SF.parseJSON(SF.readText(jobPath));
    var started = new Date().getTime();
    var result = { ok: false, stage: "start", job: jobPath, warnings: [] };
    var prefs = SF.pushPrefs();
    var doc = null;

    try {
        var style = job.style;
        if (!style || !style.layers || style.layers.length === 0) throw new Error("Style has no layers.");
        SF.resolveUnits(style, job.unitScale);

        doc = app.open(new File(job.raw));
        if (doc.mode !== DocumentMode.RGB) doc.changeMode(ChangeMode.RGB);
        if (doc.bitsPerChannel !== BitsPerChannelType.EIGHT) doc.bitsPerChannel = BitsPerChannelType.EIGHT;
        if (Math.round(doc.width.as("px")) !== job.width || Math.round(doc.height.as("px")) !== job.height)
            throw new Error("Raw atlas size " + doc.width.as("px") + "x" + doc.height.as("px") + " does not match job " + job.width + "x" + job.height);

        var src = doc.artLayers[0];
        if (src.isBackgroundLayer) src.isBackgroundLayer = false;
        src.name = "Atlas (raw)";

        // Per-glyph layers are only needed where an effect depends on layer bounds (gradient overlay),
        // or when the style forces it. Everything else gives identical pixels on one layer and is much faster.
        // style.split: true = every layer per glyph, false = never, absent = only layers with a gradient.
        function layerNeedsSplit(L) {
            if (style.split === true) return true;
            if (style.split === false) return false;
            var fx = L.effects || [];
            for (var q = 0; q < fx.length; q++) if (fx[q].type === "gradientOverlay" && fx[q].enabled !== false) return true;
            return false;
        }
        var glyphGroup = null;
        result.stage = "split";
        for (var s = 0; s < style.layers.length && !glyphGroup; s++) {
            if (!layerNeedsSplit(style.layers[s])) continue;
            var ids = SF.splitByRects(doc, src, job.glyphs);
            if (ids.length === 0) throw new Error("Split produced no glyph layers.");
            SF.selectLayers(ids);
            glyphGroup = SF.groupSelected("Glyphs");
            result.glyphLayers = ids.length;
        }

        result.stage = "style";
        var blendMap = { normal: BlendMode.NORMAL, multiply: BlendMode.MULTIPLY, screen: BlendMode.SCREEN, overlay: BlendMode.OVERLAY, softlight: BlendMode.SOFTLIGHT, darken: BlendMode.DARKEN, lighten: BlendMode.LIGHTEN };
        var built = [];
        var made = [];
        for (var i = 0; i < style.layers.length; i++) {
            var L = style.layers[i];
            var base = layerNeedsSplit(L) ? glyphGroup : src;
            doc.activeLayer = base;
            var dup = base.duplicate();
            dup.move(doc, ElementPlacement.PLACEATBEGINNING); // later layers end up on top
            dup.name = L.name || ("Layer " + (i + 1));
            dup.visible = true;

            var fx = L.effects || [];
            if (fx.length > 0) {
                if (dup.typename === "LayerSet") {
                    var kids = [];
                    for (var k = 0; k < dup.artLayers.length; k++) kids.push(dup.artLayers[k].id);
                    SF.selectLayerById(kids[0], false);
                    SF.setEffectsOnActive(fx);
                    if (kids.length > 1) {
                        SF.copyEffects();
                        SF.selectLayers(kids.slice(1));
                        SF.pasteEffects();
                    }
                } else {
                    doc.activeLayer = dup;
                    SF.setEffectsOnActive(fx);
                }
            }
            var off = L.offset || [0, 0];
            if (off[0] !== 0 || off[1] !== 0) dup.translate(UnitValue(off[0], "px"), UnitValue(off[1], "px"));
            dup.opacity = SF.num(L.opacity, 100);
            if (L.blend && dup.typename !== "LayerSet") {
                var bm = blendMap[String(L.blend).toLowerCase().replace(/[\s_-]/g, "")];
                if (bm) dup.blendMode = bm;
            }
            built.push(dup.name);
            made.push({ layer: dup, group: L.group, groupOpacity: L.groupOpacity });
        }

        // Consecutive layers with the same "group" are wrapped in one isolated group, so a shared opacity
        // (e.g. a 50 % black outline + extrude) is applied once to their union instead of stacking.
        result.stage = "group";
        for (var a = 0; a < made.length;) {
            var gname = made[a].group;
            if (!gname) { a++; continue; }
            var gids = [], gop = 100;
            var b = a;
            while (b < made.length && made[b].group === gname) {
                gids.push(made[b].layer.id);
                if (made[b].groupOpacity !== undefined && made[b].groupOpacity !== null && made[b].groupOpacity >= 0) gop = made[b].groupOpacity;
                b++;
            }
            SF.selectLayers(gids);
            var set = SF.groupSelected(gname);
            set.blendMode = BlendMode.NORMAL; // isolate: opacity applies to the merged group
            set.opacity = gop;
            a = b;
        }

        if (glyphGroup) glyphGroup.visible = false;
        src.visible = false;
        result.layers = built;

        if (job.psd) {
            result.stage = "psd";
            SF.savePSD(doc, job.psd);
            result.psd = job.psd;
        }

        if (job.mode === "build-psd") {
            result.ok = true;
            result.stage = "psd-ready";
            result.message = "Layered PSD saved. Edit it, then run finalize_psd.jsx (or 'Finalize PSD' in Unity).";
        } else {
            result.stage = "export";
            SF.savePNG(doc, job.output); // PNG save writes the composite of visible layers (effects included)
            result.output = job.output;
            result.ok = true;
            result.stage = "done";
        }
    } catch (e) {
        result.error = String(e) + (e.line ? " (line " + e.line + ")" : "");
    } finally {
        if (doc) { try { doc.close(SaveOptions.DONOTSAVECHANGES); } catch (ignore) {} }
        SF.popPrefs(prefs);
    }

    result.ms = new Date().getTime() - started;
    if (job.result) SF.writeText(job.result, SF.toJSON(result));
    return SF.toJSON(result);
})();
