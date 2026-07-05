import * as Blockly from "blockly/core";
import { registerDynamicAccessorBlock } from "./blocks-catalog";
import { getCurrentLocale } from "./locale-state";

/**
 * Non-catalog primitive/structural blocks (§9's Blockwerkstatt machinery): the
 * definition shell (typed, dynamic inputs via a mutator), a generic parameter-getter, a
 * generic caller block shared by every custom block, the "build a record" block for
 * structured outputs (§9 Outputs), plus `sk_show_message`/`sk_wait` (the Milestone 0
 * toolchain spike).
 *
 * Milestone 12 (bilingual support): labels/tooltips read the current locale
 * (`locale-state.ts`) live in each block's own `init()`, same pattern as the catalog.
 */

export function registerTrivialBlocks(): void {
    Blockly.Blocks["sk_show_message"] = {
        init: function (this: Blockly.Block) {
            this.appendValueInput("TEXT")
                .setCheck("String")
                .appendField(getCurrentLocale() === "en" ? "Show message" : "Zeige Nachricht");
            this.setPreviousStatement(true, null);
            this.setNextStatement(true, null);
            this.setColour(210);
            this.setTooltip(
                getCurrentLocale() === "en" ? "Shows a message in the log." : "Zeigt eine Nachricht im Logbuch an.",
            );
        },
    };

    Blockly.Blocks["sk_wait"] = {
        init: function (this: Blockly.Block) {
            this.appendValueInput("SECONDS")
                .setCheck("Number")
                .appendField(getCurrentLocale() === "en" ? "Wait" : "Warte");
            this.appendDummyInput().appendField(getCurrentLocale() === "en" ? "seconds" : "Sekunden");
            this.setPreviousStatement(true, null);
            this.setNextStatement(true, null);
            this.setColour(210);
            this.setTooltip(
                getCurrentLocale() === "en" ? "Waits for a given number of seconds." : "Wartet eine bestimmte Anzahl Sekunden.",
            );
        },
    };
}

/** One custom block input's shape (§9c). All six input types share this one Blockly
 * mutator-arg block (a type dropdown + a name field) rather than six separate block
 * types — inputs stay untyped value sockets with a text label only, consistent with
 * this project's existing "plain value sockets, not typed" simplification for the
 * fixed 20-block catalog (docs/05-agent-handoff.md).
 *
 * `typeLabel` is purely decorative (no runtime type-checking depends on it) and is
 * persisted as-is once a custom block is saved — switching locale after saving does not
 * retroactively re-translate an already-saved input's stored `typeLabel`, only which
 * options are offered when adding a *new* one (a known, documented simplification,
 * same class as Milestone 11's "existing dev-stage rows aren't migrated"). */
export interface CustomBlockInputSpec {
    name: string;
    typeLabel: string;
}

const INPUT_TYPE_LABELS_DE = ["Schiff", "Wegpunkt", "Ware", "Anzahl", "Preisgrenze", "Liste"] as const;
const INPUT_TYPE_LABELS_EN = ["Ship", "Waypoint", "Good", "Number", "Price limit", "List"] as const;

export function inputTypeLabels(): readonly string[] {
    return getCurrentLocale() === "en" ? INPUT_TYPE_LABELS_EN : INPUT_TYPE_LABELS_DE;
}

/** Milestone 13: a stored `typeLabel` may be either German or English text
 * (whichever locale was active when the input was created — switching locale
 * afterward doesn't retroactively re-translate an already-saved input, per the
 * doc comment on `CustomBlockInputSpec` above), so this maps both variants to
 * the same Blockly check type rather than assuming one locale. */
const TYPE_LABEL_TO_CHECK: Record<string, string> = {
    Schiff: "String",
    Ship: "String",
    Wegpunkt: "String",
    Waypoint: "String",
    Ware: "String",
    Good: "String",
    Anzahl: "Number",
    Number: "Number",
    Preisgrenze: "Number",
    "Price limit": "Number",
    Liste: "List",
    List: "List",
};

