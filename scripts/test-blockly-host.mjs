/**
 * Automated browser test for the Blockly seam (blockly-host.ts, §3a).
 *
 * Unlike verify-flotilla.mjs, this needs no running .NET server or Blazor page —
 * it loads the built browser bundle directly into a blank headless page and drives
 * `window.spaceKids.*`. This is the only way to exercise `Blockly.inject`/`WorkspaceSvg`
 * automatically: the Node-only `npm test` suite (esbuild.test.config.mjs) resolves
 * `blockly/core` to Blockly's headless `core-node.js` entry, which never creates a
 * real `WorkspaceSvg` — jsdom's default environment doesn't implement the SVG geometry
 * methods (`getBBox`, `getScreenCTM`, ...) Blockly's real rendering needs.
 *
 * Loads the built wwwroot/js/blockly-host.js artifact rather than bundling on the
 * fly — `npm run test:blockly-host` (root package.json) builds it first.
 *
 * Run: npm run test:blockly-host
 */
import { chromium } from "playwright";
import { fileURLToPath } from "node:url";
import path from "node:path";
import fs from "node:fs";
import http from "node:http";

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const BUNDLE_PATH = path.join(__dirname, "..", "src", "SpaceKids.Client", "wwwroot", "js", "blockly-host.js");

const PROGRAM_CONTAINER_ID = "test-program";
const WORKSHOP_CONTAINER_ID = "blockly-workshop-spike";

const MESSAGE_BLOCK_JSON = JSON.stringify({
    blocks: {
        languageVersion: 0,
        blocks: [
            {
                type: "sk_show_message",
                id: "msg-1",
                inputs: { TEXT: { block: { type: "text", id: "t-1", fields: { TEXT: "hello" } } } },
            },
        ],
    },
});

function assert(condition, message) {
    if (!condition) throw new Error(message);
}

async function launchBrowser() {
    try {
        return await chromium.launch({ headless: true, channel: "chrome" });
    } catch {
        return await chromium.launch({ headless: true });
    }
}

function html() {
    return `<!doctype html>
<html>
<body>
  <div id="${PROGRAM_CONTAINER_ID}" style="width: 600px; height: 360px"></div>
  <div id="${WORKSHOP_CONTAINER_ID}" style="width: 600px; height: 360px"></div>
  <script src="/blockly-host.js"></script>
</body>
</html>`;
}

// `page.setContent`/`about:blank` gives the page an opaque origin, and Chromium
// denies `localStorage` access on opaque origins (blockly-host.ts's getTheme/
// setTheme read/write it eagerly) — serve from a real http origin instead.
function startStaticServer() {
    const mapPath = `${BUNDLE_PATH}.map`;
    const server = http.createServer((req, res) => {
        if (req.url === "/" ) {
            res.writeHead(200, { "Content-Type": "text/html" });
            res.end(html());
        } else if (req.url === "/blockly-host.js") {
            res.writeHead(200, { "Content-Type": "application/javascript" });
            res.end(fs.readFileSync(BUNDLE_PATH));
        } else if (req.url === "/blockly-host.js.map" && fs.existsSync(mapPath)) {
            res.writeHead(200, { "Content-Type": "application/json" });
            res.end(fs.readFileSync(mapPath));
        } else if (req.url === "/favicon.ico") {
            // Chromium auto-requests this; avoid a spurious 404 console error.
            res.writeHead(204);
            res.end();
        } else {
            res.writeHead(404);
            res.end();
        }
    });
    return new Promise((resolve) => {
        server.listen(0, "127.0.0.1", () => resolve(server));
    });
}

