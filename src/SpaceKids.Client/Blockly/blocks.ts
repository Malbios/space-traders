import * as Blockly from "blockly/core";
import { registerDynamicAccessorBlock } from "./blocks-catalog";

/**
 * German primitive blocks (§7) plus the real custom-block mechanics (§9, Milestone 9).
 * `sk_show_message`/`sk_wait` are the Milestone 0 toolchain spike; everything else
 * here is the real Blockwerkstatt machinery — the definition shell (typed, dynamic
 * inputs via a mutator), a generic parameter-getter, a generic caller block shared by
 * every custom block, and the "build a record" block for structured outputs (§9
 * Outputs).
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

/**
 * One custom block input's shape (§9c). All six input types share this one Blockly
 * mutator-arg block (a type dropdown + a name field) rather than six separate block
 * types — inputs stay untyped value sockets with a text label only, consistent with
 * this project's existing "plain value sockets, not typed" simplification for the
 * fixed 20-block catalog (docs/05-agent-handoff.md).
 */
export interface CustomBlockInputSpec {
    name: string;
    typeLabel: string;
}

export const INPUT_TYPE_LABELS = ["Schiff", "Wegpunkt", "Ware", "Anzahl", "Preisgrenze", "Liste"] as const;

export interface CustomBlockSignature {
    id: string;
    name: string;
    inputs: CustomBlockInputSpec[];
    /** Whether the definition's return-value socket has anything plugged in. */
    hasOutput: boolean;
    /** Field names (declaration order) when the return value is an `sk_build_record`
     * block (§9 Outputs) — `undefined` for a plain-value or void return. */
    outputFields?: string[];
}

interface DefBlockExtraState {
    inputs: CustomBlockInputSpec[];
}

/**
 * The one-and-only "Eigener Block" definition shell block type (§9b/§9c). A real
 * workshop has exactly one of these: a name field, a mutator (gear icon) for
 * add/remove/rename/retype inputs (all six §9c types), a statement body, and a
 * return-value socket (a plain value, or an `sk_build_record` block for structured
 * outputs).
 */
