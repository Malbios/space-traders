# Block catalog

The German block catalog for the first release (`plan.md` §6/§7). Authored once, consumed
twice: the Milestone 3 toolbox build (`SpaceKids.Client/Blockly/blocks-catalog.ts`) and
the Milestone 4 DSL compiler/validator. Block identifiers and DSL instruction names are
English per §7; everything a player sees is German.

Each entry: German label (heading), Blockly type, DSL instruction shape, inputs,
requirements (documentation only until Milestone 4's validator enforces them), and the
German success/failure log lines shown to the child while a program runs (Milestone 6+).

Strategy blocks (§6 "do not add") are deliberately absent — nothing here decides *which*
waypoint to fly to or *whether* a trade is a good idea. That's built from these
primitives plus the programming blocks below.

## Aktionsblöcke (action blocks)

Statement blocks — connect above/below like ordinary instructions.

### Fliege zu Wegpunkt

```txt
Blockly type: navigate
DSL instruction: navigate(destination)
Inputs: destination (Wegpunkt-Wert)
Requirements: ausgewähltes Schiff, genug Treibstoff
Success log: Das Schiff fliegt zu {destination}.
Failure log: Das Schiff hat nicht genug Treibstoff.
```

### Gehe in Umlaufbahn

```txt
Blockly type: orbit
DSL instruction: orbit()
Inputs: keine
Requirements: Schiff angedockt oder unterwegs am Wegpunkt
Success log: Das Schiff ist jetzt in der Umlaufbahn.
Failure log: Das Schiff konnte nicht starten.
```

### Docke an

```txt
Blockly type: dock
DSL instruction: dock()
Inputs: keine
Requirements: Schiff in Umlaufbahn am Wegpunkt
Success log: Das Schiff hat angedockt.
Failure log: Das Schiff konnte nicht andocken.
```

### Baue Rohstoffe ab

```txt
Blockly type: extract
DSL instruction: extract()
Inputs: keine
Requirements: Schiff in Umlaufbahn an einem Asteroidenfeld, Abklingzeit vorbei
Success log: Das Schiff hat Rohstoffe abgebaut.
Failure log: Die Abklingzeit ist noch nicht vorbei.
```

### Scanne Asteroidenfeld

```txt
Blockly type: survey
DSL instruction: survey()
Inputs: keine
Requirements: Schiff in Umlaufbahn an einem Asteroidenfeld
Success log: Das Schiff hat das Asteroidenfeld gescannt.
Failure log: Der Scan ist fehlgeschlagen.
```

### Kaufe Ware

```txt
Blockly type: buyGood
DSL instruction: buyGood(tradeSymbol, units)
Inputs: tradeSymbol (Ware-Wert), units (Zahl-Wert)
Requirements: Schiff angedockt an einem Marktplatz, genug Credits
Success log: {units} Einheiten {tradeSymbol} gekauft.
Failure log: Nicht genug Credits für diesen Kauf.
```

### Verkaufe Ware

```txt
Blockly type: sellGood
DSL instruction: sellGood(tradeSymbol, units)
Inputs: tradeSymbol (Ware-Wert), units (Zahl-Wert)
Requirements: Schiff angedockt an einem Marktplatz, genug Fracht an Bord
Success log: {units} Einheiten {tradeSymbol} verkauft.
Failure log: Die Fracht enthält nicht genug {tradeSymbol}.
```

### Liefere Fracht

```txt
Blockly type: deliverContract
DSL instruction: deliverContract(contractId, tradeSymbol, units)
Inputs: contractId (Auftrag-Wert), tradeSymbol (Ware-Wert), units (Zahl-Wert)
Requirements: Schiff angedockt am Lieferwegpunkt, Auftrag angenommen, genug Fracht an Bord
Success log: Fracht für den Auftrag geliefert.
Failure log: Die Lieferung ist fehlgeschlagen.
```

### Nimm Auftrag an

```txt
Blockly type: acceptContract
DSL instruction: acceptContract(contractId)
Inputs: contractId (Auftrag-Wert)
Requirements: Auftrag ist noch nicht angenommen
Success log: Auftrag angenommen.
Failure log: Der Auftrag konnte nicht angenommen werden.
```

### Kaufe Schiff

```txt
Blockly type: purchaseShip
DSL instruction: purchaseShip(shipType, waypoint)
Inputs: shipType (Text-Wert), waypoint (Wegpunkt-Wert)
Requirements: Schiff angedockt an einer Werft, genug Credits
Success log: Neues Schiff gekauft.
Failure log: Nicht genug Credits für ein neues Schiff.
```

### Tanke auf

```txt
Blockly type: refuel
DSL instruction: refuel()
Inputs: keine
Requirements: Schiff angedockt an einem Marktplatz mit Treibstoff
Success log: Das Schiff wurde aufgetankt.
Failure log: Auftanken ist fehlgeschlagen.
```

## Informationsblöcke (information blocks)

Value blocks (§6: "they plug into variable assignments, conditions, and other inputs" —
the compiler hoists these into their own instruction per §10, invisibly to the player).

### Hole Schiffsinformationen

```txt
Blockly type: getShipInfo
DSL instruction: getShipInfo() -> Schiff
Inputs: keine
Requirements: ausgewähltes Schiff
Failure log: Schiffsinformationen konnten nicht abgerufen werden.
```

### Hole Flotteninformationen

```txt
Blockly type: getFleetInfo
DSL instruction: getFleetInfo() -> Liste von Schiff
Inputs: keine
Requirements: keine
Failure log: Flotteninformationen konnten nicht abgerufen werden.
```

### Hole Wegpunkte

```txt
Blockly type: getWaypoints
DSL instruction: getWaypoints(systemSymbol) -> Liste von Wegpunkt
Inputs: systemSymbol (Text-Wert)
Requirements: keine
Failure log: Wegpunkte konnten nicht abgerufen werden.
```

### Hole Marktdaten

```txt
Blockly type: getMarket
DSL instruction: getMarket(waypointSymbol) -> Markt
Inputs: waypointSymbol (Wegpunkt-Wert)
Requirements: Wegpunkt hat einen Marktplatz
Failure log: Marktdaten konnten nicht abgerufen werden.
```

### Hole Werftdaten

```txt
Blockly type: getShipyard
DSL instruction: getShipyard(waypointSymbol) -> Werft
Inputs: waypointSymbol (Wegpunkt-Wert)
Requirements: Wegpunkt hat eine Werft
Failure log: Werftdaten konnten nicht abgerufen werden.
```

### Hole Auftragsdaten

```txt
Blockly type: getContracts
DSL instruction: getContracts() -> Liste von Auftrag
Inputs: keine
Requirements: keine
Failure log: Auftragsdaten konnten nicht abgerufen werden.
```

### Hole Fracht

```txt
Blockly type: getCargo
DSL instruction: getCargo() -> Fracht
Inputs: keine
Requirements: ausgewähltes Schiff
Failure log: Frachtdaten konnten nicht abgerufen werden.
```

### Hole Treibstoff

```txt
Blockly type: getFuel
DSL instruction: getFuel() -> Zahl
Inputs: keine
Requirements: ausgewähltes Schiff
Failure log: Treibstoffdaten konnten nicht abgerufen werden.
```

### Hole Credits

```txt
Blockly type: getCredits
DSL instruction: getCredits() -> Zahl
Inputs: keine
Requirements: keine
Failure log: Kontostand konnte nicht abgerufen werden.
```

## Programmierblöcke (programming blocks)

§6's 14 programming blocks (Wenn, Wenn sonst, Wiederhole, Wiederhole bis, Für jedes
Element, Setze Variable, Ändere Variable, Vergleiche Werte, Rechne, Erstelle Liste, Füge
zu Liste hinzu, Hole Element aus Liste, Zeige Nachricht, Warte) are **not** new custom
blocks — all but the last two map onto Blockly's stock block library (already registered
via `import "blockly/blocks"`), which the German locale (`blockly/msg/de`) already
labels:

