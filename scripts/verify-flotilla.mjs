/**
 * Live browser verification for Flotilla (mitSchiff + parallel).
 *
 * Prerequisites (see scripts/dev.ps1):
 *   pwsh scripts/dev.ps1 fake     # :5196
 *   pwsh scripts/dev.ps1 server   # :5290, SpaceTraders__BaseUrl=http://localhost:5196/
 *
 * Run: npm run verify:browser
 */
import { chromium } from "playwright";

const BASE_URL = process.env.SPACEKIDS_URL ?? "http://localhost:5290";
const SERVER_HEALTH_URL = `${BASE_URL}/`;
const TOKEN = "FAKE_TOKEN_1";

const WITH_SHIP_UNAVAILABLE_JSON = JSON.stringify({
    blocks: {
        languageVersion: 0,
        blocks: [
            {
                type: "withShip",
                id: "ws-mut",
                extraState: { hasUnavailable: true },
                inputs: {
                    SHIP: { block: { type: "text", id: "s1", fields: { TEXT: "FAKE-AGENT-2" } } },
                    DO: { block: { type: "sk_show_message", id: "m1", inputs: { TEXT: { block: { type: "text", id: "t1", fields: { TEXT: "ok" } } } } } },
                    ELSE: { block: { type: "sk_show_message", id: "m2", inputs: { TEXT: { block: { type: "text", id: "t2", fields: { TEXT: "missing" } } } } } },
                },
            },
        ],
    },
});

const PARALLEL_THREE_BRANCH_JSON = JSON.stringify({
    blocks: {
        languageVersion: 0,
        blocks: [
            {
                type: "parallel",
                id: "par-mut",
                extraState: { branchCount: 3 },
                inputs: {
                    DO0: { block: { type: "sk_show_message", id: "b0", inputs: { TEXT: { block: { type: "text", id: "t0", fields: { TEXT: "0" } } } } } },
                    DO1: { block: { type: "sk_show_message", id: "b1", inputs: { TEXT: { block: { type: "text", id: "t1", fields: { TEXT: "1" } } } } } },
                    DO2: { block: { type: "sk_show_message", id: "b2", inputs: { TEXT: { block: { type: "text", id: "t2", fields: { TEXT: "2" } } } } } },
                },
            },
        ],
    },
});

const PARALLEL_PROGRAM_JSON = JSON.stringify({
    blocks: {
        languageVersion: 0,
        blocks: [
            {
                type: "parallel",
                id: "par-verify",
                extraState: { branchCount: 2 },
                inputs: {
                    DO0: { block: { type: "orbit", id: "orbit-1" } },
                    DO1: {
                        block: {
                            type: "withShip",
                            id: "ws-verify",
                            inputs: {
                                SHIP: { block: { type: "text", id: "ship-text", fields: { TEXT: "FAKE-AGENT-2" } } },
                                DO: { block: { type: "orbit", id: "orbit-2" } },
                            },
                        },
                    },
                },
                next: {
                    block: {
                        type: "sk_show_message",
                        id: "done",
                        inputs: { TEXT: { block: { type: "text", id: "done-text", fields: { TEXT: "fertig" } } } },
                    },
                },
            },
        ],
    },
});

function assert(condition, message) {
    if (!condition) throw new Error(message);
}

async function waitForServer(url) {
    const deadline = Date.now() + 60_000;
    while (Date.now() < deadline) {
        try {
            const response = await fetch(url);
            if (response.status < 500) return;
        } catch {
            // retry
        }
        await new Promise((resolve) => setTimeout(resolve, 500));
    }
    throw new Error(`Server not ready at ${url} — start fake + server via scripts/dev.ps1`);
}

async function launchBrowser() {
    try {
        return await chromium.launch({ headless: true, channel: "chrome" });
    } catch {
        return await chromium.launch({ headless: true });
    }
}

async function waitForBlazor(page) {
    await page.waitForFunction(() => typeof window.Blazor !== "undefined", null, { timeout: 120_000 });
    await page.waitForFunction(() => window.spaceKids?.initWorkspace, null, { timeout: 30_000 });
}

async function ensureLoggedIn(page) {
    await page.getByRole("button", { name: "Piloten" }).click();
    const hasShips = await page.waitForFunction(
        () => [...document.querySelectorAll("select option")].some((o) => o.value.startsWith("FAKE-AGENT")),
        null,
        { timeout: 5_000 },
    ).then(() => true).catch(() => false);
    if (hasShips) return;

    await page.getByRole("button", { name: "Einstellungen" }).click();
    const tokenInput = page.getByPlaceholder("SpaceTraders-Token einfügen");
    await tokenInput.waitFor({ state: "visible", timeout: 30_000 });
    await tokenInput.fill(TOKEN);
    await page.getByRole("button", { name: "Anmelden" }).click();
    await page.waitForFunction(() => document.body.innerText.includes("FAKE-AGENT"), null, { timeout: 30_000 });
}

