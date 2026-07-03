import * as Blockly from "blockly/core";
import * as De from "blockly/msg/de";

/** Configures Blockly's built-in UI text (§4). Every custom block still ships its own German label/tooltip (§7) — this only covers Blockly's own chrome. */
export function applyGermanLocale(): void {
    Blockly.setLocale(De as unknown as { [key: string]: string });
}
