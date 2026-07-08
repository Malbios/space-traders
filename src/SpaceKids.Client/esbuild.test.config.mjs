import * as esbuild from "esbuild";
import { globSync } from "node:fs";

// Headless test bundle for the TS seam (§3a) — separate from esbuild.config.mjs's
// browser bundle (Blockly/blockly-host.ts, iife, target browser) since these tests
// run under plain Node instead. `platform: "node"` makes esbuild add Node's own
// `"node"` package-export condition, which is what makes `blockly/core` resolve to
// `blockly/core-node.js` — Blockly's own headless entry point, which self-initializes
// jsdom (already a transitive dependency of `blockly`, no new install needed).
// `external: ["jsdom"]` is required, not optional: bundling jsdom inline breaks its
// `readFileSync`-relative default-stylesheet asset load (verified by hand — bundling
// it produces an ENOENT for a path that only exists relative to jsdom's own installed
// location, not the bundle's).
const entryPoints = globSync("Blockly/**/*.test.ts");

if (entryPoints.length === 0) {
    console.log("No *.test.ts files found — nothing to bundle.");
    process.exit(0);
}

await esbuild.build({
    entryPoints,
    bundle: true,
    outdir: "test-dist",
    // ".cjs", not the default ".js" — this package.json has "type": "module", so a
    // plain ".js" output would be loaded as ESM regardless of esbuild's `format:
    // "cjs"` (which emits `require()` calls), throwing "require is not defined".
    outExtension: { ".js": ".cjs" },
    platform: "node",
    format: "cjs",
    external: ["jsdom"],
    sourcemap: true,
    logLevel: "info",
});