```txt
Wenn / Wenn sonst      -> controls_if (the "sonst" branch is its own built-in mutator,
                          not a separate block type)
Wiederhole             -> controls_repeat_ext
Wiederhole bis         -> controls_whileUntil
Für jedes Element      -> controls_forEach
Setze Variable         -> variables_set
Ändere Variable        -> math_change
Vergleiche Werte       -> logic_compare
Rechne                 -> math_arithmetic
Erstelle Liste         -> lists_create_with
Füge zu Liste hinzu    -> lists_setIndex
Hole Element aus Liste -> lists_getIndex
Zeige Nachricht        -> sk_show_message (custom, Milestone 0 spike)
Warte                  -> sk_wait (custom, Milestone 0 spike)
```

No DSL instruction shapes are recorded here for the stock blocks — their compilation to
DSL constructs (if/while/for/assignment) is generic control-flow compilation, not a
catalog entry, and is Milestone 4 work.

### Additional stock blocks beyond §6's original 14

Added later (post-Milestone-3 audit) to close gaps found once real programs needed
boolean logic beyond `logic_compare`:

```txt
Wahrheitswert (true/false) -> logic_boolean (Compiler.fs: Literal(BoolLit ...))
Und/Oder                   -> logic_operation (Compiler.fs: LogicalOp(op, ...); Types.fs's
                               Expr; Eval.fs short-circuits AND/OR like F#'s own &&/||)
Nicht                      -> logic_negate (Compiler.fs: LogicalNot(...))
Verlassen/Weiter           -> controls_flow_statements (Compiler.fs: Break/Continue;
                               Types.fs's Instruction; Step.fs's advancePosition/
                               breakLoop/continueLoop implement the loop-exit semantics)
```

