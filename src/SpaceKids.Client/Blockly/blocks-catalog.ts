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
    /** Milestone 13: Blockly connection-check type ("Number"/"String"/"Boolean"/
     * "List") matching what `JobRunner.fs`'s action/info handlers actually expect —
     * physically prevents plugging a wrong-shaped block into this socket. */
    check: string;
}

interface CatalogBlockSpec {
    /** Blockly type identifier — kept English per §7. */
    type: string;
    label: LocalizedText;
    tooltip: LocalizedText;
    inputs: ValueInputSpec[];
    /** Milestone 13: check type for this block's own output (info blocks only —
     * `null` for statement-position action blocks, which have no output at all). */
    outputCheck: string | null;
}

const ACTION_BLOCKS: CatalogBlockSpec[] = [
    {
        type: "navigate",
        label: { de: "Fliege zu Wegpunkt", en: "Fly to waypoint" },
        tooltip: { de: "Fliegt das ausgewählte Schiff zu einem Wegpunkt.", en: "Flies the selected ship to a waypoint." },
        inputs: [{ name: "DESTINATION", label: { de: "Wegpunkt", en: "Waypoint" }, check: "String" }],
        outputCheck: null,
    },
    {
        type: "orbit",
        label: { de: "Gehe in Umlaufbahn", en: "Enter orbit" },
        tooltip: { de: "Bringt das Schiff in die Umlaufbahn.", en: "Puts the ship into orbit." },
        inputs: [],
        outputCheck: null,
    },
    {
        type: "dock",
        label: { de: "Docke an", en: "Dock" },
        tooltip: { de: "Lässt das Schiff andocken.", en: "Docks the ship." },
        inputs: [],
        outputCheck: null,
    },
    {
        type: "extract",
        label: { de: "Baue Rohstoffe ab", en: "Extract resources" },
        tooltip: { de: "Baut Rohstoffe am aktuellen Asteroidenfeld ab.", en: "Extracts resources at the current asteroid field." },
        inputs: [],
        outputCheck: null,
    },
    {
        type: "survey",
        label: { de: "Scanne Asteroidenfeld", en: "Survey asteroid field" },
        tooltip: { de: "Scannt das aktuelle Asteroidenfeld.", en: "Surveys the current asteroid field." },
        inputs: [],
        outputCheck: null,
    },
    {
        type: "buyGood",
        label: { de: "Kaufe Ware", en: "Buy goods" },
        tooltip: { de: "Kauft eine Ware auf dem Marktplatz.", en: "Buys a good on the marketplace." },
        inputs: [
            { name: "TRADE_SYMBOL", label: { de: "Ware", en: "Good" }, check: "String" },
            { name: "UNITS", label: { de: "Menge", en: "Units" }, check: "Number" },
        ],
        outputCheck: null,
    },
    {
        type: "sellGood",
        label: { de: "Verkaufe Ware", en: "Sell goods" },
        tooltip: { de: "Verkauft eine Ware auf dem Marktplatz.", en: "Sells a good on the marketplace." },
        inputs: [
            { name: "TRADE_SYMBOL", label: { de: "Ware", en: "Good" }, check: "String" },
            { name: "UNITS", label: { de: "Menge", en: "Units" }, check: "Number" },
        ],
        outputCheck: null,
    },
    {
        type: "deliverContract",
        label: { de: "Liefere Fracht", en: "Deliver cargo" },
        tooltip: { de: "Liefert Fracht für einen Auftrag ab.", en: "Delivers cargo for a contract." },
        inputs: [
            { name: "CONTRACT_ID", label: { de: "Auftrag", en: "Contract" }, check: "String" },
            { name: "TRADE_SYMBOL", label: { de: "Ware", en: "Good" }, check: "String" },
            { name: "UNITS", label: { de: "Menge", en: "Units" }, check: "Number" },
        ],
        outputCheck: null,
    },
    {
        type: "acceptContract",
        label: { de: "Nimm Auftrag an", en: "Accept contract" },
        tooltip: { de: "Nimmt einen Auftrag an.", en: "Accepts a contract." },
        inputs: [{ name: "CONTRACT_ID", label: { de: "Auftrag", en: "Contract" }, check: "String" }],
        outputCheck: null,
    },
    {
        type: "purchaseShip",
        label: { de: "Kaufe Schiff", en: "Buy ship" },
        tooltip: { de: "Kauft ein neues Schiff auf einer Werft.", en: "Buys a new ship at a shipyard." },
        inputs: [
            { name: "SHIP_TYPE", label: { de: "Schiffstyp", en: "Ship type" }, check: "String" },
            { name: "WAYPOINT", label: { de: "Wegpunkt", en: "Waypoint" }, check: "String" },
        ],
        outputCheck: null,
    },
    {
        type: "refuel",
        label: { de: "Tanke auf", en: "Refuel" },
        tooltip: { de: "Tankt das Schiff auf.", en: "Refuels the ship." },
        inputs: [],
        outputCheck: null,
    },
];

