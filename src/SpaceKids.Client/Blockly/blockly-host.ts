import * as Blockly from "blockly/core";
import "blockly/blocks";
import { applyBlocklyLocale } from "./i18n-locale";
import {
    registerTrivialBlocks,
    registerDefinitionShellBlock,
    registerCallerBlock,
    registerSignature,
    registerCustomBlockAccessors,
    readSignature,
} from "./blocks";
import { registerCatalogBlocks } from "./blocks-catalog";
import { buildCatalogToolbox } from "./toolbox-de";
import { serializeWorkspace as serialize, loadWorkspace as load } from "./workspace-serialization";
import { Locale, setCurrentLocale } from "./locale-state";

/**
 * The seam (§3a): the sole owner of every Blockly instance on the page. Elmish never
 * touches a Blockly object reference — everything crossing this boundary is a JSON
 * string (or a primitive), invoked from F# via IJSRuntime against `window.spaceKids.*`.
 */

applyBlocklyLocale("de");
registerTrivialBlocks();
registerDefinitionShellBlock();
registerCallerBlock();
registerCatalogBlocks();

const workspaces = new Map<string, Blockly.WorkspaceSvg>();
/** Per-workspace list of custom-block ids currently injected into that workspace's "Eigene Blöcke" category, one generic `callCustomBlock` toolbox entry each (§9b). */
const customBlockIdsByContainer = new Map<string, string[]>();
/** Per-workspace list of dynamically generated structured-output accessor block types currently injected into "Zugriffe" (§9 Outputs, Milestone 9/Part C). */
const dynamicAccessorTypesByContainer = new Map<string, string[]>();
/** Debug-only: records which event classes reached each workspace's change listener, so Milestone 0's "fires on meaningful events only, not every drag frame" check can be verified from the console/tests. Not part of the documented §3a surface. */
const changeLogByContainer = new Map<string, string[]>();

function requireWorkspace(containerId: string): Blockly.WorkspaceSvg {
    const ws = workspaces.get(containerId);
    if (!ws) {
        throw new Error(`No Blockly workspace initialized for container "${containerId}".`);
    }
    return ws;
}

function refreshToolbox(containerId: string): void {
    const ws = requireWorkspace(containerId);
    const customBlockIds = customBlockIdsByContainer.get(containerId) ?? [];
    const dynamicAccessorTypes = dynamicAccessorTypesByContainer.get(containerId) ?? [];
    ws.updateToolbox(buildCatalogToolbox(customBlockIds, dynamicAccessorTypes) as Blockly.utils.toolbox.ToolboxDefinition);
}

function initWorkspace(containerId: string, readOnly: boolean): void {
    if (workspaces.has(containerId)) {
        return;
    }
    const el = document.getElementById(containerId);
    if (!el) {
        throw new Error(`No element with id "${containerId}" to inject a Blockly workspace into.`);
    }
    customBlockIdsByContainer.set(containerId, []);
    dynamicAccessorTypesByContainer.set(containerId, []);
    changeLogByContainer.set(containerId, []);
    const ws = Blockly.inject(el, {
        toolbox: buildCatalogToolbox([], []) as Blockly.utils.toolbox.ToolboxDefinition,
        readOnly,
    });
    workspaces.set(containerId, ws);
    onWorkspaceChanged(containerId);
}

/**
 * Milestone 12 (bilingual support): switches the active locale and re-renders every
 * currently-open workspace under it. Blockly doesn't relabel already-instantiated
 * block instances after `Blockly.setLocale` — the only way to get existing blocks to
 * show the new language is to recreate them, so this captures each open workspace's
 * JSON, disposes it, re-injects a fresh one (preserving its custom-block/accessor
 * toolbox entries, unlike `destroyWorkspace` which is a one-way teardown), and reloads
 * the same JSON — same block *types*, freshly rendered labels.
 */
function setLocale(locale: Locale): void {
    setCurrentLocale(locale);
    applyBlocklyLocale(locale);

    for (const containerId of [...workspaces.keys()]) {
        const ws = workspaces.get(containerId)!;
        const json = serialize(ws);
        const readOnly = ws.options.readOnly;
        const customBlockIds = customBlockIdsByContainer.get(containerId) ?? [];
        const dynamicAccessorTypes = dynamicAccessorTypesByContainer.get(containerId) ?? [];

        ws.dispose();

        const el = document.getElementById(containerId);
        if (!el) {
            workspaces.delete(containerId);
            customBlockIdsByContainer.delete(containerId);
            dynamicAccessorTypesByContainer.delete(containerId);
            changeLogByContainer.delete(containerId);
            continue;
        }

        const newWs = Blockly.inject(el, {
            toolbox: buildCatalogToolbox(customBlockIds, dynamicAccessorTypes) as Blockly.utils.toolbox.ToolboxDefinition,
            readOnly,
        });
        workspaces.set(containerId, newWs);
        customBlockIdsByContainer.set(containerId, customBlockIds);
        dynamicAccessorTypesByContainer.set(containerId, dynamicAccessorTypes);
        changeLogByContainer.set(containerId, []);
        onWorkspaceChanged(containerId);
        load(newWs, json);
    }
}

function destroyWorkspace(containerId: string): void {
    const ws = workspaces.get(containerId);
    if (ws) {
        ws.dispose();
        workspaces.delete(containerId);
        customBlockIdsByContainer.delete(containerId);
        dynamicAccessorTypesByContainer.delete(containerId);
        changeLogByContainer.delete(containerId);
    }
}

