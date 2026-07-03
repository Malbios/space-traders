import * as Blockly from "blockly/core";

/** Thin wrappers over Blockly's serialization module — the seam's save/load surface (§3a). */

export function serializeWorkspace(workspace: Blockly.Workspace): string {
    const state = Blockly.serialization.workspaces.save(workspace);
    return JSON.stringify(state);
}

export function loadWorkspace(workspace: Blockly.Workspace, json: string): void {
    workspace.clear();
    const state = JSON.parse(json);
    Blockly.serialization.workspaces.load(state, workspace);
}