export function registerDefinitionShellBlock(): void {
    Blockly.Blocks["sk_custom_block_def"] = {
        init: function (this: Blockly.Block) {
            this.appendDummyInput("NAME_ROW").appendField("Eigener Block").appendField(new Blockly.FieldTextInput("Mein Block"), "BLOCK_NAME");
            this.appendStatementInput("BODY").setCheck(null).appendField("Inhalt");
            this.appendValueInput("RETURN").setCheck(null).appendField("Ergebnis");
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
            for (const input of inputs) {
                const argBlock = workspace.newBlock("sk_custom_block_def_mutator_arg");
                (argBlock as Blockly.BlockSvg).initSvg?.();
                argBlock.setFieldValue(input.typeLabel, "ARG_TYPE");
                argBlock.setFieldValue(input.name, "ARG_NAME");
                connection.connect(argBlock.previousConnection!);
                connection = argBlock.nextConnection!;
            }
            return containerBlock;
        },
        compose: function (this: Blockly.Block, containerBlock: Blockly.Block) {
            const newInputs: CustomBlockInputSpec[] = [];
            let argBlock = containerBlock.getInputTargetBlock("STACK");
            while (argBlock) {
                const b = argBlock as unknown as { skArgName?: string; skArgType?: string };
                newInputs.push({ name: b.skArgName ?? "eingabe", typeLabel: b.skArgType ?? "Anzahl" });
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
            this.appendDummyInput()
                .appendField(new Blockly.FieldDropdown(INPUT_TYPE_LABELS.map((t) => [t, t] as [string, string])), "ARG_TYPE")
                .appendField(new Blockly.FieldTextInput("n"), "ARG_NAME");
            this.setPreviousStatement(true);
            this.setNextStatement(true);
            this.setColour(290);
            this.contextMenu = false;
        },
        onchange: function (this: Blockly.Block) {
            (this as unknown as { skArgName: string; skArgType: string }).skArgName = this.getFieldValue("ARG_NAME");
            (this as unknown as { skArgType: string }).skArgType = this.getFieldValue("ARG_TYPE");
        },
    };

    registerParamGetBlock();
    registerBuildRecordBlock();
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

/** A value block reading one of the current workshop's own inputs (§9c: "Eingabe:
 * Schiff"). One generic block type, not one per parameter — its dropdown options are
 * computed from whichever `sk_custom_block_def` block currently lives in the same
 * workspace, since a workshop only ever has exactly one. */
function registerParamGetBlock(): void {
    Blockly.Blocks["sk_param_get"] = {
        init: function (this: Blockly.Block) {
            this.appendDummyInput()
                .appendField("Eingabe:")
                .appendField(new Blockly.FieldDropdown(() => currentWorkshopParamOptions(this)), "PARAM_NAME");
            this.setOutput(true, null);
            this.setColour(65);
            this.setTooltip("Liest den Wert einer Eingabe des eigenen Blocks.");
        },
    };
}

function currentWorkshopParamOptions(block: Blockly.Block): [string, string][] {
    const defBlock = block.workspace.getBlocksByType("sk_custom_block_def", false)[0];
    const inputs = defBlock ? ((defBlock as unknown as { skInputs: CustomBlockInputSpec[] }).skInputs ?? []) : [];
    return inputs.length > 0 ? inputs.map((i) => [i.name, i.name] as [string, string]) : [["(keine Eingaben)", ""]];
}

interface RecordFieldSpec {
    name: string;
}

interface BuildRecordExtraState {
    fields: RecordFieldSpec[];
}

/**
 * "Datensatz" (§9 Outputs, Milestone 9/Part C) — plugs into a definition block's
 * return-value socket to produce a structured record (e.g. "Marktinfo") instead of a
 * plain value. Field names declared here become the per-custom-block accessor blocks
 * (`accessor_<customBlockId>_<field>`) generated when the signature is published
 * (see `blockly-host.ts`'s `publishCustomBlockSignature`).
 */
function registerBuildRecordBlock(): void {
    Blockly.Blocks["sk_build_record"] = {
        init: function (this: Blockly.Block) {
            this.appendDummyInput("HEADER").appendField("Datensatz");
            this.setOutput(true, null);
            this.setColour(65);
            this.setTooltip("Baut einen Datensatz mit benannten Feldern (Ergebnis eines eigenen Blocks).");
            this.setMutator(new Blockly.icons.MutatorIcon(["sk_build_record_mutator_arg"], this as unknown as Blockly.BlockSvg));
            (this as unknown as { skFields: RecordFieldSpec[] }).skFields = [];
        },
        saveExtraState: function (this: Blockly.Block): BuildRecordExtraState {
            return { fields: (this as unknown as { skFields: RecordFieldSpec[] }).skFields ?? [] };
        },
        loadExtraState: function (this: Blockly.Block, state: BuildRecordExtraState) {
            (this as unknown as { skFields: RecordFieldSpec[] }).skFields = state.fields ?? [];
            rebuildRecordInputs(this as unknown as Blockly.BlockSvg);
        },
        decompose: function (this: Blockly.Block, workspace: Blockly.Workspace): Blockly.Block {
            const containerBlock = workspace.newBlock("sk_build_record_mutator_container");
            (containerBlock as Blockly.BlockSvg).initSvg?.();
            let connection = containerBlock.getInput("STACK")!.connection!;
            const fields = (this as unknown as { skFields: RecordFieldSpec[] }).skFields ?? [];
            for (const field of fields) {
                const argBlock = workspace.newBlock("sk_build_record_mutator_arg");
                (argBlock as Blockly.BlockSvg).initSvg?.();
                argBlock.setFieldValue(field.name, "FIELD_NAME");
                connection.connect(argBlock.previousConnection!);
                connection = argBlock.nextConnection!;
            }
            return containerBlock;
        },
        compose: function (this: Blockly.Block, containerBlock: Blockly.Block) {
            const newFields: RecordFieldSpec[] = [];
            let argBlock = containerBlock.getInputTargetBlock("STACK");
            while (argBlock) {
                const b = argBlock as unknown as { skFieldName?: string };
                newFields.push({ name: b.skFieldName ?? "Feld" });
                argBlock = argBlock.nextConnection && argBlock.nextConnection.targetBlock();
            }
            (this as unknown as { skFields: RecordFieldSpec[] }).skFields = newFields;
            rebuildRecordInputs(this as unknown as Blockly.BlockSvg);
        },
    };

    Blockly.Blocks["sk_build_record_mutator_container"] = {
        init: function (this: Blockly.Block) {
            this.appendDummyInput().appendField("Felder");
            this.appendStatementInput("STACK");
            this.setColour(65);
            this.setTooltip("Felder des Datensatzes (Reihenfolge = Reihenfolge im Block).");
            this.contextMenu = false;
        },
    };

    Blockly.Blocks["sk_build_record_mutator_arg"] = {
        init: function (this: Blockly.Block) {
            this.appendDummyInput().appendField("Feld").appendField(new Blockly.FieldTextInput("feld"), "FIELD_NAME");
            this.setPreviousStatement(true);
            this.setNextStatement(true);
            this.setColour(65);
            this.contextMenu = false;
        },
        onchange: function (this: Blockly.Block) {
            (this as unknown as { skFieldName: string }).skFieldName = this.getFieldValue("FIELD_NAME");
        },
    };
}

function rebuildRecordInputs(block: Blockly.BlockSvg): void {
    let i = 0;
    while (block.getInput(`FIELD_${i}`)) {
        block.removeInput(`FIELD_${i}`);
        i++;
    }
    const fields = (block as unknown as { skFields: RecordFieldSpec[] }).skFields ?? [];
    fields.forEach((field, index) => {
        block.appendValueInput(`FIELD_${index}`).appendField(field.name);
    });
}

function readRecordFieldNames(block: Blockly.Block): string[] {
    const fields = (block as unknown as { skFields: RecordFieldSpec[] }).skFields ?? [];
    return fields.map((f) => f.name);
}

export function readSignature(block: Blockly.Block, id: string): CustomBlockSignature {
    const name = block.getFieldValue("BLOCK_NAME") as string;
    const inputs = (block as unknown as { skInputs: CustomBlockInputSpec[] }).skInputs ?? [];
    const returnTarget = block.getInputTargetBlock("RETURN");
    const hasOutput = returnTarget !== null;
    const outputFields = returnTarget && returnTarget.type === "sk_build_record" ? readRecordFieldNames(returnTarget) : undefined;
    return { id, name, inputs, hasOutput, outputFields };
}

const signatureCache = new Map<string, CustomBlockSignature>();

export function registerSignature(signature: CustomBlockSignature): void {
    signatureCache.set(signature.id, signature);
}

export function getSignature(customBlockId: string): CustomBlockSignature | undefined {
    return signatureCache.get(customBlockId);
}

/**
 * One generic caller block type (`callCustomBlock`, §9b) shared by every custom
 * block — replacing the earlier Milestone 0 mini-spike's `sk_call_<id>`-per-block
 * scheme, which the real compiler (`Compiler.fs`'s `compileCustomBlockCall`) never
 * used. Each instance rebuilds its own visible shape (name label, one value input per
 * parameter — named by the parameter's own name, matching what the compiler looks up
 * — and either an output connection or previous/next statement connections) from its
 * own stored `customBlockId`, looked up in the client-side `signatureCache` populated
 * by `publishCustomBlockSignature`.
 */
export function registerCallerBlock(): void {
    Blockly.Blocks["callCustomBlock"] = {
        init: function (this: Blockly.Block) {
            this.setColour(65);
            this.setPreviousStatement(true, null);
            this.setNextStatement(true, null);
        },
        saveExtraState: function (this: Blockly.Block): { customBlockId: string } {
            return { customBlockId: (this as unknown as { skCustomBlockId: string }).skCustomBlockId };
        },
        loadExtraState: function (this: Blockly.Block, state: { customBlockId: string }) {
            (this as unknown as { skCustomBlockId: string }).skCustomBlockId = state.customBlockId;
            rebuildCallerShape(this, state.customBlockId);
        },
    };
}

function rebuildCallerShape(block: Blockly.Block, customBlockId: string): void {
    const signature = signatureCache.get(customBlockId);

    const previousArgNames = (block as unknown as { skArgNames?: string[] }).skArgNames ?? [];
    for (const name of previousArgNames) {
        if (block.getInput(name)) {
            block.removeInput(name);
        }
    }
    if (block.getInput("HEADER")) {
        block.removeInput("HEADER");
    }

    const label = signature ? signature.name : `Unbekannter Block (${customBlockId})`;
    block.appendDummyInput("HEADER").appendField(label);

    const inputs = signature?.inputs ?? [];
    inputs.forEach((input) => {
        block.appendValueInput(input.name).appendField(`${input.typeLabel} ${input.name}`);
    });
    (block as unknown as { skArgNames: string[] }).skArgNames = inputs.map((i) => i.name);
    block.setTooltip(signature ? `Ruft den eigenen Block "${signature.name}" auf.` : "Eigener Block nicht gefunden.");

    const hasOutput = signature?.hasOutput ?? false;
    if (hasOutput && !block.outputConnection) {
        block.setPreviousStatement(false);
        block.setNextStatement(false);
        block.setOutput(true, null);
    } else if (!hasOutput && block.outputConnection) {
        block.setOutput(false);
        block.setPreviousStatement(true, null);
        block.setNextStatement(true, null);
    }
}

/** Registers one dynamic accessor block per structured-output field of a custom
 * block (§9 Outputs, Milestone 9/Part C) — same `TARGET`-input/`asValue: true` shape
 * as the fixed §8 accessor blocks, generated per field rather than declared
 * statically. */
export function registerCustomBlockAccessors(signature: CustomBlockSignature): string[] {
    if (!signature.outputFields || signature.outputFields.length === 0) {
        return [];
    }
    return signature.outputFields.map((field) => {
        const blockType = `accessor_${signature.id}_${field}`;
        registerDynamicAccessorBlock(blockType, `${field} aus ${signature.name}`, `Gibt das Feld "${field}" aus "${signature.name}" zurück.`);
        return blockType;
    });
}
