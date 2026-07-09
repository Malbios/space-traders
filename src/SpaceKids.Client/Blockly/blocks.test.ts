import { test, describe } from "node:test";
import assert from "node:assert/strict";
import * as Blockly from "blockly/core";
import { registerDefinitionShellBlock, type CustomBlockInputSpec } from "./blocks";

registerDefinitionShellBlock();

describe("sk_param_get", () => {
    test("dropdown options and output check come from the workspace's sk_custom_block_def inputs", () => {
        const ws = new Blockly.Workspace();

        try {
            const defBlock = ws.newBlock("sk_custom_block_def");
            Blockly.Blocks["sk_custom_block_def"].init.call(defBlock);
            (defBlock as unknown as { skInputs: CustomBlockInputSpec[] }).skInputs = [
                { name: "schiff", typeLabel: "Schiff" },
                { name: "menge", typeLabel: "Anzahl" },
            ];

            const paramBlock = ws.newBlock("sk_param_get");
            const def = Blockly.Blocks["sk_param_get"];
            def.init.call(paramBlock);

            const dropdown = paramBlock.getField("PARAM_NAME") as Blockly.FieldDropdown;
            const optionValues = dropdown.getOptions(false).map((o) => o[1]);
            assert.deepEqual(optionValues, ["schiff", "menge"]);

            // "Schiff" maps to a String check (blocks.ts's TYPE_LABEL_TO_CHECK).
            dropdown.setValue("schiff");
            def.onchange!.call(paramBlock, {} as Blockly.Events.Abstract);
            assert.deepEqual(paramBlock.outputConnection!.getCheck(), ["String"]);

            // "Anzahl" maps to a Number check.
            dropdown.setValue("menge");
            def.onchange!.call(paramBlock, {} as Blockly.Events.Abstract);
            assert.deepEqual(paramBlock.outputConnection!.getCheck(), ["Number"]);
        } finally {
            ws.dispose();
        }
    });

    test("with no sk_custom_block_def sibling, the dropdown falls back to a single placeholder option", () => {
        const ws = new Blockly.Workspace();

        try {
            const paramBlock = ws.newBlock("sk_param_get");
            Blockly.Blocks["sk_param_get"].init.call(paramBlock);

            const dropdown = paramBlock.getField("PARAM_NAME") as Blockly.FieldDropdown;
            const options = dropdown.getOptions(false);
            assert.equal(options.length, 1);
            assert.equal(options[0]![1], "", "placeholder option's value should be empty, not a real param name");
        } finally {
            ws.dispose();
        }
    });
});

describe("sk_custom_block_def mutator (input category + free label)", () => {
    test("decompose/compose round-trips a new-style input's category and free label", () => {
        const ws = new Blockly.Workspace();

        try {
            const defBlock = ws.newBlock("sk_custom_block_def") as Blockly.BlockSvg;
            const def = Blockly.Blocks["sk_custom_block_def"];
            def.init!.call(defBlock);
            (defBlock as unknown as { skInputs: CustomBlockInputSpec[] }).skInputs = [
                { name: "zielort", typeLabel: "Text", displayLabel: "Waypoint" },
            ];

            const container = def.decompose!.call(defBlock, ws);
            const argBlock = container.getInputTargetBlock("STACK")!;
            assert.equal(argBlock.getFieldValue("ARG_TYPE"), "Text");
            assert.equal(argBlock.getFieldValue("ARG_LABEL"), "Waypoint");
            assert.equal(argBlock.getFieldValue("ARG_NAME"), "zielort");

            Blockly.Blocks["sk_custom_block_def_mutator_arg"].onchange!.call(argBlock, {} as Blockly.Events.Abstract);
            def.compose!.call(defBlock, container);

            const roundTripped = (defBlock as unknown as { skInputs: CustomBlockInputSpec[] }).skInputs;
            assert.deepEqual(roundTripped, [{ name: "zielort", typeLabel: "Text", displayLabel: "Waypoint" }]);
        } finally {
            ws.dispose();
        }
    });

    test("decompose maps an old, pre-free-label input to the Text category and preserves its old label", () => {
        const ws = new Blockly.Workspace();

        try {
            const defBlock = ws.newBlock("sk_custom_block_def") as Blockly.BlockSvg;
            const def = Blockly.Blocks["sk_custom_block_def"];
            def.init!.call(defBlock);
            (defBlock as unknown as { skInputs: CustomBlockInputSpec[] }).skInputs = [{ name: "schiff", typeLabel: "Schiff" }];

            const container = def.decompose!.call(defBlock, ws);
            const argBlock = container.getInputTargetBlock("STACK")!;
            assert.equal(argBlock.getFieldValue("ARG_TYPE"), "Text", "old String-family label should map to the Text category");
            assert.equal(argBlock.getFieldValue("ARG_LABEL"), "Schiff", "old input's own text should be preserved as its label");
        } finally {
            ws.dispose();
        }
    });
});

describe("sk_build_record", () => {
    test("loadExtraState/saveExtraState round-trip field names and rebuild FIELD_n inputs", () => {
        const ws = new Blockly.Workspace();

        try {
            const block = ws.newBlock("sk_build_record") as Blockly.BlockSvg;
            const def = Blockly.Blocks["sk_build_record"];
            def.init!.call(block);

            def.loadExtraState!.call(block, { fields: [{ name: "Wegpunkt" }, { name: "Kaufpreis" }] });

            assert.ok(block.getInput("FIELD_0"), "FIELD_0 input missing after loadExtraState");
            assert.ok(block.getInput("FIELD_1"), "FIELD_1 input missing after loadExtraState");
            assert.equal(block.getInput("FIELD_2"), null, "unexpected FIELD_2 input");

            const savedState = def.saveExtraState!.call(block) as { fields: { name: string }[] };
            assert.deepEqual(
                savedState.fields.map((f) => f.name),
                ["Wegpunkt", "Kaufpreis"],
            );
        } finally {
            ws.dispose();
        }
    });

    test("re-loading with fewer fields removes the now-stale FIELD_n inputs", () => {
        const ws = new Blockly.Workspace();

        try {
            const block = ws.newBlock("sk_build_record") as Blockly.BlockSvg;
            const def = Blockly.Blocks["sk_build_record"];
            def.init!.call(block);

            def.loadExtraState!.call(block, { fields: [{ name: "A" }, { name: "B" }, { name: "C" }] });
            assert.ok(block.getInput("FIELD_2"));

            def.loadExtraState!.call(block, { fields: [{ name: "A" }] });
            assert.ok(block.getInput("FIELD_0"));
            assert.equal(block.getInput("FIELD_1"), null);
            assert.equal(block.getInput("FIELD_2"), null);
        } finally {
            ws.dispose();
        }
    });
});
