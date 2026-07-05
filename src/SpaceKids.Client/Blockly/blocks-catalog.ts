import * as Blockly from "blockly/core";
import { getCurrentLocale } from "./locale-state";

/**
 * The real block catalog (§6/§7, docs/04-block-catalog.md) — every SpaceTraders-specific
 * action and information block planned for the first release. No execution behavior
 * here — shape (label, inputs, connections) only. DSL compilation is Milestone 4;
 * custom-block callers are Milestone 9 (see `blocks.ts` for that spike).
 *
 * Milestone 12 (bilingual support): every label/tooltip is `{ de, en }` — the current
 * locale (`locale-state.ts`) is read live inside each block's own `init()`, so switching
 * locale never needs `registerCatalogBlocks()` to run again; it only needs whichever
 * workspace is open to be torn down and recreated (Blockly doesn't relabel
 * already-instantiated blocks), which `blockly-host.ts`'s `setLocale` entry point drives.
 */

interface LocalizedText {
    de: string;
    en: string;
}

function t(text: LocalizedText): string {
    return text[getCurrentLocale()];
}

interface ValueInputSpec {
    /** Blockly input name, e.g. "TRADE_SYMBOL". */
    name: string;
    label: LocalizedText;
}

interface CatalogBlockSpec {
    /** Blockly type identifier — kept English per §7. */
    type: string;
    label: LocalizedText;
    tooltip: LocalizedText;
    inputs: ValueInputSpec[];
}

const ACTION_BLOCKS: CatalogBlockSpec[] = [
    {
        type: "navigate",
        label: { de: "Fliege zu Wegpunkt", en: "Fly to waypoint" },
        tooltip: { de: "Fliegt das ausgewählte Schiff zu einem Wegpunkt.", en: "Flies the selected ship to a waypoint." },
        inputs: [{ name: "DESTINATION", label: { de: "Wegpunkt", en: "Waypoint" } }],
    },
    {
        type: "orbit",
        label: { de: "Gehe in Umlaufbahn", en: "Enter orbit" },
        tooltip: { de: "Bringt das Schiff in die Umlaufbahn.", en: "Puts the ship into orbit." },
        inputs: [],
    },
    {
        type: "dock",
        label: { de: "Docke an", en: "Dock" },
        tooltip: { de: "Lässt das Schiff andocken.", en: "Docks the ship." },
        inputs: [],
    },
    {
        type: "extract",
        label: { de: "Baue Rohstoffe ab", en: "Extract resources" },
        tooltip: { de: "Baut Rohstoffe am aktuellen Asteroidenfeld ab.", en: "Extracts resources at the current asteroid field." },
        inputs: [],
    },
    {
        type: "survey",
        label: { de: "Scanne Asteroidenfeld", en: "Survey asteroid field" },
        tooltip: { de: "Scannt das aktuelle Asteroidenfeld.", en: "Surveys the current asteroid field." },
        inputs: [],
    },
    {
        type: "buyGood",
        label: { de: "Kaufe Ware", en: "Buy goods" },
        tooltip: { de: "Kauft eine Ware auf dem Marktplatz.", en: "Buys a good on the marketplace." },
        inputs: [
            { name: "TRADE_SYMBOL", label: { de: "Ware", en: "Good" } },
            { name: "UNITS", label: { de: "Menge", en: "Units" } },
        ],
    },
    {
        type: "sellGood",
        label: { de: "Verkaufe Ware", en: "Sell goods" },
        tooltip: { de: "Verkauft eine Ware auf dem Marktplatz.", en: "Sells a good on the marketplace." },
        inputs: [
            { name: "TRADE_SYMBOL", label: { de: "Ware", en: "Good" } },
            { name: "UNITS", label: { de: "Menge", en: "Units" } },
        ],
    },
    {
        type: "deliverContract",
        label: { de: "Liefere Fracht", en: "Deliver cargo" },
        tooltip: { de: "Liefert Fracht für einen Auftrag ab.", en: "Delivers cargo for a contract." },
        inputs: [
            { name: "CONTRACT_ID", label: { de: "Auftrag", en: "Contract" } },
            { name: "TRADE_SYMBOL", label: { de: "Ware", en: "Good" } },
            { name: "UNITS", label: { de: "Menge", en: "Units" } },
        ],
    },
    {
        type: "acceptContract",
        label: { de: "Nimm Auftrag an", en: "Accept contract" },
        tooltip: { de: "Nimmt einen Auftrag an.", en: "Accepts a contract." },
        inputs: [{ name: "CONTRACT_ID", label: { de: "Auftrag", en: "Contract" } }],
    },
    {
        type: "purchaseShip",
        label: { de: "Kaufe Schiff", en: "Buy ship" },
        tooltip: { de: "Kauft ein neues Schiff auf einer Werft.", en: "Buys a new ship at a shipyard." },
        inputs: [
            { name: "SHIP_TYPE", label: { de: "Schiffstyp", en: "Ship type" } },
            { name: "WAYPOINT", label: { de: "Wegpunkt", en: "Waypoint" } },
        ],
    },
    {
        type: "refuel",
        label: { de: "Tanke auf", en: "Refuel" },
        tooltip: { de: "Tankt das Schiff auf.", en: "Refuels the ship." },
        inputs: [],
    },
];

