import { catalogActionBlockTypes, catalogInfoBlockTypes, catalogAccessorBlockTypes } from "./blocks-catalog";

/**
 * The real German catalog-driven toolbox (§7/§19 Milestone 3). Serves both the main
 * program workspace and the block-workshop workspace — both should offer the same
 * primitives.
 *
 * `callerBlockTypes` is the live list of generated custom-block caller types (§9b) to
 * inject into "Eigene Blöcke" — regenerated and pushed via `updateToolbox` whenever a
 * signature changes (§3a, §9c). Custom-block calling itself is Milestone 9 scope; this
 * category and the Milestone 0 Part C mutator spike that feeds it are unchanged.
 */
export function buildCatalogToolbox(callerBlockTypes: string[]): object {
    return {
        kind: "categoryToolbox",
        contents: [
            {
                kind: "category",
                name: "Aktionen",
                colour: "160",
                contents: catalogActionBlockTypes.map((type) => ({ kind: "block", type })),
            },
            {
                kind: "category",
                name: "Informationen",
                colour: "230",
                contents: catalogInfoBlockTypes.map((type) => ({ kind: "block", type })),
            },
            {
                kind: "category",
                name: "Zugriffe",
                colour: "65",
                contents: catalogAccessorBlockTypes.map((type) => ({ kind: "block", type })),
            },
            {
                kind: "category",
                name: "Programmierung",
                colour: "210",
                contents: [
                    { kind: "block", type: "controls_if" },
                    { kind: "block", type: "controls_repeat_ext" },
                    { kind: "block", type: "controls_whileUntil" },
                    { kind: "block", type: "controls_forEach" },
                    { kind: "block", type: "logic_compare" },
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
                name: "Variablen",
                colour: "330",
                custom: "VARIABLE",
            },
            {
                kind: "category",
                name: "Eigener Block",
                colour: "290",
                contents: [{ kind: "block", type: "sk_custom_block_def" }],
            },
            {
                kind: "category",
                name: "Eigene Blöcke",
                colour: "65",
                contents: callerBlockTypes.map((type) => ({ kind: "block", type })),
            },
        ],
    };
}
