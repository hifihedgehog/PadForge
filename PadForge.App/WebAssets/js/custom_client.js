// PadForge Custom Controller (#296 phase 4): build-your-own pad, reWASD style.
// Play mode drives the widgets as inputs over the same WS protocol as the
// stock layouts. Edit mode (pencil) adds drag / resize / palette / grid snap,
// and Save persists the layout server-side (PadForge.xml) via
// POST /api/custom-layouts. A saved pad connects under typeKey custom:<id>,
// so it appears in Devices as its own controller.

(function () {
    "use strict";

    var params = new URLSearchParams(location.search);
    var layoutId = params.get("id") || "new";
    var isNew = layoutId === "new";

    var layout = { id: null, name: "Custom Pad", widgets: [] };
    var editMode = isNew;
    var selected = null;   // widget model currently selected in edit mode
    var ws = null;

    var canvas, toolbar, editBtn, statusEl;

    // ── WebSocket (same protocol as controller_client) ──
    function send(obj) {
        if (ws && ws.readyState === WebSocket.OPEN) ws.send(JSON.stringify(obj));
    }

    function newClientId() {
        return crypto.randomUUID ? crypto.randomUUID() : Math.random().toString(36).slice(2);
    }

    function loadClientId(key) {
        var v = sessionStorage.getItem(key);
        if (!v) { v = newClientId(); sessionStorage.setItem(key, v); }
        return v;
    }

    // Identity is per (tab, pad). An unsaved pad borrows the "new" key, and
    // once it gets a real id it takes an id-keyed identity: without the
    // re-key, building a SECOND pad in the same tab reused the first pad's
    // client id and the two sessions collided on the server.
    var clientId = loadClientId("padforge_custom_" + layoutId);

    function rekeyClientIdFor(realId) {
        clientId = loadClientId("padforge_custom_" + realId);
        sessionStorage.removeItem("padforge_custom_new");
    }

    var releaseFns = [];       // force-neutral everything currently held (page hidden)
    var reconnectPending = false;

    function connect() {
        if (!layout.id) return; // an unsaved pad has no device yet
        // One socket, one reconnect loop: a Save-triggered connect racing the
        // old socket's 3 s retry timer must not stack a second loop.
        if (ws && (ws.readyState === WebSocket.OPEN || ws.readyState === WebSocket.CONNECTING)) return;
        if (ws) { ws.onclose = null; ws.onerror = null; try { ws.close(); } catch (e) { } }
        var proto = location.protocol === "https:" ? "wss:" : "ws:";
        var url = proto + "//" + location.host + "/ws?id=" + encodeURIComponent(clientId)
            + "&layout=" + encodeURIComponent("custom:" + layout.id);
        ws = new WebSocket(url);
        ws.onopen = function () {
            document.getElementById("disconnect-message").style.display = "none";
            // Same capability report the stock client sends: no Vibration API
            // means this pad must not advertise rumble.
            send({ type: "caps", vibrate: !!navigator.vibrate });
            setStatus(layout.name + " connected");
        };
        ws.onmessage = function (ev) {
            var msg; try { msg = JSON.parse(ev.data); } catch (e) { return; }
            if (msg.type === "connected") setStatus(msg.name);
            else if (msg.type === "rumble" && navigator.vibrate && (msg.left > 0 || msg.right > 0))
                navigator.vibrate(Math.round(Math.max(msg.left, msg.right) / 65535 * 200));
        };
        ws.onclose = function () {
            document.getElementById("disconnect-message").style.display = "block";
            scheduleReconnect();
        };
        ws.onerror = function () { ws.close(); };
    }

    function scheduleReconnect() {
        // A backgrounded tab reconnects when it returns to the foreground
        // instead of burning a socket attempt every 3 s while invisible.
        if (document.hidden) { reconnectPending = true; return; }
        setTimeout(connect, 3000);
    }

    document.addEventListener("visibilitychange", function () {
        if (document.hidden) {
            // The browser stops delivering touches to a hidden page but the
            // server keeps the last state latched: let go of everything.
            for (var i = 0; i < releaseFns.length; i++) releaseFns[i]();
        } else if (reconnectPending) {
            reconnectPending = false;
            connect();
        }
    });

    function setStatus(t) { if (statusEl) statusEl.textContent = t; }

    // ── Model helpers ──
    function addWidget(kind, code, label) {
        var def = {
            kind: kind, code: code || 0, label: label || "",
            x: 0.42, y: 0.4,
            w: kind === "touch" ? 0.3 : kind === "dpad" ? 0.22 : kind === "stick" ? 0.2 : kind === "slider" ? 0.08 : 0.1,
            h: kind === "touch" ? 0.35 : kind === "dpad" ? 0.34 : kind === "stick" ? 0.32 : kind === "slider" ? 0.4 : 0.16
        };
        layout.widgets.push(def);
        render();
        select(def);
    }

    var BTN_LABELS = { 0:"A",1:"B",2:"X",3:"Y",4:"LB",5:"RB",6:"Back",7:"Start",8:"LS",9:"RS",10:"Guide",
                       11:"M1",12:"RP1",13:"LP1",14:"RP2",15:"LP2",17:"M2",18:"M3" };

    // ── Rendering ──
    function render() {
        // Let go of anything held BEFORE the elements carrying it are removed.
        // Saving or leaving edit mode re-renders, and a finger down on a button
        // at that moment lost its element and its release hook together, so the
        // server held that button pressed with nothing left to release it.
        for (var i = 0; i < releaseFns.length; i++) {
            try { releaseFns[i](); } catch (e) { }
        }
        // The old hooks are now stale closures over removed elements: drop them
        // or they accumulate one set per render for the life of the page.
        releaseFns = [];
        canvas.innerHTML = "";
        layout.widgets.forEach(function (wd) {
            var el = document.createElement("div");
            el.className = "widget";
            positionEl(el, wd);
            if (wd.kind === "button") {
                el.className += " w-button";
                el.textContent = wd.label || BTN_LABELS[wd.code] || ("B" + wd.code);
            } else if (wd.kind === "stick") {
                el.className += " w-stick";
                var knob = document.createElement("div");
                knob.className = "knob";
                el.appendChild(knob);
            } else if (wd.kind === "slider") {
                el.className += " w-slider";
                var fill = document.createElement("div"); fill.className = "fill"; el.appendChild(fill);
                var tag = document.createElement("div"); tag.className = "tag";
                tag.textContent = wd.code === 2 ? "LT" : wd.code === 5 ? "RT" : "AX" + wd.code;
                el.appendChild(tag);
            } else if (wd.kind === "dpad") {
                el.className += " w-dpad";
            } else if (wd.kind === "touch") {
                el.className += " w-touch";
                var ttag = document.createElement("div"); ttag.className = "tag";
                ttag.textContent = "touch"; el.appendChild(ttag);
            }
            var handle = document.createElement("div");
            handle.className = "handle";
            el.appendChild(handle);

            if (editMode) bindEdit(el, handle, wd);
            else bindPlay(el, wd);

            wd._el = el;
            if (wd === selected && editMode) el.classList.add("selected");
            canvas.appendChild(el);
        });
    }

    function positionEl(el, wd) {
        el.style.left = (wd.x * 100) + "%";
        el.style.top = (wd.y * 100) + "%";
        el.style.width = (wd.w * 100) + "%";
        el.style.height = (wd.h * 100) + "%";
    }

    function select(wd) {
        selected = wd;
        layout.widgets.forEach(function (o) {
            if (o._el) o._el.classList.toggle("selected", o === wd);
        });
    }

    // ── Edit interactions ──
    function snap(v) {
        return document.getElementById("snap").checked ? Math.round(v * 50) / 50 : v;
    }

    // The drag/resize state lives here, ONE set of window listeners serves
    // every widget. The old shape added four window listeners per widget per
    // render(), so a session of edits accumulated hundreds of stale closures
    // over deleted elements, each running on every pointer move.
    var editDrag = null;   // { wd, el, mode, sx, sy, ox, oy, ow, oh }

    function editPt(e) {
        var t = (e.changedTouches && e.changedTouches.length) ? e.changedTouches[0]
              : (e.touches && e.touches.length) ? e.touches[0]
              : e;
        return { x: t.clientX, y: t.clientY };
    }

    function editMove(e) {
        if (!editDrag) return;
        e.preventDefault();
        var d = editDrag;
        var p = editPt(e);
        var dx = (p.x - d.sx) / canvas.clientWidth;
        var dy = (p.y - d.sy) / canvas.clientHeight;
        if (d.mode === "move") {
            d.wd.x = snap(Math.max(0, Math.min(0.98 - d.wd.w, d.ox + dx)));
            d.wd.y = snap(Math.max(0, Math.min(0.98 - d.wd.h, d.oy + dy)));
        } else {
            d.wd.w = snap(Math.max(0.04, Math.min(0.9, d.ow + dx)));
            d.wd.h = snap(Math.max(0.04, Math.min(0.9, d.oh + dy)));
        }
        positionEl(d.el, d.wd);
    }

    function editUp() { editDrag = null; }

    function wireEditWindowListeners() {
        window.addEventListener("touchmove", editMove, { passive: false });
        window.addEventListener("mousemove", editMove);
        window.addEventListener("touchend", editUp);
        window.addEventListener("mouseup", editUp);
    }

    function bindEdit(el, handle, wd) {
        function down(e) {
            e.preventDefault(); e.stopPropagation();
            select(wd);
            var p = editPt(e);
            editDrag = {
                wd: wd, el: el,
                mode: (e.target === handle) ? "resize" : "move",
                sx: p.x, sy: p.y, ox: wd.x, oy: wd.y, ow: wd.w, oh: wd.h
            };
        }
        el.addEventListener("touchstart", down, { passive: false });
        el.addEventListener("mousedown", down);
    }

    // ── Play interactions ──
    function bindPlay(el, wd) {
        if (wd.kind === "button") bindButton(el, wd);
        else if (wd.kind === "stick") bindStick(el, wd);
        else if (wd.kind === "slider") bindSlider(el, wd);
        else if (wd.kind === "dpad") bindDpad(el, wd);
        else if (wd.kind === "touch") bindTouch(el);
    }

    function haptic() { if (navigator.vibrate) navigator.vibrate(20); }

    // One finger, the RIGHT finger: e.touches lists every contact on the
    // screen, so with a face button already held that other finger became
    // touches[0] and the widget read its position. Touch events dispatch to
    // the element the touch STARTED on, so changedTouches only ever holds
    // this widget's own touches. Mouse events carry neither list.
    function widgetTouch(e) {
        return (e.changedTouches && e.changedTouches.length) ? e.changedTouches[0]
             : (e.touches && e.touches.length) ? e.touches[0]
             : e;
    }

    function bindButton(el, wd) {
        var engaged = false;
        function down(e) {
            e.preventDefault();
            engaged = true;
            el.classList.add("pressed");
            send({ type: "input", kind: "button", code: wd.code, value: 1 });
            haptic();
        }
        function up(e) {
            if (e && e.preventDefault) e.preventDefault();
            // mouseleave fires on every pass-over: only a held button releases.
            if (!engaged) return;
            engaged = false;
            el.classList.remove("pressed");
            send({ type: "input", kind: "button", code: wd.code, value: 0 });
        }
        releaseFns.push(function () { up(null); });
        el.addEventListener("touchstart", down, { passive: false });
        el.addEventListener("touchend", up, { passive: false });
        el.addEventListener("touchcancel", up, { passive: false });
        el.addEventListener("mousedown", down);
        el.addEventListener("mouseup", up);
        el.addEventListener("mouseleave", up);
    }

    function bindStick(el, wd) {
        // wd.code = base axis (0 = LX/LY, 3 = RX/RY).
        var knob = el.querySelector(".knob");
        var active = false;
        function update(e) {
            var r = el.getBoundingClientRect();
            var t = widgetTouch(e);
            var nx = ((t.clientX - r.left) / r.width - 0.5) * 2;
            var ny = ((t.clientY - r.top) / r.height - 0.5) * 2;
            var mag = Math.sqrt(nx * nx + ny * ny);
            if (mag > 1) { nx /= mag; ny /= mag; }
            knob.style.left = (28 + nx * 26) + "%";
            knob.style.top = (28 + ny * 26) + "%";
            send({ type: "input", kind: "axis", code: wd.code, value: Math.round((nx * 0.5 + 0.5) * 65535) });
            send({ type: "input", kind: "axis", code: wd.code + 1, value: Math.round((ny * 0.5 + 0.5) * 65535) });
        }
        function down(e) { e.preventDefault(); active = true; update(e); }
        function move(e) { if (active) { e.preventDefault(); update(e); } }
        function up(e) {
            if (!active) return;
            if (e && e.preventDefault) e.preventDefault();
            active = false;
            knob.style.left = "28%"; knob.style.top = "28%";
            send({ type: "input", kind: "axis", code: wd.code, value: 32767 });
            send({ type: "input", kind: "axis", code: wd.code + 1, value: 32767 });
        }
        releaseFns.push(function () { up(null); });
        el.addEventListener("touchstart", down, { passive: false });
        el.addEventListener("touchmove", move, { passive: false });
        el.addEventListener("touchend", up, { passive: false });
        el.addEventListener("touchcancel", up, { passive: false });
        el.addEventListener("mousedown", down);
        el.addEventListener("mousemove", move);
        el.addEventListener("mouseup", up);
        el.addEventListener("mouseleave", up);
    }

    function bindSlider(el, wd) {
        // The reWASD-style trigger slider: absolute vertical position IS the
        // value. Top = full pull. Release resets.
        var fill = el.querySelector(".fill");
        var active = false, lastTs = 0;
        function update(e) {
            var now = (window.performance && performance.now) ? performance.now() : Date.now();
            if (now - lastTs < 16) return;
            lastTs = now;
            var r = el.getBoundingClientRect();
            var v = 1 - Math.max(0, Math.min(1, (widgetTouch(e).clientY - r.top) / r.height));
            fill.style.height = (v * 100) + "%";
            send({ type: "input", kind: "axis", code: wd.code, value: Math.round(v * 65535) });
        }
        function down(e) { e.preventDefault(); active = true; lastTs = 0; update(e); haptic(); }
        function move(e) { if (active) { e.preventDefault(); update(e); } }
        function up(e) {
            if (!active) return;
            if (e && e.preventDefault) e.preventDefault();
            active = false;
            fill.style.height = "0%";
            send({ type: "input", kind: "axis", code: wd.code, value: 0 });
        }
        releaseFns.push(function () { up(null); });
        el.addEventListener("touchstart", down, { passive: false });
        el.addEventListener("touchmove", move, { passive: false });
        el.addEventListener("touchend", up, { passive: false });
        el.addEventListener("touchcancel", up, { passive: false });
        el.addEventListener("mousedown", down);
        el.addEventListener("mousemove", move);
        el.addEventListener("mouseup", up);
        el.addEventListener("mouseleave", up);
    }

    function bindDpad(el, wd) {
        var active = false;
        function update(e) {
            var r = el.getBoundingClientRect();
            var t = widgetTouch(e);
            var nx = ((t.clientX - r.left) / r.width - 0.5) * 2;
            var ny = ((t.clientY - r.top) / r.height - 0.5) * 2;
            if (Math.sqrt(nx * nx + ny * ny) < 0.25) { send({ type: "input", kind: "pov", code: 0, value: -1 }); return; }
            // 8-way: angle from up, clockwise, snapped to 45°.
            var deg = Math.atan2(nx, -ny) * 180 / Math.PI;
            if (deg < 0) deg += 360;
            var oct = Math.round(deg / 45) % 8;
            send({ type: "input", kind: "pov", code: 0, value: oct * 4500 });
        }
        function down(e) { e.preventDefault(); active = true; update(e); haptic(); }
        function move(e) { if (active) { e.preventDefault(); update(e); } }
        function up(e) {
            if (!active) return;
            if (e && e.preventDefault) e.preventDefault();
            active = false;
            send({ type: "input", kind: "pov", code: 0, value: -1 });
        }
        releaseFns.push(function () { up(null); });
        el.addEventListener("touchstart", down, { passive: false });
        el.addEventListener("touchmove", move, { passive: false });
        el.addEventListener("touchend", up, { passive: false });
        el.addEventListener("touchcancel", up, { passive: false });
        el.addEventListener("mousedown", down);
        el.addEventListener("mousemove", move);
        el.addEventListener("mouseup", up);
        el.addEventListener("mouseleave", up);
    }

    // The touch surface takes no widget code: a custom pad has exactly one
    // touchpad surface and it drives finger 0/1 of the device's only pad.
    function bindTouch(el) {
        // Fingers are tracked by IDENTIFIER, exactly as the stock client's
        // touchpad zone does. The old positional read walked e.touches, the
        // screen-global list: a finger held anywhere else took slot 0, so the
        // pad reported that finger's position, and lifting either finger
        // renumbered the rest and made the surviving contact jump.
        var finger0Id = null, finger1Id = null;

        function norm(t) {
            var r = el.getBoundingClientRect();
            return {
                x: Math.max(0, Math.min(1, (t.clientX - r.left) / r.width)),
                y: Math.max(0, Math.min(1, (t.clientY - r.top) / r.height))
            };
        }

        function onStart(e) {
            e.preventDefault();
            for (var i = 0; i < e.changedTouches.length; i++) {
                var t = e.changedTouches[i], p = norm(t);
                if (finger0Id === null) {
                    finger0Id = t.identifier;
                    send({ type: "touchpad", finger: 0, x: p.x, y: p.y, down: true });
                } else if (finger1Id === null) {
                    finger1Id = t.identifier;
                    send({ type: "touchpad", finger: 1, x: p.x, y: p.y, down: true });
                }
            }
        }

        function onMove(e) {
            e.preventDefault();
            for (var i = 0; i < e.changedTouches.length; i++) {
                var t = e.changedTouches[i], p = norm(t);
                if (t.identifier === finger0Id)
                    send({ type: "touchpad", finger: 0, x: p.x, y: p.y, down: true });
                else if (t.identifier === finger1Id)
                    send({ type: "touchpad", finger: 1, x: p.x, y: p.y, down: true });
            }
        }

        function onEnd(e) {
            e.preventDefault();
            for (var i = 0; i < e.changedTouches.length; i++) {
                var t = e.changedTouches[i];
                if (t.identifier === finger0Id) {
                    send({ type: "touchpad", finger: 0, x: 0, y: 0, down: false });
                    finger0Id = null;
                } else if (t.identifier === finger1Id) {
                    send({ type: "touchpad", finger: 1, x: 0, y: 0, down: false });
                    finger1Id = null;
                }
            }
        }

        releaseFns.push(function () {
            if (finger0Id !== null) { send({ type: "touchpad", finger: 0, x: 0, y: 0, down: false }); finger0Id = null; }
            if (finger1Id !== null) { send({ type: "touchpad", finger: 1, x: 0, y: 0, down: false }); finger1Id = null; }
        });

        el.addEventListener("touchstart", onStart, { passive: false });
        el.addEventListener("touchmove", onMove, { passive: false });
        el.addEventListener("touchend", onEnd, { passive: false });
        // touchcancel: a system gesture or an incoming call ends the touch
        // without a touchend, and the finger stayed down forever.
        el.addEventListener("touchcancel", onEnd, { passive: false });
    }

    // ── Toolbar ──
    function setEditMode(on) {
        editMode = on;
        toolbar.classList.toggle("on", on);
        editBtn.classList.toggle("on", on);
        selected = null;
        render();
    }

    function wireToolbar() {
        editBtn.addEventListener("click", function () { setEditMode(!editMode); });

        document.getElementById("palette").addEventListener("change", function () {
            var v = this.value; this.value = "";
            if (!v) return;
            if (v === "dpad") addWidget("dpad", 0);
            else if (v === "touch") addWidget("touch", 0);
            else if (v[0] === "b") addWidget("button", parseInt(v.slice(1), 10));
            else if (v[0] === "s") addWidget("stick", parseInt(v.slice(1), 10));
            else if (v[0] === "t") addWidget("slider", parseInt(v.slice(1), 10));
        });

        document.getElementById("delWidget").addEventListener("click", function () {
            if (!selected) return;
            layout.widgets = layout.widgets.filter(function (w) { return w !== selected; });
            selected = null;
            render();
        });

        document.getElementById("saveBtn").addEventListener("click", function () {
            // A pad with no widgets connects as a device that can never send
            // anything: catch it here rather than shipping a dead controller.
            if (!layout.widgets.length) { setStatus("Add at least one control first"); return; }
            layout.name = document.getElementById("padName").value.trim() || "Custom Pad";
            var body = { name: layout.name, widgets: layout.widgets.map(function (w) {
                return { kind: w.kind, x: w.x, y: w.y, w: w.w, h: w.h, code: w.code, label: w.label || "" };
            }) };
            if (layout.id) body.id = layout.id;
            var xhr = new XMLHttpRequest();
            xhr.open("POST", "/api/custom-layouts", true);
            xhr.onload = function () {
                if (xhr.status !== 200) { setStatus("Save failed"); return; }
                var res; try { res = JSON.parse(xhr.responseText); }
                catch (e) { setStatus("Save failed"); return; }
                if (!res || !res.id) { setStatus("Save failed"); return; }
                var firstSave = !layout.id;
                layout.id = res.id;
                if (firstSave) rekeyClientIdFor(res.id);
                history.replaceState(null, "", "/custom.html?id=" + res.id);
                setStatus("Saved");
                setEditMode(false);
                if (firstSave || !ws || ws.readyState !== WebSocket.OPEN) connect();
            };
            xhr.send(JSON.stringify(body));
        });

        document.getElementById("deleteLayoutBtn").addEventListener("click", function () {
            if (!layout.id) { location.href = "/"; return; }
            var xhr = new XMLHttpRequest();
            xhr.open("DELETE", "/api/custom-layouts?id=" + encodeURIComponent(layout.id), true);
            xhr.onload = function () {
                // Navigating away on a failed delete told the user the pad was
                // gone when it was not.
                if (xhr.status !== 200) { setStatus("Delete failed"); return; }
                location.href = "/";
            };
            xhr.onerror = function () { setStatus("Delete failed"); };
            xhr.send();
        });
    }

    // ── Init ──
    document.addEventListener("DOMContentLoaded", function () {
        canvas = document.getElementById("canvas");
        toolbar = document.getElementById("toolbar");
        editBtn = document.getElementById("editBtn");
        statusEl = document.getElementById("statusBar");
        document.oncontextmenu = function (e) { e.preventDefault(); return false; };
        document.getElementById("disconnect-message").addEventListener("click", function () { location.reload(); });
        wireToolbar();
        wireEditWindowListeners();

        if (isNew) {
            // Seed a starter shape so edit mode is not a blank page.
            layout.widgets = [
                { kind: "stick", code: 0, x: 0.06, y: 0.45, w: 0.2, h: 0.34 },
                { kind: "button", code: 0, x: 0.82, y: 0.55, w: 0.1, h: 0.17 },
                { kind: "button", code: 1, x: 0.9, y: 0.4, w: 0.1, h: 0.17 },
                { kind: "slider", code: 5, x: 0.9, y: 0.04, w: 0.07, h: 0.3 },
            ];
            setEditMode(true);
            setStatus("New pad: arrange, then Save");
        } else {
            var xhr = new XMLHttpRequest();
            xhr.open("GET", "/api/custom-layouts", true);
            xhr.onerror = function () { setStatus("Load failed"); };
            xhr.onload = function () {
                if (xhr.status !== 200) { setStatus("Load failed"); return; }
                var list; try { list = JSON.parse(xhr.responseText); }
                catch (e) { setStatus("Load failed"); return; }
                if (!list || !list.length) { setStatus("Pad not found"); setEditMode(true); return; }
                var found = null;
                for (var i = 0; i < list.length; i++) if (list[i].id === layoutId) found = list[i];
                if (!found) { setStatus("Pad not found"); setEditMode(true); return; }
                layout = found;
                if (!layout.widgets) layout.widgets = [];
                document.getElementById("padName").value = layout.name || "";
                render();
                connect();
            };
            xhr.send();
        }
        render();
        document.getElementById("padName").value = layout.name || "";
    });
})();
