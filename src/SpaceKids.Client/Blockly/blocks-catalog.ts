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

interface AccessorBlockSpec {
    /** Blockly type identifier — kept English per §7. */
    type: string;
    /** German block label, e.g. "Wegpunkt aus Schiff". */
    label: string;
    tooltip: string;
    /** The DSL record field this pulls out (§8) — matches the German key
     * `SpaceKids.Server.JobRunner`'s info-read conversion puts into the `VRecord`. */
    fieldName: string;
}

/**
 * One accessor block per reachable field of every §8 "friendly structured record"
 * (Schiff/Fracht/Ware/Werft/Schiffstyp/Markt/Handelsware/Auftrag/Wegpunkt) — a value
 * block taking the record as its one input ("TARGET") and returning the named field.
 * Milestone 9/Part B: the record types themselves have no other way to reach their
 * fields from a program (§8: "without turning every API response into a complicated
 * object tree" — but the fields must still be reachable one at a time).
 */
const ACCESSOR_BLOCKS: AccessorBlockSpec[] = [
    // Schiff (getShipInfo / getFleetInfo items)
    { type: "shipName", label: "Name aus Schiff", tooltip: "Gibt den Namen eines Schiffs zurück.", fieldName: "Name" },
    { type: "shipWaypoint", label: "Wegpunkt aus Schiff", tooltip: "Gibt den aktuellen Wegpunkt eines Schiffs zurück.", fieldName: "Wegpunkt" },
    { type: "shipStatus", label: "Status aus Schiff", tooltip: "Gibt den Status eines Schiffs zurück.", fieldName: "Status" },
    { type: "shipFuel", label: "Treibstoff aus Schiff", tooltip: "Gibt den Treibstoffstand eines Schiffs zurück.", fieldName: "Treibstoff" },
    { type: "shipCargoUnits", label: "Frachteinheiten aus Schiff", tooltip: "Gibt die belegten Frachteinheiten eines Schiffs zurück.", fieldName: "Frachteinheiten" },
    { type: "shipCargoCapacity", label: "Frachtkapazität aus Schiff", tooltip: "Gibt die Frachtkapazität eines Schiffs zurück.", fieldName: "Frachtkapazität" },
    // Fracht (getCargo)
    { type: "cargoUnits", label: "Einheiten aus Fracht", tooltip: "Gibt die belegten Einheiten einer Fracht zurück.", fieldName: "Einheiten" },
    { type: "cargoCapacity", label: "Kapazität aus Fracht", tooltip: "Gibt die Kapazität einer Fracht zurück.", fieldName: "Kapazität" },
    { type: "cargoGoods", label: "Waren aus Fracht", tooltip: "Gibt die Liste der Waren einer Fracht zurück.", fieldName: "Waren" },
    // Ware (a Fracht's Waren list item)
    { type: "goodName", label: "Name aus Ware", tooltip: "Gibt den Namen einer Ware zurück.", fieldName: "Name" },
    { type: "goodUnits", label: "Einheiten aus Ware", tooltip: "Gibt die Einheiten einer Ware zurück.", fieldName: "Einheiten" },
    // Werft (getShipyard)
    { type: "shipyardWaypoint", label: "Wegpunkt aus Werft", tooltip: "Gibt den Wegpunkt einer Werft zurück.", fieldName: "Wegpunkt" },
    { type: "shipyardTypes", label: "Schiffstypen aus Werft", tooltip: "Gibt die Liste der Schiffstypen einer Werft zurück.", fieldName: "Schiffstypen" },
    // Schiffstyp (a Werft's Schiffstypen list item)
    { type: "shipyardTypeName", label: "Typ aus Schiffstyp", tooltip: "Gibt die Typbezeichnung eines Schiffstyps zurück.", fieldName: "Typ" },
    { type: "shipyardTypePrice", label: "Preis aus Schiffstyp", tooltip: "Gibt den Preis eines Schiffstyps zurück.", fieldName: "Preis" },
    // Markt (getMarket)
    { type: "marketWaypoint", label: "Wegpunkt aus Markt", tooltip: "Gibt den Wegpunkt eines Marktes zurück.", fieldName: "Wegpunkt" },
    { type: "marketGoods", label: "Handelswaren aus Markt", tooltip: "Gibt die Liste der Handelswaren eines Marktes zurück.", fieldName: "Handelswaren" },
    // Handelsware (a Markt's Handelswaren list item)
    { type: "tradeGoodName", label: "Name aus Handelsware", tooltip: "Gibt den Namen einer Handelsware zurück.", fieldName: "Name" },
    { type: "tradeGoodBuyPrice", label: "Kaufpreis aus Handelsware", tooltip: "Gibt den Kaufpreis einer Handelsware zurück.", fieldName: "Kaufpreis" },
    { type: "tradeGoodSellPrice", label: "Verkaufspreis aus Handelsware", tooltip: "Gibt den Verkaufspreis einer Handelsware zurück.", fieldName: "Verkaufspreis" },
    // Auftrag (getContracts items)
    { type: "contractId", label: "Id aus Auftrag", tooltip: "Gibt die Id eines Auftrags zurück.", fieldName: "Id" },
    { type: "contractType", label: "Typ aus Auftrag", tooltip: "Gibt den Typ eines Auftrags zurück.", fieldName: "Typ" },
    { type: "contractAccepted", label: "Angenommen aus Auftrag", tooltip: "Gibt zurück, ob ein Auftrag angenommen wurde.", fieldName: "Angenommen" },
    { type: "contractFulfilled", label: "Erfüllt aus Auftrag", tooltip: "Gibt zurück, ob ein Auftrag erfüllt wurde.", fieldName: "Erfüllt" },
    // Wegpunkt (getWaypoints items)
    { type: "waypointSymbolField", label: "Symbol aus Wegpunkt", tooltip: "Gibt das Symbol eines Wegpunkts zurück.", fieldName: "Symbol" },
    { type: "waypointTypeField", label: "Typ aus Wegpunkt", tooltip: "Gibt den Typ eines Wegpunkts zurück.", fieldName: "Typ" },
];

