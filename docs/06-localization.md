# Localization glossary

Stable German/English terminology for SpaceKids. Internal code, DSL field keys, API
client names, and database columns stay English; child-facing Blockly labels and UI
strings use these terms (see `plan.md` §4–§5 and Milestone 12).

| Internal / API term | German visual term | English visual term |
| ------------------- | ------------------ | ------------------- |
| agent               | Kapitän            | Captain             |
| ship                | Schiff             | Ship                |
| fleet               | Flotte             | Fleet               |
| waypoint            | Wegpunkt           | Waypoint            |
| system              | Sternensystem      | Star system         |
| contract            | Auftrag            | Contract            |
| cargo               | Fracht             | Cargo               |
| credits             | Credits            | Credits             |
| orbit               | Umlaufbahn         | Orbit               |
| dock                | Andocken           | Dock                |
| navigate            | Fliegen            | Fly                 |
| extract             | Abbauen            | Extract             |
| trade               | Handeln            | Trade               |
| buy                 | Kaufen             | Buy                 |
| sell                | Verkaufen          | Sell                |
| deliver             | Liefern            | Deliver             |
| survey              | Scannen            | Survey              |
| fuel                | Treibstoff         | Fuel                |
| cooldown            | Abklingzeit        | Cooldown            |
| asteroid field      | Asteroidenfeld     | Asteroid field      |
| marketplace         | Marktplatz         | Marketplace         |
| shipyard            | Werft              | Shipyard            |
| custom block        | Eigener Block      | Custom block        |
| block workshop      | Blockwerkstatt     | Block workshop      |
| with ship (scope)   | mit Schiff         | with ship           |
| parallel branches   | parallel           | parallel            |
| pilot               | Pilot              | Pilot               |

## Example sentences (German)

Use short, concrete wording in logs and tooltips:

```txt
Das Schiff fliegt zum Wegpunkt.
Das Schiff wartet auf Treibstoff.
Die Fracht ist voll.
Der Auftrag braucht noch 8 Einheiten Kupfer.
Schiff FAKE-AGENT-2 wartet auf ein anderes Programm.
```

## Runtime locale

A single process-wide setting (`app_settings.locale`) picks German or English for
Blockly chrome, catalog block labels, `Main.fs` UI strings, and most server-side
validation messages. DSL `VRecord` field keys remain canonical English (`Fuel`,
`Waypoint`, …) regardless of display language.