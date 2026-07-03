/**
 * Trivial German toolbox for the Milestone 0 spike — two or three primitive blocks plus
 * the definition-shell block, per plan.md Milestone 0 Parts B and C. The real
 * catalog-driven toolbox is Milestone 3.
 *
 * `callerBlockTypes` is the live list of generated custom-block caller types (§9b) to
 * inject into "Eigene Blöcke" — regenerated and pushed via `updateToolbox` whenever a
 * signature changes (§3a, §9c).
 */
export function buildTrivialToolbox(callerBlockTypes: string[]): object {
    return {
        kind: "categoryToolbox",
        contents: [
            {
                kind: "category",
                name: "Programm",
                colour: "210",
                contents: [{ kind: "block", type: "sk_show_message" }, { kind: "block", type: "sk_wait" }, { kind: "block", type: "math_number" }, { kind: "block", type: "text" }],
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