const INFO_BLOCKS: CatalogBlockSpec[] = [
    {
        type: "getShipInfo",
        label: { de: "Hole Schiffsinformationen", en: "Get ship info" },
        tooltip: { de: "Gibt Informationen über das ausgewählte Schiff zurück.", en: "Returns information about the selected ship." },
        inputs: [],
    },
    {
        type: "getFleetInfo",
        label: { de: "Hole Flotteninformationen", en: "Get fleet info" },
        tooltip: { de: "Gibt Informationen über die gesamte Flotte zurück.", en: "Returns information about the whole fleet." },
        inputs: [],
    },
    {
        type: "getWaypoints",
        label: { de: "Hole Wegpunkte", en: "Get waypoints" },
        tooltip: { de: "Gibt die Wegpunkte eines Sternensystems zurück.", en: "Returns the waypoints of a star system." },
        inputs: [{ name: "SYSTEM_SYMBOL", label: { de: "Sternensystem", en: "System" } }],
    },
    {
        type: "getMarket",
        label: { de: "Hole Marktdaten", en: "Get market data" },
        tooltip: { de: "Gibt die Marktdaten eines Wegpunkts zurück.", en: "Returns the market data of a waypoint." },
        inputs: [{ name: "WAYPOINT_SYMBOL", label: { de: "Wegpunkt", en: "Waypoint" } }],
    },
    {
        type: "getShipyard",
        label: { de: "Hole Werftdaten", en: "Get shipyard data" },
        tooltip: { de: "Gibt die Werftdaten eines Wegpunkts zurück.", en: "Returns the shipyard data of a waypoint." },
        inputs: [{ name: "WAYPOINT_SYMBOL", label: { de: "Wegpunkt", en: "Waypoint" } }],
    },
    {
        type: "getContracts",
        label: { de: "Hole Auftragsdaten", en: "Get contracts" },
        tooltip: { de: "Gibt die aktuellen Aufträge zurück.", en: "Returns the current contracts." },
        inputs: [],
    },
    {
        type: "getCargo",
        label: { de: "Hole Fracht", en: "Get cargo" },
        tooltip: { de: "Gibt die Fracht des ausgewählten Schiffs zurück.", en: "Returns the cargo of the selected ship." },
        inputs: [],
    },
    {
        type: "getFuel",
        label: { de: "Hole Treibstoff", en: "Get fuel" },
        tooltip: { de: "Gibt den Treibstoffstand des ausgewählten Schiffs zurück.", en: "Returns the fuel level of the selected ship." },
        inputs: [],
    },
    {
        type: "getCredits",
        label: { de: "Hole Credits", en: "Get credits" },
        tooltip: { de: "Gibt den aktuellen Kontostand zurück.", en: "Returns the current account balance." },
        inputs: [],
    },
];