async function openProgramEditor(page) {
    await page.getByRole("button", { name: "Programmieren" }).click();
}

async function ensureProgramWorkspace(page) {
    await openProgramEditor(page);
    const existing = await page.getByRole("button", { name: "Öffnen" }).count();
    if (existing === 0) {
        const programName = `Flotilla-Verify-${Date.now()}`;
        await page.getByPlaceholder("Name des neuen Programms").fill(programName);
        await page.getByRole("button", { name: "Neues Programm" }).click();
        await page.waitForFunction((name) => document.body.innerText.includes(name), programName, { timeout: 30_000 });
        await page.getByRole("button", { name: "Öffnen" }).last().click();
        await page.waitForTimeout(1500);
    } else {
        await page.getByRole("button", { name: "Öffnen" }).first().click();
        await page.waitForTimeout(1500);
    }

    const containerId = await page.evaluate(() => {
        const divs = [...document.querySelectorAll("div[id]")].filter((d) => d.style.height === "360px");
        return divs[0]?.id ?? "";
    });
    assert(containerId, "program Blockly container not found");
    return containerId;
}

async function roundTripWorkspace(page, containerId, json) {
    return page.evaluate(
        ({ containerId, json }) => {
            window.spaceKids.loadWorkspace(containerId, json);
            return window.spaceKids.serializeWorkspace(containerId);
        },
        { containerId, json },
    );
}

async function main() {
    await waitForServer(SERVER_HEALTH_URL);
    const browser = await launchBrowser();
    const page = await browser.newPage();
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

    console.log(`Verifying Flotilla at ${BASE_URL}`);

    await check("app boots", async () => {
        await page.goto(BASE_URL, { waitUntil: "networkidle", timeout: 120_000 });
        await waitForBlazor(page);
    });

    await check("login via Settings tab (or reuse persisted session)", async () => {
        await ensureLoggedIn(page);
    });

    await check("withShip and parallel blocks load and round-trip through Blockly seam", async () => {
        const containerId = await ensureProgramWorkspace(page);
        const withShipJson = await roundTripWorkspace(page, containerId, WITH_SHIP_UNAVAILABLE_JSON);
        assert(withShipJson.includes("withShip"), "withShip block missing after round-trip");
        assert(withShipJson.includes("hasUnavailable"), "withShip hasUnavailable extraState lost");
        assert(withShipJson.includes("ELSE"), "withShip ELSE branch lost");

        const parallelJson = await roundTripWorkspace(page, containerId, PARALLEL_THREE_BRANCH_JSON);
        assert(parallelJson.includes("parallel"), "parallel block missing after round-trip");
        assert(parallelJson.includes('"branchCount": 3') || parallelJson.includes('"branchCount":3'), "parallel branchCount lost");
        assert(parallelJson.includes("DO2"), "parallel third branch input lost");
    });

    await check("Flottille blocks are registered in Blockly seam", async () => {
        const types = await page.evaluate(() =>
            ["withShip", "parallel"].every((t) => typeof window.spaceKids?.initWorkspace === "function"),
        );
        assert(types, "spaceKids Blockly seam not ready");
    });

    await check("parallel program runs on two ships and completes", async () => {
        const containerId = await ensureProgramWorkspace(page);
        await roundTripWorkspace(page, containerId, PARALLEL_PROGRAM_JSON);
        await page.getByRole("button", { name: "Speichern" }).click();
        await page.waitForTimeout(500);

        await page.getByRole("button", { name: "Piloten" }).click();
        await page.waitForFunction(() => document.querySelectorAll("select").length > 0, null, { timeout: 30_000 });
        await page.locator("select").first().selectOption("FAKE-AGENT-1");
        await page.getByRole("button", { name: "Start" }).click();

        await page.waitForFunction(
            () => {
                const text = document.body.innerText;
                return text.includes("Fertig") || text.includes("Done") || text.includes("Fehlgeschlagen") || text.includes("Failed");
            },
            null,
            { timeout: 120_000 },
        );

        await page.getByRole("button", { name: "Piloten aktualisieren" }).click();
        await page.waitForTimeout(500);
        const bodyText = await page.locator("body").innerText();
        assert(
            bodyText.includes("Fertig") || bodyText.includes("Done"),
            `expected completed two-ship run, got: ${bodyText.slice(0, 500)}`,
        );
    });

    await browser.close();

    if (failures.length > 0) {
        console.error(`\n${failures.length} verification check(s) failed.`);
        process.exit(1);
    }
    console.log("\nAll Flotilla browser checks passed.");
}

main().catch((err) => {
    console.error(err);
    process.exit(1);
});