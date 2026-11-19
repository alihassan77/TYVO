// TYVO - build a Photoshop Styles library (.asl) from style JSON files.
//
// The .asl file is written directly (Photoshop's own descriptor serializer + the documented .asl layout),
// so nothing is added to or removed from the user's Styles panel and no document is opened.
// Each style layer becomes one style: "<Style name>" or "<Style name> - <Layer name>" for multi-layer styles.
//
// Automatic: var SF_ASL_JOB = "C:/path/asl_job.json"; $.evalFile(".../build_asl.jsx");
//   asl_job.json: { "styles": ["<abs style json>", ...], "output": "<abs .asl>", "result": "<abs result json>" }
// Manual: File > Scripts > Browse... then pick style JSON files and an output file.
//
// .asl layout: u16 2, "8BSL", u16 3, u32 patternsLength (0), u32 styleCount,
//   per style: u32 length, [v16 descriptor 'null' {Nm, Idnt}], [v16 descriptor 'Styl' {documentMode, Lefx}], pad to 4.
#target photoshop

$.evalFile(File($.fileName).path + "/sf_common.jsxinc");

SF.u32 = function (n) {
    return String.fromCharCode((n >>> 24) & 255, (n >>> 16) & 255, (n >>> 8) & 255, n & 255);
};

SF.uuid = function () {
    var h = "0123456789abcdef", s = "";
    for (var i = 0; i < 36; i++) s += (i === 8 || i === 13 || i === 18 || i === 23) ? "-" : h.charAt(Math.floor(Math.random() * 16));
    return s;
};

// Serialize one style record (without the leading length).
SF.styleRecord = function (name, effects) {
    var head = new ActionDescriptor();
    head.putString(SF.c("Nm  "), name);
    head.putString(SF.c("Idnt"), SF.uuid());

    var body = new ActionDescriptor();
    body.putObject(SF.s("documentMode"), SF.s("documentMode"), new ActionDescriptor());
    body.putObject(SF.c("Lefx"), SF.c("Lefx"), SF.buildEffects(effects));

    var b = body.toStream();
    // toStream writes: u32 16, unicode name (u32 1 + u16 0), u32 0 + 4-char class "null". Patch class -> "Styl".
    if (b.substr(14, 4) !== "null") throw new Error("Unexpected descriptor stream layout.");
    b = b.substr(0, 14) + "Styl" + b.substr(18);

    var rec = head.toStream() + b;
    while (rec.length % 4 !== 0) rec += String.fromCharCode(0);
    return rec;
};

function sfBuildAslMain() {
    var job;
    if (typeof SF_ASL_JOB !== "undefined" && SF_ASL_JOB) {
        job = SF.parseJSON(SF.readText(SF_ASL_JOB));
    } else {
        var files = File.openDialog("Select style JSON files", "*.json", true);
        if (!files || files.length === 0) return;
        var out = File.saveDialog("Save styles library as", "*.asl");
        if (!out) return;
        job = { styles: [], output: out.fsName.replace(/(\.asl)?$/i, ".asl") };
        for (var f = 0; f < files.length; f++) job.styles.push(files[f].fsName);
    }

    var result = { ok: false, output: job.output, styles: [], warnings: [] };
    try {
        var records = [];
        for (var i = 0; i < job.styles.length; i++) {
            var style = SF.parseJSON(SF.readText(job.styles[i]));
            if (String(style.units || "px").toLowerCase() === "cap") {
                // .asl has no font context: assume 60 px capital height (a typical title size).
                SF.resolveUnits(style, 0.6);
                result.warnings.push(style.name + ": cap-relative sizes converted for a 60 px capital height.");
            }
            var layers = style.layers || [];
            for (var j = 0; j < layers.length; j++) {
                var fx = layers[j].effects || [];
                if (fx.length === 0) continue;
                var name = layers.length > 1 ? style.name + " - " + (layers[j].name || ("Layer " + (j + 1))) : style.name;
                records.push(SF.styleRecord(name, fx));
                result.styles.push(name);
                var off = layers[j].offset;
                if (off && (off[0] !== 0 || off[1] !== 0))
                    result.warnings.push(name + ": layer offset (" + off[0] + "," + off[1] + ") is not stored in .asl; move that layer by hand.");
            }
        }
        if (records.length === 0) throw new Error("No style layers with effects found.");

        var data = String.fromCharCode(0, 2) + "8BSL" + String.fromCharCode(0, 3) + SF.u32(0) + SF.u32(records.length);
        for (var r = 0; r < records.length; r++) data += SF.u32(records[r].length) + records[r];

        var file = new File(job.output);
        file.encoding = "BINARY";
        if (!file.open("w")) throw new Error("Cannot write " + job.output);
        file.write(data);
        file.close();
        result.ok = true;
        result.bytes = data.length;
    } catch (e) {
        result.error = String(e) + (e.line ? " (line " + e.line + ")" : "");
    }
    if (job.result) SF.writeText(job.result, SF.toJSON(result));
    return SF.toJSON(result);
}

sfBuildAslMain();
