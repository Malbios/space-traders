import * as esbuild from "esbuild";

// Bundles the TS seam (§3a) + Blockly + the German locale into one plain <script> the
// server-rendered page loads directly — no dev server, one command (§3b).
await esbuild.build({
    entryPoints: ["Blockly/blockly-host.ts"],
    bundle: true,
    outfile: "wwwroot/js/blockly-host.js",
    format: "iife",
    target: "es2022",
    sourcemap: true,
    logLevel: "info",
});
