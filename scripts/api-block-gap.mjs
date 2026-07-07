import fs from "fs";

const spec = JSON.parse(fs.readFileSync(new URL("./SpaceTraders.openapi.json", import.meta.url), "utf8"));

/** operationId -> Blockly action/info block type, when covered */
const ACTION_MAP = {
    "navigate-ship": "navigate",
    "orbit-ship": "orbit",
    "dock-ship": "dock",
    "extract-resources": "extract",
    "create-survey": "survey",
    "purchase-cargo": "buyGood",
    "sell-cargo": "sellGood",
    "deliver-contract": "deliverContract",
    "accept-contract": "acceptContract",
    "fulfill-contract": "fulfillContract",
    negotiateContract: "negotiateContract",
    "purchase-ship": "purchaseShip",
    "refuel-ship": "refuel",
    "create-chart": "createChart",
    "extract-resources-with-survey": "extractWithSurvey",
    "install-ship-module": "installModule",
    "install-mount": "installMount",
    jettison: "jettison",
    "jump-ship": "jump",
    "ship-refine": "refine",
    "remove-ship-module": "removeModule",
    "remove-mount": "removeMount",
    "repair-ship": "repair",
    "create-ship-ship-scan": "scanShips",
    "create-ship-system-scan": "scanSystems",
    "create-ship-waypoint-scan": "scanWaypoints",
    "scrap-ship": "scrapShip",
    "siphon-resources": "siphon",
    "transfer-cargo": "transferCargo",
    "warp-ship": "warp",
    "supply-construction": null, // missing action block
};

const INFO_MAP = {
    "get-my-ship": "getShipInfo",
    "get-my-ships": "getFleetInfo",
    "get-system-waypoints": "getWaypoints",
    "get-market": "getMarket",
    "get-shipyard": "getShipyard",
    "get-contracts": "getContracts",
    "get-my-ship-cargo": "getCargo",
    "get-my-agent": "getCredits", // partial: only credits surfaced
};

/** Endpoints we intentionally skip (meta/auth/websocket/register) */
const SKIP = new Set([
    "get-status",
    "register",
    "get-error-codes",
    "websocket-departure-events",
]);

/** Dashboard/UI only — wired but no Blockly block */
const UI_ONLY = new Set(["get-factions", "get-my-factions", "get-faction"]);

const ops = [];
for (const [path, methods] of Object.entries(spec.paths)) {
    for (const [method, op] of Object.entries(methods)) {
        if (method === "parameters") continue;
        ops.push({
            method: method.toUpperCase(),
            path,
            operationId: op.operationId,
            tags: op.tags ?? [],
            summary: op.summary ?? "",
        });
    }
}
ops.sort((a, b) => a.path.localeCompare(b.path) || a.method.localeCompare(b.method));

const covered = [];
const missing = [];
const partial = [];
const skipped = [];
const uiOnly = [];

for (const op of ops) {
    if (SKIP.has(op.operationId)) {
        skipped.push(op);
        continue;
    }
    if (UI_ONLY.has(op.operationId)) {
        uiOnly.push(op);
        continue;
    }

    const block = ACTION_MAP[op.operationId] ?? INFO_MAP[op.operationId];
    if (block) {
        covered.push({ ...op, block });
        continue;
    }

    if (op.method === "GET" && op.operationId === "get-repair-ship") {
        partial.push({ ...op, note: "repair POST exists; GET is a cost quote only" });
        continue;
    }
    if (op.method === "GET" && op.operationId === "get-scrap-ship") {
        partial.push({ ...op, note: "scrapShip POST exists; GET is a value quote only" });
        continue;
    }
    if (op.operationId === "get-contract") {
        partial.push({ ...op, note: "getContracts returns list; single-contract fetch used internally for reconciliation" });
        continue;
    }
    if (op.operationId === "get-ship-cooldown") {
        partial.push({ ...op, note: "cooldown fields are on getShipInfo / post-action responses" });
        continue;
    }
    if (op.operationId === "get-ship-nav") {
        partial.push({ ...op, note: "nav fields are on getShipInfo (ship snapshot)" });
        continue;
    }
    if (op.operationId === "get-waypoint") {
        partial.push({ ...op, note: "getWaypoints returns full list including each waypoint" });
        continue;
    }

    missing.push(op);
}

function printGroup(title, items, fmt) {
    console.log(`\n## ${title} (${items.length})`);
    for (const item of items) console.log(fmt(item));
}

console.log(`SpaceTraders OpenAPI v${spec.info.version} — ${ops.length} operations`);
printGroup("Covered by Blockly blocks", covered, (o) => `  [${o.block}] ${o.method} ${o.path} (${o.operationId})`);
printGroup("Dashboard / UI only (no block by design)", uiOnly, (o) => `  ${o.method} ${o.path} (${o.operationId})`);
printGroup("Skipped (meta/auth/register)", skipped, (o) => `  ${o.method} ${o.path} (${o.operationId})`);
printGroup("Partially covered / internal / quote-only GETs", partial, (o) => `  ${o.method} ${o.path} (${o.operationId}) — ${o.note}`);
printGroup("Missing Blockly blocks (candidate additions)", missing, (o) => `  ${o.method} ${o.path} — ${o.operationId}: ${o.summary}`);

console.log("\n--- Summary ---");
console.log(`Covered: ${covered.length}`);
console.log(`UI only: ${uiOnly.length}`);
console.log(`Skipped: ${skipped.length}`);
console.log(`Partial: ${partial.length}`);
console.log(`Missing: ${missing.length}`);