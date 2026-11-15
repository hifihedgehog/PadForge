// PadForge Custom Controller (#296 phase 4): build-your-own pad, reWASD style.
// Play mode drives the widgets as inputs over the same WS protocol as the
// stock layouts. Edit mode (pencil) adds drag / resize / palette / grid snap,
// and Save persists the layout server-side (PadForge.xml) via
// POST /api/custom-layouts. A saved pad connects under typeKey custom:<id>,
// so it appears in Devices as its own controller.
//
// Where each control goes on this screen is js/pad_fit.js's: a control keeps
// its shape and its size on a screen of any shape, and a pad may have an
// arrangement of its own for a screen held sideways and for one held upright.

(function () {
    "use strict";

    var params = new URLSearchParams(location.search);
    var layoutId = params.get("id") || "new";
    var isNew = layoutId === "new";

    var layout = PadFit.read({ name: "Custom Pad", widgets: [] });
    var editMode = isNew;
    var selected = null;   // widget model currently selected in edit mode
    var ws = null;

    var canvas, toolbar, editBtn, statusEl;

    // The screen, and the pad as it is drawn on it (PadFit.place).
    var screenW = 0, screenH = 0;
    var shown = { face: 0, own: true, scale: 0, natural: 0, rects: [] };

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
        // The viewer's name from the home page (#555), so the streamer can
        // tell phones apart.
        var viewerName = "";
        try { viewerName = localStorage.getItem("padforge_viewer_name") || ""; } catch (e) { }
        if (viewerName) url += "&name=" + encodeURIComponent(viewerName);
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

    // The page's line says its piece and goes, so it stays over no control.
    var statusFade = null;
    function setStatus(t) {
        if (!statusEl) return;
        statusEl.textContent = t;
        statusEl.classList.remove("faded");
        clearTimeout(statusFade);
        statusFade = setTimeout(function () { statusEl.classList.add("faded"); }, 4000);
    }

    var BTN_LABELS = { 0:"A",1:"B",2:"X",3:"Y",4:"LB",5:"RB",6:"Back",7:"Start",8:"LS",9:"RS",10:"Guide",
                       11:"M1",12:"RP1",13:"LP1",14:"RP2",15:"LP2",17:"M2",18:"M3" };

    // A D-pad's face: a cross, an arm that lights for each way it is pushed,
    // and a tip on each arm. It is drawn in a box of 100 so it is the same
    // cross at any size.
    var DPAD_ART =
        '<svg viewBox="0 0 100 100" aria-hidden="true">' +
        '<path class="face" d="M36 4H64a4 4 0 0 1 4 4V32H92a4 4 0 0 1 4 4V64a4 4 0 0 1-4 4H68V92a4 4 0 0 1-4 4H36a4 4 0 0 1-4-4V68H8a4 4 0 0 1-4-4V36a4 4 0 0 1 4-4H32V8a4 4 0 0 1 4-4Z"/>' +
        '<rect class="arm n" x="34" y="6" width="32" height="28" rx="3"/>' +
        '<rect class="arm e" x="66" y="34" width="28" height="32" rx="3"/>' +
        '<rect class="arm s" x="34" y="66" width="32" height="28" rx="3"/>' +
        '<rect class="arm w" x="6" y="34" width="28" height="32" rx="3"/>' +
        '<path class="tip n" d="M50 12L58 24H42Z"/>' +
        '<path class="tip e" d="M88 50L76 58V42Z"/>' +
        '<path class="tip s" d="M50 88L42 76H58Z"/>' +
        '<path class="tip w" d="M12 50L24 42V58Z"/>' +
        '</svg>';
    // The arms lit for each of the eight ways, clockwise from up.
    var DPAD_ARMS = ["n", "ne", "e", "se", "s", "sw", "w", "nw"];

    function lightDpad(el, way) {
        var lit = way < 0 ? "" : DPAD_ARMS[way];
        ["n", "e", "s", "w"].forEach(function (arm) {
            el.classList.toggle("lit-" + arm, lit.indexOf(arm) >= 0);
        });
    }

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
                el.innerHTML = DPAD_ART;
            } else if (wd.kind === "touch") {
                el.className += " w-touch";
                var ttag = document.createElement("div"); ttag.className = "tag";
                ttag.textContent = "Touch"; el.appendChild(ttag);
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
        fit();
    }

    // Draws the pad for the screen as it is now. Called again when the
    // screen changes: the phone is turned, the browser's bars come or go, a
    // window is resized.
    function fit() {
        screenW = canvas.clientWidth;
        screenH = canvas.clientHeight;
        shown = PadFit.place(layout, screenW, screenH);
        layout.widgets.forEach(function (wd, i) {
            if (wd._el && shown.rects[i]) setBox(wd._el, wd, shown.rects[i]);
        });
        showEditAids();
    }

    // A control's box, in the screen's pixels. Its words are sized with it.
    function setBox(el, wd, r) {
        el.style.left = r.x + "px";
        el.style.top = r.y + "px";
        el.style.width = r.w + "px";
        el.style.height = r.h + "px";
        var small = Math.min(r.w, r.h);
        var words = wd.kind === "button" ? Math.max(10, Math.min(44, small * 0.34))
                                         : Math.max(9, Math.min(15, small * 0.2));
        el.style.fontSize = words + "px";
    }

    function select(wd) {
        selected = wd;
        layout.widgets.forEach(function (o) {
            if (o._el) o._el.classList.toggle("selected", o === wd);
        });
    }

    // ── Edit interactions ──

    // The grid's step in pixels: the same along and down, so what snaps to
    // it keeps its shape.
    function gridStep() { return PadFit.GRID * Math.min(screenW, screenH) / PadFit.SHORT; }

    function snap(v) {
        if (!document.getElementById("snap").checked) return v;
        var step = gridStep();
        return step > 0 ? Math.round(v / step) * step : v;
    }

    // The first edit on a screen makes the pad as it is drawn here the pad's
    // own arrangement for a screen held this way. Until then a pad built on
    // another screen is only fitted to this one.
    function takeScreen() {
        if (shown.own && Math.abs(layout.faces[shown.face] - screenW / screenH) < 1e-6) return;
        PadFit.adopt(layout, shown.rects, screenW, screenH);
        shown = PadFit.place(layout, screenW, screenH);
    }

    // Keeps a moved or resized control's box as the pad's.
    function keepBox(wd, r) {
        var i = layout.widgets.indexOf(wd);
        shown.rects[i] = r;
        wd.at[shown.face] = { x: r.x / screenW, y: r.y / screenH, w: r.w / screenW, h: r.h / screenH };
        setBox(wd._el, wd, r);
    }

    // The grid's dots, the way back to a fitted arrangement, and the line
    // that says the pad is only fitted to this screen.
    function showEditAids() {
        var grid = editMode && document.getElementById("snap").checked;
        canvas.classList.toggle("grid", grid);
        if (grid) canvas.style.backgroundSize = gridStep() + "px " + gridStep() + "px";
        var auto = document.getElementById("autoBtn");
        auto.hidden = !(editMode && shown.own && layout.faces[0] && layout.faces[1]);
        if (editMode && layout.widgets.length && !shown.own)
            setStatus("Fitted to this screen. Move a control to arrange it here.");
        keepBarOnScreen();
    }

    // The drag/resize state lives here, ONE set of window listeners serves
    // every widget. The old shape added four window listeners per widget per
    // render(), so a session of edits accumulated hundreds of stale closures
    // over deleted elements, each running on every pointer move.
    var editDrag = null;   // { wd, mode, sx, sy, r, moved }: r is the box the drag began with
    var barDrag = null;    // { sx, sy, x, y }: the tools being moved
    var missedFit = false; // the screen changed during a drag

    function editPt(e) {
        var t = (e.changedTouches && e.changedTouches.length) ? e.changedTouches[0]
              : (e.touches && e.touches.length) ? e.touches[0]
              : e;
        return { x: t.clientX, y: t.clientY };
    }

    function editMove(e) {
        if (barDrag) { e.preventDefault(); moveBar(editPt(e)); return; }
        if (!editDrag) return;
        e.preventDefault();
        var d = editDrag, p = editPt(e);
        var dx = p.x - d.sx, dy = p.y - d.sy;
        if (!d.moved) {
            // A tap picks a control and leaves it, and the pad, as they are.
            if (Math.abs(dx) + Math.abs(dy) < 4) return;
            d.moved = true;
            takeScreen();
            var from = shown.rects[layout.widgets.indexOf(d.wd)];
            d.r = { x: from.x, y: from.y, w: from.w, h: from.h };
            // The tools stand aside while a control is dragged, so one can
            // be put where they are.
            document.body.classList.add("dragging");
        }
        var r = { x: d.r.x, y: d.r.y, w: d.r.w, h: d.r.h };
        if (d.mode === "move") {
            r.x = Math.max(0, Math.min(screenW - r.w, snap(d.r.x + dx)));
            r.y = Math.max(0, Math.min(screenH - r.h, snap(d.r.y + dy)));
        } else {
            var least = PadFit.SMALLEST * Math.min(screenW, screenH) / PadFit.SHORT;
            var mostW = screenW - r.x, mostH = screenH - r.y;
            if (PadFit.isSquare(d.wd.kind)) {
                // A stick and a D-pad stay as wide as they are tall.
                var side = Math.max(least, Math.min(mostW, mostH, snap(Math.max(d.r.w + dx, d.r.h + dy))));
                r.w = r.h = side;
            } else {
                r.w = Math.max(least, Math.min(mostW, snap(d.r.w + dx)));
                r.h = Math.max(least, Math.min(mostH, snap(d.r.h + dy)));
                // A button drawn nearly round is round. Drawn wide or tall it is a pill.
                if (d.wd.kind === "button" && PadFit.nearSquare(r.w, r.h)) r.w = r.h = Math.min(r.w, r.h);
            }
        }
        keepBox(d.wd, r);
    }

    function editUp() {
        if (!editDrag && !barDrag) return;
        editDrag = null;
        barDrag = null;
        document.body.classList.remove("dragging");
        if (missedFit) { missedFit = false; fit(); }
    }

    function wireEditWindowListeners() {
        window.addEventListener("touchmove", editMove, { passive: false });
        window.addEventListener("mousemove", editMove);
        window.addEventListener("touchend", editUp);
        window.addEventListener("touchcancel", editUp);
        window.addEventListener("mouseup", editUp);
    }

    function bindEdit(el, handle, wd) {
        function down(e) {
            e.preventDefault(); e.stopPropagation();
            select(wd);
            var p = editPt(e);
            editDrag = { wd: wd, mode: (e.target === handle) ? "resize" : "move", sx: p.x, sy: p.y, r: null, moved: false };
        }
        el.addEventListener("touchstart", down, { passive: false });
        el.addEventListener("mousedown", down);
    }

    // ── The tools: a small panel that can be moved off a control ──
    function moveBar(p) {
        placeBar(barDrag.x + p.x - barDrag.sx, barDrag.y + p.y - barDrag.sy);
    }

    function placeBar(x, y) {
        var w = toolbar.offsetWidth, h = toolbar.offsetHeight;
        toolbar.classList.add("moved");
        toolbar.style.left = Math.max(0, Math.min(screenW - w, x)) + "px";
        toolbar.style.top = Math.max(0, Math.min(screenH - h, y)) + "px";
    }

    function keepBarOnScreen() {
        // Hidden, it has no box to keep: it is where it was left when it shows again.
        if (!toolbar.classList.contains("moved") || !toolbar.classList.contains("on")) return;
        var box = toolbar.getBoundingClientRect();
        placeBar(box.left, box.top);
    }

    function wireBarGrip() {
        var grip = document.getElementById("barGrip");
        function down(e) {
            e.preventDefault(); e.stopPropagation();
            var p = editPt(e), box = toolbar.getBoundingClientRect();
            barDrag = { sx: p.x, sy: p.y, x: box.left, y: box.top };
        }
        grip.addEventListener("touchstart", down, { passive: false });
        grip.addEventListener("mousedown", down);
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
            if (Math.sqrt(nx * nx + ny * ny) < 0.25) {
                lightDpad(el, -1);
                send({ type: "input", kind: "pov", code: 0, value: -1 });
                return;
            }
            // 8-way: angle from up, clockwise, snapped to 45°.
            var deg = Math.atan2(nx, -ny) * 180 / Math.PI;
            if (deg < 0) deg += 360;
            var oct = Math.round(deg / 45) % 8;
            lightDpad(el, oct);
            send({ type: "input", kind: "pov", code: 0, value: oct * 4500 });
        }
        function down(e) { e.preventDefault(); active = true; update(e); haptic(); }
        function move(e) { if (active) { e.preventDefault(); update(e); } }
        function up(e) {
            if (!active) return;
            if (e && e.preventDefault) e.preventDefault();
            active = false;
            lightDpad(el, -1);
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

    function addWidget(kind, code) {
        takeScreen();
        var wd = PadFit.add(layout, kind, code || 0, "", screenW, screenH);
        render();
        select(wd);
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

        document.getElementById("snap").addEventListener("change", showEditAids);

        // Gives up this screen's own arrangement: the pad is fitted here
        // again from the one made for the other way of holding the screen.
        document.getElementById("autoBtn").addEventListener("click", function () {
            if (PadFit.drop(layout, screenW, screenH)) { selected = null; render(); }
        });

        document.getElementById("saveBtn").addEventListener("click", function () {
            // A pad with no widgets connects as a device that can never send
            // anything: catch it here rather than shipping a dead controller.
            if (!layout.widgets.length) { setStatus("Add at least one control first"); return; }
            layout.name = document.getElementById("padName").value.trim() || "Custom Pad";
            var body = PadFit.write(layout);
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
        wireBarGrip();

        // The screen changed. A drag in hand is finished first: its numbers
        // are this screen's.
        function screenChanged() {
            if (editDrag || barDrag) { missedFit = true; return; }
            fit();
        }
        window.addEventListener("resize", screenChanged);
        window.addEventListener("orientationchange", screenChanged);

        if (isNew) {
            // Seed a starter shape so edit mode is not a blank page.
            layout = PadFit.starter(canvas.clientWidth || window.innerWidth, canvas.clientHeight || window.innerHeight);
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
                layout = PadFit.read(found);
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
