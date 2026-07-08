import {
    catalogActionBlockTypes,
    catalogInfoBlockTypes,
    genericRecordFieldBlockTypes,
    flotillaBlockTypes,
    getCatalogBlockLabel,
} from "./blocks-catalog";
import { getCurrentLocale } from "./locale-state";

/** Milestone 12 (bilingual support): category names vary by locale — the block-type-list
 * structure itself stays single-sourced below, read live so switching locale and
 * rebuilding the toolbox (via `updateToolbox`/a workspace reinit) picks up the new
 * names without duplicating this whole function into a second file. */
const CATEGORY_NAMES = {
    de: {
        actions: "Aktionen",
        info: "Informationen",
        accessors: "Zugriffe",
        logic: "Logik",
        loops: "Schleifen",
        math: "Mathe",
        text: "Text",
        lists: "Listen",
        flow: "Ablauf",
        flotilla: "Flotille",
        variables: "Variablen",
        customBlock: "Eigener Block",
        customBlocks: "Eigene Blöcke",
    },
    en: {
        actions: "Actions",
        info: "Information",
        accessors: "Accessors",
        logic: "Logic",
        loops: "Loops",
        math: "Math",
        text: "Text",
        lists: "Lists",
        flow: "Flow",
        flotilla: "Flotilla",
        variables: "Variables",
        customBlock: "Custom block",
        customBlocks: "Custom blocks",
    },
};

const PROGRAMMING_BLOCK_LABELS: Record<string, { de: string; en: string }> = {
    controls_flow_statements: { de: "Abbrechen/Fortsetzen", en: "Break/continue" },
    controls_forEach: { de: "für jedes Element", en: "for each" },
    controls_if: { de: "falls", en: "if" },
    controls_repeat_ext: { de: "wiederhole", en: "repeat" },
    controls_whileUntil: { de: "solange/bis", en: "while/until" },
    lists_create_with: { de: "Liste erstellen", en: "create list" },
    lists_getIndex: { de: "Listenelement holen", en: "get list item" },
    lists_setIndex: { de: "Listenelement setzen", en: "set list item" },
    logic_boolean: { de: "wahr/falsch", en: "true/false" },
    logic_compare: { de: "vergleichen", en: "compare" },
    logic_negate: { de: "nicht", en: "not" },
    logic_operation: { de: "und/oder", en: "and/or" },
    math_arithmetic: { de: "rechnen", en: "arithmetic" },
    math_change: { de: "Zahl ändern", en: "change number" },
    math_number: { de: "Zahl", en: "number" },
    sk_show_message: { de: "Nachricht anzeigen", en: "show message" },
    sk_wait: { de: "warten", en: "wait" },
    text: { de: "Text", en: "text" },
};

/** Split from one overloaded "Logik" bucket into per-kind groups (a real block was
 * found lumped in wrong — `text`, a string primitive, sat in "Logik") — each group
 * becomes its own toolbox category below. `PROGRAMMING_BLOCK_LABELS` stays the single
 * shared label lookup across all of them, keyed by block type, not category. */
const LOGIC_BLOCK_TYPES: string[] = ["controls_if", "logic_compare", "logic_operation", "logic_negate", "logic_boolean"];
const LOOP_BLOCK_TYPES: string[] = ["controls_repeat_ext", "controls_whileUntil", "controls_forEach", "controls_flow_statements"];
const MATH_BLOCK_TYPES: string[] = ["math_arithmetic", "math_change", "math_number"];
const TEXT_BLOCK_TYPES: string[] = ["text"];
const LIST_BLOCK_TYPES: string[] = ["lists_create_with", "lists_setIndex", "lists_getIndex"];
const FLOW_BLOCK_TYPES: string[] = ["sk_show_message", "sk_wait"];

function programmingBlockLabel(blockType: string): string {
    const labels = PROGRAMMING_BLOCK_LABELS[blockType];
    return labels ? labels[getCurrentLocale()] : blockType;
}

function sortByLabel(types: string[], labelOf: (type: string) => string): string[] {
    const locale = getCurrentLocale();
    return [...types].sort((left, right) => labelOf(left).localeCompare(labelOf(right), locale));
}

export interface CustomBlockToolboxEntry {
    id: string;
    name: string;
}

/** One "Eigene Blöcke" toolbox entry (§9b) — the generic `callCustomBlock` block type
 * carrying its target's `customBlockId` as `extraState`, so the flyout places a fresh
 * instance already shaped for that specific custom block. */
function callerToolboxEntry(entry: CustomBlockToolboxEntry): object {
    return { kind: "block", type: "callCustomBlock", extraState: { customBlockId: entry.id } };
}