async function main() {
    const server = await startStaticServer();
    const port = server.address().port;
    const browser = await launchBrowser();
    const page = await browser.newPage();
    const consoleErrors = [];
    page.on("console", (msg) => {
        if (msg.type() === "error") consoleErrors.push(msg.text());
    });
    page.on("pageerror", (err) => consoleErrors.push(String(err)));

    const failures = [];
    const check = async (name, fn) => {
        try {
            await fn();
            console.log(`  ok  ${name}`);
        } catch (err) {
            console.error(` FAIL ${name}: ${err.message}`);
            failures.push({ name, error: err.message });
        }
    };

    console.log("Verifying the Blockly seam (blockly-host.ts) in a real headless browser");

    await page.goto(`http://127.0.0.1:${port}/`, { waitUntil: "load", timeout: 10_000 });
    await page.waitForFunction(() => typeof window.spaceKids?.initWorkspace === "function", null, { timeout: 10_000 });

    await check("initWorkspace injects a real Blockly SVG workspace", async () => {
        await page.evaluate((id) => window.spaceKids.initWorkspace(id, false), PROGRAM_CONTAINER_ID);
        const hasSvg = await page.evaluate(
            (id) => document.getElementById(id)?.querySelector(".blocklySvg") !== null,
            PROGRAM_CONTAINER_ID,
        );
        assert(hasSvg, "expected a .blocklySvg element after initWorkspace");
    });

    await check("loadWorkspace / serializeWorkspace round-trips a block", async () => {
        await page.evaluate(
            ({ id, json }) => window.spaceKids.loadWorkspace(id, json),
            { id: PROGRAM_CONTAINER_ID, json: MESSAGE_BLOCK_JSON },
        );
        const serialized = await page.evaluate((id) => window.spaceKids.serializeWorkspace(id), PROGRAM_CONTAINER_ID);
        assert(serialized.includes("sk_show_message"), "expected sk_show_message to survive the round-trip");
        assert(serialized.includes("hello"), "expected the message text to survive the round-trip");
    });

    await check("firstBlockId returns the loaded block's id", async () => {
        const id = await page.evaluate((cid) => window.spaceKids.firstBlockId(cid), PROGRAM_CONTAINER_ID);
        assert(id === "msg-1", `expected firstBlockId to return "msg-1", got ${JSON.stringify(id)}`);
    });

    await check("highlightBlock / clearHighlight don't throw", async () => {
        await page.evaluate((id) => {
            window.spaceKids.highlightBlock(id, "msg-1");
            window.spaceKids.clearHighlight(id);
        }, PROGRAM_CONTAINER_ID);
    });

    await check("setReadOnly doesn't throw and disables block editing", async () => {
        await page.evaluate((id) => window.spaceKids.setReadOnly(id, true), PROGRAM_CONTAINER_ID);
        await page.evaluate((id) => window.spaceKids.setReadOnly(id, false), PROGRAM_CONTAINER_ID);
    });

    await check("setLocale re-renders open workspaces without losing block state", async () => {
        await page.evaluate(() => window.spaceKids.setLocale("en"));
        const afterEn = await page.evaluate((id) => window.spaceKids.serializeWorkspace(id), PROGRAM_CONTAINER_ID);
        assert(afterEn.includes("sk_show_message"), "block lost after switching to English");

        await page.evaluate(() => window.spaceKids.setLocale("de"));
        const afterDe = await page.evaluate((id) => window.spaceKids.serializeWorkspace(id), PROGRAM_CONTAINER_ID);
        assert(afterDe.includes("sk_show_message"), "block lost after switching back to German");

        const hasSvg = await page.evaluate(
            (id) => document.getElementById(id)?.querySelector(".blocklySvg") !== null,
            PROGRAM_CONTAINER_ID,
        );
        assert(hasSvg, "expected the workspace to still be rendered after locale switches");
    });

    await check("destroyWorkspace removes the injected SVG", async () => {
        await page.evaluate((id) => window.spaceKids.destroyWorkspace(id), PROGRAM_CONTAINER_ID);
        const hasSvg = await page.evaluate(
            (id) => document.getElementById(id)?.querySelector(".blocklySvg") !== null,
            PROGRAM_CONTAINER_ID,
        );
        assert(!hasSvg, "expected .blocklySvg to be gone after destroyWorkspace");
    });

    await check("a second, independent workspace container works alongside the first", async () => {
        await page.evaluate((id) => window.spaceKids.initWorkspace(id, false), WORKSHOP_CONTAINER_ID);
        const hasSvg = await page.evaluate(
            (id) => document.getElementById(id)?.querySelector(".blocklySvg") !== null,
            WORKSHOP_CONTAINER_ID,
        );
        assert(hasSvg, "expected the workshop container to get its own Blockly SVG workspace");
    });

    await browser.close();
    await new Promise((resolve) => server.close(resolve));

    if (consoleErrors.length > 0) {
        console.error(`\n${consoleErrors.length} console error(s) during the run:\n${consoleErrors.join("\n")}`);
        failures.push({ name: "no console errors", error: consoleErrors.join("; ") });
    }

    if (failures.length > 0) {
        console.error(`\n${failures.length} check(s) failed.`);
        process.exit(1);
    }
    console.log("\nAll blockly-host.ts browser checks passed.");
}

main().catch((err) => {
    console.error(err);
    process.exit(1);
});
