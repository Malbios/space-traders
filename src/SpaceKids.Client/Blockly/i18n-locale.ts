import * as Blockly from "blockly/core";
import * as De from "blockly/msg/de";
import * as En from "blockly/msg/en";
import { Locale } from "./locale-state";

/** Configures Blockly's built-in UI text (§4/Milestone 12). Every custom block still
 * ships its own per-locale label/tooltip (§7, `blocks.ts`/`blocks-catalog.ts`) — this
 * only covers Blockly's own chrome (context menus, the trash can, etc.). */
export function applyBlocklyLocale(locale: Locale): void {
    Blockly.setLocale((locale === "en" ? En : De) as unknown as { [key: string]: string });
}