export interface CatalogToolboxOptions {
    /** Blockwerkstatt only: "Eigener Block" definition blocks (def shell, param get, build record). */
    includeDefinitionCategory?: boolean;
}

/**
 * The real German catalog-driven toolbox (§7/§19 Milestone 3). Program workspaces get
 * catalog primitives plus saved custom-block callers; the Blockwerkstatt workshop also
 * gets the "Eigener Block" definition category.
 *
 * `customBlocks` is the live list of custom blocks (§9b) to inject into "Eigene
 * Blöcke", one generic `callCustomBlock` entry per id — regenerated and pushed via
 * `updateToolbox` whenever a signature changes (§3a, §9c). `dynamicAccessorTypes` are
 * the per-custom-block structured-output accessor blocks (§9 Outputs, Milestone
 * 9/Part C), appended alongside the fixed §8 accessors in "Zugriffe".
 */
export function buildCatalogToolbox(
    customBlocks: CustomBlockToolboxEntry[],
    dynamicAccessorTypes: string[],
    options: CatalogToolboxOptions = {},
): object {
    const names = CATEGORY_NAMES[getCurrentLocale()];

    const sortedActions = sortByLabel(catalogActionBlockTypes, getCatalogBlockLabel);
    const sortedInfo = sortByLabel(catalogInfoBlockTypes, getCatalogBlockLabel);
    const sortedAccessors = sortByLabel(
        genericRecordFieldBlockTypes.concat(dynamicAccessorTypes),
        (type) => (genericRecordFieldBlockTypes.includes(type) ? getCatalogBlockLabel(type) : type),
    );
    const sortedFlotilla = sortByLabel(flotillaBlockTypes, getCatalogBlockLabel);
    const sortedLogic = sortByLabel(LOGIC_BLOCK_TYPES, programmingBlockLabel);
    const sortedLoops = sortByLabel(LOOP_BLOCK_TYPES, programmingBlockLabel);
    const sortedMath = sortByLabel(MATH_BLOCK_TYPES, programmingBlockLabel);
    const sortedText = sortByLabel(TEXT_BLOCK_TYPES, programmingBlockLabel);
    const sortedLists = sortByLabel(LIST_BLOCK_TYPES, programmingBlockLabel);
    const sortedFlow = sortByLabel(FLOW_BLOCK_TYPES, programmingBlockLabel);
    const sortedCustomBlocks = [...customBlocks].sort((left, right) =>
        left.name.localeCompare(right.name, getCurrentLocale()),
    );

    const definitionCategory = {
        kind: "category",
        name: names.customBlock,
        colour: "290",
        contents: [
            { kind: "block", type: "sk_custom_block_def" },
            { kind: "block", type: "sk_param_get" },
            { kind: "block", type: "sk_build_record" },
        ],
    };

    return {
        kind: "categoryToolbox",
        contents: [
            {
                kind: "category",
                name: names.actions,
                colour: "160",
                contents: sortedActions.map((type) => ({ kind: "block", type })),
            },
            {
                kind: "category",
                name: names.info,
                colour: "230",
                contents: sortedInfo.map((type) => ({ kind: "block", type })),
            },
            {
                kind: "category",
                name: names.accessors,
                colour: "65",
                contents: sortedAccessors.map((type) => ({ kind: "block", type })),
            },
            {
                kind: "category",
                name: names.logic,
                colour: "210",
                contents: sortedLogic.map((type) => ({ kind: "block", type })),
            },
            {
                kind: "category",
                name: names.loops,
                colour: "120",
                contents: sortedLoops.map((type) => ({ kind: "block", type })),
            },
            {
                kind: "category",
                name: names.math,
                colour: "255",
                contents: sortedMath.map((type) => ({ kind: "block", type })),
            },
            {
                kind: "category",
                name: names.text,
                colour: "45",
                contents: sortedText.map((type) => ({ kind: "block", type })),
            },
            {
                kind: "category",
                name: names.lists,
                colour: "315",
                contents: sortedLists.map((type) => ({ kind: "block", type })),
            },
            {
                kind: "category",
                name: names.flow,
                colour: "190",
                contents: sortedFlow.map((type) => ({ kind: "block", type })),
            },
            {
                kind: "category",
                name: names.flotilla,
                colour: "20",
                contents: sortedFlotilla.map((type) => ({ kind: "block", type })),
            },
            {
                kind: "category",
                name: names.variables,
                colour: "330",
                custom: "VARIABLE",
            },
            ...(options.includeDefinitionCategory ? [definitionCategory] : []),
            {
                kind: "category",
                name: names.customBlocks,
                colour: "65",
                contents: sortedCustomBlocks.map(callerToolboxEntry),
            },
        ],
    };
}