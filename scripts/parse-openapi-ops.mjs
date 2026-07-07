import fs from "fs";

const raw = fs
    .readFileSync(
        new URL("../agent-tools/77536ed2-e18f-4d39-b79f-6f215a19e810.txt", import.meta.url),
        "utf8",
    )
    .replace(/\r?\n/g, " ");

const methods = ["get", "post", "put", "patch", "delete"];
const ops = [];

for (const method of methods) {
    const re = new RegExp(
        `"(/[^"]+)":\\{${method}":\\{[^}]*?"operationId":"([^"]+)"`,
        "g",
    );
    let m;
    while ((m = re.exec(raw))) {
        ops.push({ path: m[1], method: method.toUpperCase(), operationId: m[2] });
    }
}

ops.sort((a, b) => a.path.localeCompare(b.path) || a.method.localeCompare(b.method));

const seen = new Set();
for (const o of ops) {
    const k = `${o.method} ${o.path}`;
    if (seen.has(k)) continue;
    seen.add(k);
    console.log(`${k} | ${o.operationId}`);
}
console.log("TOTAL", seen.size);