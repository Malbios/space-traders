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
        type: "fulfillContract",
        label: { de: "Schließe Auftrag ab", en: "Fulfill contract" },
        tooltip: {
            de: "Schließt einen angenommenen Auftrag ab.",
            en: "Fulfills an accepted contract.",
        },
        inputs: [{ name: "CONTRACT_ID", label: { de: "Auftrag", en: "Contract" }, check: "String" }],
        outputCheck: null,
    },
    {
        type: "negotiateContract",
        label: { de: "Verhandle Auftrag", en: "Negotiate contract" },
        tooltip: {
            de: "Verhandelt einen neuen Auftrag mit dem Hauptquartier.",
            en: "Negotiates a new contract with headquarters.",
        },
        inputs: [],
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
    {
        type: "createChart",
        label: { de: "Erstelle Karte", en: "Create chart" },
        tooltip: { de: "Erstellt eine Karte aus Vermessungsdaten.", en: "Creates a chart from survey data." },
        inputs: [],
        outputCheck: null,
    },
    {
        type: "extractWithSurvey",
        label: { de: "Baue mit Vermessung ab", en: "Extract with survey" },
        tooltip: { de: "Baut Rohstoffe mit einer Vermessungssignatur ab.", en: "Extracts resources using a survey signature." },
        inputs: [{ name: "SURVEY_SIGNATURE", label: { de: "Vermessung", en: "Survey" }, check: "String" }],
        outputCheck: null,
    },
    {
        type: "installModule",
        label: { de: "Installiere Modul", en: "Install module" },
        tooltip: { de: "Installiert ein Modul am Schiff.", en: "Installs a module on the ship." },
        inputs: [{ name: "MODULE_SYMBOL", label: { de: "Modul", en: "Module" }, check: "String" }],
        outputCheck: null,
    },
    {
        type: "installMount",
        label: { de: "Installiere Aufsatz", en: "Install mount" },
        tooltip: { de: "Installiert einen Aufsatz am Schiff.", en: "Installs a mount on the ship." },
        inputs: [{ name: "MOUNT_SYMBOL", label: { de: "Aufsatz", en: "Mount" }, check: "String" }],
        outputCheck: null,
    },
    {
        type: "jettison",
        label: { de: "Wirf Fracht ab", en: "Jettison cargo" },
        tooltip: { de: "Wirft Fracht aus dem Schiff.", en: "Jettisons cargo from the ship." },
        inputs: [
            { name: "TRADE_SYMBOL", label: { de: "Ware", en: "Good" }, check: "String" },
            { name: "UNITS", label: { de: "Menge", en: "Units" }, check: "Number" },
        ],
        outputCheck: null,
    },
    {
        type: "jump",
        label: { de: "Springe zu Wegpunkt", en: "Jump to waypoint" },
        tooltip: { de: "Springt über ein Sprungtor zu einem verbundenen Wegpunkt.", en: "Jumps through a gate to a connected waypoint." },
        inputs: [{ name: "DESTINATION", label: { de: "Wegpunkt", en: "Waypoint" }, check: "String" }],
        outputCheck: null,
    },
    {
        type: "refine",
        label: { de: "Veredle Rohstoffe", en: "Refine goods" },
        tooltip: { de: "Veredelt Rohstoffe im Schiff.", en: "Refines raw goods on the ship." },
        inputs: [{ name: "PRODUCE", label: { de: "Erzeugnis", en: "Product" }, check: "String" }],
        outputCheck: null,
    },
    {
        type: "removeModule",
        label: { de: "Entferne Modul", en: "Remove module" },
        tooltip: { de: "Entfernt ein Modul vom Schiff.", en: "Removes a module from the ship." },
        inputs: [{ name: "MODULE_SYMBOL", label: { de: "Modul", en: "Module" }, check: "String" }],
        outputCheck: null,
    },
    {
        type: "removeMount",
        label: { de: "Entferne Aufsatz", en: "Remove mount" },
        tooltip: { de: "Entfernt einen Aufsatz vom Schiff.", en: "Removes a mount from the ship." },
        inputs: [{ name: "MOUNT_SYMBOL", label: { de: "Aufsatz", en: "Mount" }, check: "String" }],
        outputCheck: null,
    },
    {
        type: "repair",
        label: { de: "Repariere Schiff", en: "Repair ship" },
        tooltip: { de: "Repariert das Schiff auf einer Werft.", en: "Repairs the ship at a shipyard." },
        inputs: [],
        outputCheck: null,
    },
    {
        type: "scanShips",
        label: { de: "Scanne Schiffe", en: "Scan ships" },
        tooltip: { de: "Scannt Schiffe im Umfeld.", en: "Scans nearby ships." },
        inputs: [],
        outputCheck: null,
    },
    {
        type: "scanSystems",
        label: { de: "Scanne Systeme", en: "Scan systems" },
        tooltip: { de: "Scannt nahe Sternensysteme.", en: "Scans nearby star systems." },
        inputs: [],
        outputCheck: null,
    },
    {
        type: "scanWaypoints",
        label: { de: "Scanne Wegpunkte", en: "Scan waypoints" },
        tooltip: { de: "Scannt Wegpunkte im aktuellen System.", en: "Scans waypoints in the current system." },
        inputs: [],
        outputCheck: null,
    },
    {
        type: "scrapShip",
        label: { de: "Verschrotte Schiff", en: "Scrap ship" },
        tooltip: { de: "Verschrottet das Schiff.", en: "Scraps the ship." },
        inputs: [],
        outputCheck: null,
    },
    {
        type: "siphon",
        label: { de: "Entnehme Gas", en: "Siphon gas" },
        tooltip: { de: "Entnimmt Gas aus einem Gasriesen.", en: "Siphons gas from a gas giant." },
        inputs: [],
        outputCheck: null,
    },
    {
        type: "transferCargo",
        label: { de: "Übertrage Fracht", en: "Transfer cargo" },
        tooltip: { de: "Überträgt Fracht auf ein anderes Schiff.", en: "Transfers cargo to another ship." },
        inputs: [
            { name: "TRADE_SYMBOL", label: { de: "Ware", en: "Good" }, check: "String" },
            { name: "UNITS", label: { de: "Menge", en: "Units" }, check: "Number" },
            { name: "SHIP_SYMBOL", label: { de: "Zielschiff", en: "Target ship" }, check: "String" },
        ],
        outputCheck: null,
    },
    {
        type: "warp",
        label: { de: "Warpe zu Wegpunkt", en: "Warp to waypoint" },
        tooltip: { de: "Warpt das Schiff zu einem Wegpunkt.", en: "Warps the ship to a waypoint." },
        inputs: [{ name: "DESTINATION", label: { de: "Wegpunkt", en: "Waypoint" }, check: "String" }],
        outputCheck: null,
    },
    {
        type: "supplyConstruction",
        label: { de: "Liefere Baumaterial", en: "Supply construction" },
        tooltip: { de: "Liefert Baumaterial für einen Bauplatz.", en: "Supplies construction materials at a construction site." },
        inputs: [
            { name: "WAYPOINT_SYMBOL", label: { de: "Wegpunkt", en: "Waypoint" }, check: "String" },
            { name: "TRADE_SYMBOL", label: { de: "Ware", en: "Good" }, check: "String" },
            { name: "UNITS", label: { de: "Menge", en: "Units" }, check: "Number" },
        ],
        outputCheck: null,
    },
    {
        type: "patchShipNav",
        label: { de: "Setze Flugmodus", en: "Set flight mode" },
        tooltip: { de: "Ändert den Flugmodus des Schiffs.", en: "Changes the ship's flight mode." },
        inputs: [{ name: "FLIGHT_MODE", label: { de: "Flugmodus", en: "Flight mode" }, check: "String" }],
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
    {
        type: "getRepairCost",
        label: { de: "Hole Reparaturkosten", en: "Get repair cost" },
        tooltip: { de: "Gibt die Reparaturkosten des ausgewählten Schiffs zurück.", en: "Returns the repair cost of the selected ship." },
        inputs: [],
        outputCheck: "PriceRecord",
    },
    {
        type: "getScrapValue",
        label: { de: "Hole Verschrottungswert", en: "Get scrap value" },
        tooltip: { de: "Gibt den Verschrottungswert des ausgewählten Schiffs zurück.", en: "Returns the scrap value of the selected ship." },
        inputs: [],
        outputCheck: "PriceRecord",
    },
    {
        type: "getWaypoint",
        label: { de: "Hole Wegpunkt", en: "Get waypoint" },
        tooltip: { de: "Gibt die Daten eines Wegpunkts zurück.", en: "Returns the data of a waypoint." },
        inputs: [{ name: "WAYPOINT_SYMBOL", label: { de: "Wegpunkt", en: "Waypoint" }, check: "String" }],
        outputCheck: "WaypointRecord",
    },
    {
        type: "getMyAgent",
        label: { de: "Hole meine Agentendaten", en: "Get my agent" },
        tooltip: { de: "Gibt die Daten des eigenen Agenten zurück.", en: "Returns the data of your own agent." },
        inputs: [],
        outputCheck: "AgentRecord",
    },
    {
        type: "getPublicAgent",
        label: { de: "Hole öffentliche Agentendaten", en: "Get public agent" },
        tooltip: { de: "Gibt die öffentlichen Daten eines Agenten zurück.", en: "Returns the public data of an agent." },
        inputs: [{ name: "AGENT_SYMBOL", label: { de: "Agent", en: "Agent" }, check: "String" }],
        outputCheck: "AgentRecord",
    },
    {
        type: "getPublicAgents",
        label: { de: "Hole öffentliche Agenten", en: "Get public agents" },
        tooltip: { de: "Gibt die Liste aller öffentlichen Agenten zurück.", en: "Returns the list of all public agents." },
        inputs: [],
        outputCheck: "List",
    },
    {
        type: "getCooldown",
        label: { de: "Hole Abklingzeit", en: "Get cooldown" },
        tooltip: { de: "Gibt die Abklingzeit des ausgewählten Schiffs zurück.", en: "Returns the cooldown of the selected ship." },
        inputs: [],
        outputCheck: "CooldownRecord",
    },
    {
        type: "getNav",
        label: { de: "Hole Navigationsdaten", en: "Get navigation" },
        tooltip: { de: "Gibt die Navigationsdaten des ausgewählten Schiffs zurück.", en: "Returns the navigation data of the selected ship." },
        inputs: [],
        outputCheck: "NavRecord",
    },
    {
        type: "getSupplyChain",
        label: { de: "Hole Lieferkette", en: "Get supply chain" },
        tooltip: { de: "Gibt die globale Lieferkette zurück.", en: "Returns the global supply chain." },
        inputs: [],
        outputCheck: "List",
    },
    {
        type: "getShipModules",
        label: { de: "Hole Schiffsmodule", en: "Get ship modules" },
        tooltip: { de: "Gibt die installierten Module des ausgewählten Schiffs zurück.", en: "Returns the installed modules of the selected ship." },
        inputs: [],
        outputCheck: "List",
    },
    {
        type: "getShipMounts",
        label: { de: "Hole Schiffsaufsätze", en: "Get ship mounts" },
        tooltip: { de: "Gibt die installierten Aufsätze des ausgewählten Schiffs zurück.", en: "Returns the installed mounts of the selected ship." },
        inputs: [],
        outputCheck: "List",
    },
    {
        type: "getConstruction",
        label: { de: "Hole Bauplatz", en: "Get construction" },
        tooltip: { de: "Gibt die Daten eines Bauplatzes zurück.", en: "Returns the data of a construction site." },
        inputs: [{ name: "WAYPOINT_SYMBOL", label: { de: "Wegpunkt", en: "Waypoint" }, check: "String" }],
        outputCheck: "ConstructionRecord",
    },
    {
        type: "getJumpGate",
        label: { de: "Hole Sprungtor", en: "Get jump gate" },
        tooltip: { de: "Gibt die Daten eines Sprungtors zurück.", en: "Returns the data of a jump gate." },
        inputs: [{ name: "WAYPOINT_SYMBOL", label: { de: "Wegpunkt", en: "Waypoint" }, check: "String" }],
        outputCheck: "JumpGateRecord",
    },
    {
        type: "getSystems",
        label: { de: "Hole Sternensysteme", en: "Get systems" },
        tooltip: { de: "Gibt die Liste aller Sternensysteme zurück.", en: "Returns the list of all star systems." },
        inputs: [],
        outputCheck: "List",
    },
    {
        type: "getSystem",
        label: { de: "Hole Sternensystem", en: "Get system" },
        tooltip: { de: "Gibt die Daten eines Sternensystems zurück.", en: "Returns the data of a star system." },
        inputs: [{ name: "SYSTEM_SYMBOL", label: { de: "Sternensystem", en: "System" }, check: "String" }],
        outputCheck: "SystemRecord",
    },
    {
        type: "getFaction",
        label: { de: "Hole Fraktion", en: "Get faction" },
        tooltip: { de: "Gibt die Daten einer Fraktion zurück.", en: "Returns the data of a faction." },
        inputs: [{ name: "FACTION_SYMBOL", label: { de: "Fraktion", en: "Faction" }, check: "String" }],
        outputCheck: "FactionRecord",
    },
    {
        type: "getFactions",
        label: { de: "Hole Fraktionen", en: "Get factions" },
        tooltip: { de: "Gibt die Liste aller Fraktionen zurück.", en: "Returns the list of all factions." },
        inputs: [],
        outputCheck: "List",
    },
    {
        type: "getMyFactions",
        label: { de: "Hole meine Fraktionen", en: "Get my factions" },
        tooltip: { de: "Gibt die eigenen Fraktionsbeziehungen zurück.", en: "Returns your faction reputations." },
        inputs: [],
        outputCheck: "List",
    },
];

interface RecordFieldSpec {
    /** The DSL record field this reads (§8) — a canonical English key, decoupled from
     * display language (Milestone 12), matching `SpaceKids.Server.JobRunner`'s
     * info-read conversion into the `VRecord`. Also the dropdown option's *value*
     * (its display text is `label`, translated separately). */
    name: string;
    label: LocalizedText;
    /** Milestone 13: check type this field produces — the block's own output check
     * is recomputed live to this whenever the dropdown selection changes. */
    outputCheck: string;
}

interface RecordFieldBlockSpec {
    /** Blockly type identifier — kept English per §7. */
    type: string;
    /** Used in the block's own "Feld aus X"/"Field from X" label prefix. */
    recordLabel: LocalizedText;
    /** Milestone 13: check type for the `TARGET` input — the record shape this block
     * reads from (e.g. `"ShipRecord"`), so e.g. a `Markt`-shaped record can't be
     * plugged into a `shipField` block. */
    targetCheck: string;
    fields: RecordFieldSpec[];
}

/**
 * One generic "field from X" block per §8/post-roadmap "friendly structured record"
 * shape — a value block taking the record as its one input ("TARGET") plus a `FIELD`
 * dropdown selecting which field to return, replacing the old one-Blockly-type-per-
 * field scheme (29 block types collapsed into these 9, discoverability was the
 * problem — see docs/decisions.md). Extended in the same redesign to cover the
 * newer record shapes that previously had no field access at all (`AgentRecord`
 * through `SupplyChainEntry` below) — with this generic shape, adding one is a data
 * entry, not a bespoke block.
 */
/** Exported (not just kept module-private) so `blocks-catalog.test.ts` can assert
 * registered block behavior against the same spec data that drives it, rather than
 * duplicating a second hand-maintained expectation list that could drift. */
export const RECORD_FIELD_BLOCKS: RecordFieldBlockSpec[] = [
    // Schiff (getShipInfo / getFleetInfo items)
    { type: "shipField", recordLabel: { de: "Schiff", en: "ship" }, targetCheck: "ShipRecord", fields: [
        { name: "Name", label: { de: "Name", en: "Name" }, outputCheck: "String" },
        { name: "Waypoint", label: { de: "Wegpunkt", en: "Waypoint" }, outputCheck: "String" },
        { name: "Status", label: { de: "Status", en: "Status" }, outputCheck: "String" },
        { name: "Fuel", label: { de: "Treibstoff", en: "Fuel" }, outputCheck: "Number" },
        { name: "CargoUnits", label: { de: "Frachteinheiten", en: "Cargo units" }, outputCheck: "Number" },
        { name: "CargoCapacity", label: { de: "Frachtkapazität", en: "Cargo capacity" }, outputCheck: "Number" },
    ] },
    // Fracht (getCargo)
    { type: "cargoField", recordLabel: { de: "Fracht", en: "cargo" }, targetCheck: "CargoRecord", fields: [
        { name: "Units", label: { de: "Einheiten", en: "Units" }, outputCheck: "Number" },
        { name: "Capacity", label: { de: "Kapazität", en: "Capacity" }, outputCheck: "Number" },
        { name: "Goods", label: { de: "Waren", en: "Goods" }, outputCheck: "List" },
    ] },
    // Ware (a Fracht's Waren list item)
    { type: "goodField", recordLabel: { de: "Ware", en: "good" }, targetCheck: "GoodRecord", fields: [
        { name: "Name", label: { de: "Name", en: "Name" }, outputCheck: "String" },
        { name: "Units", label: { de: "Einheiten", en: "Units" }, outputCheck: "Number" },
    ] },
    // Werft (getShipyard)
    { type: "shipyardField", recordLabel: { de: "Werft", en: "shipyard" }, targetCheck: "ShipyardRecord", fields: [
        { name: "Waypoint", label: { de: "Wegpunkt", en: "Waypoint" }, outputCheck: "String" },
        { name: "Types", label: { de: "Schiffstypen", en: "Ship types" }, outputCheck: "List" },
    ] },
    // Schiffstyp (a Werft's Schiffstypen list item) — `Type`/`Price` are always
    // present (populated even from the price-free `shipTypes` fallback, §8); the
    // rest are only populated when a ship of yours is docked at that shipyard (see
    // `ShipyardShipEntry`'s own doc comment in `SpaceTraders/Types.fs`).
    { type: "shipyardTypeField", recordLabel: { de: "Schiffstyp", en: "ship type" }, targetCheck: "ShipyardTypeRecord", fields: [
        { name: "Type", label: { de: "Typ", en: "Type" }, outputCheck: "String" },
        { name: "Price", label: { de: "Preis", en: "Price" }, outputCheck: "Number" },
        { name: "Name", label: { de: "Name", en: "Name" }, outputCheck: "String" },
        { name: "Description", label: { de: "Beschreibung", en: "Description" }, outputCheck: "String" },
        { name: "Supply", label: { de: "Angebot", en: "Supply" }, outputCheck: "String" },
        { name: "Activity", label: { de: "Aktivität", en: "Activity" }, outputCheck: "String" },
        { name: "Frame", label: { de: "Rahmen", en: "Frame" }, outputCheck: "FrameRecord" },
        { name: "Reactor", label: { de: "Reaktor", en: "Reactor" }, outputCheck: "ReactorRecord" },
        { name: "Engine", label: { de: "Antrieb", en: "Engine" }, outputCheck: "EngineRecord" },
        { name: "Modules", label: { de: "Module", en: "Modules" }, outputCheck: "List" },
        { name: "Mounts", label: { de: "Aufsätze", en: "Mounts" }, outputCheck: "List" },
        { name: "Crew", label: { de: "Besatzung", en: "Crew" }, outputCheck: "CrewRecord" },
    ] },
    // Rahmen (a Schiffstyp's Frame)
    { type: "frameField", recordLabel: { de: "Rahmen", en: "frame" }, targetCheck: "FrameRecord", fields: [
        { name: "Symbol", label: { de: "Symbol", en: "Symbol" }, outputCheck: "String" },
        { name: "Name", label: { de: "Name", en: "Name" }, outputCheck: "String" },
        { name: "Description", label: { de: "Beschreibung", en: "Description" }, outputCheck: "String" },
        { name: "ModuleSlots", label: { de: "Modulplätze", en: "Module slots" }, outputCheck: "Number" },
        { name: "MountingPoints", label: { de: "Befestigungspunkte", en: "Mounting points" }, outputCheck: "Number" },
        { name: "FuelCapacity", label: { de: "Treibstoffkapazität", en: "Fuel capacity" }, outputCheck: "Number" },
        { name: "Requirements", label: { de: "Anforderungen", en: "Requirements" }, outputCheck: "RequirementsRecord" },
    ] },
    // Reaktor (a Schiffstyp's Reactor)
    { type: "reactorField", recordLabel: { de: "Reaktor", en: "reactor" }, targetCheck: "ReactorRecord", fields: [
        { name: "Symbol", label: { de: "Symbol", en: "Symbol" }, outputCheck: "String" },
        { name: "Name", label: { de: "Name", en: "Name" }, outputCheck: "String" },
        { name: "Description", label: { de: "Beschreibung", en: "Description" }, outputCheck: "String" },
        { name: "PowerOutput", label: { de: "Energieleistung", en: "Power output" }, outputCheck: "Number" },
        { name: "Requirements", label: { de: "Anforderungen", en: "Requirements" }, outputCheck: "RequirementsRecord" },
    ] },
    // Antrieb (a Schiffstyp's Engine)
    { type: "engineField", recordLabel: { de: "Antrieb", en: "engine" }, targetCheck: "EngineRecord", fields: [
        { name: "Symbol", label: { de: "Symbol", en: "Symbol" }, outputCheck: "String" },
        { name: "Name", label: { de: "Name", en: "Name" }, outputCheck: "String" },
        { name: "Description", label: { de: "Beschreibung", en: "Description" }, outputCheck: "String" },
        { name: "Speed", label: { de: "Geschwindigkeit", en: "Speed" }, outputCheck: "Number" },
        { name: "Requirements", label: { de: "Anforderungen", en: "Requirements" }, outputCheck: "RequirementsRecord" },
    ] },
    // Modul (a Schiffstyp's Module list item — distinct from `moduleField`, which
    // reads an already-*installed* module on one of the player's own ships)
    { type: "shipyardModuleField", recordLabel: { de: "Modul", en: "module" }, targetCheck: "ShipyardModuleRecord", fields: [
        { name: "Symbol", label: { de: "Symbol", en: "Symbol" }, outputCheck: "String" },
        { name: "Name", label: { de: "Name", en: "Name" }, outputCheck: "String" },
        { name: "Description", label: { de: "Beschreibung", en: "Description" }, outputCheck: "String" },
        { name: "Capacity", label: { de: "Kapazität", en: "Capacity" }, outputCheck: "Number" },
        { name: "Range", label: { de: "Reichweite", en: "Range" }, outputCheck: "Number" },
        { name: "Requirements", label: { de: "Anforderungen", en: "Requirements" }, outputCheck: "RequirementsRecord" },
    ] },
    // Aufsatz (a Schiffstyp's Mounts list item — distinct from `mountField`, which
    // reads an already-*installed* mount on one of the player's own ships)
    { type: "shipyardMountField", recordLabel: { de: "Aufsatz", en: "mount" }, targetCheck: "ShipyardMountRecord", fields: [
        { name: "Symbol", label: { de: "Symbol", en: "Symbol" }, outputCheck: "String" },
        { name: "Name", label: { de: "Name", en: "Name" }, outputCheck: "String" },
        { name: "Description", label: { de: "Beschreibung", en: "Description" }, outputCheck: "String" },
        { name: "Strength", label: { de: "Stärke", en: "Strength" }, outputCheck: "Number" },
        { name: "Deposits", label: { de: "Vorkommen", en: "Deposits" }, outputCheck: "List" },
        { name: "Requirements", label: { de: "Anforderungen", en: "Requirements" }, outputCheck: "RequirementsRecord" },
    ] },
    // Besatzung (a Schiffstyp's Crew)
    { type: "crewField", recordLabel: { de: "Besatzung", en: "crew" }, targetCheck: "CrewRecord", fields: [
        { name: "Required", label: { de: "Benötigt", en: "Required" }, outputCheck: "Number" },
        { name: "Capacity", label: { de: "Kapazität", en: "Capacity" }, outputCheck: "Number" },
    ] },
    // Anforderungen (the shared power/crew/slots struct every frame/reactor/engine/
    // module/mount above carries)
    { type: "requirementsField", recordLabel: { de: "Anforderungen", en: "requirements" }, targetCheck: "RequirementsRecord", fields: [
        { name: "Power", label: { de: "Energie", en: "Power" }, outputCheck: "Number" },
        { name: "Crew", label: { de: "Besatzung", en: "Crew" }, outputCheck: "Number" },
        { name: "Slots", label: { de: "Plätze", en: "Slots" }, outputCheck: "Number" },
    ] },
    // Markt (getMarket)
    { type: "marketField", recordLabel: { de: "Markt", en: "market" }, targetCheck: "MarketRecord", fields: [
        { name: "Waypoint", label: { de: "Wegpunkt", en: "Waypoint" }, outputCheck: "String" },
        { name: "Goods", label: { de: "Handelswaren", en: "Trade goods" }, outputCheck: "List" },
    ] },
    // Handelsware (a Markt's Handelswaren list item)
    { type: "tradeGoodField", recordLabel: { de: "Handelsware", en: "trade good" }, targetCheck: "TradeGoodRecord", fields: [
        { name: "Name", label: { de: "Name", en: "Name" }, outputCheck: "String" },
        { name: "BuyPrice", label: { de: "Kaufpreis", en: "Buy price" }, outputCheck: "Number" },
        { name: "SellPrice", label: { de: "Verkaufspreis", en: "Sell price" }, outputCheck: "Number" },
    ] },
    // Auftrag (getContracts items)
    { type: "contractField", recordLabel: { de: "Auftrag", en: "contract" }, targetCheck: "ContractRecord", fields: [
        { name: "Id", label: { de: "Id", en: "Id" }, outputCheck: "String" },
        { name: "Type", label: { de: "Typ", en: "Type" }, outputCheck: "String" },
        { name: "Accepted", label: { de: "Angenommen", en: "Accepted" }, outputCheck: "Boolean" },
        { name: "Fulfilled", label: { de: "Erfüllt", en: "Fulfilled" }, outputCheck: "Boolean" },
    ] },
    // Wegpunkt (getWaypoints / getWaypoint)
    { type: "waypointField", recordLabel: { de: "Wegpunkt", en: "waypoint" }, targetCheck: "WaypointRecord", fields: [
        { name: "Symbol", label: { de: "Symbol", en: "Symbol" }, outputCheck: "String" },
        { name: "Type", label: { de: "Typ", en: "Type" }, outputCheck: "String" },
        { name: "System", label: { de: "System", en: "System" }, outputCheck: "String" },
        { name: "HasShipyard", label: { de: "Hat Werft", en: "Has shipyard" }, outputCheck: "Boolean" },
        { name: "HasMarket", label: { de: "Hat Markt", en: "Has market" }, outputCheck: "Boolean" },
    ] },
    // Agent (getMyAgent / getPublicAgent / getPublicAgents items)
    { type: "agentField", recordLabel: { de: "Agent", en: "agent" }, targetCheck: "AgentRecord", fields: [
        { name: "Symbol", label: { de: "Symbol", en: "Symbol" }, outputCheck: "String" },
        { name: "Headquarters", label: { de: "Hauptquartier", en: "Headquarters" }, outputCheck: "String" },
        { name: "Credits", label: { de: "Credits", en: "Credits" }, outputCheck: "Number" },
        { name: "StartingFaction", label: { de: "Startfraktion", en: "Starting faction" }, outputCheck: "String" },
        { name: "ShipCount", label: { de: "Schiffsanzahl", en: "Ship count" }, outputCheck: "Number" },
    ] },
    // Sternensystem (getSystem / getSystems items)
    { type: "systemField", recordLabel: { de: "Sternensystem", en: "star system" }, targetCheck: "SystemRecord", fields: [
        { name: "Symbol", label: { de: "Symbol", en: "Symbol" }, outputCheck: "String" },
        { name: "Sector", label: { de: "Sektor", en: "Sector" }, outputCheck: "String" },
        { name: "Type", label: { de: "Typ", en: "Type" }, outputCheck: "String" },
        { name: "X", label: { de: "X", en: "X" }, outputCheck: "Number" },
        { name: "Y", label: { de: "Y", en: "Y" }, outputCheck: "Number" },
        { name: "Name", label: { de: "Name", en: "Name" }, outputCheck: "String" },
        { name: "Constellation", label: { de: "Konstellation", en: "Constellation" }, outputCheck: "String" },
    ] },
    // Fraktion (getFaction / getFactions items)
    { type: "factionField", recordLabel: { de: "Fraktion", en: "faction" }, targetCheck: "FactionRecord", fields: [
        { name: "Symbol", label: { de: "Symbol", en: "Symbol" }, outputCheck: "String" },
        { name: "Name", label: { de: "Name", en: "Name" }, outputCheck: "String" },
        { name: "Description", label: { de: "Beschreibung", en: "Description" }, outputCheck: "String" },
        { name: "Headquarters", label: { de: "Hauptquartier", en: "Headquarters" }, outputCheck: "String" },
        { name: "IsRecruiting", label: { de: "Rekrutiert", en: "Recruiting" }, outputCheck: "Boolean" },
    ] },
    // Fraktionsruf (getMyFactions items)
    { type: "factionReputationField", recordLabel: { de: "Fraktionsruf", en: "faction reputation" }, targetCheck: "FactionReputationRecord", fields: [
        { name: "Symbol", label: { de: "Symbol", en: "Symbol" }, outputCheck: "String" },
        { name: "Reputation", label: { de: "Ruf", en: "Reputation" }, outputCheck: "Number" },
    ] },
    // Sprungtor (getJumpGate)
    { type: "jumpGateField", recordLabel: { de: "Sprungtor", en: "jump gate" }, targetCheck: "JumpGateRecord", fields: [
        { name: "Symbol", label: { de: "Symbol", en: "Symbol" }, outputCheck: "String" },
        { name: "Connections", label: { de: "Verbindungen", en: "Connections" }, outputCheck: "List" },
    ] },
    // Bauplatz (getConstruction)
    { type: "constructionField", recordLabel: { de: "Bauplatz", en: "construction site" }, targetCheck: "ConstructionRecord", fields: [
        { name: "Symbol", label: { de: "Symbol", en: "Symbol" }, outputCheck: "String" },
        { name: "IsComplete", label: { de: "Fertig", en: "Complete" }, outputCheck: "Boolean" },
        { name: "Materials", label: { de: "Materialien", en: "Materials" }, outputCheck: "List" },
    ] },
    // Baumaterial (a Bauplatz's Materialien list item)
    { type: "constructionMaterialField", recordLabel: { de: "Baumaterial", en: "construction material" }, targetCheck: "ConstructionMaterialRecord", fields: [
        { name: "TradeSymbol", label: { de: "Ware", en: "Good" }, outputCheck: "String" },
        { name: "UnitsRequired", label: { de: "Benötigt", en: "Required" }, outputCheck: "Number" },
        { name: "UnitsFulfilled", label: { de: "Erfüllt", en: "Fulfilled" }, outputCheck: "Number" },
    ] },
    // Navigation (getNav)
    { type: "navField", recordLabel: { de: "Navigation", en: "navigation" }, targetCheck: "NavRecord", fields: [
        { name: "Waypoint", label: { de: "Wegpunkt", en: "Waypoint" }, outputCheck: "String" },
        { name: "System", label: { de: "System", en: "System" }, outputCheck: "String" },
        { name: "Status", label: { de: "Status", en: "Status" }, outputCheck: "String" },
        { name: "FlightMode", label: { de: "Flugmodus", en: "Flight mode" }, outputCheck: "String" },
    ] },
    // Abklingzeit (getCooldown)
    { type: "cooldownField", recordLabel: { de: "Abklingzeit", en: "cooldown" }, targetCheck: "CooldownRecord", fields: [
        { name: "Ship", label: { de: "Schiff", en: "Ship" }, outputCheck: "String" },
        { name: "TotalSeconds", label: { de: "Gesamtsekunden", en: "Total seconds" }, outputCheck: "Number" },
        { name: "RemainingSeconds", label: { de: "Restsekunden", en: "Remaining seconds" }, outputCheck: "Number" },
        { name: "Expiration", label: { de: "Ablauf", en: "Expiration" }, outputCheck: "String" },
    ] },
    // Preis (getRepairCost / getScrapValue)
    { type: "priceField", recordLabel: { de: "Preis", en: "price" }, targetCheck: "PriceRecord", fields: [
        { name: "Waypoint", label: { de: "Wegpunkt", en: "Waypoint" }, outputCheck: "String" },
        { name: "Ship", label: { de: "Schiff", en: "Ship" }, outputCheck: "String" },
        { name: "TotalPrice", label: { de: "Gesamtpreis", en: "Total price" }, outputCheck: "Number" },
    ] },
    // Modul (getShipModules items)
    { type: "moduleField", recordLabel: { de: "Modul", en: "module" }, targetCheck: "ModuleRecord", fields: [
        { name: "Symbol", label: { de: "Symbol", en: "Symbol" }, outputCheck: "String" },
        { name: "Name", label: { de: "Name", en: "Name" }, outputCheck: "String" },
    ] },
    // Aufsatz (getShipMounts items)
    { type: "mountField", recordLabel: { de: "Aufsatz", en: "mount" }, targetCheck: "MountRecord", fields: [
        { name: "Symbol", label: { de: "Symbol", en: "Symbol" }, outputCheck: "String" },
        { name: "Name", label: { de: "Name", en: "Name" }, outputCheck: "String" },
    ] },
    // Lieferkette (getSupplyChain items)
    { type: "supplyChainField", recordLabel: { de: "Lieferkette", en: "supply chain" }, targetCheck: "SupplyChainRecord", fields: [
        { name: "Export", label: { de: "Export", en: "Export" }, outputCheck: "String" },
        { name: "Import", label: { de: "Import", en: "Import" }, outputCheck: "String" },
    ] },
];

/** `RECORD_FIELD_BLOCKS`'s per-shape field lists, indexed by `targetCheck` — the
 * lookup `registerGenericRecordFieldBlock`'s dropdown uses to narrow to just the
 * fields for whatever's directly wired into `TARGET`. */
const FIELD_SET_BY_CHECK: Record<string, RecordFieldSpec[]> = Object.fromEntries(
    RECORD_FIELD_BLOCKS.map((spec) => [spec.targetCheck, spec.fields]),
);

/** Every `targetCheck` string `RECORD_FIELD_BLOCKS` declares — the generic block's
 * own `TARGET` check, so it still refuses genuinely unrelated things (e.g. a plain
 * `"String"`-checked block) while accepting any known record shape, plus anything
 * `null`-checked (stock Blockly variable blocks) per Blockly's normal
 * any-connects-to-checked rule. */
const RECORD_FIELD_TARGET_CHECKS: string[] = RECORD_FIELD_BLOCKS.map((spec) => spec.targetCheck);

/** Every distinct field name across all `RECORD_FIELD_BLOCKS` shapes, first
 * occurrence (array order above) winning on a name collision — auditing this found
 * exactly one live collision: `"Goods"` is `cargoField`'s "Waren"/"Goods" vs.
 * `marketField`'s "Handelswaren"/"Trade goods" (both `outputCheck: "List"`, so no
 * type conflict, just a label choice). Used as the generic block's dropdown when
 * `TARGET`'s connected block's shape can't be resolved — most commonly because it's
 * a bare `variables_get`, which (like all stock Blockly variable blocks) carries no
 * connection check at all, so there's no way to know which shape a variable actually
 * holds (see docs/generic-accessor-block-plan.md). Which shape a variable-held value
 * really is can't be known once this fallback list is showing anyway, so there's no
 * more "correct" choice on a collision than first-wins. */
const ALL_FIELDS_MERGED: RecordFieldSpec[] = (() => {
    const seenNames = new Set<string>();
    const merged: RecordFieldSpec[] = [];
    for (const spec of RECORD_FIELD_BLOCKS) {
        for (const field of spec.fields) {
            if (!seenNames.has(field.name)) {
                seenNames.add(field.name);
                merged.push(field);
            }
        }
    }
    return merged;
})();

/** The field set `recordField`'s `FIELD` dropdown should currently offer: narrowed to
 * the connected block's own record shape when `TARGET` is directly wired to
 * something with a recognized check, `ALL_FIELDS_MERGED` otherwise. */
function fieldSetFor(block: Blockly.Block): RecordFieldSpec[] {
    const targetBlock = block.getInputTargetBlock("TARGET");
    const directChecks = targetBlock?.outputConnection?.getCheck() ?? null;
    let matchedCheck = directChecks?.find((c) => FIELD_SET_BY_CHECK[c]);

    // Phase 2 (docs/generic-accessor-block-plan.md): a bare `variables_get` has no
    // connection check of its own, but `registerVariableTypeTagging` keeps the
    // variable's own `type` tagged with whatever record shape it was last assigned
    // from, so fall back to reading that when the direct check didn't resolve.
    if (!matchedCheck && targetBlock?.type === "variables_get") {
        const varType = (targetBlock.getField("VAR") as Blockly.FieldVariable | null)?.getVariable()?.getType();
        if (varType && FIELD_SET_BY_CHECK[varType]) {
            matchedCheck = varType;
        }
    }

    return matchedCheck ? FIELD_SET_BY_CHECK[matchedCheck] : ALL_FIELDS_MERGED;
}

/**
 * The single connection-aware "field from record" block that generalizes
 * `RECORD_FIELD_BLOCKS`'s 21 fixed-shape types (docs/generic-accessor-block-plan.md)
 * — one block instead of picking the right shape up front. Registered *alongside*
 * `RECORD_FIELD_BLOCKS`, not replacing it: those stay registered forever for
 * programs/custom blocks saved before this generalization existed, same lesson
 * `LEGACY_ACCESSOR_BLOCKS` already establishes. Only the toolbox (`toolbox-de.ts`)
 * stops offering the old types for new authoring.
 */
function registerGenericRecordFieldBlock(): void {
    Blockly.Blocks["recordField"] = {
        init: function (this: Blockly.Block) {
            this.appendValueInput("TARGET")
                .setCheck(RECORD_FIELD_TARGET_CHECKS)
                .appendField(t({ de: "Feld", en: "Field" }))
                .appendField(
                    new Blockly.FieldDropdown(() => fieldSetFor(this).map((f) => [t(f.label), f.name] as [string, string])),
                    "FIELD",
                )
                .appendField(t({ de: "aus...", en: "from..." }));
            this.setOutput(true, null);
            this.setColour(ACCESSOR_COLOUR);
            this.setTooltip(
                t({
                    de: "Gibt ein Feld eines Datensatzes zurück – die Auswahl passt sich an, was angeschlossen ist.",
                    en: "Returns a field of a record – the choices adapt to whatever's connected.",
                }),
            );
        },
        // Same live-recompute reasoning as `registerRecordFieldBlock`'s own
        // `onchange` (Milestone 13): the output check depends on which field is
        // currently selected, and here also on which field *set* is currently
        // showing (narrowed vs. merged), so both must be re-read on every change.
        onchange: function (this: Blockly.Block) {
            const selected = this.getFieldValue("FIELD");
            const match = fieldSetFor(this).find((f) => f.name === selected);
            this.setOutput(true, match ? match.outputCheck : null);
        },
    };
}

interface LegacyAccessorBlockSpec {
    /** Blockly type identifier — the pre-refactor one-block-per-field name. */
    type: string;
    label: LocalizedText;
    tooltip: LocalizedText;
    targetCheck: string;
    outputCheck: string;
}

/**
 * Compatibility shims for the 29 one-block-type-per-field accessors that existed
 * before "Replace per-field accessor blocks with generic field-dropdown blocks"
 * collapsed them into `RECORD_FIELD_BLOCKS`'s FIELD-dropdown blocks. Not added to the
 * toolbox — nobody should drag a fresh one of these in — but still registered with
 * Blockly so a program or custom block saved *before* that refactor still
 * deserializes and renders instead of throwing "Invalid block definition" and
 * silently rendering as an empty workspace (discovered when exactly that happened to
 * real saved programs the same day the refactor landed). `Compiler.fs`'s
 * `LEGACY_ACCESSOR_FIELD_NAMES` mirrors this list (kept in sync manually, same as
 * `GENERIC_ACCESSOR_TYPES` already was) so these old blocks still compile, not just
 * render.
 */
const LEGACY_ACCESSOR_BLOCKS: LegacyAccessorBlockSpec[] = [
    { type: "shipName", label: { de: "Name aus Schiff", en: "Name from ship" }, tooltip: { de: "Gibt den Namen eines Schiffs zurück.", en: "Returns a ship's name." }, targetCheck: "ShipRecord", outputCheck: "String" },
    { type: "shipWaypoint", label: { de: "Wegpunkt aus Schiff", en: "Waypoint from ship" }, tooltip: { de: "Gibt den aktuellen Wegpunkt eines Schiffs zurück.", en: "Returns a ship's current waypoint." }, targetCheck: "ShipRecord", outputCheck: "String" },
    { type: "shipStatus", label: { de: "Status aus Schiff", en: "Status from ship" }, tooltip: { de: "Gibt den Status eines Schiffs zurück.", en: "Returns a ship's status." }, targetCheck: "ShipRecord", outputCheck: "String" },
    { type: "shipFuel", label: { de: "Treibstoff aus Schiff", en: "Fuel from ship" }, tooltip: { de: "Gibt den Treibstoffstand eines Schiffs zurück.", en: "Returns a ship's fuel level." }, targetCheck: "ShipRecord", outputCheck: "Number" },
    { type: "shipCargoUnits", label: { de: "Frachteinheiten aus Schiff", en: "Cargo units from ship" }, tooltip: { de: "Gibt die belegten Frachteinheiten eines Schiffs zurück.", en: "Returns a ship's used cargo units." }, targetCheck: "ShipRecord", outputCheck: "Number" },
    { type: "shipCargoCapacity", label: { de: "Frachtkapazität aus Schiff", en: "Cargo capacity from ship" }, tooltip: { de: "Gibt die Frachtkapazität eines Schiffs zurück.", en: "Returns a ship's cargo capacity." }, targetCheck: "ShipRecord", outputCheck: "Number" },
    { type: "cargoUnits", label: { de: "Einheiten aus Fracht", en: "Units from cargo" }, tooltip: { de: "Gibt die belegten Einheiten einer Fracht zurück.", en: "Returns the used units of a cargo." }, targetCheck: "CargoRecord", outputCheck: "Number" },
    { type: "cargoCapacity", label: { de: "Kapazität aus Fracht", en: "Capacity from cargo" }, tooltip: { de: "Gibt die Kapazität einer Fracht zurück.", en: "Returns the capacity of a cargo." }, targetCheck: "CargoRecord", outputCheck: "Number" },
    { type: "cargoGoods", label: { de: "Waren aus Fracht", en: "Goods from cargo" }, tooltip: { de: "Gibt die Liste der Waren einer Fracht zurück.", en: "Returns the list of goods in a cargo." }, targetCheck: "CargoRecord", outputCheck: "List" },
    { type: "goodName", label: { de: "Name aus Ware", en: "Name from good" }, tooltip: { de: "Gibt den Namen einer Ware zurück.", en: "Returns a good's name." }, targetCheck: "GoodRecord", outputCheck: "String" },
    { type: "goodUnits", label: { de: "Einheiten aus Ware", en: "Units from good" }, tooltip: { de: "Gibt die Einheiten einer Ware zurück.", en: "Returns a good's units." }, targetCheck: "GoodRecord", outputCheck: "Number" },
    { type: "shipyardWaypoint", label: { de: "Wegpunkt aus Werft", en: "Waypoint from shipyard" }, tooltip: { de: "Gibt den Wegpunkt einer Werft zurück.", en: "Returns a shipyard's waypoint." }, targetCheck: "ShipyardRecord", outputCheck: "String" },
    { type: "shipyardTypes", label: { de: "Schiffstypen aus Werft", en: "Ship types from shipyard" }, tooltip: { de: "Gibt die Liste der Schiffstypen einer Werft zurück.", en: "Returns the list of ship types at a shipyard." }, targetCheck: "ShipyardRecord", outputCheck: "List" },
    { type: "shipyardTypeName", label: { de: "Typ aus Schiffstyp", en: "Type from ship type" }, tooltip: { de: "Gibt die Typbezeichnung eines Schiffstyps zurück.", en: "Returns a ship type's designation." }, targetCheck: "ShipyardTypeRecord", outputCheck: "String" },
    { type: "shipyardTypePrice", label: { de: "Preis aus Schiffstyp", en: "Price from ship type" }, tooltip: { de: "Gibt den Preis eines Schiffstyps zurück.", en: "Returns a ship type's price." }, targetCheck: "ShipyardTypeRecord", outputCheck: "Number" },
    { type: "marketWaypoint", label: { de: "Wegpunkt aus Markt", en: "Waypoint from market" }, tooltip: { de: "Gibt den Wegpunkt eines Marktes zurück.", en: "Returns a market's waypoint." }, targetCheck: "MarketRecord", outputCheck: "String" },
    { type: "marketGoods", label: { de: "Handelswaren aus Markt", en: "Trade goods from market" }, tooltip: { de: "Gibt die Liste der Handelswaren eines Marktes zurück.", en: "Returns the list of trade goods at a market." }, targetCheck: "MarketRecord", outputCheck: "List" },
    { type: "tradeGoodName", label: { de: "Name aus Handelsware", en: "Name from trade good" }, tooltip: { de: "Gibt den Namen einer Handelsware zurück.", en: "Returns a trade good's name." }, targetCheck: "TradeGoodRecord", outputCheck: "String" },
    { type: "tradeGoodBuyPrice", label: { de: "Kaufpreis aus Handelsware", en: "Buy price from trade good" }, tooltip: { de: "Gibt den Kaufpreis einer Handelsware zurück.", en: "Returns a trade good's buy price." }, targetCheck: "TradeGoodRecord", outputCheck: "Number" },
    { type: "tradeGoodSellPrice", label: { de: "Verkaufspreis aus Handelsware", en: "Sell price from trade good" }, tooltip: { de: "Gibt den Verkaufspreis einer Handelsware zurück.", en: "Returns a trade good's sell price." }, targetCheck: "TradeGoodRecord", outputCheck: "Number" },
    { type: "contractId", label: { de: "Id aus Auftrag", en: "Id from contract" }, tooltip: { de: "Gibt die Id eines Auftrags zurück.", en: "Returns a contract's id." }, targetCheck: "ContractRecord", outputCheck: "String" },
    { type: "contractType", label: { de: "Typ aus Auftrag", en: "Type from contract" }, tooltip: { de: "Gibt den Typ eines Auftrags zurück.", en: "Returns a contract's type." }, targetCheck: "ContractRecord", outputCheck: "String" },
    { type: "contractAccepted", label: { de: "Angenommen aus Auftrag", en: "Accepted from contract" }, tooltip: { de: "Gibt zurück, ob ein Auftrag angenommen wurde.", en: "Returns whether a contract was accepted." }, targetCheck: "ContractRecord", outputCheck: "Boolean" },
    { type: "contractFulfilled", label: { de: "Erfüllt aus Auftrag", en: "Fulfilled from contract" }, tooltip: { de: "Gibt zurück, ob ein Auftrag erfüllt wurde.", en: "Returns whether a contract was fulfilled." }, targetCheck: "ContractRecord", outputCheck: "Boolean" },
    { type: "waypointSymbolField", label: { de: "Symbol aus Wegpunkt", en: "Symbol from waypoint" }, tooltip: { de: "Gibt das Symbol eines Wegpunkts zurück.", en: "Returns a waypoint's symbol." }, targetCheck: "WaypointRecord", outputCheck: "String" },
    { type: "waypointTypeField", label: { de: "Typ aus Wegpunkt", en: "Type from waypoint" }, tooltip: { de: "Gibt den Typ eines Wegpunkts zurück.", en: "Returns a waypoint's type." }, targetCheck: "WaypointRecord", outputCheck: "String" },
    { type: "waypointSystemField", label: { de: "System aus Wegpunkt", en: "System from waypoint" }, tooltip: { de: "Gibt das Sternensystem eines Wegpunkts zurück.", en: "Returns a waypoint's star system." }, targetCheck: "WaypointRecord", outputCheck: "String" },
    { type: "waypointHasShipyard", label: { de: "Hat Werft aus Wegpunkt", en: "Has shipyard from waypoint" }, tooltip: { de: "Gibt zurück, ob ein Wegpunkt eine Werft hat.", en: "Returns whether a waypoint has a shipyard." }, targetCheck: "WaypointRecord", outputCheck: "Boolean" },
    { type: "waypointHasMarket", label: { de: "Hat Markt aus Wegpunkt", en: "Has market from waypoint" }, tooltip: { de: "Gibt zurück, ob ein Wegpunkt einen Markt hat.", en: "Returns whether a waypoint has a market." }, targetCheck: "WaypointRecord", outputCheck: "Boolean" },
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

/** Catalog record-field blocks read `currentLocale` live in their own `init()` —
 * unlike `registerDynamicAccessorBlock` below (custom-block accessors), whose label
 * is child-authored free text, not fixed catalog vocabulary, and stays
 * locale-independent. The `FIELD` dropdown's options are rebuilt on every open (a
 * function, not a static array) so a language switch relabels them too, same
 * reasoning as `sk_param_get`'s dropdown in `blocks.ts`. */
function registerRecordFieldBlock(spec: RecordFieldBlockSpec): void {
    Blockly.Blocks[spec.type] = {
        init: function (this: Blockly.Block) {
            this.appendValueInput("TARGET")
                .setCheck(spec.targetCheck)
                .appendField(t({ de: "Feld", en: "Field" }))
                .appendField(new Blockly.FieldDropdown(() => spec.fields.map((f) => [t(f.label), f.name] as [string, string])), "FIELD")
                .appendField(`${t({ de: "aus", en: "from" })} ${t(spec.recordLabel)}`);
            this.setOutput(true, spec.fields[0]?.outputCheck ?? null);
            this.setColour(ACCESSOR_COLOUR);
            this.setTooltip(
                t({
                    de: `Gibt ein Feld eines "${t(spec.recordLabel)}"-Datensatzes zurück.`,
                    en: `Returns a field of a "${t(spec.recordLabel)}" record.`,
                }),
            );
        },
        // Milestone 13: the output's check type depends on *which* field is
        // currently selected, so it's recomputed on every change rather than set
        // once in `init()` — same pattern as `sk_param_get`'s own `onchange`.
        onchange: function (this: Blockly.Block) {
            const selected = this.getFieldValue("FIELD");
            const match = spec.fields.find((f) => f.name === selected);
            this.setOutput(true, match ? match.outputCheck : null);
        },
    };
}

/**
 * Registers a single `TARGET`-input/`asValue: true` accessor block, exactly the same
 * shape the fixed §8 accessor blocks above use. Exported for Milestone 9/Part C's
 * per-custom-block structured-output accessors (`accessor_<customBlockId>_<field>`),
 * which are generated dynamically per block rather than declared statically here.
 * Takes a plain string label/tooltip (child-authored field names, out of scope for
 * Milestone 12's bilingual support), unlike the fixed catalog's own `registerRecordFieldBlock`.
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

/** Registers one `LEGACY_ACCESSOR_BLOCKS` compatibility shim — same fixed-label shape the pre-refactor per-field accessor blocks used, kept static (no live locale re-read) since these only ever need to render an already-saved block, never be freshly authored. */
function registerLegacyAccessorBlock(spec: LegacyAccessorBlockSpec): void {
    Blockly.Blocks[spec.type] = {
        init: function (this: Blockly.Block) {
            this.appendValueInput("TARGET").setCheck(spec.targetCheck).appendField(t(spec.label));
            this.setOutput(true, spec.outputCheck);
            this.setColour(ACCESSOR_COLOUR);
            this.setTooltip(t(spec.tooltip));
        },
    };
}

/** Registers all SpaceTraders action/information blocks (§6/§7) plus the §8 accessor blocks (Milestone 9/Part B). Idempotent — safe to call once at seam init, same as the other register* functions in blocks.ts. */
export function registerCatalogBlocks(): void {
    ACTION_BLOCKS.forEach((spec) => registerBlock(spec, ACTION_COLOUR, false));
    INFO_BLOCKS.forEach((spec) => registerBlock(spec, INFO_COLOUR, true));
    RECORD_FIELD_BLOCKS.forEach((spec) => registerRecordFieldBlock(spec));
    registerGenericRecordFieldBlock();
    LEGACY_ACCESSOR_BLOCKS.forEach((spec) => registerLegacyAccessorBlock(spec));

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

/**
 * Generic accessor Phase 2 (docs/generic-accessor-block-plan.md): stock Blockly
 * variables carry no connection check at all, so `recordField`'s dropdown can't
 * narrow through a variable the way it can through a directly-wired info block —
 * unless the variable itself is tagged with the record shape it currently holds.
 * Blockly's variable model already has a native, otherwise-unused `type` field for
 * exactly this. Patches stock `variables_set`'s `onchange` (it has none by default,
 * confirmed directly) to keep that tag in sync with whatever's wired into `VALUE`:
 * stamps a recognized record check onto the variable, clears it back to untyped on a
 * reassignment to something recognized but NOT a record (a plain literal, a
 * non-record info block, ...), and leaves it alone when nothing new can be inferred
 * (`VALUE` disconnected, or wired to another untyped/bare variable) rather than
 * guess. Since `onchange` fires on every relevant workspace event, not just changes
 * to this exact block, this also retroactively (re)tags variables the first time an
 * already-saved program is opened after this ships — not just newly-authored
 * assignments. This only ever narrows `recordField`'s dropdown as a convenience; it
 * never changes what compiles or runs (`Accessor(field, target)` stays purely
 * name-keyed at runtime, same as `GENERIC_ACCESSOR_TYPES` already establishes) — a
 * variable genuinely reused for two different record shapes at different points in
 * one program is a known, accepted limitation (the tag is one value per variable,
 * not per assignment site), not something this is meant to solve. Idempotent
 * (patches the stock `init`/adds `onchange` once, safe to call once at seam init).
 */
export function registerVariableTypeTagging(): void {
    const originalVariablesSet = Blockly.Blocks["variables_set"].init;
    Blockly.Blocks["variables_set"].init = function (this: Blockly.Block) {
        originalVariablesSet.call(this);
    };
    Blockly.Blocks["variables_set"].onchange = function (this: Blockly.Block) {
        const varModel = (this.getField("VAR") as Blockly.FieldVariable | null)?.getVariable();
        if (!varModel) {
            return;
        }

        const valueCheck = this.getInputTargetBlock("VALUE")?.outputConnection?.getCheck() ?? null;
        const matchedRecordCheck = valueCheck?.find((c) => FIELD_SET_BY_CHECK[c]);

        if (matchedRecordCheck && varModel.getType() !== matchedRecordCheck) {
            this.workspace.getVariableMap().changeVariableType(varModel, matchedRecordCheck);
        } else if (valueCheck && !matchedRecordCheck && varModel.getType() !== "") {
            this.workspace.getVariableMap().changeVariableType(varModel, "");
        }
    };
}

const FLOTILLA_BLOCK_LABELS: Record<string, LocalizedText> = {
    withShip: { de: "mit Schiff", en: "with ship" },
    parallel: { de: "parallel", en: "parallel" },
};

/** "Feld aus X"/"Field from X" — the same prefix text `registerRecordFieldBlock`
 * renders, reconstructed here for the toolbox sorter (which needs a label per block
 * type without instantiating the block). */
const recordFieldBlockLabel = (spec: RecordFieldBlockSpec): LocalizedText => ({
    de: `Feld aus ${spec.recordLabel.de}`,
    en: `Field from ${spec.recordLabel.en}`,
});

const GENERIC_RECORD_FIELD_LABEL: LocalizedText = { de: "Feld aus...", en: "Field from..." };

const catalogLabelByType: Record<string, LocalizedText> = Object.fromEntries([
    ...ACTION_BLOCKS.map((spec) => [spec.type, spec.label] as const),
    ...INFO_BLOCKS.map((spec) => [spec.type, spec.label] as const),
    ...RECORD_FIELD_BLOCKS.map((spec) => [spec.type, recordFieldBlockLabel(spec)] as const),
    ["recordField", GENERIC_RECORD_FIELD_LABEL] as const,
    ...Object.entries(FLOTILLA_BLOCK_LABELS),
]);

/** Visible label for a catalog block type in the current locale — used by the toolbox sorter. */
export function getCatalogBlockLabel(blockType: string): string {
    const label = catalogLabelByType[blockType];
    return label ? t(label) : blockType;
}

export const catalogActionBlockTypes: string[] = ACTION_BLOCKS.map((spec) => spec.type);
export const catalogInfoBlockTypes: string[] = INFO_BLOCKS.map((spec) => spec.type);
export const catalogRecordFieldBlockTypes: string[] = RECORD_FIELD_BLOCKS.map((spec) => spec.type);
/** The single connection-aware accessor block offered in the toolbox going forward
 * (docs/generic-accessor-block-plan.md) — `catalogRecordFieldBlockTypes`'s 21 fixed
 * types stay registered and stay exported (still needed by back-compat rendering and
 * existing tests) but are no longer what `toolbox-de.ts` offers for new authoring. */
export const genericRecordFieldBlockTypes: string[] = ["recordField"];
export const flotillaBlockTypes: string[] = ["withShip", "parallel"];