interface AccessorBlockSpec {
    /** Blockly type identifier — kept English per §7. */
    type: string;
    label: LocalizedText;
    tooltip: LocalizedText;
    /** The DSL record field this pulls out (§8) — a canonical English key, decoupled
     * from display language (Milestone 12) — matches
     * `SpaceKids.Server.JobRunner`'s info-read conversion into the `VRecord`. */
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
    { type: "shipName", label: { de: "Name aus Schiff", en: "Name from ship" }, tooltip: { de: "Gibt den Namen eines Schiffs zurück.", en: "Returns a ship's name." }, fieldName: "Name" },
    { type: "shipWaypoint", label: { de: "Wegpunkt aus Schiff", en: "Waypoint from ship" }, tooltip: { de: "Gibt den aktuellen Wegpunkt eines Schiffs zurück.", en: "Returns a ship's current waypoint." }, fieldName: "Waypoint" },
    { type: "shipStatus", label: { de: "Status aus Schiff", en: "Status from ship" }, tooltip: { de: "Gibt den Status eines Schiffs zurück.", en: "Returns a ship's status." }, fieldName: "Status" },
    { type: "shipFuel", label: { de: "Treibstoff aus Schiff", en: "Fuel from ship" }, tooltip: { de: "Gibt den Treibstoffstand eines Schiffs zurück.", en: "Returns a ship's fuel level." }, fieldName: "Fuel" },
    { type: "shipCargoUnits", label: { de: "Frachteinheiten aus Schiff", en: "Cargo units from ship" }, tooltip: { de: "Gibt die belegten Frachteinheiten eines Schiffs zurück.", en: "Returns a ship's used cargo units." }, fieldName: "CargoUnits" },
    { type: "shipCargoCapacity", label: { de: "Frachtkapazität aus Schiff", en: "Cargo capacity from ship" }, tooltip: { de: "Gibt die Frachtkapazität eines Schiffs zurück.", en: "Returns a ship's cargo capacity." }, fieldName: "CargoCapacity" },
    // Fracht (getCargo)
    { type: "cargoUnits", label: { de: "Einheiten aus Fracht", en: "Units from cargo" }, tooltip: { de: "Gibt die belegten Einheiten einer Fracht zurück.", en: "Returns the used units of a cargo." }, fieldName: "Units" },
    { type: "cargoCapacity", label: { de: "Kapazität aus Fracht", en: "Capacity from cargo" }, tooltip: { de: "Gibt die Kapazität einer Fracht zurück.", en: "Returns the capacity of a cargo." }, fieldName: "Capacity" },
    { type: "cargoGoods", label: { de: "Waren aus Fracht", en: "Goods from cargo" }, tooltip: { de: "Gibt die Liste der Waren einer Fracht zurück.", en: "Returns the list of goods in a cargo." }, fieldName: "Goods" },
    // Ware (a Fracht's Waren list item)
    { type: "goodName", label: { de: "Name aus Ware", en: "Name from good" }, tooltip: { de: "Gibt den Namen einer Ware zurück.", en: "Returns a good's name." }, fieldName: "Name" },
    { type: "goodUnits", label: { de: "Einheiten aus Ware", en: "Units from good" }, tooltip: { de: "Gibt die Einheiten einer Ware zurück.", en: "Returns a good's units." }, fieldName: "Units" },
    // Werft (getShipyard)
    { type: "shipyardWaypoint", label: { de: "Wegpunkt aus Werft", en: "Waypoint from shipyard" }, tooltip: { de: "Gibt den Wegpunkt einer Werft zurück.", en: "Returns a shipyard's waypoint." }, fieldName: "Waypoint" },
    { type: "shipyardTypes", label: { de: "Schiffstypen aus Werft", en: "Ship types from shipyard" }, tooltip: { de: "Gibt die Liste der Schiffstypen einer Werft zurück.", en: "Returns the list of ship types at a shipyard." }, fieldName: "Types" },
    // Schiffstyp (a Werft's Schiffstypen list item)
    { type: "shipyardTypeName", label: { de: "Typ aus Schiffstyp", en: "Type from ship type" }, tooltip: { de: "Gibt die Typbezeichnung eines Schiffstyps zurück.", en: "Returns a ship type's designation." }, fieldName: "Type" },
    { type: "shipyardTypePrice", label: { de: "Preis aus Schiffstyp", en: "Price from ship type" }, tooltip: { de: "Gibt den Preis eines Schiffstyps zurück.", en: "Returns a ship type's price." }, fieldName: "Price" },
    // Markt (getMarket)
    { type: "marketWaypoint", label: { de: "Wegpunkt aus Markt", en: "Waypoint from market" }, tooltip: { de: "Gibt den Wegpunkt eines Marktes zurück.", en: "Returns a market's waypoint." }, fieldName: "Waypoint" },
    { type: "marketGoods", label: { de: "Handelswaren aus Markt", en: "Trade goods from market" }, tooltip: { de: "Gibt die Liste der Handelswaren eines Marktes zurück.", en: "Returns the list of trade goods at a market." }, fieldName: "Goods" },
    // Handelsware (a Markt's Handelswaren list item)
    { type: "tradeGoodName", label: { de: "Name aus Handelsware", en: "Name from trade good" }, tooltip: { de: "Gibt den Namen einer Handelsware zurück.", en: "Returns a trade good's name." }, fieldName: "Name" },
    { type: "tradeGoodBuyPrice", label: { de: "Kaufpreis aus Handelsware", en: "Buy price from trade good" }, tooltip: { de: "Gibt den Kaufpreis einer Handelsware zurück.", en: "Returns a trade good's buy price." }, fieldName: "BuyPrice" },
    { type: "tradeGoodSellPrice", label: { de: "Verkaufspreis aus Handelsware", en: "Sell price from trade good" }, tooltip: { de: "Gibt den Verkaufspreis einer Handelsware zurück.", en: "Returns a trade good's sell price." }, fieldName: "SellPrice" },
    // Auftrag (getContracts items)
    { type: "contractId", label: { de: "Id aus Auftrag", en: "Id from contract" }, tooltip: { de: "Gibt die Id eines Auftrags zurück.", en: "Returns a contract's id." }, fieldName: "Id" },
    { type: "contractType", label: { de: "Typ aus Auftrag", en: "Type from contract" }, tooltip: { de: "Gibt den Typ eines Auftrags zurück.", en: "Returns a contract's type." }, fieldName: "Type" },
    { type: "contractAccepted", label: { de: "Angenommen aus Auftrag", en: "Accepted from contract" }, tooltip: { de: "Gibt zurück, ob ein Auftrag angenommen wurde.", en: "Returns whether a contract was accepted." }, fieldName: "Accepted" },
    { type: "contractFulfilled", label: { de: "Erfüllt aus Auftrag", en: "Fulfilled from contract" }, tooltip: { de: "Gibt zurück, ob ein Auftrag erfüllt wurde.", en: "Returns whether a contract was fulfilled." }, fieldName: "Fulfilled" },
    // Wegpunkt (getWaypoints items)
    { type: "waypointSymbolField", label: { de: "Symbol aus Wegpunkt", en: "Symbol from waypoint" }, tooltip: { de: "Gibt das Symbol eines Wegpunkts zurück.", en: "Returns a waypoint's symbol." }, fieldName: "Symbol" },
    { type: "waypointTypeField", label: { de: "Typ aus Wegpunkt", en: "Type from waypoint" }, tooltip: { de: "Gibt den Typ eines Wegpunkts zurück.", en: "Returns a waypoint's type." }, fieldName: "Type" },
];

