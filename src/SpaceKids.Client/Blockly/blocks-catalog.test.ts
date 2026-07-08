import { test, describe } from "node:test";
import assert from "node:assert/strict";
import * as Blockly from "blockly/core";
import "blockly/blocks";
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

describe("generic connection-aware record field block (recordField)", () => {
    test("TARGET accepts every known record check", () => {
        const ws = new Blockly.Workspace();
        try {
            const accessor = ws.newBlock("recordField");
            Blockly.Blocks["recordField"].init.call(accessor);

            const checks = accessor.getInput("TARGET")!.connection!.getCheck();
            assert.ok(checks, "TARGET has no check at all");
            for (const spec of RECORD_FIELD_BLOCKS) {
                assert.ok(checks!.includes(spec.targetCheck), `TARGET doesn't accept ${spec.targetCheck}`);
            }
        } finally {
            ws.dispose();
        }
    });

    test("narrows the FIELD dropdown to just the connected block's own shape when TARGET is directly wired", () => {
        const ws = new Blockly.Workspace();
        try {
            const shipInfo = ws.newBlock("getShipInfo");
            Blockly.Blocks["getShipInfo"].init.call(shipInfo);

            const accessor = ws.newBlock("recordField");
            Blockly.Blocks["recordField"].init.call(accessor);
            accessor.getInput("TARGET")!.connection!.connect(shipInfo.outputConnection!);

            const dropdown = accessor.getField("FIELD") as Blockly.FieldDropdown;
            const optionValues = dropdown.getOptions(false).map((o) => o[1]);
            const shipSpec = RECORD_FIELD_BLOCKS.find((s) => s.type === "shipField")!;
            assert.deepEqual(optionValues, shipSpec.fields.map((f) => f.name));
        } finally {
            ws.dispose();
        }
    });

    test("falls back to the merged field list across all shapes when TARGET has no connection check (e.g. a variable)", () => {
        // Stock `variables_get` itself would be the realistic stand-in here, but
        // instantiating one in this headless harness needs Blockly's i18n `Msg`
        // strings loaded (`RENAME_VARIABLE` etc.), which aren't set up outside the
        // real app (`blockly-host.ts`'s `applyBlocklyLocale`) — unrelated to the
        // logic under test. An ad hoc unchecked-output block exercises the exact
        // same path (`outputConnection.getCheck()` returning `null`) without that
        // dependency.
        Blockly.Blocks["__test_unchecked_value__"] = {
            init: function (this: Blockly.Block) {
                this.setOutput(true, null);
            },
        };

        const ws = new Blockly.Workspace();
        try {
            const varBlock = ws.newBlock("__test_unchecked_value__");
            Blockly.Blocks["__test_unchecked_value__"].init.call(varBlock);

            const accessor = ws.newBlock("recordField");
            Blockly.Blocks["recordField"].init.call(accessor);
            accessor.getInput("TARGET")!.connection!.connect(varBlock.outputConnection!);

            const dropdown = accessor.getField("FIELD") as Blockly.FieldDropdown;
            const options = dropdown.getOptions(false);
            const optionValues = options.map((o) => o[1]);

            const allDistinctNames = new Set(RECORD_FIELD_BLOCKS.flatMap((s) => s.fields.map((f) => f.name)));
            assert.equal(optionValues.length, allDistinctNames.size);

            // The "Goods" collision (cargoField's "Waren"/"Goods" vs. marketField's
            // "Handelswaren"/"Trade goods", both List-typed) must resolve to
            // cargoField's label, since it's first in RECORD_FIELD_BLOCKS order.
            const goodsOption = options.find((o) => o[1] === "Goods");
            assert.ok(goodsOption, "expected a merged \"Goods\" field option");
            assert.equal(goodsOption![0], "Waren");
        } finally {
            ws.dispose();
        }
    });

    test("falls back to the merged field list when nothing is connected to TARGET", () => {
        const ws = new Blockly.Workspace();
        try {
            const accessor = ws.newBlock("recordField");
            Blockly.Blocks["recordField"].init.call(accessor);

            const dropdown = accessor.getField("FIELD") as Blockly.FieldDropdown;
            const allDistinctNames = new Set(RECORD_FIELD_BLOCKS.flatMap((s) => s.fields.map((f) => f.name)));
            assert.equal(dropdown.getOptions(false).length, allDistinctNames.size);
        } finally {
            ws.dispose();
        }
    });

    test("output check follows the currently selected field, for whichever field set is currently showing", () => {
        const ws = new Blockly.Workspace();
        try {
            const shipInfo = ws.newBlock("getShipInfo");
            Blockly.Blocks["getShipInfo"].init.call(shipInfo);

            const accessor = ws.newBlock("recordField");
            const def = Blockly.Blocks["recordField"];
            def.init.call(accessor);
            accessor.getInput("TARGET")!.connection!.connect(shipInfo.outputConnection!);

            const dropdown = accessor.getField("FIELD") as Blockly.FieldDropdown;
            dropdown.setValue("Fuel");
            def.onchange!.call(accessor, {} as Blockly.Events.Abstract);
            assert.deepEqual(accessor.outputConnection!.getCheck(), ["Number"]);
        } finally {
            ws.dispose();
        }
    });
});
