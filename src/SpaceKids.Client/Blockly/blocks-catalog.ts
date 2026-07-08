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
const RECORD_FIELD_BLOCKS: RecordFieldBlockSpec[] = [
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
    // Schiffstyp (a Werft's Schiffstypen list item)
    { type: "shipyardTypeField", recordLabel: { de: "Schiffstyp", en: "ship type" }, targetCheck: "ShipyardTypeRecord", fields: [
        { name: "Type", label: { de: "Typ", en: "Type" }, outputCheck: "String" },
        { name: "Price", label: { de: "Preis", en: "Price" }, outputCheck: "Number" },
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

/** Registers all SpaceTraders action/information blocks (§6/§7) plus the §8 accessor blocks (Milestone 9/Part B). Idempotent — safe to call once at seam init, same as the other register* functions in blocks.ts. */
export function registerCatalogBlocks(): void {
    ACTION_BLOCKS.forEach((spec) => registerBlock(spec, ACTION_COLOUR, false));
    INFO_BLOCKS.forEach((spec) => registerBlock(spec, INFO_COLOUR, true));
    RECORD_FIELD_BLOCKS.forEach((spec) => registerRecordFieldBlock(spec));

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

const catalogLabelByType: Record<string, LocalizedText> = Object.fromEntries([
    ...ACTION_BLOCKS.map((spec) => [spec.type, spec.label] as const),
    ...INFO_BLOCKS.map((spec) => [spec.type, spec.label] as const),
    ...RECORD_FIELD_BLOCKS.map((spec) => [spec.type, recordFieldBlockLabel(spec)] as const),
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
export const flotillaBlockTypes: string[] = ["withShip", "parallel"];
