import * as Blockly from "blockly/core";

/**
 * German primitive blocks for the Milestone 0 toolchain/seam spike (§3a/§7).
 * Only a trivial handful exist here — the real block catalog (§7, docs/04-block-catalog.md)
 * is authored in Milestone 3. These two prove save/load/highlight works end to end.
 */
export function registerTrivialBlocks(): void {
    Blockly.Blocks["sk_show_message"] = {
        init: function (this: Blockly.Block) {
            this.appendValueInput("TEXT").setCheck("String").appendField("Zeige Nachricht");
            this.setPreviousStatement(true, null);
            this.setNextStatement(true, null);
            this.setColour(210);
            this.setTooltip("Zeigt eine Nachricht im Logbuch an.");
        },
    };

    Blockly.Blocks["sk_wait"] = {
        init: function (this: Blockly.Block) {
            this.appendValueInput("SECONDS").setCheck("Number").appendField("Warte");
            this.appendDummyInput().appendField("Sekunden");
            this.setPreviousStatement(true, null);
            this.setNextStatement(true, null);
            this.setColour(210);
            this.setTooltip("Wartet eine bestimmte Anzahl Sekunden.");
        },
    };
}

/** One input's shape in a custom block's typed, dynamic signature (§9c). Only "Zahl" exists in the Milestone 0 mini-spike; the full type set (Schiff, Wegpunkt, Ware, ...) is Milestone 9 scope. */
export interface CustomBlockInputSpec {
    name: string;
    /** Blockly type-check string used on the corresponding value input, e.g. "Number" for Zahl. */
    check: string;
    /** German label for this input type, shown in the mutator's input list, e.g. "Zahl". */
    typeLabel: string;
}

export interface CustomBlockSignature {
    id: string;
    name: string;
    inputs: CustomBlockInputSpec[];
}

const NUMBER_INPUT: CustomBlockInputSpec = { name: "Zahl", check: "Number", typeLabel: "Zahl" };

interface DefBlockExtraState {
    inputs: CustomBlockInputSpec[];
}

/**
 * The one-and-only "Eigener Block" definition shell block type (§9b/§9c).
 * A real workshop has exactly one of these; its mutator (gear icon) adds/removes
 * inputs from the fixed NUMBER_INPUT template — Milestone 0's mini-spike only needs
 * to prove one typed input can be added/removed and regenerates a caller elsewhere.
 */
