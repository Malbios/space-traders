import * as Blockly from "blockly/core";

/** Thin wrappers over Blockly's serialization module — the seam's save/load surface (§3a). */

export function serializeWorkspace(workspace: Blockly.Workspace): string {
    const state = Blockly.serialization.workspaces.save(workspace);
    return JSON.stringify(state);
}

export function loadWorkspace(workspace: Blockly.Workspace, json: string): void {
    workspace.clear();
    let state: { [key: string]: any };
    try {
        state = JSON.parse(json);
    } catch (err) {
        throw new Error(`loadWorkspace: invalid workspace JSON (${(err as Error).message}).`);
    }
    Blockly.serialization.workspaces.load(state, workspace);
}
