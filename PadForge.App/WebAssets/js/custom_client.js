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

    var clientIdKey = "padforge_custom_" + layoutId;
    var clientId = sessionStorage.getItem(clientIdKey);
    if (!clientId) {
        clientId = crypto.randomUUID ? crypto.randomUUID() : Math.random().toString(36).slice(2);
        sessionStorage.setItem(clientIdKey, clientId);
    }

    function connect() {
        if (!layout.id) return; // an unsaved pad has no device yet
        var proto = location.protocol === "https:" ? "wss:" : "ws:";
        var url = proto + "//" + location.host + "/ws?id=" + encodeURIComponent(clientId)
            + "&layout=" + encodeURIComponent("custom:" + layout.id);
        ws = new WebSocket(url);
        ws.onopen = function () { setStatus(layout.name + " connected"); };
        ws.onmessage = function (ev) {
            var msg; try { msg = JSON.parse(ev.data); } catch (e) { return; }
            if (msg.type === "connected") setStatus(msg.name);
            else if (msg.type === "rumble" && navigator.vibrate && (msg.left > 0 || msg.right > 0))
                navigator.vibrate(Math.round(Math.max(msg.left, msg.right) / 65535 * 200));
        };
        ws.onclose = function () {
            document.getElementById("disconnect-message").style.display = "block";
            setTimeout(connect, 3000);
        };
        ws.onerror = function () { ws.close(); };
    }

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

    function bindEdit(el, handle, wd) {
        var mode = null, sx = 0, sy = 0, ox = 0, oy = 0, ow = 0, oh = 0;
        function pt(e) { var t = e.touches && e.touches.length ? e.touches[0] : e; return { x: t.clientX, y: t.clientY }; }

        function down(e) {
            e.preventDefault(); e.stopPropagation();
            select(wd);
            var p = pt(e);
            sx = p.x; sy = p.y; ox = wd.x; oy = wd.y; ow = wd.w; oh = wd.h;
            mode = (e.target === handle) ? "resize" : "move";
        }
        function move(e) {
            if (!mode) return;
            e.preventDefault();
            var p = pt(e);
            var dx = (p.x - sx) / canvas.clientWidth;
            var dy = (p.y - sy) / canvas.clientHeight;
            if (mode === "move") {
                wd.x = snap(Math.max(0, Math.min(0.98 - wd.w, ox + dx)));
                wd.y = snap(Math.max(0, Math.min(0.98 - wd.h, oy + dy)));
            } else {
                wd.w = snap(Math.max(0.04, Math.min(0.9, ow + dx)));
                wd.h = snap(Math.max(0.04, Math.min(0.9, oh + dy)));
            }
            positionEl(el, wd);
        }
        function up() { mode = null; }

        el.addEventListener("touchstart", down, { passive: false });
        el.addEventListener("mousedown", down);
        window.addEventListener("touchmove", move, { passive: false });
        window.addEventListener("mousemove", move);
        window.addEventListener("touchend", up);
        window.addEventListener("mouseup", up);
    }

    // ── Play interactions ──
    function bindPlay(el, wd) {
        if (wd.kind === "button") bindButton(el, wd);
        else if (wd.kind === "stick") bindStick(el, wd);
        else if (wd.kind === "slider") bindSlider(el, wd);
        else if (wd.kind === "dpad") bindDpad(el, wd);
        else if (wd.kind === "touch") bindTouch(el, wd);
    }

    function haptic() { if (navigator.vibrate) navigator.vibrate(20); }

    function bindButton(el, wd) {
        function down(e) {
            e.preventDefault();
            el.classList.add("pressed");
            send({ type: "input", kind: "button", code: wd.code, value: 1 });
            haptic();
        }
        function up(e) {
            e.preventDefault();
            el.classList.remove("pressed");
            send({ type: "input", kind: "button", code: wd.code, value: 0 });
        }
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
        function pt(e) { var t = e.touches && e.touches.length ? e.touches[0] : e; return { x: t.clientX, y: t.clientY }; }
        function update(e) {
            var r = el.getBoundingClientRect();
            var p = pt(e);
            var nx = ((p.x - r.left) / r.width - 0.5) * 2;
            var ny = ((p.y - r.top) / r.height - 0.5) * 2;
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
            e.preventDefault(); active = false;
            knob.style.left = "28%"; knob.style.top = "28%";
            send({ type: "input", kind: "axis", code: wd.code, value: 32767 });
            send({ type: "input", kind: "axis", code: wd.code + 1, value: 32767 });
        }
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
        function pt(e) { var t = e.touches && e.touches.length ? e.touches[0] : e; return t.clientY; }
        function update(e) {
            var now = (window.performance && performance.now) ? performance.now() : Date.now();
            if (now - lastTs < 16) return;
            lastTs = now;
            var r = el.getBoundingClientRect();
            var v = 1 - Math.max(0, Math.min(1, (pt(e) - r.top) / r.height));
            fill.style.height = (v * 100) + "%";
            send({ type: "input", kind: "axis", code: wd.code, value: Math.round(v * 65535) });
        }
        function down(e) { e.preventDefault(); active = true; update(e); haptic(); }
        function move(e) { if (active) { e.preventDefault(); update(e); } }
        function up(e) {
            if (!active) return;
            e.preventDefault(); active = false;
            fill.style.height = "0%";
            send({ type: "input", kind: "axis", code: wd.code, value: 0 });
        }
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
        function pt(e) { var t = e.touches && e.touches.length ? e.touches[0] : e; return { x: t.clientX, y: t.clientY }; }
        function update(e) {
            var r = el.getBoundingClientRect();
            var p = pt(e);
            var nx = ((p.x - r.left) / r.width - 0.5) * 2;
            var ny = ((p.y - r.top) / r.height - 0.5) * 2;
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
            e.preventDefault(); active = false;
            send({ type: "input", kind: "pov", code: 0, value: -1 });
        }
        el.addEventListener("touchstart", down, { passive: false });
        el.addEventListener("touchmove", move, { passive: false });
        el.addEventListener("touchend", up, { passive: false });
        el.addEventListener("touchcancel", up, { passive: false });
        el.addEventListener("mousedown", down);
        el.addEventListener("mousemove", move);
        el.addEventListener("mouseup", up);
        el.addEventListener("mouseleave", up);
    }

    function bindTouch(el, wd) {
        function handle(e, down) {
            e.preventDefault();
            var r = el.getBoundingClientRect();
            for (var i = 0; i < Math.min(e.touches ? e.touches.length : 1, 2); i++) {
                var t = e.touches ? e.touches[i] : e;
                var x = Math.max(0, Math.min(1, (t.clientX - r.left) / r.width));
                var y = Math.max(0, Math.min(1, (t.clientY - r.top) / r.height));
                send({ type: "touchpad", finger: i, x: x, y: y, down: down });
            }
            if (!down || (e.touches && e.touches.length < 2))
                send({ type: "touchpad", finger: 1, x: 0, y: 0, down: false });
        }
        el.addEventListener("touchstart", function (e) { handle(e, true); }, { passive: false });
        el.addEventListener("touchmove", function (e) { handle(e, true); }, { passive: false });
        el.addEventListener("touchend", function (e) {
            e.preventDefault();
            send({ type: "touchpad", finger: 0, x: 0, y: 0, down: false });
            send({ type: "touchpad", finger: 1, x: 0, y: 0, down: false });
        }, { passive: false });
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
            layout.name = document.getElementById("padName").value.trim() || "Custom Pad";
            var body = { name: layout.name, widgets: layout.widgets.map(function (w) {
                return { kind: w.kind, x: w.x, y: w.y, w: w.w, h: w.h, code: w.code, label: w.label || "" };
            }) };
            if (layout.id) body.id = layout.id;
            var xhr = new XMLHttpRequest();
            xhr.open("POST", "/api/custom-layouts", true);
            xhr.onload = function () {
                if (xhr.status !== 200) { setStatus("Save failed"); return; }
                var res = JSON.parse(xhr.responseText);
                var firstSave = !layout.id;
                layout.id = res.id;
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
            xhr.onload = function () { location.href = "/"; };
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
            xhr.onload = function () {
                if (xhr.status !== 200) { setStatus("Load failed"); return; }
                var list = JSON.parse(xhr.responseText);
                var found = null;
                for (var i = 0; i < list.length; i++) if (list[i].id === layoutId) found = list[i];
                if (!found) { setStatus("Pad not found"); setEditMode(true); return; }
                layout = found;
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