`controls_flow_statements` (break/continue) was also considered in that audit and found
to be a real, felt gap (no way to stop a `forEach` early) — unlike `logic_boolean`/
`logic_operation`/`logic_negate`, it needed loop-exit semantics threaded through
`Step.fs`'s `ForEach`/`WhileUntil`/`Repeat` handling in the scheduler core, not just a
new `Expr`/toolbox entry, so it was implemented as its own follow-up. `Validator.fs`
rejects a `Break`/`Continue` used outside any loop (a server-side backstop for a
stored/hand-crafted program — Blockly's own `controls_flow_in_loop_check` extension
already guards this client-side).

## Datensätze und Zugriffsblöcke (records and accessor blocks, §8, Milestone 9/Part B)

The 9 information blocks above return one of the "friendly structured records" below
(kept flat per §8's own instruction), never a raw nested API response. A record's
fields are only reachable through the matching accessor block — a value block with one
input (`TARGET`, the record) and no other inputs.

```txt
Schiff (getShipInfo, one item of getFleetInfo's list)
  Name, Waypoint, Status, Fuel, CargoUnits, CargoCapacity

Fracht (getCargo)
  Units, Capacity, Goods (Liste von Ware)

Ware (one item of Fracht's Goods list)
  Name, Units

Werft (getShipyard)
  Waypoint, Types (Liste von Schiffstyp)

Schiffstyp (one item of Werft's Types list)
  Type, Price

Markt (getMarket)
  Waypoint, Goods (Liste von Handelsware)

Handelsware (one item of Markt's Goods list)
  Name, BuyPrice, SellPrice

Auftrag (one item of getContracts' list)
  Id, Type, Accepted, Fulfilled

Wegpunkt (one item of getWaypoints' list)
  Symbol, Type, System, HasShipyard, HasMarket
```

Accessor blocks (Blockly type -> German label -> record field, all colour 65,
"Zugriffe" toolbox category). The record field names are canonical English keys
(Milestone 12/bilingual support decoupled the runtime `VRecord` contract from
display language — the field is never itself shown to the player, only the
accessor block's own German/English label is):

```txt
shipName             Name aus Schiff              -> Schiff.Name
shipWaypoint         Wegpunkt aus Schiff           -> Schiff.Waypoint
shipStatus           Status aus Schiff             -> Schiff.Status
shipFuel             Treibstoff aus Schiff         -> Schiff.Fuel
shipCargoUnits       Frachteinheiten aus Schiff    -> Schiff.CargoUnits
shipCargoCapacity    Frachtkapazität aus Schiff    -> Schiff.CargoCapacity
cargoUnits           Einheiten aus Fracht          -> Fracht.Units
cargoCapacity        Kapazität aus Fracht          -> Fracht.Capacity
cargoGoods           Waren aus Fracht              -> Fracht.Goods
goodName             Name aus Ware                 -> Ware.Name
goodUnits            Einheiten aus Ware            -> Ware.Units
shipyardWaypoint     Wegpunkt aus Werft            -> Werft.Waypoint
shipyardTypes        Schiffstypen aus Werft        -> Werft.Types
shipyardTypeName     Typ aus Schiffstyp            -> Schiffstyp.Type
shipyardTypePrice    Preis aus Schiffstyp          -> Schiffstyp.Price
marketWaypoint       Wegpunkt aus Markt            -> Markt.Waypoint
marketGoods          Handelswaren aus Markt        -> Markt.Goods
tradeGoodName        Name aus Handelsware          -> Handelsware.Name
tradeGoodBuyPrice    Kaufpreis aus Handelsware     -> Handelsware.BuyPrice
tradeGoodSellPrice   Verkaufspreis aus Handelsware -> Handelsware.SellPrice
contractId           Id aus Auftrag                -> Auftrag.Id
contractType         Typ aus Auftrag               -> Auftrag.Type
contractAccepted     Angenommen aus Auftrag        -> Auftrag.Accepted
contractFulfilled    Erfüllt aus Auftrag           -> Auftrag.Fulfilled
waypointSymbolField  Symbol aus Wegpunkt           -> Wegpunkt.Symbol
waypointTypeField    Typ aus Wegpunkt              -> Wegpunkt.Type
waypointSystemField  System aus Wegpunkt           -> Wegpunkt.System
waypointHasShipyard  Hat Werft aus Wegpunkt        -> Wegpunkt.HasShipyard
waypointHasMarket    Hat Markt aus Wegpunkt        -> Wegpunkt.HasMarket
```

Registered in `blocks-catalog.ts`'s `ACCESSOR_BLOCKS` array (also exported as
`accessorFieldNames` for reference) and compiled by `Compiler.fs`'s own
`ACCESSOR_BLOCKS: Map<string, string>` — the two tables are kept in sync manually;
this doc is the source of truth for both. `Markt.Goods`'s and `Werft.Types`'s price
fields are only populated by the real API when a ship is present at that waypoint —
otherwise both fall back to a price of 0 (documented simplification, same class as
"market is always headquarters").

## Custom-block structured outputs (§9, Milestone 9/Part C)

A different, player-authored counterpart to the table above: a custom block's own
definition can plug an `sk_build_record` block into its `RETURN` socket instead of a
plain value. `sk_build_record` has its own mutator (add/remove named field rows,
each a value-input socket) and compiles to `Expr.RecordLiteral of (string * Expr)
list` — evaluated by `Eval.eval` into the same `VRecord` the 9 static records above
use, so the existing `Accessor` evaluation needs no changes.

For each field name declared on a custom block's `sk_build_record`, the client
dynamically registers one accessor block type, `accessor_<customBlockId>_<field>`,
reusing the exact `TARGET`-input/`asValue: true` shape the static accessor blocks
above use. Unlike the static table, these aren't hand-catalogued here — they're
generated at runtime (`registerCustomBlockAccessors` in `blocks.ts`,
`publishCustomBlockSignature` in `blockly-host.ts`) whenever a custom block's
signature is published to another workspace's toolbox, and compiled by `Compiler.fs`
via a dynamic match arm (`t when t.StartsWith("accessor_")`) rather than the static
`ACCESSOR_BLOCKS` map — there's no fixed list to keep in sync for these.
