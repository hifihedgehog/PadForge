// Runs SignalRGB plugins against a stand-in device and prints every frame each one sends,
// so tools/verify_plugin_rows.py can hold them against PadForge's rows. Reading a plugin misses
// what it does at run time, such as a helper that reads its index as hexadecimal.
//
//   node tools/verify_plugin_rows.mjs GITHUB_FOLDER rows.tsv > ran.jsonl
//
// rows.tsv comes from PluginRowFramesDump in PadForge.Tests. The clones are signal-plugins-beta,
// signal-plugins and srgbmods-qmk-plugins under GITHUB_FOLDER. A plugin only on a branch comes out
// of git at the commits tools/gen_plugin_key_tables.py reads. Each plugin renders three times, the
// last two with nothing changed and two seconds apart, which shows whether it keeps sending.
import fs from "node:fs";
import vm from "node:vm";
import path from "node:path";
import { execFileSync } from "node:child_process";

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

// The commits the generator reads beyond the checkouts, best first.
function pins(repo, name) {
  const text = fs.readFileSync(new URL("./gen_plugin_key_tables.py", import.meta.url), "utf8");
  const list = new RegExp(name + " = \\[([\\s\\S]*?)\\n\\]").exec(text)[1];
  return [...list.matchAll(/"([0-9a-f]{40})"/g)].map(m => ({ repo, commit: m[1] }));
}
const branches = [...pins("signal-plugins", "SIGNAL_BRANCH_FILES"), ...pins("srgbmods-qmk-plugins", "QMK_BRANCHES")];
const git = (repo, ...args) => execFileSync("git", ["-C", folder + "/" + repo, ...args], { encoding: "utf8", maxBuffer: 1 << 28 });
const listed = new Map();
function branchFile(plugin) {
  for (const { repo, commit } of branches) {
    if (!listed.has(commit)) listed.set(commit, git(repo, "ls-tree", "-r", "--name-only", commit).split("\n"));
    const hit = listed.get(commit).find(f => f === plugin || f.endsWith("/" + plugin));
    if (hit) return git(repo, "show", commit + ":" + hit);
  }
  return null;
}