const INFO_BLOCKS: CatalogBlockSpec[] = [
    {
        type: "getShipInfo",
        label: { de: "Hole Schiffsinformationen", en: "Get ship info" },
        tooltip: { de: "Gibt Informationen über das ausgewählte Schiff zurück.", en: "Returns information about the selected ship." },
        inputs: [],
        outputCheck: "ShipRecord",
    },
    {
        type: "getFleetInfo",
        label: { de: "Hole Flotteninformationen", en: "Get fleet info" },
        tooltip: { de: "Gibt Informationen über die gesamte Flotte zurück.", en: "Returns information about the whole fleet." },
        inputs: [],
        outputCheck: "List",
    },
    {
        type: "getWaypoints",
        label: { de: "Hole Wegpunkte", en: "Get waypoints" },
        tooltip: { de: "Gibt die Wegpunkte eines Sternensystems zurück.", en: "Returns the waypoints of a star system." },
        inputs: [{ name: "SYSTEM_SYMBOL", label: { de: "Sternensystem", en: "System" }, check: "String" }],
        outputCheck: "List",
    },
    {
        type: "getMarket",
        label: { de: "Hole Marktdaten", en: "Get market data" },
        tooltip: { de: "Gibt die Marktdaten eines Wegpunkts zurück.", en: "Returns the market data of a waypoint." },
        inputs: [{ name: "WAYPOINT_SYMBOL", label: { de: "Wegpunkt", en: "Waypoint" }, check: "String" }],
        outputCheck: "MarketRecord",
    },
    {
        type: "getShipyard",
        label: { de: "Hole Werftdaten", en: "Get shipyard data" },
        tooltip: { de: "Gibt die Werftdaten eines Wegpunkts zurück.", en: "Returns the shipyard data of a waypoint." },
        inputs: [{ name: "WAYPOINT_SYMBOL", label: { de: "Wegpunkt", en: "Waypoint" }, check: "String" }],
        outputCheck: "ShipyardRecord",
    },
    {
        type: "getContracts",
        label: { de: "Hole Auftragsdaten", en: "Get contracts" },
        tooltip: { de: "Gibt die aktuellen Aufträge zurück.", en: "Returns the current contracts." },
        inputs: [],
        outputCheck: "List",
    },
    {
        type: "getCargo",
        label: { de: "Hole Fracht", en: "Get cargo" },
        tooltip: { de: "Gibt die Fracht des ausgewählten Schiffs zurück.", en: "Returns the cargo of the selected ship." },
        inputs: [],
        outputCheck: "CargoRecord",
    },
    {
        type: "getFuel",
        label: { de: "Hole Treibstoff", en: "Get fuel" },
        tooltip: { de: "Gibt den Treibstoffstand des ausgewählten Schiffs zurück.", en: "Returns the fuel level of the selected ship." },
        inputs: [],
        outputCheck: "Number",
    },
    {
        type: "getCredits",
        label: { de: "Hole Credits", en: "Get credits" },
        tooltip: { de: "Gibt den aktuellen Kontostand zurück.", en: "Returns the current account balance." },
        inputs: [],
        outputCheck: "Number",
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
    /** Milestone 13: check type for the `TARGET` input — the record shape this
     * accessor reads from (e.g. `"ShipRecord"`), so e.g. a `Markt`-shaped record
     * can't be plugged into a `shipFuel` accessor. */
    targetCheck: string;
    /** Milestone 13: check type for this accessor's own output value. */
    outputCheck: string;
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
    { type: "shipName", label: { de: "Name aus Schiff", en: "Name from ship" }, tooltip: { de: "Gibt den Namen eines Schiffs zurück.", en: "Returns a ship's name." }, fieldName: "Name", targetCheck: "ShipRecord", outputCheck: "String" },
    { type: "shipWaypoint", label: { de: "Wegpunkt aus Schiff", en: "Waypoint from ship" }, tooltip: { de: "Gibt den aktuellen Wegpunkt eines Schiffs zurück.", en: "Returns a ship's current waypoint." }, fieldName: "Waypoint", targetCheck: "ShipRecord", outputCheck: "String" },
    { type: "shipStatus", label: { de: "Status aus Schiff", en: "Status from ship" }, tooltip: { de: "Gibt den Status eines Schiffs zurück.", en: "Returns a ship's status." }, fieldName: "Status", targetCheck: "ShipRecord", outputCheck: "String" },
    { type: "shipFuel", label: { de: "Treibstoff aus Schiff", en: "Fuel from ship" }, tooltip: { de: "Gibt den Treibstoffstand eines Schiffs zurück.", en: "Returns a ship's fuel level." }, fieldName: "Fuel", targetCheck: "ShipRecord", outputCheck: "Number" },
    { type: "shipCargoUnits", label: { de: "Frachteinheiten aus Schiff", en: "Cargo units from ship" }, tooltip: { de: "Gibt die belegten Frachteinheiten eines Schiffs zurück.", en: "Returns a ship's used cargo units." }, fieldName: "CargoUnits", targetCheck: "ShipRecord", outputCheck: "Number" },
    { type: "shipCargoCapacity", label: { de: "Frachtkapazität aus Schiff", en: "Cargo capacity from ship" }, tooltip: { de: "Gibt die Frachtkapazität eines Schiffs zurück.", en: "Returns a ship's cargo capacity." }, fieldName: "CargoCapacity", targetCheck: "ShipRecord", outputCheck: "Number" },
    // Fracht (getCargo)
    { type: "cargoUnits", label: { de: "Einheiten aus Fracht", en: "Units from cargo" }, tooltip: { de: "Gibt die belegten Einheiten einer Fracht zurück.", en: "Returns the used units of a cargo." }, fieldName: "Units", targetCheck: "CargoRecord", outputCheck: "Number" },
    { type: "cargoCapacity", label: { de: "Kapazität aus Fracht", en: "Capacity from cargo" }, tooltip: { de: "Gibt die Kapazität einer Fracht zurück.", en: "Returns the capacity of a cargo." }, fieldName: "Capacity", targetCheck: "CargoRecord", outputCheck: "Number" },
    { type: "cargoGoods", label: { de: "Waren aus Fracht", en: "Goods from cargo" }, tooltip: { de: "Gibt die Liste der Waren einer Fracht zurück.", en: "Returns the list of goods in a cargo." }, fieldName: "Goods", targetCheck: "CargoRecord", outputCheck: "List" },
    // Ware (a Fracht's Waren list item)
    { type: "goodName", label: { de: "Name aus Ware", en: "Name from good" }, tooltip: { de: "Gibt den Namen einer Ware zurück.", en: "Returns a good's name." }, fieldName: "Name", targetCheck: "GoodRecord", outputCheck: "String" },
    { type: "goodUnits", label: { de: "Einheiten aus Ware", en: "Units from good" }, tooltip: { de: "Gibt die Einheiten einer Ware zurück.", en: "Returns a good's units." }, fieldName: "Units", targetCheck: "GoodRecord", outputCheck: "Number" },
    // Werft (getShipyard)
    { type: "shipyardWaypoint", label: { de: "Wegpunkt aus Werft", en: "Waypoint from shipyard" }, tooltip: { de: "Gibt den Wegpunkt einer Werft zurück.", en: "Returns a shipyard's waypoint." }, fieldName: "Waypoint", targetCheck: "ShipyardRecord", outputCheck: "String" },
    { type: "shipyardTypes", label: { de: "Schiffstypen aus Werft", en: "Ship types from shipyard" }, tooltip: { de: "Gibt die Liste der Schiffstypen einer Werft zurück.", en: "Returns the list of ship types at a shipyard." }, fieldName: "Types", targetCheck: "ShipyardRecord", outputCheck: "List" },
    // Schiffstyp (a Werft's Schiffstypen list item)
    { type: "shipyardTypeName", label: { de: "Typ aus Schiffstyp", en: "Type from ship type" }, tooltip: { de: "Gibt die Typbezeichnung eines Schiffstyps zurück.", en: "Returns a ship type's designation." }, fieldName: "Type", targetCheck: "ShipyardTypeRecord", outputCheck: "String" },
    { type: "shipyardTypePrice", label: { de: "Preis aus Schiffstyp", en: "Price from ship type" }, tooltip: { de: "Gibt den Preis eines Schiffstyps zurück.", en: "Returns a ship type's price." }, fieldName: "Price", targetCheck: "ShipyardTypeRecord", outputCheck: "Number" },
    // Markt (getMarket)
    { type: "marketWaypoint", label: { de: "Wegpunkt aus Markt", en: "Waypoint from market" }, tooltip: { de: "Gibt den Wegpunkt eines Marktes zurück.", en: "Returns a market's waypoint." }, fieldName: "Waypoint", targetCheck: "MarketRecord", outputCheck: "String" },
    { type: "marketGoods", label: { de: "Handelswaren aus Markt", en: "Trade goods from market" }, tooltip: { de: "Gibt die Liste der Handelswaren eines Marktes zurück.", en: "Returns the list of trade goods at a market." }, fieldName: "Goods", targetCheck: "MarketRecord", outputCheck: "List" },
    // Handelsware (a Markt's Handelswaren list item)
    { type: "tradeGoodName", label: { de: "Name aus Handelsware", en: "Name from trade good" }, tooltip: { de: "Gibt den Namen einer Handelsware zurück.", en: "Returns a trade good's name." }, fieldName: "Name", targetCheck: "TradeGoodRecord", outputCheck: "String" },
    { type: "tradeGoodBuyPrice", label: { de: "Kaufpreis aus Handelsware", en: "Buy price from trade good" }, tooltip: { de: "Gibt den Kaufpreis einer Handelsware zurück.", en: "Returns a trade good's buy price." }, fieldName: "BuyPrice", targetCheck: "TradeGoodRecord", outputCheck: "Number" },
    { type: "tradeGoodSellPrice", label: { de: "Verkaufspreis aus Handelsware", en: "Sell price from trade good" }, tooltip: { de: "Gibt den Verkaufspreis einer Handelsware zurück.", en: "Returns a trade good's sell price." }, fieldName: "SellPrice", targetCheck: "TradeGoodRecord", outputCheck: "Number" },
    // Auftrag (getContracts items)
    { type: "contractId", label: { de: "Id aus Auftrag", en: "Id from contract" }, tooltip: { de: "Gibt die Id eines Auftrags zurück.", en: "Returns a contract's id." }, fieldName: "Id", targetCheck: "ContractRecord", outputCheck: "String" },
    { type: "contractType", label: { de: "Typ aus Auftrag", en: "Type from contract" }, tooltip: { de: "Gibt den Typ eines Auftrags zurück.", en: "Returns a contract's type." }, fieldName: "Type", targetCheck: "ContractRecord", outputCheck: "String" },
    { type: "contractAccepted", label: { de: "Angenommen aus Auftrag", en: "Accepted from contract" }, tooltip: { de: "Gibt zurück, ob ein Auftrag angenommen wurde.", en: "Returns whether a contract was accepted." }, fieldName: "Accepted", targetCheck: "ContractRecord", outputCheck: "Boolean" },
    { type: "contractFulfilled", label: { de: "Erfüllt aus Auftrag", en: "Fulfilled from contract" }, tooltip: { de: "Gibt zurück, ob ein Auftrag erfüllt wurde.", en: "Returns whether a contract was fulfilled." }, fieldName: "Fulfilled", targetCheck: "ContractRecord", outputCheck: "Boolean" },
    // Wegpunkt (getWaypoints items)
    { type: "waypointSymbolField", label: { de: "Symbol aus Wegpunkt", en: "Symbol from waypoint" }, tooltip: { de: "Gibt das Symbol eines Wegpunkts zurück.", en: "Returns a waypoint's symbol." }, fieldName: "Symbol", targetCheck: "WaypointRecord", outputCheck: "String" },
    { type: "waypointTypeField", label: { de: "Typ aus Wegpunkt", en: "Type from waypoint" }, tooltip: { de: "Gibt den Typ eines Wegpunkts zurück.", en: "Returns a waypoint's type." }, fieldName: "Type", targetCheck: "WaypointRecord", outputCheck: "String" },
    { type: "waypointHasShipyard", label: { de: "Hat Werft aus Wegpunkt", en: "Has shipyard from waypoint" }, tooltip: { de: "Gibt zurück, ob ein Wegpunkt eine Werft hat.", en: "Returns whether a waypoint has a shipyard." }, fieldName: "HasShipyard", targetCheck: "WaypointRecord", outputCheck: "Boolean" },
    { type: "waypointHasMarket", label: { de: "Hat Markt aus Wegpunkt", en: "Has market from waypoint" }, tooltip: { de: "Gibt zurück, ob ein Wegpunkt einen Markt hat.", en: "Returns whether a waypoint has a market." }, fieldName: "HasMarket", targetCheck: "WaypointRecord", outputCheck: "Boolean" },
];

const ACTION_COLOUR = 160;
const INFO_COLOUR = 230;
const ACCESSOR_COLOUR = 65;
const FLOTILLA_COLOUR = 20;

interface WithShipExtraState {
    hasUnavailable?: boolean;
}

interface ParallelExtraState {
    branchCount?: number;
}

function rebuildWithShipUnavailableBranch(block: Blockly.Block): void {
    const hasUnavailable = (block as unknown as { skHasUnavailable?: boolean }).skHasUnavailable ?? false;
    const hasInput = block.getInput("ELSE") !== null;

    if (hasUnavailable && !hasInput) {
        block.appendStatementInput("ELSE").appendField(t({ de: "falls nicht verfügbar", en: "if unavailable" }));
    } else if (!hasUnavailable && hasInput) {
        block.removeInput("ELSE");
    }
}

function rebuildParallelBranches(block: Blockly.Block): void {
    const branchCount = Math.max(2, (block as unknown as { skBranchCount?: number }).skBranchCount ?? 2);

    for (let idx = 20; idx >= branchCount; idx -= 1) {
        if (block.getInput(`DO${idx}`) !== null) {
            block.removeInput(`DO${idx}`);
        }
    }

    for (let idx = 0; idx < branchCount; idx += 1) {
        if (block.getInput(`DO${idx}`) === null) {
            block.appendStatementInput(`DO${idx}`).appendField(t({ de: `Zweig ${idx + 1}`, en: `branch ${idx + 1}` }));
        }
    }
}

function registerBlock(spec: CatalogBlockSpec, colour: number, asValue: boolean): void {
    Blockly.Blocks[spec.type] = {
        init: function (this: Blockly.Block) {
            this.appendDummyInput().appendField(t(spec.label));
            spec.inputs.forEach((inputSpec) => {
                this.appendValueInput(inputSpec.name).setCheck(inputSpec.check).appendField(t(inputSpec.label));
            });
            if (asValue) {
                this.setOutput(true, spec.outputCheck);
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
            this.appendValueInput("TARGET").setCheck(spec.targetCheck).appendField(t(spec.label));
            this.setOutput(true, spec.outputCheck);
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
 * `targetCheck` (Milestone 13) ties the accessor to its owning custom block's own
 * synthetic record-check type (`"CustomRecord_<customBlockId>"`) — `null` (untyped)
 * for the fixed catalog case's default, though every real call site now passes one.
 */
export function registerDynamicAccessorBlock(blockType: string, label: string, tooltip: string, targetCheck: string | null = null): void {
    Blockly.Blocks[blockType] = {
        init: function (this: Blockly.Block) {
            this.appendValueInput("TARGET").setCheck(targetCheck).appendField(label);
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

    Blockly.Blocks["withShip"] = {
        init: function (this: Blockly.Block) {
            this.appendValueInput("SHIP").setCheck("String").appendField(t({ de: "mit Schiff", en: "with ship" }));
            this.appendStatementInput("DO").appendField(t({ de: "mache", en: "do" }));
            this.setPreviousStatement(true, null);
            this.setNextStatement(true, null);
            this.setColour(FLOTILLA_COLOUR);
            this.setTooltip(t({
                de: "Führt die Blöcke mit diesem Schiff als aktuellem Schiff aus.",
                en: "Runs the nested blocks with this ship as the current ship.",
            }));
            this.setMutator(new Blockly.icons.MutatorIcon(["withShip_unavailable_arg"], this as unknown as Blockly.BlockSvg));
            (this as unknown as { skHasUnavailable: boolean }).skHasUnavailable = false;
        },
        saveExtraState: function (this: Blockly.Block): WithShipExtraState {
            return { hasUnavailable: (this as unknown as { skHasUnavailable?: boolean }).skHasUnavailable ?? false };
        },
        loadExtraState: function (this: Blockly.Block, state: WithShipExtraState) {
            (this as unknown as { skHasUnavailable: boolean }).skHasUnavailable = state.hasUnavailable ?? false;
            rebuildWithShipUnavailableBranch(this);
        },
        decompose: function (this: Blockly.Block, workspace: Blockly.Workspace): Blockly.Block {
            const containerBlock = workspace.newBlock("withShip_mutator_container");
            (containerBlock as Blockly.BlockSvg).initSvg?.();

            if ((this as unknown as { skHasUnavailable?: boolean }).skHasUnavailable) {
                const unavailableBlock = workspace.newBlock("withShip_unavailable_arg");
                (unavailableBlock as Blockly.BlockSvg).initSvg?.();
                containerBlock.getInput("STACK")!.connection!.connect(unavailableBlock.previousConnection!);
            }

            return containerBlock;
        },
        compose: function (this: Blockly.Block, containerBlock: Blockly.Block) {
            const hasUnavailable = containerBlock.getInputTargetBlock("STACK") !== null;
            (this as unknown as { skHasUnavailable: boolean }).skHasUnavailable = hasUnavailable;
            rebuildWithShipUnavailableBranch(this);
        },
    };

    Blockly.Blocks["withShip_mutator_container"] = {
        init: function (this: Blockly.Block) {
            this.appendDummyInput().appendField(t({ de: "Optionen", en: "Options" }));
            this.appendStatementInput("STACK");
            this.setColour(FLOTILLA_COLOUR);
            this.contextMenu = false;
        },
    };

    Blockly.Blocks["withShip_unavailable_arg"] = {
        init: function (this: Blockly.Block) {
            this.appendDummyInput().appendField(t({ de: "falls nicht verfügbar", en: "if unavailable" }));
            this.setPreviousStatement(true);
            this.setNextStatement(false);
            this.setColour(FLOTILLA_COLOUR);
            this.contextMenu = false;
        },
    };

    Blockly.Blocks["parallel"] = {
        init: function (this: Blockly.Block) {
            this.appendDummyInput().appendField(t({ de: "parallel", en: "parallel" }));
            (this as unknown as { skBranchCount: number }).skBranchCount = 2;
            rebuildParallelBranches(this);
            this.setPreviousStatement(true, null);
            this.setNextStatement(true, null);
            this.setColour(FLOTILLA_COLOUR);
            this.setTooltip(t({
                de: "Führt mehrere Zweige gleichzeitig aus und wartet, bis alle fertig sind.",
                en: "Runs multiple branches at the same time and waits until all are finished.",
            }));
            this.setMutator(new Blockly.icons.MutatorIcon(["parallel_branch_arg"], this as unknown as Blockly.BlockSvg));
        },
        saveExtraState: function (this: Blockly.Block): ParallelExtraState {
            return { branchCount: (this as unknown as { skBranchCount?: number }).skBranchCount ?? 2 };
        },
        loadExtraState: function (this: Blockly.Block, state: ParallelExtraState) {
            (this as unknown as { skBranchCount: number }).skBranchCount = Math.max(2, state.branchCount ?? 2);
            rebuildParallelBranches(this);
        },
        decompose: function (this: Blockly.Block, workspace: Blockly.Workspace): Blockly.Block {
            const containerBlock = workspace.newBlock("parallel_mutator_container");
            (containerBlock as Blockly.BlockSvg).initSvg?.();
            const count = Math.max(2, (this as unknown as { skBranchCount?: number }).skBranchCount ?? 2);
            let connection = containerBlock.getInput("STACK")!.connection;

            for (let i = 0; i < count; i += 1) {
                const branchBlock = workspace.newBlock("parallel_branch_arg");
                (branchBlock as Blockly.BlockSvg).initSvg?.();
                connection!.connect(branchBlock.previousConnection!);
                connection = branchBlock.nextConnection;
            }

            return containerBlock;
        },
        compose: function (this: Blockly.Block, containerBlock: Blockly.Block) {
            let count = 0;
            let item = containerBlock.getInputTargetBlock("STACK");
            while (item) {
                count += 1;
                item = item.getNextBlock();
            }

            (this as unknown as { skBranchCount: number }).skBranchCount = Math.max(2, count);
            rebuildParallelBranches(this);
        },
    };

    Blockly.Blocks["parallel_mutator_container"] = {
        init: function (this: Blockly.Block) {
            this.appendDummyInput().appendField(t({ de: "Zweige", en: "Branches" }));
            this.appendStatementInput("STACK");
            this.setColour(FLOTILLA_COLOUR);
            this.contextMenu = false;
        },
    };

    Blockly.Blocks["parallel_branch_arg"] = {
        init: function (this: Blockly.Block) {
            this.appendDummyInput().appendField(t({ de: "Zweig", en: "branch" }));
            this.setPreviousStatement(true);
            this.setNextStatement(true);
            this.setColour(FLOTILLA_COLOUR);
            this.contextMenu = false;
        },
    };
}

/**
 * Milestone 13/Part B gave every catalog/accessor block's own sockets a real
 * `.setCheck` type, but stock Blockly control blocks were left untouched —
 * `controls_forEach`'s "LIST" input has no check at all, so a record-shaped
 * block (e.g. `getShipyard`'s output) can be wired straight into a `forEach`
 * without Blockly refusing the connection, only surfacing as a confusing
 * runtime DSL error (`Eval.asList`) once the program actually runs. `"List"`
 * is the exact same check string every list-producing catalog/accessor block
 * already declares as its `outputCheck` (`getWaypoints`, `shipyardTypes`,
 * `marketGoods`, ...), so this doesn't block any legitimate program — an
 * unchecked block (e.g. `variables_get`) still connects fine either way.
 * Idempotent (patches the stock `init` once, safe to call once at seam init).
 */
export function registerStockBlockChecks(): void {
    const originalForEach = Blockly.Blocks["controls_forEach"].init;
    Blockly.Blocks["controls_forEach"].init = function (this: Blockly.Block) {
        originalForEach.call(this);
        this.getInput("LIST")?.connection?.setCheck("List");
    };

    // `controls_if`'s IF0 and `controls_whileUntil`'s BOOL already check stock
    // Blockly's own `"Boolean"`, and `controls_repeat_ext`'s TIMES already
    // checks `"Number"` — the same literal strings our own catalog blocks use,
    // so those three were never actually broken (confirmed live). Only
    // `logic_compare`'s A/B operands have no check at all: `Eval.fs`'s
    // `Comparison` case only meaningfully compares `VString`/coerces everything
    // else via `asFloat`, so a `VList`/`VRecord` operand only ever failed at
    // runtime, not at edit time.
    const originalCompare = Blockly.Blocks["logic_compare"].init;
    Blockly.Blocks["logic_compare"].init = function (this: Blockly.Block) {
        originalCompare.call(this);
        const primitiveChecks = ["String", "Number", "Boolean"];
        this.getInput("A")?.connection?.setCheck(primitiveChecks);
        this.getInput("B")?.connection?.setCheck(primitiveChecks);
    };
}

export const catalogActionBlockTypes: string[] = ACTION_BLOCKS.map((spec) => spec.type);
export const catalogInfoBlockTypes: string[] = INFO_BLOCKS.map((spec) => spec.type);
export const catalogAccessorBlockTypes: string[] = ACCESSOR_BLOCKS.map((spec) => spec.type);
export const flotillaBlockTypes: string[] = ["withShip", "parallel"];
/** Block type -> DSL record field name, consumed by `Compiler.fs`'s `ACCESSOR_BLOCKS` table (kept in sync manually — see docs/04-block-catalog.md). */
export const accessorFieldNames: Record<string, string> = Object.fromEntries(
    ACCESSOR_BLOCKS.map((spec) => [spec.type, spec.fieldName]),
);
