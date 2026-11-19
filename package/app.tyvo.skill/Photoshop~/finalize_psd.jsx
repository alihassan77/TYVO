// TYVO - merge an artist-edited layered PSD and export the stylized atlas PNG.
// Uses the same job JSON as stylize_atlas.jsx (fields: psd, output, result, width, height).
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
    var result = { ok: false, stage: "finalize", job: jobPath, warnings: [] };
    var prefs = SF.pushPrefs();
    var doc = null;
    try {
        if (!job.psd || !new File(job.psd).exists) throw new Error("Layered PSD not found: " + job.psd);
        doc = app.open(new File(job.psd));
        if (Math.round(doc.width.as("px")) !== job.width || Math.round(doc.height.as("px")) !== job.height)
            throw new Error("PSD canvas was resized. It must stay " + job.width + "x" + job.height + " or glyph UVs break.");
        SF.savePNG(doc, job.output); // PNG save writes the composite of visible layers (effects included)
        result.ok = true;
        result.output = job.output;
    } catch (e) {
        result.error = String(e) + (e.line ? " (line " + e.line + ")" : "");
    } finally {
        if (doc) { try { doc.close(SaveOptions.DONOTSAVECHANGES); } catch (ignore) {} }
        SF.popPrefs(prefs);
    }
    if (job.result) SF.writeText(job.result, SF.toJSON(result));
    return SF.toJSON(result);
})();
