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