function checkForTypeLabel(typeLabel: string): string | null {
    return TYPE_LABEL_TO_CHECK[typeLabel] ?? null;
}

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
            const en = getCurrentLocale() === "en";
            this.appendDummyInput("NAME_ROW")
                .appendField(en ? "Custom block" : "Eigener Block")
                .appendField(new Blockly.FieldTextInput(en ? "My block" : "Mein Block"), "BLOCK_NAME");
            this.appendStatementInput("BODY").setCheck(null).appendField(en ? "Body" : "Inhalt");
            this.appendValueInput("RETURN").setCheck(null).appendField(en ? "Result" : "Ergebnis");
            this.setColour(290);
            this.setTooltip(
                en
                    ? "Defines the logic of a custom, reusable block."
                    : "Definiert die Logik eines eigenen, wiederverwendbaren Blocks.",
            );
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
                newInputs.push({ name: b.skArgName ?? "eingabe", typeLabel: b.skArgType ?? inputTypeLabels()[3]! });
                argBlock = argBlock.nextConnection && argBlock.nextConnection.targetBlock();
            }
            (this as unknown as { skInputs: CustomBlockInputSpec[] }).skInputs = newInputs;
            rebuildInputRow(this as unknown as Blockly.BlockSvg);
        },
    };

    Blockly.Blocks["sk_custom_block_def_mutator_container"] = {
        init: function (this: Blockly.Block) {
            const en = getCurrentLocale() === "en";
            this.appendDummyInput().appendField(en ? "Inputs" : "Eingaben");
            this.appendStatementInput("STACK");
            this.setColour(290);
            this.setTooltip(
                en
                    ? "Inputs of the custom block (order = order in the block)."
                    : "Eingaben des eigenen Blocks (Reihenfolge = Reihenfolge im Block).",
            );
            this.contextMenu = false;
        },
    };

    Blockly.Blocks["sk_custom_block_def_mutator_arg"] = {
        init: function (this: Blockly.Block) {
            this.appendDummyInput()
                .appendField(new Blockly.FieldDropdown(() => inputTypeLabels().map((t) => [t, t] as [string, string])), "ARG_TYPE")
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
    const en = getCurrentLocale() === "en";
    inputs.forEach((input, index) => {
        block.appendDummyInput(`INPUT_${index}`).appendField(`${en ? "Input" : "Eingabe"}: ${input.typeLabel} ${input.name}`);
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
                .appendField(getCurrentLocale() === "en" ? "Input:" : "Eingabe:")
                .appendField(new Blockly.FieldDropdown(() => currentWorkshopParamOptions(this)), "PARAM_NAME");
            this.setOutput(true, null);
            this.setColour(65);
            this.setTooltip(
                getCurrentLocale() === "en"
                    ? "Reads the value of one of the custom block's inputs."
                    : "Liest den Wert einer Eingabe des eigenen Blocks.",
            );
        },
        // Milestone 13: the output's check type depends on *which* parameter is
        // currently selected, so it's recomputed on every change rather than set
        // once in `init()` — reads the same `skInputs` the dropdown's own options
        // come from.
        onchange: function (this: Blockly.Block) {
            const defBlock = this.workspace.getBlocksByType("sk_custom_block_def", false)[0];
            const inputs = defBlock ? ((defBlock as unknown as { skInputs: CustomBlockInputSpec[] }).skInputs ?? []) : [];
            const selected = this.getFieldValue("PARAM_NAME");
            const match = inputs.find((i) => i.name === selected);
            this.setOutput(true, match ? checkForTypeLabel(match.typeLabel) : null);
        },
    };
}

function currentWorkshopParamOptions(block: Blockly.Block): [string, string][] {
    const defBlock = block.workspace.getBlocksByType("sk_custom_block_def", false)[0];
    const inputs = defBlock ? ((defBlock as unknown as { skInputs: CustomBlockInputSpec[] }).skInputs ?? []) : [];
    const none = getCurrentLocale() === "en" ? "(no inputs)" : "(keine Eingaben)";
    return inputs.length > 0 ? inputs.map((i) => [i.name, i.name] as [string, string]) : [[none, ""]];
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
            this.appendDummyInput("HEADER").appendField(getCurrentLocale() === "en" ? "Record" : "Datensatz");
            this.setOutput(true, null);
            this.setColour(65);
            this.setTooltip(
                getCurrentLocale() === "en"
                    ? "Builds a record with named fields (the result of a custom block)."
                    : "Baut einen Datensatz mit benannten Feldern (Ergebnis eines eigenen Blocks).",
            );
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
                newFields.push({ name: b.skFieldName ?? (getCurrentLocale() === "en" ? "field" : "Feld") });
                argBlock = argBlock.nextConnection && argBlock.nextConnection.targetBlock();
            }
            (this as unknown as { skFields: RecordFieldSpec[] }).skFields = newFields;
            rebuildRecordInputs(this as unknown as Blockly.BlockSvg);
        },
    };

    Blockly.Blocks["sk_build_record_mutator_container"] = {
        init: function (this: Blockly.Block) {
            const en = getCurrentLocale() === "en";
            this.appendDummyInput().appendField(en ? "Fields" : "Felder");
            this.appendStatementInput("STACK");
            this.setColour(65);
            this.setTooltip(
                en
                    ? "Fields of the record (order = order in the block)."
                    : "Felder des Datensatzes (Reihenfolge = Reihenfolge im Block).",
            );
            this.contextMenu = false;
        },
    };

    Blockly.Blocks["sk_build_record_mutator_arg"] = {
        init: function (this: Blockly.Block) {
            this.appendDummyInput()
                .appendField(getCurrentLocale() === "en" ? "Field" : "Feld")
                .appendField(new Blockly.FieldTextInput(getCurrentLocale() === "en" ? "field" : "feld"), "FIELD_NAME");
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
    const en = getCurrentLocale() === "en";

    const previousArgNames = (block as unknown as { skArgNames?: string[] }).skArgNames ?? [];
    for (const name of previousArgNames) {
        if (block.getInput(name)) {
            block.removeInput(name);
        }
    }
    if (block.getInput("HEADER")) {
        block.removeInput("HEADER");
    }

    const label = signature ? signature.name : en ? `Unknown block (${customBlockId})` : `Unbekannter Block (${customBlockId})`;
    block.appendDummyInput("HEADER").appendField(label);

    const inputs = signature?.inputs ?? [];
    inputs.forEach((input) => {
        block.appendValueInput(input.name).setCheck(checkForTypeLabel(input.typeLabel)).appendField(`${input.typeLabel} ${input.name}`);
    });
    (block as unknown as { skArgNames: string[] }).skArgNames = inputs.map((i) => i.name);
    block.setTooltip(
        signature
            ? en
                ? `Calls the custom block "${signature.name}".`
                : `Ruft den eigenen Block "${signature.name}" auf.`
            : en
              ? "Custom block not found."
              : "Eigener Block nicht gefunden.",
    );

    const hasOutput = signature?.hasOutput ?? false;
    // Milestone 13: a structured-output custom block (§9 Outputs) gets a synthetic
    // check tying its own output to its matching dynamic accessors' TARGET check
    // (`registerCustomBlockAccessors` below) — a plain-value output's static type
    // isn't known (whatever expression the author plugged into "Ergebnis"), so it
    // stays unchecked, same as before.
    const outputCheck = signature && signature.outputFields ? `CustomRecord_${signature.id}` : null;
    if (hasOutput && !block.outputConnection) {
        block.setPreviousStatement(false);
        block.setNextStatement(false);
        block.setOutput(true, outputCheck);
    } else if (hasOutput && block.outputConnection) {
        block.setOutput(true, outputCheck);
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
        const en = getCurrentLocale() === "en";
        registerDynamicAccessorBlock(
            blockType,
            en ? `${field} from ${signature.name}` : `${field} aus ${signature.name}`,
            en ? `Returns the field "${field}" from "${signature.name}".` : `Gibt das Feld "${field}" aus "${signature.name}" zurück.`,
            `CustomRecord_${signature.id}`,
        );
        return blockType;
    });
}
