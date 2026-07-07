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
import { registerCatalogBlocks, registerStockBlockChecks } from "./blocks-catalog";
import { buildCatalogToolbox, type CustomBlockToolboxEntry } from "./toolbox-de";
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
registerStockBlockChecks();

/** A minimal self-contained dark theme (§ Settings tab dark mode) — defined here
 * rather than pulling in the separate `@blockly/theme-dark` package for a
 * handful of color overrides. Falls back to Blockly's own built-in `Classic`
 * theme for "light". */
const darkBlocklyTheme = Blockly.Theme.defineTheme("spacekids-dark", {
    name: "spacekids-dark",
    base: Blockly.Themes.Classic,
    componentStyles: {
        workspaceBackgroundColour: "#1e1e1e",
        toolboxBackgroundColour: "#2a2a2a",
        toolboxForegroundColour: "#e8e8e8",
        flyoutBackgroundColour: "#2a2a2a",
        flyoutForegroundColour: "#e8e8e8",
        flyoutOpacity: 1,
        scrollbarColour: "#555555",
        insertionMarkerColour: "#ffffff",
        insertionMarkerOpacity: 0.3,
        cursorColour: "#d0d0d0",
    },
});

function blocklyThemeFor(theme: string): Blockly.Theme {
    return theme === "dark" ? darkBlocklyTheme : Blockly.Themes.Classic;
}

const workspaces = new Map<string, Blockly.WorkspaceSvg>();
/** Per-workspace list of custom blocks currently injected into that workspace's "Eigene Blöcke" category (§9b). */
const customBlocksByContainer = new Map<string, CustomBlockToolboxEntry[]>();
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
    const customBlocks = customBlocksByContainer.get(containerId) ?? [];
    const dynamicAccessorTypes = dynamicAccessorTypesByContainer.get(containerId) ?? [];
    ws.updateToolbox(buildCatalogToolbox(customBlocks, dynamicAccessorTypes) as Blockly.utils.toolbox.ToolboxDefinition);
}

const deferAfterLayout = (fn: () => void): Promise<void> =>
    new Promise((resolve) => {
        setTimeout(() => {
            fn();
            resolve();
        }, 0);
    });

/** Blazor re-renders can replace the container element and wipe Blockly's injected
 * SVG while our `workspaces` map still holds a stale handle — detect and re-inject. */
function remountIfDomLost(containerId: string): void {
    const el = document.getElementById(containerId);
    if (!el) {
        throw new Error(`No element with id "${containerId}" to inject a Blockly workspace into.`);
    }
    if (workspaces.has(containerId) && el.querySelector(".blocklySvg") === null) {
        destroyWorkspace(containerId);
    }
}

function resizeWorkspaceNow(containerId: string): void {
    const ws = workspaces.get(containerId);
    if (!ws) {
        return;
    }
    Blockly.svgResize(ws);
    ws.resizeContents();
}

/** Re-measure a workspace after its container becomes visible (e.g. a hidden sub-tab
 * was switched on). Blockly injects into zero-size hidden divs render as blank white
 * canvases with no toolbox until this runs. */
function resizeWorkspace(containerId: string): void {
    void deferAfterLayout(() => resizeWorkspaceNow(containerId));
}

/** Idempotent init + layout refresh — safe to call whenever a workspace's tab/panel
 * becomes visible. Returns a Promise so F# can await post-render remounting. */
async function ensureWorkspaceReady(containerId: string, readOnly: boolean): Promise<void> {
    await deferAfterLayout(() => {
        remountIfDomLost(containerId);
        initWorkspace(containerId, readOnly);
        resizeWorkspaceNow(containerId);
    });
}