function loadWorkspace(containerId: string, json: string): void {
    load(requireWorkspace(containerId), json);
}

function serializeWorkspace(containerId: string): string {
    return serialize(requireWorkspace(containerId));
}

/** Only fires on meaningful mutations (create/delete/change/finished-move) — not on every drag frame — per §3a. */
const MEANINGFUL_EVENT_TYPES = new Set<string>([Blockly.Events.BLOCK_CREATE, Blockly.Events.BLOCK_DELETE, Blockly.Events.BLOCK_CHANGE, Blockly.Events.BLOCK_MOVE]);

function onWorkspaceChanged(containerId: string): void {
    const ws = requireWorkspace(containerId);
    ws.addChangeListener((event: Blockly.Events.Abstract) => {
        if (!MEANINGFUL_EVENT_TYPES.has(event.type)) {
            return;
        }
        // A drag-in-progress fires many intermediate events, but Blockly only emits a
        // single BLOCK_MOVE once the drag ends (isUiEvent groups the rest) — no separate
        // "move-end" filter is needed beyond checking the event class itself.
        const log = changeLogByContainer.get(containerId);
        log?.push(event.type);
    });
}

function getChangeLog(containerId: string): string[] {
    return changeLogByContainer.get(containerId) ?? [];
}

function setReadOnly(containerId: string, readOnly: boolean): void {
    requireWorkspace(containerId).options.readOnly = readOnly;
    (requireWorkspace(containerId) as Blockly.WorkspaceSvg).setResizesEnabled(true);
    for (const block of requireWorkspace(containerId).getAllBlocks(false)) {
        block.setEditable(!readOnly);
        block.setMovable(!readOnly);
        block.setDeletable(!readOnly);
    }
}

function highlightBlock(containerId: string, blockId: string): void {
    requireWorkspace(containerId).highlightBlock(blockId);
}

function clearHighlight(containerId: string): void {
    requireWorkspace(containerId).highlightBlock(null as unknown as string);
}

function firstBlockId(containerId: string): string | null {
    const blocks = requireWorkspace(containerId).getAllBlocks(false);
    return blocks[0]?.id ?? null;
}

const sleep = (ms: number): Promise<void> => new Promise((resolve) => setTimeout(resolve, ms));

/**
 * Milestone 3 (§19): highlights each block of the first top-level statement stack in
 * sequence, with a short pause between each — a fake/simulated run to prove highlighting
 * works across the full catalog. Not real DSL execution (Milestone 4 builds that).
 */
async function simulateRun(containerId: string): Promise<void> {
    const ws = requireWorkspace(containerId);
    let block = ws.getTopBlocks(true)[0] ?? null;
    while (block) {
        highlightBlock(containerId, block.id);
        await sleep(700);
        block = block.getNextBlock();
    }
    clearHighlight(containerId);
}

let nextCustomBlockSeq = 1;

/**
 * Reads the signature off the one `sk_custom_block_def` block living in the workshop
 * workspace `defContainerId` (§9b/§9c), caches it (so the generic `callCustomBlock`
 * caller type can rebuild its shape per-instance), registers any structured-output
 * accessor blocks it needs (§9 Outputs), and injects both into `targetContainerId`'s
 * toolbox (a *different* workspace — a program, or another block's workshop, since
 * blocks can call blocks).
 */
function publishCustomBlockSignature(defContainerId: string, targetContainerId: string, customBlockId: string | null): string {
    const defWs = requireWorkspace(defContainerId);
    const defBlock = defWs.getBlocksByType("sk_custom_block_def", false)[0];
    if (!defBlock) {
        throw new Error(`No "sk_custom_block_def" block found in workspace "${defContainerId}".`);
    }
    const id = customBlockId ?? `spike-${nextCustomBlockSeq++}`;
    const signature = readSignature(defBlock, id);
    registerSignature(signature);
    const accessorTypes = registerCustomBlockAccessors(signature);

    const existingIds = customBlockIdsByContainer.get(targetContainerId) ?? [];
    if (!existingIds.includes(id)) {
        customBlockIdsByContainer.set(targetContainerId, [...existingIds, id]);
    }

    const existingAccessors = dynamicAccessorTypesByContainer.get(targetContainerId) ?? [];
    const newAccessors = accessorTypes.filter((t) => !existingAccessors.includes(t));
    if (newAccessors.length > 0) {
        dynamicAccessorTypesByContainer.set(targetContainerId, [...existingAccessors, ...newAccessors]);
    }

    refreshToolbox(targetContainerId);
    return id;
}

interface SpaceKidsHost {
    initWorkspace: typeof initWorkspace;
    destroyWorkspace: typeof destroyWorkspace;
    loadWorkspace: typeof loadWorkspace;
    serializeWorkspace: typeof serializeWorkspace;
    setReadOnly: typeof setReadOnly;
    highlightBlock: typeof highlightBlock;
    clearHighlight: typeof clearHighlight;
    firstBlockId: typeof firstBlockId;
    getChangeLog: typeof getChangeLog;
    publishCustomBlockSignature: typeof publishCustomBlockSignature;
    simulateRun: typeof simulateRun;
    setLocale: typeof setLocale;
}

declare global {
    interface Window {
        spaceKids: SpaceKidsHost;
    }
}

window.spaceKids = {
    initWorkspace,
    destroyWorkspace,
    loadWorkspace,
    serializeWorkspace,
    setReadOnly,
    highlightBlock,
    clearHighlight,
    firstBlockId,
    getChangeLog,
    publishCustomBlockSignature,
    simulateRun,
    setLocale,
};
