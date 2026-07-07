import fs from "fs";

const raw = fs
    .readFileSync(
        new URL("../agent-tools/77536ed2-e18f-4d39-b79f-6f215a19e810.txt", import.meta.url),
        "utf8",
    )
    .replace(/\r?\n/g, " ");

const methods = ["get", "post", "put", "patch", "delete"];
const ops = new Map();

for (const method of methods) {
    const re = new RegExp(`"(/[^"]+)":\\{${method}":\\{operationId":"([^"]+)"`, "g");
    let m;
    while ((m = re.exec(raw))) {
        const key = `${method.toUpperCase()} ${m[1]}`;
        ops.set(key, m[2]);
    }
}

const sorted = [...ops.entries()].sort((a, b) => a[0].localeCompare(b[0]));
for (const [k, id] of sorted) console.log(`${k} | ${id}`);
console.log("TOTAL", sorted.length);