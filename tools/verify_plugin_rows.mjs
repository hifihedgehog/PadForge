// Runs SignalRGB plugins against a stand-in device and prints every frame each one sends,
// so tools/verify_plugin_rows.py can hold them against PadForge's rows. Reading a plugin misses
// what it does at run time, such as a helper that reads its index as hexadecimal.
//
//   node tools/verify_plugin_rows.mjs GITHUB_FOLDER rows.tsv > ran.jsonl
//
// rows.tsv comes from PluginRowFramesDump in PadForge.Tests. The clones are signal-plugins-beta,
// signal-plugins and srgbmods-qmk-plugins under GITHUB_FOLDER.
import fs from "node:fs";
import vm from "node:vm";
import path from "node:path";

const folder = process.argv[2];
const roots = [folder + "/srgbmods-qmk-plugins", folder + "/signal-plugins-beta/Plugins", folder + "/signal-plugins/Plugins"];

const index = new Map();
function walk(dir) {
  for (const e of fs.readdirSync(dir, { withFileTypes: true })) {
    const full = dir + "/" + e.name;
    if (e.isDirectory()) { if (e.name !== ".git") walk(full); }
    else if (e.name.endsWith(".js")) { if (!index.has(e.name)) index.set(e.name, []); index.get(e.name).push(full); }
  }
}
for (const r of roots) { if (fs.existsSync(r)) walk(r); }

function find(plugin) {
  const base = plugin.split("/").pop();
  const hits = index.get(base) || [];
  return hits.find(h => h.endsWith("/" + plugin)) || hits[0] || null;
}

function universal() {
  const f = function () { return universal(); };
  return new Proxy(f, {
    get(_, k) {
      if (k === Symbol.toPrimitive) return () => 0;
      if (k === "length") return 0;
      return universal();
    },
    apply() { return universal(); },
    construct() { return universal(); },
  });
}

// The color every light shows, with three different channels so their order is visible.
const COLOR = [0x11, 0x22, 0x33];

function bytes(array, length) {
  const out = new Array(Math.max(length ?? 0, array.length)).fill(0);
  for (let i = 0; i < array.length; i++) {
    const v = array[i];
    out[i] = v === undefined || v === null || Number.isNaN(v) ? 0 : (Number(v) & 0xFF);
  }
  return Buffer.from(length ? out.slice(0, length) : out).toString("hex").toUpperCase();
}

function makeDevice(product, vendor, log) {
  const known = {
    write(a, n) { log.push("O:" + bytes(a, n)); return n; },
    send_report(a, n) { log.push("F:" + bytes(a, n)); return n; },
    get_report(a, n) { return new Array(n).fill(0); },
    input_report(a, n) { return new Array(n).fill(0); },
    // A read sends the question back. KBM's plugin needs its model named.
    read(a, n) {
      const out = new Array(n).fill(0);
      if (vendor === 0x4A4C && a && a[1] === 0x0D) {
        const model = product === 0xE989 ? "x,x,x,x,JLMK60V2RGB,1," : "x,x,x,x,MK69_JLRGB,1,";
        [...model].forEach((c, i) => { out[i] = c.charCodeAt(0); });
        return out;
      }
      (a || []).forEach((v, i) => { if (i < n) out[i] = v; });
      return out;
    },
    getLastReadSize() { return 0; },
    pause() {},
    color() { return COLOR.slice(); },
    productId() { return product; },
    vendorId() { return vendor; },
    createColorArray(color, count) {
      const out = [];
      const c = /^#?([a-f\d]{2})([a-f\d]{2})([a-f\d]{2})$/i.exec(String(color));
      for (let i = 0; i < count; i++) out.push(parseInt(c?.[1] ?? "0", 16), parseInt(c?.[2] ?? "0", 16), parseInt(c?.[3] ?? "0", 16));
      return out;
    },
    log() {},
    notify() {},
    getHidInfo() { return { path: "", manufacturer: "", product: "", serial_number: "" }; },
    getHidEndpoints() { return []; },
    getMotherboardName() { return ""; },
    getProductName() { return ""; },
    firmwareVersion() { return ""; },
    fileExists() { return false; },
    getNumberOfChannels() { return 0; },
    channel() { return universal(); },
  };
  return new Proxy(known, { get(t, k) { return k in t ? t[k] : (() => undefined); } });
}