export function registerDefinitionShellBlock(): void {
    Blockly.Blocks["sk_custom_block_def"] = {
        init: function (this: Blockly.Block) {
            this.appendDummyInput("NAME_ROW").appendField("Eigener Block").appendField(new Blockly.FieldTextInput("Mein Block"), "BLOCK_NAME");
            this.appendStatementInput("BODY").setCheck(null).appendField("Inhalt");
            this.setColour(290);
            this.setTooltip("Definiert die Logik eines eigenen, wiederverwendbaren Blocks.");
            this.setMutator(
                new Blockly.icons.MutatorIcon(["sk_custom_block_def_mutator_arg"], this as unknown as Blockly.BlockSvg),
            );
            (this as unknown as { skInputs: CustomBlockInputSpec[] }).skInputs = [];
        },
        saveExtraState: function (this: Blockly.Block): DefBlockExtraState {
            return { inputs: (this as unknown as { skInputs: CustomBlockInputSpec[] }).skInputs ?? [] };
        },
        loadExtraState: function (this: Blockly.Block, state: DefBlockExtraState) {
            (this as unknown as { skInputs: CustomBlockInputSpec[] }).skInputs = state.inputs ?? [];
            rebuildInputRow(this as unknown as Blockly.BlockSvg);
        },
        decompose: function (this: Blockly.Block, workspace: Blockly.Workspace): Blockly.Block {
            const containerBlock = workspace.newBlock("sk_custom_block_def_mutator_container");
            (containerBlock as Blockly.BlockSvg).initSvg?.();
            let connection = containerBlock.getInput("STACK")!.connection!;
            const inputs = (this as unknown as { skInputs: CustomBlockInputSpec[] }).skInputs ?? [];
            for (const _ of inputs) {
                const argBlock = workspace.newBlock("sk_custom_block_def_mutator_arg");
                (argBlock as Blockly.BlockSvg).initSvg?.();
                connection.connect(argBlock.previousConnection!);
                connection = argBlock.nextConnection!;
            }
            return containerBlock;
        },
        compose: function (this: Blockly.Block, containerBlock: Blockly.Block) {
            const newInputs: CustomBlockInputSpec[] = [];
            let argBlock = containerBlock.getInputTargetBlock("STACK");
            while (argBlock) {
                newInputs.push({ ...NUMBER_INPUT, name: (argBlock as unknown as { skArgName: string }).skArgName ?? NUMBER_INPUT.name });
                argBlock = argBlock.nextConnection && argBlock.nextConnection.targetBlock();
            }
            (this as unknown as { skInputs: CustomBlockInputSpec[] }).skInputs = newInputs;
            rebuildInputRow(this as unknown as Blockly.BlockSvg);
        },
    };

    Blockly.Blocks["sk_custom_block_def_mutator_container"] = {
        init: function (this: Blockly.Block) {
            this.appendDummyInput().appendField("Eingaben");
            this.appendStatementInput("STACK");
            this.setColour(290);
            this.setTooltip("Eingaben des eigenen Blocks (Reihenfolge = Reihenfolge im Block).");
            this.contextMenu = false;
        },
    };

    Blockly.Blocks["sk_custom_block_def_mutator_arg"] = {
        init: function (this: Blockly.Block) {
            this.appendDummyInput().appendField("Zahl").appendField(new Blockly.FieldTextInput("n"), "ARG_NAME");
            this.setPreviousStatement(true);
            this.setNextStatement(true);
            this.setColour(290);
            this.contextMenu = false;
        },
        onchange: function (this: Blockly.Block) {
            (this as unknown as { skArgName: string }).skArgName = this.getFieldValue("ARG_NAME");
        },
    };
}

function rebuildInputRow(block: Blockly.BlockSvg): void {
    let i = 0;
    while (block.getInput(`INPUT_${i}`)) {
        block.removeInput(`INPUT_${i}`);
        i++;
    }
    const inputs = (block as unknown as { skInputs: CustomBlockInputSpec[] }).skInputs ?? [];
    inputs.forEach((input, index) => {
        block.appendDummyInput(`INPUT_${index}`).appendField(`Eingabe: ${input.typeLabel} ${input.name}`);
    });
}

export function readSignature(block: Blockly.Block, id: string): CustomBlockSignature {
    const name = block.getFieldValue("BLOCK_NAME") as string;
    const inputs = (block as unknown as { skInputs: CustomBlockInputSpec[] }).skInputs ?? [];
    return { id, name, inputs };
}

/**
 * Generates (or regenerates) a caller block type from a stored signature (§9b/§9c).
 * Registering the same type id again overwrites Blockly.Blocks[type], which is exactly
 * how "change the signature; regenerate the caller" (Milestone 0 Part C) is meant to work —
 * existing caller blocks already on a workspace keep their connections; only the shape
 * of newly-placed ones (and the toolbox entry) reflects the new signature.
 */
export function registerCallerBlock(signature: CustomBlockSignature): string {
    const blockType = `sk_call_${signature.id}`;
    Blockly.Blocks[blockType] = {
        init: function (this: Blockly.Block) {
            this.appendDummyInput().appendField(signature.name);
            signature.inputs.forEach((input) => {
                this.appendValueInput(input.name).setCheck(input.check).appendField(`${input.typeLabel} ${input.name}`);
            });
            this.setPreviousStatement(true, null);
            this.setNextStatement(true, null);
            this.setColour(65);
            this.setTooltip(`Ruft den eigenen Block "${signature.name}" auf.`);
        },
    };
    return blockType;
}
