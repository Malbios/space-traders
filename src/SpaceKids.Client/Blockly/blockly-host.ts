import * as Blockly from "blockly/core";
import "blockly/blocks";
import { applyGermanLocale } from "./i18n-locale";
import { registerTrivialBlocks, registerDefinitionShellBlock, registerCallerBlock, readSignature, CustomBlockSignature } from "./blocks";
import { buildTrivialToolbox } from "./toolbox-de";
import { serializeWorkspace as serialize, loadWorkspace as load } from "./workspace-serialization";

/**
 * The seam (§3a): the sole owner of every Blockly instance on the page. Elmish never
 * touches a Blockly object reference — everything crossing this boundary is a JSON
 * string (or a primitive), invoked from F# via IJSRuntime against `window.spaceKids.*`.
 */

applyGermanLocale();
registerTrivialBlocks();
registerDefinitionShellBlock();

const workspaces = new Map<string, Blockly.WorkspaceSvg>();
/** Per-workspace list of generated custom-block caller types currently injected into that workspace's "Eigene Blöcke" category (§9b). */
const callerBlockTypesByContainer = new Map<string, string[]>();
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
    const callerTypes = callerBlockTypesByContainer.get(containerId) ?? [];
    ws.updateToolbox(buildTrivialToolbox(callerTypes) as Blockly.utils.toolbox.ToolboxDefinition);
}

function initWorkspace(containerId: string, readOnly: boolean): void {
    if (workspaces.has(containerId)) {
        return;
    }
    const el = document.getElementById(containerId);
    if (!el) {
        throw new Error(`No element with id "${containerId}" to inject a Blockly workspace into.`);
    }
    callerBlockTypesByContainer.set(containerId, []);
    changeLogByContainer.set(containerId, []);
    const ws = Blockly.inject(el, {
        toolbox: buildTrivialToolbox([]) as Blockly.utils.toolbox.ToolboxDefinition,
        readOnly,
    });
    workspaces.set(containerId, ws);
    onWorkspaceChanged(containerId);
}

function destroyWorkspace(containerId: string): void {
    const ws = workspaces.get(containerId);
    if (ws) {
        ws.dispose();
        workspaces.delete(containerId);
        callerBlockTypesByContainer.delete(containerId);
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

let nextCustomBlockSeq = 1;

/**
 * Milestone 0 Part C mechanics spike (§9): reads the signature off the one
 * `sk_custom_block_def` block living in the workshop workspace `defContainerId`,
 * generates/regenerates its caller block type, and injects that caller into the
 * "Eigene Blöcke" category of `targetContainerId` (a *different* workspace) —
 * proving cross-workspace toolbox updates work before Milestone 9 builds on it.
 */
function publishCustomBlockSignature(defContainerId: string, targetContainerId: string, customBlockId: string | null): string {
    const defWs = requireWorkspace(defContainerId);
    const defBlock = defWs.getBlocksByType("sk_custom_block_def", false)[0];
    if (!defBlock) {
        throw new Error(`No "sk_custom_block_def" block found in workspace "${defContainerId}".`);
    }
    const id = customBlockId ?? `spike-${nextCustomBlockSeq++}`;
    const signature: CustomBlockSignature = readSignature(defBlock, id);
    const blockType = registerCallerBlock(signature);

    const existing = callerBlockTypesByContainer.get(targetContainerId) ?? [];
    if (!existing.includes(blockType)) {
        callerBlockTypesByContainer.set(targetContainerId, [...existing, blockType]);
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
};