// Every copy of a plugin, best first, as its text and its folder.
function copies(plugin) {
  const base = plugin.split("/").pop();
  const hits = index.get(base) || [];
  const exact = hits.filter(h => h.endsWith("/" + plugin));
  const found = (exact.length ? exact.concat(hits.filter(h => !exact.includes(h))) : hits)
    .map(h => ({ text: fs.readFileSync(h, "utf8"), dir: path.dirname(h) }));
  if (found.length) return found;
  const onBranch = plugin.endsWith(".js") ? branchFile(plugin) : null;
  return onBranch ? [{ text: onBranch, dir: null }] : [];
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

// The colors the lights show, with three different channels so their order is visible. A strip's
// LEDs show one color. A light with a place shows a color made of its place, so a frame shows which
// LED has which place, and any other light a color made of where the plugin lists it. The row's
// lights show the same colors in PluginRowFramesDump.
const COLOR = [0x11, 0x22, 0x33];
const colorAtPlace = (x, y) => [x & 0xFF, y & 0xFF, 0x55];
const colorOfRank = rank => [0x10 + rank, 0x20, 0x30 + rank];

function bytes(array, length) {
  const out = new Array(Math.max(length ?? 0, array.length)).fill(0);
  for (let i = 0; i < array.length; i++) {
    const v = array[i];
    out[i] = v === undefined || v === null || Number.isNaN(v) ? 0 : (Number(v) & 0xFF);
  }
  return Buffer.from(length ? out.slice(0, length) : out).toString("hex").toUpperCase();
}

function put(out, at, text) {
  [...String(text)].forEach((c, i) => { if (at + i < out.length) out[at + i] = c.charCodeAt(0); });
  return out;
}

// What a device answers where a plugin's frames depend on it, the same answers
// PluginRowFramesDump gives the row. Null: the question comes back.
function answerOf(plugin, vendor, product, model, a, n, last) {
  const base = plugin.split("/").pop();
  const out = new Array(n).fill(0);
  const asked = (...head) => head.every((v, i) => a?.[i] === v);
  if (vendor === 0x4A4C && a?.[1] === 0x0D) return put(out, 0, product === 0xE989 ? "x,x,x,x,JLMK60V2RGB,1," : "x,x,x,x,MK69_JLRGB,1,");
  switch (base) {
    case "Nuvoton_Keyboard_Controller.js":
    case "Xiaohua_Keyboard_Controller.js":
      return a?.[1] === 0x0D ? put(out, 0, "x,x,x,x," + model + ",1,") : null;
    case "Nuvoton_Keyboard2_Controller.js":
      return asked(0x06, 0xBE, 0x02) ? put(out, 4, model) : asked(0x06, 0xBE, 0x03) ? out : null;
    case "ASUS_ROG_Claymore_II.js":
      if (asked(0x00, 0x12) && a.length === 2) { out[17] = model === "Left" ? 0x00 : 0x01; return out; }
      return null;
    case "NZXT_Function_Keyboard.js":
      if (asked(0x43, 0x81, 0x00, 0x01)) { [out[3], out[4], out[5]] = model === "new" ? [1, 3, 71] : [1, 3, 70]; return out; }
      return null;
    case "ASUS_Omni_Device.js": {
      const mouse = /^(R3MPGDD21800|SAMPGDD00EAH|R9MPGDD81805|E503FF3290C8)$/.test(model);
      if (asked(0x02, 0x12, 0x14, 0x02)) return mouse ? out : put(out, 5, model);
      if (asked(0x03, 0x12, 0x12, 0x01)) return put(out, 5, model);
      if (asked(0x02, 0x12, 0x03) || asked(0x03, 0x12, 0x00, 0x02)) { out[5] = 1; return out; }
      return null;
    }
    case "Corsair_Headset_Controller.js":
      // An answer whose first byte is 2 would say it sleeps.
      if (asked(0x02)) { (a || []).forEach((v, i) => { if (i < n) out[i] = v; }); out[0] = 0x03; return out; }
      return null;
    case "GMMK_Pro_QMK_Keyboard.js":
    case "GMMK_3_QMK_Keyboard.js":
      if (a?.length === 1 && last?.[0] === 0x00 && last[1] === 0x23) { [out[0], out[1], out[2], out[3], out[4]] = [0x00, 0x23, 0x01, 0x02, Number(model)]; return out; }
      return null;
    default:
      return null;
  }
}

function makeDevice(product, vendor, plugin, model, stripLeds, shading, log, listed) {
  let last = null;
  let answered = 0;
  let places = null;
  const uses = new Map();
  const known = {
    write(a, n) { log.push("O:" + bytes(a, n)); last = a; return n; },
    send_report(a, n) { log.push("F:" + bytes(a, n)); last = a; return n; },
    get_report(a, n) {
      const out = new Array(n).fill(0);
      if (plugin.endsWith("Sinowealth_Keyboard_Controller2.js")) { out[6] = Math.floor(Number(model) / 255); out[7] = Number(model) % 255; }
      return out;
    },
    input_report(a, n) { return new Array(n).fill(0); },
    read(a, n) {
      const answer = answerOf(plugin, vendor, product, model, a, n, last);
      if (answer) {
        // A question asked once is answered once.
        if (a?.length === 1) { last = null; answered = n; }
        return answer;
      }
      answered = 0;
      const out = new Array(n).fill(0);
      (a || []).forEach((v, i) => { if (i < n) out[i] = v; });
      return out;
    },
    getLastReadSize() { const size = answered; answered = 0; return size; },
    pause() {},
    setControllableLeds(names, positions) { places = positions; },
    addProperty(p) { known.onProperty?.(p); },
    startPicture() { uses.clear(); },
    color(x, y) {
      if (shading === "place") return colorAtPlace(x, y);
      if (shading !== "rank") return COLOR.slice();
      // Two lights at one place are its first and second use of it.
      const list = places ?? listed() ?? [];
      const key = x + "," + y;
      const use = uses.get(key) ?? 0;
      uses.set(key, use + 1);
      const at = list.flatMap((p, i) => (p[0] === x && p[1] === y ? [i] : []));
      return colorOfRank(Math.max(0, at[Math.min(use, at.length - 1)] ?? 0));
    },
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
    getHidInfo() { return { path: "", manufacturer: "", product: model, serial_number: "" }; },
    getDeviceInfo() { return { product: model }; },
    getHidEndpoints() { return []; },
    getMotherboardName() { return ""; },
    getProductName() { return ""; },
    firmwareVersion() { return ""; },
    fileExists() { return false; },
    getNumberOfChannels() { return 0; },
    channel() {
      if (!stripLeds) return universal();
      return {
        ledCount: stripLeds,
        LedCount: () => stripLeds,
        shouldPulseColors: () => false,
        getColors(kind, order = "RGB") {
          const out = [];
          for (let i = 0; i < stripLeds; i++) for (const c of order) out.push(COLOR["RGB".indexOf(c)]);
          return out;
        },
      };
    },
  };
  return new Proxy(known, { get(t, k) { return k in t ? t[k] : (() => undefined); } });
}

// A plugin's code with its imports resolved: a file beside it runs in a scope of its own, and the
// host's libraries are stand-ins.
function moduleCode(text, dir) {
  const stubs = [];
  let out = text.replace(/^\s*import\s+(?:\{([^}]*)\}|(\w+)|\*\s+as\s+(\w+))[^;\n]*;?\s*$/gm, (whole, named, single, star) => {
    const beside = /from\s+"(\.\/[^"]+)"/.exec(whole);
    if (beside && named && dir) {
      const names = named.split(",").map(n => n.trim()).filter(Boolean).join(", ");
      return `var { ${names} } = (function () {\n${moduleCode(fs.readFileSync(dir + "/" + beside[1], "utf8"), dir)}\nreturn { ${names} };\n})();`;
    }
    if (named) for (const n of named.split(",")) { const m = n.trim().split(/\s+as\s+/).pop(); if (m) stubs.push(m); }
    if (single) stubs.push(single);
    if (star) stubs.push(star);
    return "";
  });
  out = out.replace(/^\s*export\s+default\s+/gm, "var __default = ");
  // The endpoint search needs a real device's list.
  out = out.replace(/!this\.getInitialized\(\)/g, "false");
  // A comma the plugin lacks, which stops SignalRGB too.
  out = out.replace('"Fn" "Menu"', '"Fn", "Menu"');
  out = out.replace(/^(\s*)export\s+(?=(?:async\s+)?function|class|const|let|var)/gm, "$1");
  const head = stubs.map(s => `var ${s} = __universal();`).join("\n");
  return head + "\n" + out;
}

