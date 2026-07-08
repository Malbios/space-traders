import { test, describe } from "node:test";
import assert from "node:assert/strict";
import * as Blockly from "blockly/core";
import "blockly/blocks";
import { applyBlocklyLocale } from "./i18n-locale";
import {
    registerCatalogBlocks,
    registerVariableTypeTagging,
    getCatalogBlockLabel,
    catalogActionBlockTypes,
    catalogInfoBlockTypes,
    catalogRecordFieldBlockTypes,
    RECORD_FIELD_BLOCKS,
} from "./blocks-catalog";

// Headless (no SVG/browser) — see esbuild.test.config.mjs for how `blockly/core`
// resolves to Blockly's own `core-node.js` entry under Node, which self-initializes
// jsdom. `new Blockly.Workspace()` (not `WorkspaceSvg`) never touches rendering.
// `applyBlocklyLocale` is needed before any real `variables_set`/`variables_get`
// block is instantiated — their stock `VAR` field's `dropdownCreate` reads
// `Blockly.Msg.RENAME_VARIABLE`/`DELETE_VARIABLE`, which are unset without it and
// crash with a bare `Cannot read properties of undefined (reading 'replace')`
// (found live while writing the Phase 2 variable-type-tagging tests below).
applyBlocklyLocale("de");
registerCatalogBlocks();
registerVariableTypeTagging();

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

describe("variable type tagging (Phase 2: narrowing recordField through a variable)", () => {
    function makeShipInfoBlock(ws: Blockly.Workspace): Blockly.Block {
        const b = ws.newBlock("getShipInfo");
        Blockly.Blocks["getShipInfo"].init.call(b);
        return b;
    }

    function makeMarketBlock(ws: Blockly.Workspace): Blockly.Block {
        const b = ws.newBlock("getMarket");
        Blockly.Blocks["getMarket"].init.call(b);
        return b;
    }

    function makeVariablesSet(ws: Blockly.Workspace, varName: string) {
        const varModel = ws.getVariableMap().createVariable(varName);
        const block = ws.newBlock("variables_set");
        (block.getField("VAR") as Blockly.FieldVariable).setValue(varModel.getId());
        return { block, varModel };
    }

    function makeVariablesGet(ws: Blockly.Workspace, varModel: ReturnType<Blockly.VariableMap["createVariable"]>): Blockly.Block {
        const block = ws.newBlock("variables_get");
        (block.getField("VAR") as Blockly.FieldVariable).setValue(varModel.getId());
        return block;
    }

    function fireOnchange(block: Blockly.Block): void {
        Blockly.Blocks["variables_set"].onchange!.call(block, {} as Blockly.Events.Abstract);
    }

    test("tags a variable's type when assigned from a known record shape, narrowing recordField reading it", () => {
        const ws = new Blockly.Workspace();
        try {
            const { block: setBlock, varModel } = makeVariablesSet(ws, "schiff");
            const shipInfo = makeShipInfoBlock(ws);
            setBlock.getInput("VALUE")!.connection!.connect(shipInfo.outputConnection!);
            fireOnchange(setBlock);

            assert.equal(varModel.getType(), "ShipRecord");

            const getBlock = makeVariablesGet(ws, varModel);
            const accessor = ws.newBlock("recordField");
            Blockly.Blocks["recordField"].init.call(accessor);
            accessor.getInput("TARGET")!.connection!.connect(getBlock.outputConnection!);

            const dropdown = accessor.getField("FIELD") as Blockly.FieldDropdown;
            const shipSpec = RECORD_FIELD_BLOCKS.find((s) => s.type === "shipField")!;
            assert.deepEqual(
                dropdown.getOptions(false).map((o) => o[1]),
                shipSpec.fields.map((f) => f.name),
            );
        } finally {
            ws.dispose();
        }
    });

    test("clears the tag when reassigned to a plain literal, falling recordField back to the merged list", () => {
        const ws = new Blockly.Workspace();
        try {
            const { block: setBlock, varModel } = makeVariablesSet(ws, "schiff");
            const shipInfo = makeShipInfoBlock(ws);
            setBlock.getInput("VALUE")!.connection!.connect(shipInfo.outputConnection!);
            fireOnchange(setBlock);
            assert.equal(varModel.getType(), "ShipRecord");

            setBlock.getInput("VALUE")!.connection!.disconnect();
            const text = ws.newBlock("text");
            setBlock.getInput("VALUE")!.connection!.connect(text.outputConnection!);
            fireOnchange(setBlock);

            assert.equal(varModel.getType(), "");

            const getBlock = makeVariablesGet(ws, varModel);
            const accessor = ws.newBlock("recordField");
            Blockly.Blocks["recordField"].init.call(accessor);
            accessor.getInput("TARGET")!.connection!.connect(getBlock.outputConnection!);

            const dropdown = accessor.getField("FIELD") as Blockly.FieldDropdown;
            const allDistinctNames = new Set(RECORD_FIELD_BLOCKS.flatMap((s) => s.fields.map((f) => f.name)));
            assert.equal(dropdown.getOptions(false).length, allDistinctNames.size);
        } finally {
            ws.dispose();
        }
    });

    test("updates the tag when reassigned to a different known record shape", () => {
        const ws = new Blockly.Workspace();
        try {
            const { block: setBlock, varModel } = makeVariablesSet(ws, "sache");
            const shipInfo = makeShipInfoBlock(ws);
            setBlock.getInput("VALUE")!.connection!.connect(shipInfo.outputConnection!);
            fireOnchange(setBlock);
            assert.equal(varModel.getType(), "ShipRecord");

            setBlock.getInput("VALUE")!.connection!.disconnect();
            const market = makeMarketBlock(ws);
            setBlock.getInput("VALUE")!.connection!.connect(market.outputConnection!);
            fireOnchange(setBlock);

            assert.equal(varModel.getType(), "MarketRecord");
        } finally {
            ws.dispose();
        }
    });

    test("leaves the tag untouched when VALUE is disconnected or wired to another bare variable (ambiguous, not a clear reassignment)", () => {
        const ws = new Blockly.Workspace();
        try {
            const { block: setBlock, varModel } = makeVariablesSet(ws, "schiff");
            const shipInfo = makeShipInfoBlock(ws);
            setBlock.getInput("VALUE")!.connection!.connect(shipInfo.outputConnection!);
            fireOnchange(setBlock);
            assert.equal(varModel.getType(), "ShipRecord");

            setBlock.getInput("VALUE")!.connection!.disconnect();
            fireOnchange(setBlock);
            assert.equal(varModel.getType(), "ShipRecord", "disconnecting VALUE should not clobber a known tag");

            const other = ws.getVariableMap().createVariable("anderes");
            const otherGet = makeVariablesGet(ws, other);
            setBlock.getInput("VALUE")!.connection!.connect(otherGet.outputConnection!);
            fireOnchange(setBlock);
            assert.equal(
                varModel.getType(),
                "ShipRecord",
                "wiring through another bare (untyped) variable is ambiguous, not a clear reassignment",
            );
        } finally {
            ws.dispose();
        }
    });
});
