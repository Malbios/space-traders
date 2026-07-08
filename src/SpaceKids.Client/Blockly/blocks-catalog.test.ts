import { test, describe } from "node:test";
import assert from "node:assert/strict";
import * as Blockly from "blockly/core";
import {
    registerCatalogBlocks,
    getCatalogBlockLabel,
    catalogActionBlockTypes,
    catalogInfoBlockTypes,
    catalogRecordFieldBlockTypes,
    RECORD_FIELD_BLOCKS,
} from "./blocks-catalog";

// Headless (no SVG/browser) — see esbuild.test.config.mjs for how `blockly/core`
// resolves to Blockly's own `core-node.js` entry under Node, which self-initializes
// jsdom. `new Blockly.Workspace()` (not `WorkspaceSvg`) never touches rendering.
registerCatalogBlocks();

test("no block type name collides across action/info/record-field catalogs", () => {
    const allTypes = [...catalogActionBlockTypes, ...catalogInfoBlockTypes, ...catalogRecordFieldBlockTypes];
    const unique = new Set(allTypes);
    assert.equal(unique.size, allTypes.length, "a block type name is registered more than once");
});

test("getCatalogBlockLabel returns a non-empty label for every registered catalog block type", () => {
    const allTypes = [...catalogActionBlockTypes, ...catalogInfoBlockTypes, ...catalogRecordFieldBlockTypes];

    for (const type of allTypes) {
        const label = getCatalogBlockLabel(type);
        assert.notEqual(label, type, `${type}: label lookup fell back to the raw block type (missing catalog entry)`);
        assert.ok(label.length > 0, `${type}: label is empty`);
    }
});

describe("record-field ('Feld aus X') blocks", () => {
    for (const spec of RECORD_FIELD_BLOCKS) {
        test(`${spec.type}: TARGET check, dropdown options, and output-check switching`, () => {
            const ws = new Blockly.Workspace();

            try {
                const block = ws.newBlock(spec.type);
                const def = Blockly.Blocks[spec.type];
                def.init.call(block);

                const targetInput = block.getInput("TARGET");
                assert.ok(targetInput, `${spec.type}: no TARGET input`);
                assert.deepEqual(targetInput!.connection!.getCheck(), [spec.targetCheck]);

                const dropdown = block.getField("FIELD") as Blockly.FieldDropdown;
                assert.ok(dropdown, `${spec.type}: no FIELD dropdown`);
                const optionValues = dropdown.getOptions(false).map((o) => o[1]);
                assert.deepEqual(
                    optionValues,
                    spec.fields.map((f) => f.name),
                    `${spec.type}: dropdown options don't match its own field spec`,
                );

                // Default (freshly dropped) output check matches the first field.
                assert.deepEqual(block.outputConnection!.getCheck(), [spec.fields[0]!.outputCheck]);

                // Every field, not just the default one, must correctly drive the
                // output check when selected — this is the live onchange behavior
                // added for the field-dropdown redesign.
                for (const field of spec.fields) {
                    dropdown.setValue(field.name);
                    def.onchange!.call(block, {} as Blockly.Events.Abstract);
                    assert.deepEqual(
                        block.outputConnection!.getCheck(),
                        [field.outputCheck],
                        `${spec.type}: selecting "${field.name}" didn't produce output check "${field.outputCheck}"`,
                    );
                }
            } finally {
                ws.dispose();
            }
        });
    }

    test("every record shape has at least one field and no duplicate field names within itself", () => {
        for (const spec of RECORD_FIELD_BLOCKS) {
            assert.ok(spec.fields.length > 0, `${spec.type}: no fields declared`);
            const names = spec.fields.map((f) => f.name);
            assert.equal(new Set(names).size, names.length, `${spec.type}: duplicate field name within its own spec`);
        }
    });
});