function transform(text, dir) {
  return moduleCode(text, dir) + `
;globalThis.__api = {
  Initialize: typeof Initialize === "function" ? Initialize : null,
  Render: typeof Render === "function" ? Render : null,
  LedPositions: typeof LedPositions === "function" ? LedPositions : null,
  ControllableParameters: typeof ControllableParameters === "function" ? ControllableParameters : null,
};`;
}

const rows = fs.readFileSync(process.argv[3], "utf8").split(/\r?\n/).filter(Boolean);
const done = new Set();
for (const row of rows) {
  const fields = row.split("\t");
  const [productHex, vendorHex, plugin] = fields;
  const model = fields[10] ?? "";
  const stripLeds = Number(fields[11] ?? 0);
  const shading = fields[13] ?? "flat";
  const key = productHex + "|" + plugin + "|" + model;
  if (done.has(key)) continue;
  done.add(key);
  const found = copies(plugin);
  if (!found.length) { console.log(JSON.stringify({ product: productHex, plugin, model, error: "no source" })); continue; }
  let result;
  // A copy that lacks the product leaves the next copy to try.
  for (const { text, dir } of found) {
    result = run(text, dir, productHex, vendorHex, plugin, model, stripLeds, shading);
    if (!result.error) break;
  }
  console.log(JSON.stringify(result));
}

function run(text, dir, productHex, vendorHex, plugin, model, stripLeds, shading) {
  const log = [];
  const sandbox = {
    device: makeDevice(parseInt(productHex, 16), parseInt(vendorHex, 16), plugin, model, stripLeds, shading, log, () => sandbox.__api?.LedPositions?.()),
    console: { log() {}, error() {}, warn() {}, info() {}, debug() {} },
    __universal: universal,
    DpiController: universal(),
    service: universal(),
    Assert: universal(),
    systemInfo: universal(),
    setTimeout() {},
  };
  const asBoolean = value => (value === "false" ? false : value === "true" ? true : value);
  sandbox.device.onProperty = p => { if (p?.property) sandbox[p.property] = asBoolean(p.default); };
  const context = vm.createContext(sandbox);
  const result = { product: productHex, plugin, model };
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
    vm.runInContext(transform(text, dir), context, { timeout: 4000, filename: path.basename(plugin) });
    const api = sandbox.__api;
    const params = api.ControllableParameters ? api.ControllableParameters() : [];
    for (const p of params) {
      // The host reads a boolean's default as a boolean.
      if (p && p.property) sandbox[p.property] = asBoolean(p.default);
    }
    for (const name of ["LightingMode", "shutdownColor", "forcedColor"]) {
      if (!(name in sandbox)) sandbox[name] = name === "LightingMode" ? "Canvas" : "#009bde";
    }
    if (api.Initialize) guarded("__api.Initialize()");
    result.init = log.splice(0);
    if (api.Render) {
      sandbox.device.startPicture();
      guarded("__api.Render()");
      result.render = log.splice(0);
      vm.runInContext("var __now = Date.now(); Date.now = () => __now;", context);
      guarded("__api.Render()");
      result.again = log.splice(0).length;
      vm.runInContext("__now += 2000;", context);
      guarded("__api.Render()");
      result.later = log.splice(0).length;
    }
  } catch (e) {
    result.error = String(e && e.message || e).slice(0, 200);
    result.init = result.init ?? log.splice(0);
  }
  return result;
}