const ACTION_COLOUR = 160;
const INFO_COLOUR = 230;
const ACCESSOR_COLOUR = 65;

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

function registerAccessorBlock(spec: AccessorBlockSpec): void {
    registerDynamicAccessorBlock(spec.type, spec.label, spec.tooltip);
}

/**
 * Registers a single `TARGET`-input/`asValue: true` accessor block, exactly the same
 * shape the fixed §8 accessor blocks above use. Exported for Milestone 9/Part C's
 * per-custom-block structured-output accessors (`accessor_<customBlockId>_<field>`),
 * which are generated dynamically per block rather than declared statically here.
 */
export function registerDynamicAccessorBlock(blockType: string, label: string, tooltip: string): void {
    Blockly.Blocks[blockType] = {
        init: function (this: Blockly.Block) {
            this.appendValueInput("TARGET").appendField(label);
            this.setOutput(true, null);
            this.setColour(ACCESSOR_COLOUR);
            this.setTooltip(tooltip);
        },
    };
}

/** Registers all 20 SpaceTraders action/information blocks (§6/§7) plus the §8 accessor blocks (Milestone 9/Part B). Idempotent — safe to call once at seam init, same as the other register* functions in blocks.ts. */
export function registerCatalogBlocks(): void {
    ACTION_BLOCKS.forEach((spec) => registerBlock(spec, ACTION_COLOUR, false));
    INFO_BLOCKS.forEach((spec) => registerBlock(spec, INFO_COLOUR, true));
    ACCESSOR_BLOCKS.forEach((spec) => registerAccessorBlock(spec));
}

export const catalogActionBlockTypes: string[] = ACTION_BLOCKS.map((spec) => spec.type);
export const catalogInfoBlockTypes: string[] = INFO_BLOCKS.map((spec) => spec.type);
export const catalogAccessorBlockTypes: string[] = ACCESSOR_BLOCKS.map((spec) => spec.type);
/** Block type -> DSL record field name, consumed by `Compiler.fs`'s `ACCESSOR_BLOCKS` table (kept in sync manually — see docs/04-block-catalog.md). */
export const accessorFieldNames: Record<string, string> = Object.fromEntries(
    ACCESSOR_BLOCKS.map((spec) => [spec.type, spec.fieldName]),
);