const ACTION_COLOUR = 160;
const INFO_COLOUR = 230;
const ACCESSOR_COLOUR = 65;

function registerBlock(spec: CatalogBlockSpec, colour: number, asValue: boolean): void {
    Blockly.Blocks[spec.type] = {
        init: function (this: Blockly.Block) {
            this.appendDummyInput().appendField(t(spec.label));
            spec.inputs.forEach((inputSpec) => {
                this.appendValueInput(inputSpec.name).appendField(t(inputSpec.label));
            });
            if (asValue) {
                this.setOutput(true, null);
            } else {
                this.setPreviousStatement(true, null);
                this.setNextStatement(true, null);
            }
            this.setColour(colour);
            this.setTooltip(t(spec.tooltip));
        },
    };
}

/** Catalog accessor blocks read `currentLocale` live in their own `init()` — unlike
 * `registerDynamicAccessorBlock` below (custom-block accessors), whose label is
 * child-authored free text, not fixed catalog vocabulary, and stays locale-independent. */
function registerAccessorBlock(spec: AccessorBlockSpec): void {
    Blockly.Blocks[spec.type] = {
        init: function (this: Blockly.Block) {
            this.appendValueInput("TARGET").appendField(t(spec.label));
            this.setOutput(true, null);
            this.setColour(ACCESSOR_COLOUR);
            this.setTooltip(t(spec.tooltip));
        },
    };
}

/**
 * Registers a single `TARGET`-input/`asValue: true` accessor block, exactly the same
 * shape the fixed §8 accessor blocks above use. Exported for Milestone 9/Part C's
 * per-custom-block structured-output accessors (`accessor_<customBlockId>_<field>`),
 * which are generated dynamically per block rather than declared statically here.
 * Takes a plain string label/tooltip (child-authored field names, out of scope for
 * Milestone 12's bilingual support), unlike the fixed catalog's own `registerAccessorBlock`.
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