function initWorkspace(containerId: string, readOnly: boolean): void {
    if (workspaces.has(containerId)) {
        return;
    }
    const el = document.getElementById(containerId);
    if (!el) {
        throw new Error(`No element with id "${containerId}" to inject a Blockly workspace into.`);
    }
    customBlocksByContainer.set(containerId, []);
    dynamicAccessorTypesByContainer.set(containerId, []);
    changeLogByContainer.set(containerId, []);
    const ws = Blockly.inject(el, {
        toolbox: buildCatalogToolbox([], []) as Blockly.utils.toolbox.ToolboxDefinition,
        readOnly,
        theme: blocklyThemeFor(getTheme()),
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
        const customBlocks = customBlocksByContainer.get(containerId) ?? [];
        const dynamicAccessorTypes = dynamicAccessorTypesByContainer.get(containerId) ?? [];

        ws.dispose();

        const el = document.getElementById(containerId);
        if (!el) {
            workspaces.delete(containerId);
            customBlocksByContainer.delete(containerId);
            dynamicAccessorTypesByContainer.delete(containerId);
            changeLogByContainer.delete(containerId);
            continue;
        }

        const newWs = Blockly.inject(el, {
            toolbox: buildCatalogToolbox(customBlocks, dynamicAccessorTypes) as Blockly.utils.toolbox.ToolboxDefinition,
            readOnly,
            theme: blocklyThemeFor(getTheme()),
        });
        workspaces.set(containerId, newWs);
        customBlocksByContainer.set(containerId, customBlocks);
        dynamicAccessorTypesByContainer.set(containerId, dynamicAccessorTypes);
        changeLogByContainer.set(containerId, []);
        onWorkspaceChanged(containerId);
        load(newWs, json);
    }
}

const THEME_STORAGE_KEY = "spacekids-theme";

/** Settings tab: a per-browser display preference, stored in `localStorage` since it
 * has no bearing on any server-side state (unlike locale/poll-interval). */
function getTheme(): string {
    return window.localStorage.getItem(THEME_STORAGE_KEY) ?? "light";
}

function setTheme(theme: string): void {
    window.localStorage.setItem(THEME_STORAGE_KEY, theme);
    document.documentElement.setAttribute("data-theme", theme);
    const blocklyTheme = blocklyThemeFor(theme);
    for (const ws of workspaces.values()) {
        ws.setTheme(blocklyTheme);
    }
}

const LOG_LEVEL_STORAGE_KEY = "spacekids-log-level";

/** Settings tab: gates the routine-activity trace logging (`LoadPilots`/`WatchTick`
 * polling) -- same per-browser `localStorage` reasoning as `getTheme`/`setTheme`. */
function getLogLevel(): string {
    return window.localStorage.getItem(LOG_LEVEL_STORAGE_KEY) ?? "off";
}

function setLogLevel(level: string): void {
    window.localStorage.setItem(LOG_LEVEL_STORAGE_KEY, level);
}

function destroyWorkspace(containerId: string): void {
    const ws = workspaces.get(containerId);
    if (ws) {
        try {
            ws.dispose();
        } catch {
            // Container DOM may already have been replaced by a Blazor re-render.
        }
        workspaces.delete(containerId);
        customBlocksByContainer.delete(containerId);
        dynamicAccessorTypesByContainer.delete(containerId);
        changeLogByContainer.delete(containerId);
    }
}

async function loadWorkspace(containerId: string, json: string): Promise<void> {
    await deferAfterLayout(() => {
        remountIfDomLost(containerId);
        if (!workspaces.has(containerId)) {
            initWorkspace(containerId, false);
        }
        const ws = requireWorkspace(containerId);
        load(ws, json);
        resizeWorkspaceNow(containerId);
    });
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

    const existing = customBlocksByContainer.get(targetContainerId) ?? [];
    const entry: CustomBlockToolboxEntry = { id, name: signature.name };
    const without = existing.filter((block) => block.id !== id);
    customBlocksByContainer.set(targetContainerId, [...without, entry]);

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
    ensureWorkspaceReady: typeof ensureWorkspaceReady;
    resizeWorkspace: typeof resizeWorkspace;
    setLocale: typeof setLocale;
    getTheme: typeof getTheme;
    setTheme: typeof setTheme;
    getLogLevel: typeof getLogLevel;
    setLogLevel: typeof setLogLevel;
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
    ensureWorkspaceReady,
    resizeWorkspace,
    setLocale,
    getTheme,
    setTheme,
    getLogLevel,
    setLogLevel,
};
