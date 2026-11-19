// TYVO - Split to Layers (manual artist tool).
// Splits the active layer of the active document into one layer per glyph, inside a "Glyphs" group.
//
//  * Exact mode: if a "<doc name>.job.json" (written by the Unity package) sits next to the document,
//    glyphs are cut by their real atlas rectangles. No gap guessing.
//  * Gap mode: otherwise pieces closer than the gap (default 4 px) are kept together (dot of i, j, ä ...).
//
// Run: File > Scripts > Browse... > split_to_layers.jsx
#target photoshop

$.evalFile(File($.fileName).path + "/sf_common.jsxinc");

(function () {
    if (app.documents.length === 0) { alert("Open the atlas PNG first."); return; }
    var doc = app.activeDocument;
    var layer = doc.activeLayer;
    if (layer.typename !== "ArtLayer") { alert("Select the atlas pixel layer (not a group)."); return; }

    var gap = (typeof SF_GAP !== "undefined") ? SF_GAP : null;
    var jobFile = null;
    try {
        var base = doc.fullName.fsName.replace(/\.[^\.]+$/, "");
        var candidates = [new File(base + ".job.json"), new File(base.replace(/_raw$/i, "") + ".job.json")];
        for (var c = 0; c < candidates.length; c++) if (candidates[c].exists) { jobFile = candidates[c]; break; }
    } catch (ignore) { /* unsaved document */ }

    var interactive = (typeof SF_GAP === "undefined");
    if (!jobFile && gap === null) {
        var answer = prompt("Split when gap is larger than (pixels):", "4", "Split to Layers");
        if (answer === null) return;
        gap = parseInt(answer, 10);
        if (isNaN(gap) || gap < 1) gap = 4;
    }

    var prefs = SF.pushPrefs();
    var started = new Date().getTime();
    var ids;
    try {
        if (layer.isBackgroundLayer) layer.isBackgroundLayer = false;
        if (jobFile) {
            var job = SF.parseJSON(SF.readText(jobFile.fsName));
            ids = SF.splitByRects(doc, layer, job.glyphs);
        } else {
            ids = SF.splitByGap(doc, layer, gap);
        }
        if (ids.length > 0) {
            SF.selectLayers(ids);
            SF.groupSelected("Glyphs");
            layer.visible = false;
        }
    } finally {
        SF.popPrefs(prefs);
    }
    var msg = "Split into " + ids.length + " layers (" + (jobFile ? "exact glyph rects" : "gap " + gap + " px") + ") in " +
        Math.round((new Date().getTime() - started) / 100) / 10 + " s.";
    if (interactive) alert(msg);
    return msg;
})();
