import * as Blockly from "blockly/core";

/**
 * The real German block catalog (§6/§7, docs/04-block-catalog.md) — every
 * SpaceTraders-specific action and information block planned for the first release.
 * No execution behavior here — shape (label, inputs, connections) only. DSL compilation
 * is Milestone 4; custom-block callers are Milestone 9 (see `blocks.ts` for that spike).
 */

interface ValueInputSpec {
    /** Blockly input name, e.g. "TRADE_SYMBOL". */
    name: string;
    /** German field label shown next to the input socket, e.g. "Ware". */
    label: string;
}

interface CatalogBlockSpec {
    /** Blockly type identifier — kept English per §7. */
    type: string;
    /** German block label. */
    label: string;
    tooltip: string;
    inputs: ValueInputSpec[];
}

const ACTION_BLOCKS: CatalogBlockSpec[] = [
    { type: "navigate", label: "Fliege zu Wegpunkt", tooltip: "Fliegt das ausgewählte Schiff zu einem Wegpunkt.", inputs: [{ name: "DESTINATION", label: "Wegpunkt" }] },
    { type: "orbit", label: "Gehe in Umlaufbahn", tooltip: "Bringt das Schiff in die Umlaufbahn.", inputs: [] },
    { type: "dock", label: "Docke an", tooltip: "Lässt das Schiff andocken.", inputs: [] },
    { type: "extract", label: "Baue Rohstoffe ab", tooltip: "Baut Rohstoffe am aktuellen Asteroidenfeld ab.", inputs: [] },
    { type: "survey", label: "Scanne Asteroidenfeld", tooltip: "Scannt das aktuelle Asteroidenfeld.", inputs: [] },
    {
        type: "buyGood",
        label: "Kaufe Ware",
        tooltip: "Kauft eine Ware auf dem Marktplatz.",
        inputs: [
            { name: "TRADE_SYMBOL", label: "Ware" },
            { name: "UNITS", label: "Menge" },
        ],
    },
    {
        type: "sellGood",
        label: "Verkaufe Ware",
        tooltip: "Verkauft eine Ware auf dem Marktplatz.",
        inputs: [
            { name: "TRADE_SYMBOL", label: "Ware" },
            { name: "UNITS", label: "Menge" },
        ],
    },
    {
        type: "deliverContract",
        label: "Liefere Fracht",
        tooltip: "Liefert Fracht für einen Auftrag ab.",
        inputs: [
            { name: "CONTRACT_ID", label: "Auftrag" },
            { name: "TRADE_SYMBOL", label: "Ware" },
            { name: "UNITS", label: "Menge" },
        ],
    },
    { type: "acceptContract", label: "Nimm Auftrag an", tooltip: "Nimmt einen Auftrag an.", inputs: [{ name: "CONTRACT_ID", label: "Auftrag" }] },
    {
        type: "purchaseShip",
        label: "Kaufe Schiff",
        tooltip: "Kauft ein neues Schiff auf einer Werft.",
        inputs: [
            { name: "SHIP_TYPE", label: "Schiffstyp" },
            { name: "WAYPOINT", label: "Wegpunkt" },
        ],
    },
    { type: "refuel", label: "Tanke auf", tooltip: "Tankt das Schiff auf.", inputs: [] },
];

const INFO_BLOCKS: CatalogBlockSpec[] = [
    { type: "getShipInfo", label: "Hole Schiffsinformationen", tooltip: "Gibt Informationen über das ausgewählte Schiff zurück.", inputs: [] },
    { type: "getFleetInfo", label: "Hole Flotteninformationen", tooltip: "Gibt Informationen über die gesamte Flotte zurück.", inputs: [] },
    { type: "getWaypoints", label: "Hole Wegpunkte", tooltip: "Gibt die Wegpunkte eines Sternensystems zurück.", inputs: [{ name: "SYSTEM_SYMBOL", label: "Sternensystem" }] },
    { type: "getMarket", label: "Hole Marktdaten", tooltip: "Gibt die Marktdaten eines Wegpunkts zurück.", inputs: [{ name: "WAYPOINT_SYMBOL", label: "Wegpunkt" }] },
    { type: "getShipyard", label: "Hole Werftdaten", tooltip: "Gibt die Werftdaten eines Wegpunkts zurück.", inputs: [{ name: "WAYPOINT_SYMBOL", label: "Wegpunkt" }] },
    { type: "getContracts", label: "Hole Auftragsdaten", tooltip: "Gibt die aktuellen Aufträge zurück.", inputs: [] },
    { type: "getCargo", label: "Hole Fracht", tooltip: "Gibt die Fracht des ausgewählten Schiffs zurück.", inputs: [] },
    { type: "getFuel", label: "Hole Treibstoff", tooltip: "Gibt den Treibstoffstand des ausgewählten Schiffs zurück.", inputs: [] },
    { type: "getCredits", label: "Hole Credits", tooltip: "Gibt den aktuellen Kontostand zurück.", inputs: [] },
];

const ACTION_COLOUR = 160;
const INFO_COLOUR = 230;

function registerBlock(spec: CatalogBlockSpec, colour: number, asValue: boolean): void {
    Blockly.Blocks[spec.type] = {
        init: function (this: Blockly.Block) {
            this.appendDummyInput().appendField(spec.label);
            spec.inputs.forEach((inputSpec) => {
                this.appendValueInput(inputSpec.name).appendField(inputSpec.label);
            });
            if (asValue) {
                this.setOutput(true, null);
            } else {
                this.setPreviousStatement(true, null);
                this.setNextStatement(true, null);
            }
            this.setColour(colour);
            this.setTooltip(spec.tooltip);
        },
    };
}

/** Registers all 20 SpaceTraders action/information blocks (§6/§7). Idempotent — safe to call once at seam init, same as the other register* functions in blocks.ts. */
export function registerCatalogBlocks(): void {
    ACTION_BLOCKS.forEach((spec) => registerBlock(spec, ACTION_COLOUR, false));
    INFO_BLOCKS.forEach((spec) => registerBlock(spec, INFO_COLOUR, true));
}

export const catalogActionBlockTypes: string[] = ACTION_BLOCKS.map((spec) => spec.type);
export const catalogInfoBlockTypes: string[] = INFO_BLOCKS.map((spec) => spec.type);
