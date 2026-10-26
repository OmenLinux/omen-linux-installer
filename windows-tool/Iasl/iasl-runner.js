#!/usr/bin/env node
"use strict";
/**
 * Thin CLI wrapper around the same iasl.js/iasl.wasm build omen-web uses
 * in the browser (ACPICA iASL compiled with Emscripten, see
 * https://github.com/alessandromrc/iasl-wasm). Runs unmodified under
 * Node (the module checks process.versions.node itself), so omen-prep
 * shells out to `node iasl-runner.js` instead of depending on a
 * separately-sourced native iasl.exe.
 *
 * Usage:
 *   node iasl-runner.js disasm --dsdt <path> [--ssdt <path> ...] --out <dir> [--force]
 *   node iasl-runner.js compile --dsl <path> --out <dir>
 *
 * Writes <out>/result.json ({ exitCode, ok, dslName|null, logs }) and,
 * on success, the output file (<dslName> for disasm, DSDT.aml for
 * compile) into <out>. Exit code is 0 on success, 1 otherwise.
 */
const fs = require("fs");
const path = require("path");
const createIaslModule = require("./iasl.js");

function parseArgs(argv) {
  const args = { ssdt: [] };
  for (let i = 0; i < argv.length; i++) {
    const a = argv[i];
    if (a === "--dsdt") args.dsdt = argv[++i];
    else if (a === "--dsl") args.dsl = argv[++i];
    else if (a === "--ssdt") args.ssdt.push(argv[++i]);
    else if (a === "--out") args.out = argv[++i];
    else if (a === "--force") args.force = true;
  }
  return args;
}

async function loadModule(logs) {
  return createIaslModule({
    locateFile: (p) => path.join(__dirname, p),
    print: (t) => logs.push({ stream: "stdout", text: String(t) }),
    printErr: (t) => logs.push({ stream: "stderr", text: String(t) }),
  });
}

function callMain(mod, args, logs) {
  try {
    return mod.callMain(args);
  } catch (e) {
    logs.push({ stream: "stderr", text: String(e && e.message ? e.message : e) });
    return -1;
  }
}

function writeResult(outDir, result) {
  fs.mkdirSync(outDir, { recursive: true });
  fs.writeFileSync(path.join(outDir, "result.json"), JSON.stringify(result, null, 2));
}

async function runDisasm(args) {
  const logs = [];
  const mod = await loadModule(logs);
  const FS = mod.FS;
  FS.mkdir("/work");
  FS.chdir("/work");

  FS.writeFile("tab.aml", fs.readFileSync(args.dsdt), { encoding: "binary" });

  const extNames = [];
  for (const ssdtPath of args.ssdt) {
    const name = path.basename(ssdtPath);
    FS.writeFile(name, fs.readFileSync(ssdtPath), { encoding: "binary" });
    extNames.push(name);
  }

  const cliArgs = [];
  if (extNames.length) cliArgs.push("-e", ...extNames);
  cliArgs.push(args.force ? "-df" : "-d", "tab.aml");

  const exitCode = callMain(mod, cliArgs, logs);

  const dslNames = FS.readdir(".").filter((n) => n.endsWith(".dsl")).sort();
  const dslName = dslNames.length ? dslNames[dslNames.length - 1] : null;
  const dslText = dslName ? FS.readFile(dslName, { encoding: "utf8" }) : null;

  if (dslText !== null) {
    fs.mkdirSync(args.out, { recursive: true });
    fs.writeFileSync(path.join(args.out, dslName), dslText, "utf8");
  }

  writeResult(args.out, { exitCode, ok: dslText !== null, dslName, logs });
  process.exitCode = dslText !== null ? 0 : 1;
}

async function runCompile(args) {
  const logs = [];
  const mod = await loadModule(logs);
  const FS = mod.FS;
  FS.mkdir("/work");
  FS.chdir("/work");

  FS.writeFile("dsdt.asl", fs.readFileSync(args.dsl, "utf8"), { encoding: "utf8" });

  const exitCode = callMain(mod, ["dsdt.asl"], logs);

  let amlData = null;
  try {
    amlData = FS.readFile("dsdt.aml", { encoding: "binary" });
  } catch {
    /* no output produced */
  }

  if (amlData) {
    fs.mkdirSync(args.out, { recursive: true });
    fs.writeFileSync(path.join(args.out, "DSDT.aml"), Buffer.from(amlData));
  }

  writeResult(args.out, { exitCode, ok: !!amlData, logs });
  process.exitCode = amlData ? 0 : 1;
}

async function main() {
  const [mode, ...rest] = process.argv.slice(2);
  const args = parseArgs(rest);
  if (!args.out) {
    console.error("Missing --out <dir>");
    process.exitCode = 2;
    return;
  }
  if (mode === "disasm") await runDisasm(args);
  else if (mode === "compile") await runCompile(args);
  else {
    console.error("Usage: iasl-runner.js <disasm|compile> --dsdt|--dsl <path> [--ssdt <path> ...] --out <dir> [--force]");
    process.exitCode = 2;
  }
}

main().catch((e) => {
  console.error(e);
  process.exitCode = 1;
});