function transform(text) {
  const stubs = [];
  let out = text.replace(/^\s*import\s+(?:\{([^}]*)\}|(\w+)|\*\s+as\s+(\w+))[^;\n]*;?\s*$/gm, (_, named, single, star) => {
    if (named) for (const n of named.split(",")) { const m = n.trim().split(/\s+as\s+/).pop(); if (m) stubs.push(m); }
    if (single) stubs.push(single);
    if (star) stubs.push(star);
    return "";
  });
  out = out.replace(/^\s*export\s+default\s+/gm, "var __default = ");
  // The endpoint search needs a real device's list.
  out = out.replace(/!this\.getInitialized\(\)/g, "false");
  out = out.replace(/^(\s*)export\s+(?=(?:async\s+)?function|class|const|let|var)/gm, "$1");
  const head = stubs.map(s => `var ${s} = __universal();`).join("\n");
  return head + "\n" + out + `
;globalThis.__api = {
  Initialize: typeof Initialize === "function" ? Initialize : null,
  Render: typeof Render === "function" ? Render : null,
  ControllableParameters: typeof ControllableParameters === "function" ? ControllableParameters : null,
};`;
}

const rows = fs.readFileSync(process.argv[3], "utf8").split(/\r?\n/).filter(Boolean);
const done = new Set();
for (const row of rows) {
  const [productHex, vendorHex, plugin] = row.split("\t");
  const key = productHex + "|" + plugin;
  if (done.has(key)) continue;
  done.add(key);
  const file = find(plugin);
  if (!file) { console.log(JSON.stringify({ product: productHex, plugin, error: "no source" })); continue; }
  const log = [];
  const sandbox = {
    device: makeDevice(parseInt(productHex, 16), parseInt(vendorHex, 16), log),
    console: { log() {}, error() {}, warn() {}, info() {}, debug() {} },
    __universal: universal,
    DpiController: universal(),
    service: universal(),
    Assert: universal(),
    systemInfo: universal(),
    setTimeout() {},
  };
  const context = vm.createContext(sandbox);
  const result = { product: productHex, plugin };
  // A global a plugin reads that the host sets is defined as the run finds it.
  function guarded(code) {
    for (let tries = 0; tries < 40; tries++) {
      try {
        vm.runInContext(code, context, { timeout: 4000 });
        return;
      } catch (e) {
        const m = /^(\w+) is not defined$/.exec(String(e && e.message || ""));
        if (!m) throw e;
        sandbox[m[1]] = /^(battery|dpi|permissions|lcd|systemInfo|mouse|keyboard|macroInputArray|deviceDiscovery)/i.test(m[1]) ? universal() : false;
        log.length = 0;
      }
    }
    throw new Error("too many undefined names");
  }
  try {
    vm.runInContext(transform(fs.readFileSync(file, "utf8")), context, { timeout: 4000, filename: path.basename(file) });
    const api = sandbox.__api;
    const params = api.ControllableParameters ? api.ControllableParameters() : [];
    for (const p of params) {
      if (p && p.property) sandbox[p.property] = p.default;
    }
    for (const name of ["LightingMode", "shutdownColor", "forcedColor"]) {
      if (!(name in sandbox)) sandbox[name] = name === "LightingMode" ? "Canvas" : "#009bde";
    }
    if (api.Initialize) guarded("__api.Initialize()");
    result.init = log.splice(0);
    if (api.Render) guarded("__api.Render()");
    result.render = log.splice(0);
  } catch (e) {
    result.error = String(e && e.message || e).slice(0, 200);
    result.init = result.init ?? log.splice(0);
  }
  console.log(JSON.stringify(result));
}
