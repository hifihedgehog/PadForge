// PadForge browser gamepad forwarding (discussion #402, issue #415).
//
// A controller paired to this phone, or built into this handheld, is read
// through the Gamepad API and forwarded to PadForge as a "Browser Gamepad"
// device, one WebSocket per pad. The wire is the web controller's own:
// {type:"input", kind:"button"|"axis"|"pov", code, value} on PadForge's
// normalized slots, and {type:"caps"} once.
//
// Freshness is acknowledged, never assumed. The server sends {type:"ping",
// n} once a second. This page echoes {type:"hb", n} only while it is
// visible, has sampled the pad within the last half second, and its socket
// is not congested. The server counts a pad fresh only on a timely echo, so
// a slow link draining old presses cannot keep a dead page alive. While a
// pad is expired the server drops its input and holds everything released.
// When a fresh echo arrives the server sends {type:"resync"} and this page
// resends everything it holds. The ping also carries the slot's current
// rumble, so rumble here is a lease renewed by pings, not a latch.
(function () {
    "use strict";

    // ── Constants ──────────────────────────────────────────────────────
    // Standard mapping (W3C Gamepad, "standard"): browser button index to
    // PadForge slot. Triggers (6, 7) and the D-pad (12..15) are handled
    // separately: triggers are analog axes 2 and 5, the D-pad is one hat.
    var STD_BUTTON_TO_SLOT = { 0: 0, 1: 1, 2: 2, 3: 3, 4: 4, 5: 5, 8: 6, 9: 7, 10: 8, 11: 9, 16: 10 };
    var STD_TRIGGER_TO_AXIS = { 6: 2, 7: 5 };
    var STD_AXIS_TO_SERVER = [0, 1, 3, 4];
    // Extra browser buttons (17 and up) in standard mode land on PadForge's
    // extended slots in this order. Slot 16 is the touchpad click and is
    // never assigned. The server's GamepadExtendedSlots mirrors this table.
    var EXTRA_SLOTS = [11, 12, 13, 14, 15, 17, 18, 19, 20, 21];
    var STANDARD_BUTTON_COUNT = 17;
    var MAX_RAW_BUTTON_SLOT = 21;   // raw mode: browser index i -> slot i, skipping 16
    var MAX_AXES = 6;
    var FRESH_SAMPLE_MS = 500;       // an echo needs a poll this recent
    var RUMBLE_EFFECT_MS = 250;
    var RUMBLE_RENEW_MS = 100;
    var RUMBLE_LEASE_MS = 3000;      // no ping this long: rumble stops
    var BUFFER_LIMIT_BYTES = 64 * 1024;
    var CONGESTION_REPLACE_MS = 3000; // a socket over the limit this long is replaced
    var IDENTITY_CLAIM_MS = 150;

    // ── Identity: per tab, and claimed across tabs ─────────────────────
    // sessionStorage is per tab, but a duplicated tab or an opener-created
    // window starts with a copy of it. On load the page claims its token on
    // a BroadcastChannel. A tab already holding the same token answers, and
    // the newcomer mints its own, so two tabs never replace each other's
    // server sessions under one key.
    var clientIdKey = "padforge_gamepad_client_id";
    var clientId = null;
    try { clientId = sessionStorage.getItem(clientIdKey); } catch (e) { }
    function mintClientId() {
        var id = "gp-" + Math.random().toString(36).slice(2, 10) + Date.now().toString(36);
        try { sessionStorage.setItem(clientIdKey, id); } catch (e) { }
        return id;
    }
    if (!clientId) clientId = mintClientId();
    var identityChannel = null;
    function settleIdentity(done) {
        try { identityChannel = new BroadcastChannel("padforge_gamepad_identity"); } catch (e) { identityChannel = null; }
        if (!identityChannel) { done(); return; }
        var nonce = Math.random().toString(36).slice(2);
        var taken = false;
        identityChannel.onmessage = function (ev) {
            var m = ev.data || {};
            if (m.nonce === nonce) return;
            if (m.claim === clientId) identityChannel.postMessage({ taken: clientId, nonce: nonce });
            else if (m.taken === clientId) taken = true;
        };
        identityChannel.postMessage({ claim: clientId, nonce: nonce });
        setTimeout(function () { if (taken) clientId = mintClientId(); done(); }, IDENTITY_CLAIM_MS);
    }

    // ── Pure translation (the server-side contract test pins these tables) ──
    function quantizeStick(v) {
        if (typeof v !== "number" || isNaN(v)) v = 0;
        if (v < -1) v = -1; else if (v > 1) v = 1;
        // Piecewise so neutral is exactly 32767 and the ends are 0 and 65535.
        var raw = v < 0 ? Math.round(32767 + v * 32767) : Math.round(32767 + v * 32768);
        return raw < 0 ? 0 : raw > 65535 ? 65535 : raw;
    }
    function quantizeTrigger(v) {
        if (typeof v !== "number" || isNaN(v)) v = 0;
        if (v < 0) v = 0; else if (v > 1) v = 1;
        return Math.round(v * 65535);
    }
    // Opposing directions cancel: up+down is neutral on that axis.
    function composePov(up, down, left, right) {
        var x = (right ? 1 : 0) - (left ? 1 : 0);
        var y = (down ? 1 : 0) - (up ? 1 : 0);
        if (x === 0 && y === 0) return -1;
        var deg = Math.atan2(x, -y) * 180 / Math.PI;   // 0 = north, 90 = east
        if (deg < 0) deg += 360;
        return (Math.round(deg / 45) % 8) * 4500;
    }
    function rawButtonSlot(i) {
        // Raw mode: index i is slot i, slot 16 (touchpad click) is skipped, so
        // 16 and up shift by one, and anything past 21 is dropped.
        var slot = i < 16 ? i : i + 1;
        return slot <= MAX_RAW_BUTTON_SLOT ? slot : -1;
    }
    function isPressed(b) {
        if (b == null) return false;
        if (typeof b === "object") return !!b.pressed;
        return b > 0.5;
    }
    function buttonValue(b) {
        if (b == null) return 0;
        if (typeof b === "object") return typeof b.value === "number" ? b.value : (b.pressed ? 1 : 0);
        return b;
    }
    // Translate one Gamepad sample into {buttons:{slot:0|1}, axes:{code:raw}, pov:int|null}.
    function translate(gp, mode) {
        var out = { buttons: {}, axes: {}, pov: null, droppedButtons: 0, droppedAxes: 0 };
        var buttons = gp.buttons || [], axes = gp.axes || [];
        if (mode === "standard") {
            for (var i = 0; i < buttons.length; i++) {
                if (STD_BUTTON_TO_SLOT.hasOwnProperty(i)) out.buttons[STD_BUTTON_TO_SLOT[i]] = isPressed(buttons[i]) ? 1 : 0;
                else if (STD_TRIGGER_TO_AXIS.hasOwnProperty(i)) out.axes[STD_TRIGGER_TO_AXIS[i]] = quantizeTrigger(buttonValue(buttons[i]));
                else if (i >= 12 && i <= 15) { /* composed below */ }
                else if (i >= STANDARD_BUTTON_COUNT) {
                    var k = i - STANDARD_BUTTON_COUNT;
                    if (k < EXTRA_SLOTS.length) out.buttons[EXTRA_SLOTS[k]] = isPressed(buttons[i]) ? 1 : 0;
                    else out.droppedButtons++;
                }
            }
            out.pov = composePov(isPressed(buttons[12]), isPressed(buttons[13]), isPressed(buttons[14]), isPressed(buttons[15]));
            for (var a = 0; a < axes.length; a++) {
                if (a < STD_AXIS_TO_SERVER.length) out.axes[STD_AXIS_TO_SERVER[a]] = quantizeStick(axes[a]);
                else out.droppedAxes++;
            }
        } else {
            // Raw: every axis is forwarded as the browser reports it, on a
            // centered scale (-1 -> 0, 0 -> 32767, +1 -> 65535). A trigger the
            // pad reports as an axis at -1 reads at the low end at rest. The
            // server's timeout neutral centers every raw axis until this page
            // reports again.
            for (var j = 0; j < buttons.length; j++) {
                var slot = rawButtonSlot(j);
                if (slot < 0) out.droppedButtons++;
                else out.buttons[slot] = isPressed(buttons[j]) ? 1 : 0;
            }
            for (var b = 0; b < axes.length; b++) {
                if (b < MAX_AXES) out.axes[b] = quantizeStick(axes[b]);
                else out.droppedAxes++;
            }
        }
        return out;
    }
    function neutralFor(mode, buttonCount, axisCount) {
        var fake = { buttons: [], axes: [] };
        for (var i = 0; i < buttonCount; i++) fake.buttons.push({ pressed: false, value: 0 });
        for (var a = 0; a < axisCount; a++) fake.axes.push(0);
        return translate(fake, mode);
    }

    // Exposed for the contract test host and the page's own self-check.
    window.PadForgeGamepad = {
        quantizeStick: quantizeStick, quantizeTrigger: quantizeTrigger,
        composePov: composePov, rawButtonSlot: rawButtonSlot, translate: translate, neutralFor: neutralFor,
        EXTRA_SLOTS: EXTRA_SLOTS, STD_BUTTON_TO_SLOT: STD_BUTTON_TO_SLOT,
        STD_TRIGGER_TO_AXIS: STD_TRIGGER_TO_AXIS, STD_AXIS_TO_SERVER: STD_AXIS_TO_SERVER
    };

    // ── Forwarding slots ───────────────────────────────────────────────
    // A slot is a logical forwarded device: reused for a pad that returns
    // with the same Gamepad.id, otherwise the lowest free one. The browser
    // index is only the live lookup key. Two pads with identical ids that
    // both leave and return in the other order swap slots, which the API
    // cannot prevent.
    var slots = [];
    var pollHandle = null;
    var lastPollTs = 0;
    var vibrateFn = navigator.vibrate ? navigator.vibrate.bind(navigator) : null;
    var phoneVibrate = { timer: null, level: 0 };
    var wake = { sentinel: null, pending: false, wanted: false };
    var lastRender = "";

    function now() { return (window.performance && performance.now) ? performance.now() : Date.now(); }
    function el(id) { return document.getElementById(id); }
    function setStatus(text) { var s = el("status"); if (s && s.textContent !== text) s.textContent = text; }

    function findSlotByIndex(index) {
        for (var i = 0; i < slots.length; i++) if (slots[i].live && slots[i].index === index) return slots[i];
        return null;
    }
    function claimSlot(gp) {
        for (var i = 0; i < slots.length; i++)
            if (!slots[i].live && slots[i].id === gp.id) return slots[i];
        for (var j = 0; j < slots.length; j++)
            if (!slots[j].live) return slots[j];
        var s = { n: slots.length + 1, id: gp.id, live: false, ws: null, gen: 0, sent: null, needSnapshot: true,
                  rumbleTimer: null, rumbleGen: 0, rumbleLeft: 0, rumbleRight: 0, actuatorFailed: false,
                  lastPingTs: 0, phoneLevel: 0, overSince: 0, dropped: 0, serverName: null };
        slots.push(s);
        return s;
    }
    function anyLive() { for (var i = 0; i < slots.length; i++) if (slots[i].live) return true; return false; }
    function isOpen(slot) { return !!slot.ws && slot.ws.readyState === WebSocket.OPEN; }

    function wsUrlFor(slot) {
        var proto = location.protocol === "https:" ? "wss:" : "ws:";
        return proto + "//" + location.host + "/ws?id=" + encodeURIComponent(clientId + "-g" + slot.n)
            + "&layout=gamepad&mode=" + slot.mode + "&buttons=" + slot.buttons + "&axes=" + slot.axes;
    }

    function retireSocket(ws) {
        // Any state: a CONNECTING socket closes on open, an OPEN one now.
        try { ws.close(); } catch (e) { }
    }

    function openSlot(slot, gp) {
        slot.id = gp.id;
        slot.index = gp.index;
        slot.mode = gp.mapping === "standard" ? "standard" : "raw";
        slot.buttons = (gp.buttons || []).length;
        slot.axes = (gp.axes || []).length;
        slot.live = true;
        slot.sent = null;
        slot.needSnapshot = true;
        slot.overSince = 0;
        slot.lastPingTs = 0;
        slot.actuatorFailed = false;      // a new connection gets the actuator a fresh try
        setRumble(slot, 0, 0);
        if (slot.ws) retireSocket(slot.ws);
        slot.gen++;
        var gen = slot.gen;
        var ws;
        try { ws = new WebSocket(wsUrlFor(slot)); }
        catch (e) { setStatus("Could not open the connection: " + e.message); slot.live = false; return; }
        slot.ws = ws;
        ws.onopen = function () {
            if (slot.gen !== gen || slot.ws !== ws) { retireSocket(ws); return; }
            var actuator = gp.vibrationActuator;
            var canRumble = actuatorUsable(actuator) || !!vibrateFn;
            sendOn(slot, { type: "caps", vibrate: canRumble, mapping: gp.mapping || "", buttons: slot.buttons, axes: slot.axes });
            slot.sent = null;          // a new server device starts neutral: resend everything
            slot.needSnapshot = true;
            slot.lastPingTs = now();   // the lease starts at open, the first ping is a second away
            requestWakeLock();
            renderPads(true);
        };
        ws.onmessage = function (ev) {
            if (slot.gen !== gen) return;
            var msg; try { msg = JSON.parse(ev.data); } catch (e) { return; }
            if (msg.type === "ping") onPing(slot, msg);
            else if (msg.type === "connected") { slot.serverName = msg.name; renderPads(true); }
            else if (msg.type === "resync") { slot.sent = null; slot.needSnapshot = true; }
            else if (msg.type === "rumble") applyRumble(slot, msg.left | 0, msg.right | 0);
        };
        ws.onclose = function () {
            if (slot.gen !== gen) return;
            setRumble(slot, 0, 0);
            slot.ws = null;
            if (slot.live) setTimeout(function () {
                if (slot.gen !== gen || !slot.live || document.hidden || slot.ws) return;
                var g = liveGamepad(slot);
                if (g) openSlot(slot, g);
            }, 3000);
            renderPads(true);
        };
        ws.onerror = function () { retireSocket(ws); };
    }

    function closeSlot(slot) {
        setRumble(slot, 0, 0);
        if (slot.ws) {
            if (isOpen(slot)) sendNeutral(slot);
            retireSocket(slot.ws);      // CONNECTING sockets too
        }
        slot.gen++;                     // orphan every pending handler
        slot.ws = null;
        slot.live = false;
        if (!anyLive()) releaseWakeLock();
        renderPads(true);
    }

    function sendOn(slot, obj) {
        var ws = slot.ws;
        if (!ws || ws.readyState !== WebSocket.OPEN) return false;
        ws.send(JSON.stringify(obj));
        return true;
    }

    function sendNeutral(slot) {
        var n = neutralFor(slot.mode, slot.buttons, slot.axes);
        var k;
        for (k in n.buttons) sendOn(slot, { type: "input", kind: "button", code: +k, value: 0 });
        for (k in n.axes) sendOn(slot, { type: "input", kind: "axis", code: +k, value: n.axes[k] });
        if (n.pov !== null) sendOn(slot, { type: "input", kind: "pov", code: 0, value: -1 });
        slot.sent = null;
        slot.needSnapshot = true;
    }

    // The server's once-a-second ping. The echo says "this page is sampling
    // the pad right now": not while hidden, not while the socket is
    // congested, not when the last successful poll is older than
    // FRESH_SAMPLE_MS. The ping also carries the slot's current rumble,
    // which renews the rumble lease and restores rumble after a gap.
    function onPing(slot, msg) {
        slot.lastPingTs = now();
        if (typeof msg.l === "number" && typeof msg.r === "number") applyRumble(slot, msg.l | 0, msg.r | 0);
        if (document.hidden) return;
        if (now() - lastPollTs > FRESH_SAMPLE_MS) return;
        if (slot.ws && slot.ws.bufferedAmount > BUFFER_LIMIT_BYTES) return;
        sendOn(slot, { type: "hb", n: msg.n });
    }

    function liveGamepad(slot) {
        var pads;
        try { pads = navigator.getGamepads ? navigator.getGamepads() : []; } catch (e) { return null; }
        var gp = pads && pads[slot.index];
        return gp && gp.connected ? gp : null;
    }

    // ── Poll loop: emit only what changed, or everything after a (re)open ──
    function poll() {
        pollHandle = requestAnimationFrame(poll);
        if (document.hidden) return;
        var pads;
        try { pads = navigator.getGamepads ? navigator.getGamepads() : []; } catch (e) { return; }
        lastPollTs = now();
        var i, gp, dirty = false;
        for (i = 0; i < pads.length; i++) {
            gp = pads[i];
            if (!gp || !gp.connected) continue;
            if (!findSlotByIndex(gp.index)) { var s = claimSlot(gp); openSlot(s, gp); dirty = true; }
        }
        for (i = 0; i < slots.length; i++) {
            var slot = slots[i];
            if (!slot.live) continue;
            gp = pads[slot.index];
            if (!gp || !gp.connected) continue;
            var ws = slot.ws;
            if (!ws || ws.readyState !== WebSocket.OPEN) continue;
            if (ws.bufferedAmount > BUFFER_LIMIT_BYTES) {
                // Input behind a slowly draining queue is stale by the time it
                // lands. Stop queuing. If the queue does not clear in
                // CONGESTION_REPLACE_MS, open a replacement socket at once
                // rather than waiting for the old one to drain and close: the
                // server retires the old session under the same key, and the
                // new one starts with a snapshot.
                slot.needSnapshot = true;
                if (!slot.overSince) slot.overSince = now();
                else if (now() - slot.overSince > CONGESTION_REPLACE_MS) { openSlot(slot, gp); dirty = true; }
                continue;
            }
            slot.overSince = 0;
            var t = translate(gp, slot.mode);
            var full = slot.needSnapshot || !slot.sent;
            if (full) slot.sent = { buttons: {}, axes: {}, pov: undefined };
            var k;
            for (k in t.buttons) if (full || slot.sent.buttons[k] !== t.buttons[k]) {
                if (sendOn(slot, { type: "input", kind: "button", code: +k, value: t.buttons[k] })) slot.sent.buttons[k] = t.buttons[k];
            }
            for (k in t.axes) if (full || slot.sent.axes[k] !== t.axes[k]) {
                if (sendOn(slot, { type: "input", kind: "axis", code: +k, value: t.axes[k] })) slot.sent.axes[k] = t.axes[k];
            }
            if (t.pov !== null && (full || slot.sent.pov !== t.pov)) {
                if (sendOn(slot, { type: "input", kind: "pov", code: 0, value: t.pov })) slot.sent.pov = t.pov;
            }
            slot.needSnapshot = false;
            var dropped = t.droppedButtons + t.droppedAxes;
            if (dropped !== slot.dropped) { slot.dropped = dropped; dirty = true; }
        }
        if (dirty) renderPads(true);
    }

    // ── Rumble: the pad's own actuator, else the phone's vibrator ──────
    function actuatorUsable(actuator) {
        if (!actuator || typeof actuator.playEffect !== "function") return false;
        // Chromium lists the effects it can play. When the list exists and
        // lacks dual-rumble, empty included, the actuator cannot serve us.
        if (Array.isArray(actuator.effects) && actuator.effects.indexOf("dual-rumble") < 0) return false;
        return true;
    }
    function leaseAlive(slot) { return slot.live && now() - slot.lastPingTs <= RUMBLE_LEASE_MS; }
    // A rumble from the server, or carried by a ping. Hidden pages rumble
    // nothing, and a value equal to the current one is not restarted.
    function applyRumble(slot, left, right) {
        if (document.hidden) { left = 0; right = 0; }
        if (left === slot.rumbleLeft && right === slot.rumbleRight) return;
        setRumble(slot, left, right);
    }
    function stopRumbleTimer(slot) {
        if (slot.rumbleTimer) { clearInterval(slot.rumbleTimer); slot.rumbleTimer = null; }
    }
    function setRumble(slot, left, right) {
        slot.rumbleLeft = left; slot.rumbleRight = right;
        var gp = slot.live ? liveGamepad(slot) : null;
        var actuator = gp && gp.vibrationActuator;
        var strong = Math.max(0, Math.min(1, left / 65535)), weak = Math.max(0, Math.min(1, right / 65535));
        stopRumbleTimer(slot);
        slot.rumbleGen++;
        var myGen = slot.rumbleGen;
        if (actuatorUsable(actuator) && !slot.actuatorFailed) {
            if (strong === 0 && weak === 0) {
                if (typeof actuator.reset === "function") { try { actuator.reset().catch(function () { }); } catch (e) { } }
                return;
            }
            var fail = function () {
                // Only this request's own failure may touch this request's timer,
                // and a failed actuator hands the pad to the phone's vibrator
                // until the pad's next connection.
                if (slot.rumbleGen !== myGen) return;
                stopRumbleTimer(slot);
                slot.actuatorFailed = true;
                setRumble(slot, left, right);
            };
            var fire = function () {
                if (slot.rumbleGen !== myGen) return;
                if (!leaseAlive(slot)) { stopRumbleTimer(slot); slot.rumbleLeft = 0; slot.rumbleRight = 0; return; }
                try {
                    actuator.playEffect("dual-rumble", { startDelay: 0, duration: RUMBLE_EFFECT_MS, strongMagnitude: strong, weakMagnitude: weak })
                        .then(function (r) {
                            // "complete" and "preempted" are the normal results of a renewed
                            // effect. Chromium resolves failures such as "not-supported".
                            if (r !== "complete" && r !== "preempted") fail();
                        }, fail);
                } catch (e) { fail(); }
            };
            fire();
            slot.rumbleTimer = setInterval(fire, RUMBLE_RENEW_MS);
            return;
        }
        // Phone vibrator, shared by every pad without an actuator: the level is
        // the strongest request, so one pad stopping never cancels another's.
        slot.phoneLevel = Math.max(strong, weak);
        refreshPhoneVibrate();
    }
    function refreshPhoneVibrate() {
        var level = 0;
        for (var i = 0; i < slots.length; i++) {
            var s = slots[i];
            if (!s.live || !s.phoneLevel) continue;
            if (!leaseAlive(s)) { s.phoneLevel = 0; s.rumbleLeft = 0; s.rumbleRight = 0; continue; }
            level = Math.max(level, s.phoneLevel);
        }
        phoneVibrate.level = level;
        if (!vibrateFn) return;
        if (level === 0) { if (phoneVibrate.timer) { clearInterval(phoneVibrate.timer); phoneVibrate.timer = null; } try { vibrateFn(0); } catch (e) { } return; }
        if (!phoneVibrate.timer) {
            var pulse = function () {
                refreshPhoneVibrate();          // re-evaluates the leases, stops itself at zero
                if (phoneVibrate.level && !document.hidden) { try { vibrateFn(Math.round(phoneVibrate.level * 200)); } catch (e) { } }
            };
            pulse();
            phoneVibrate.timer = setInterval(pulse, 150);
        }
    }
    function stopAllRumble() {
        for (var i = 0; i < slots.length; i++) if (slots[i].live) setRumble(slots[i], 0, 0);
        if (phoneVibrate.timer) { clearInterval(phoneVibrate.timer); phoneVibrate.timer = null; }
    }

    // ── Wake lock: one per page, serialized, only where the API exists ──
    function requestWakeLock() {
        wake.wanted = true;
        if (!("wakeLock" in navigator) || wake.sentinel || wake.pending || document.hidden) return;
        var p;
        try { p = navigator.wakeLock.request("screen"); } catch (e) { return; }
        wake.pending = true;
        p.then(function (sentinel) {
            wake.pending = false;
            if (!wake.wanted || !anyLive() || wake.sentinel) { sentinel.release().catch(function () { }); return; }
            wake.sentinel = sentinel;
            sentinel.addEventListener("release", function () { if (wake.sentinel === sentinel) wake.sentinel = null; renderPads(true); });
            renderPads(true);
        }, function () { wake.pending = false; });
    }
    function releaseWakeLock() {
        wake.wanted = false;
        var s = wake.sentinel;
        wake.sentinel = null;
        if (s) { try { s.release().catch(function () { }); } catch (e) { } }
    }

    // ── Visibility ────────────────────────────────────────────────────
    document.addEventListener("visibilitychange", function () {
        if (document.hidden) {
            stopAllRumble();
            for (var i = 0; i < slots.length; i++) if (slots[i].live) sendNeutral(slots[i]);
        } else {
            for (var j = 0; j < slots.length; j++) {
                var s = slots[j];
                if (!s.live) continue;
                s.needSnapshot = true;
                if (!s.ws) { var g = liveGamepad(s); if (g) openSlot(s, g); }
            }
            if (wake.wanted) requestWakeLock();
        }
    });

    window.addEventListener("gamepaddisconnected", function (e) {
        var slot = findSlotByIndex(e.gamepad.index);
        if (slot) closeSlot(slot);
    });
    window.addEventListener("gamepadconnected", function () { renderPads(true); });

    // ── UI ────────────────────────────────────────────────────────────
    function renderPads(force) {
        var host = el("pads");
        if (!host) return;
        var rows = [];
        var live = 0, open = 0;
        for (var i = 0; i < slots.length; i++) {
            var s = slots[i];
            if (!s.live) continue;
            live++;
            var gp = liveGamepad(s);
            var opened = isOpen(s);
            if (opened) open++;
            var extras = s.mode === "standard" ? Math.min(EXTRA_SLOTS.length, Math.max(0, s.buttons - STANDARD_BUTTON_COUNT)) : 0;
            var notes = [];
            if (extras > 0) notes.push(extras + " extra button" + (extras > 1 ? "s" : "") + " on the paddle and Misc slots");
            if (s.dropped) notes.push(s.dropped + " control" + (s.dropped > 1 ? "s" : "") + " beyond PadForge's slots dropped");
            if (s.mode === "raw") notes.push("raw layout: the browser did not recognize this pad, so button i is slot i and axis i is axis i, forwarded as the browser reports them");
            rows.push({
                name: (s.serverName || ("Browser Gamepad " + s.n)) + " · " + (opened ? "forwarding" : "connecting"),
                meta: (gp ? gp.id : s.id) + " · " + s.buttons + " buttons, " + s.axes + " axes · " + (s.mode === "standard" ? "standard layout" : "raw layout"),
                note: notes.join(" · ")
            });
        }
        var lockText = !("wakeLock" in navigator)
            ? "This address cannot hold the screen awake. Set the screen timeout long enough while you play."
            : (wake.sentinel ? "Screen held awake while a pad is forwarded." : (live ? "Screen wake lock not held." : ""));
        var key = JSON.stringify(rows) + "|" + lockText + "|" + live + "|" + open;
        if (!force && key === lastRender) return;
        lastRender = key;
        host.innerHTML = "";
        rows.forEach(function (r) {
            var row = document.createElement("div");
            row.className = "pad";
            row.innerHTML = '<div class="pad-name"></div><div class="pad-meta"></div><div class="pad-note"></div>';
            row.querySelector(".pad-name").textContent = r.name;
            row.querySelector(".pad-meta").textContent = r.meta;
            row.querySelector(".pad-note").textContent = r.note;
            host.appendChild(row);
        });
        var prompt = el("prompt");
        if (prompt) prompt.style.display = live ? "none" : "";
        var lock = el("lock");
        if (lock) lock.textContent = lockText;
        setStatus(open ? ("Forwarding " + open + (open > 1 ? " pads" : " pad"))
            : live ? ("Connecting " + live + (live > 1 ? " pads" : " pad"))
            : "Waiting for a controller");
    }

    function start() {
        var supported = false;
        try { supported = typeof navigator.getGamepads === "function"; if (supported) navigator.getGamepads(); } catch (e) { supported = false; }
        if (!supported) {
            setStatus("This browser does not expose game controllers to pages.");
            var p = el("prompt"); if (p) p.textContent = "Try Chrome on Android, or a desktop browser on a handheld PC.";
            return;
        }
        renderPads(true);
        settleIdentity(function () { poll(); });
    }

    document.addEventListener("DOMContentLoaded", start);
})();
