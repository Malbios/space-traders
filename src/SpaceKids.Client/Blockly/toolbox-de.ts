import { catalogActionBlockTypes, catalogInfoBlockTypes, catalogAccessorBlockTypes, flotillaBlockTypes } from "./blocks-catalog";
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
        variables: "Variablen",
        customBlock: "Eigener Block",
        customBlocks: "Eigene Blöcke",
    },
    en: {
        actions: "Actions",
        info: "Information",
        accessors: "Accessors",
        logic: "Logic",
        variables: "Variables",
        customBlock: "Custom block",
        customBlocks: "Custom blocks",
    },
};

/** One "Eigene Blöcke" toolbox entry (§9b) — the generic `callCustomBlock` block type
 * carrying its target's `customBlockId` as `extraState`, so the flyout places a fresh
 * instance already shaped for that specific custom block. */
function callerToolboxEntry(customBlockId: string): object {
    return { kind: "block", type: "callCustomBlock", extraState: { customBlockId } };
}

/**
 * The real German catalog-driven toolbox (§7/§19 Milestone 3). Serves both the main
 * program workspace and the block-workshop workspace — both should offer the same
 * primitives.
 *
 * `customBlockIds` is the live list of custom blocks (§9b) to inject into "Eigene
 * Blöcke", one generic `callCustomBlock` entry per id — regenerated and pushed via
 * `updateToolbox` whenever a signature changes (§3a, §9c). `dynamicAccessorTypes` are
 * the per-custom-block structured-output accessor blocks (§9 Outputs, Milestone
 * 9/Part C), appended alongside the fixed §8 accessors in "Zugriffe".
 */
export function buildCatalogToolbox(customBlockIds: string[], dynamicAccessorTypes: string[]): object {
    const names = CATEGORY_NAMES[getCurrentLocale()];

    return {
        kind: "categoryToolbox",
        contents: [
            {
                kind: "category",
                name: names.actions,
                colour: "160",
                contents: catalogActionBlockTypes.map((type) => ({ kind: "block", type })),
            },
            {
                kind: "category",
                name: names.info,
                colour: "230",
                contents: catalogInfoBlockTypes.map((type) => ({ kind: "block", type })),
            },
            {
                kind: "category",
                name: names.accessors,
                colour: "65",
                contents: catalogAccessorBlockTypes
                    .map((type) => ({ kind: "block", type }))
                    .concat(dynamicAccessorTypes.map((type) => ({ kind: "block", type }))),
            },
            {
                kind: "category",
                name: names.logic,
                colour: "210",
                contents: [
                    ...flotillaBlockTypes.map((type) => ({ kind: "block", type })),
                    { kind: "block", type: "controls_if" },
                    { kind: "block", type: "controls_repeat_ext" },
                    { kind: "block", type: "controls_whileUntil" },
                    { kind: "block", type: "controls_forEach" },
                    { kind: "block", type: "controls_flow_statements" },
                    { kind: "block", type: "logic_compare" },
                    { kind: "block", type: "logic_operation" },
                    { kind: "block", type: "logic_negate" },
                    { kind: "block", type: "logic_boolean" },
                    { kind: "block", type: "math_arithmetic" },
                    { kind: "block", type: "math_change" },
                    { kind: "block", type: "math_number" },
                    { kind: "block", type: "text" },
                    { kind: "block", type: "lists_create_with" },
                    { kind: "block", type: "lists_setIndex" },
                    { kind: "block", type: "lists_getIndex" },
                    { kind: "block", type: "sk_show_message" },
                    { kind: "block", type: "sk_wait" },
                ],
            },
            {
                kind: "category",
                name: names.variables,
                colour: "330",
                custom: "VARIABLE",
            },
            {
                kind: "category",
                name: names.customBlock,
                colour: "290",
                contents: [
                    { kind: "block", type: "sk_custom_block_def" },
                    { kind: "block", type: "sk_param_get" },
                    { kind: "block", type: "sk_build_record" },
                ],
            },
            {
                kind: "category",
                name: names.customBlocks,
                colour: "65",
                contents: customBlockIds.map(callerToolboxEntry),
            },
        ],
    };
}
