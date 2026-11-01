// PadForge controller picture (issue #555): a read-only page a streamer adds
// to the broadcast as a browser source. It draws the watched slot's
// controller with the web controller's art, lights it from the slot's live
// output, and shows who is playing through Twitch chat. It sends nothing.
//
// ?slot=N watches slot N, numbered as PadForge shows them. Without it, the
// page watches the first slot the Twitch Chat row is assigned to. ?layout=
// draws another controller and ?finish= another finish (WLG is Yuriy's,
// from ForgePlay). ?code= is the plain address's access code, which the
// socket carries too.
(function () {
    "use strict";

    var params = new URLSearchParams(location.search);
    var slotParam = params.get("slot") || "auto";
    var layoutParam = (params.get("layout") || "").toLowerCase();
    var finishParam = params.get("finish") || "";
    var code = params.get("code") || "";

    var padBox = document.getElementById("pad-box");
    var padEl = document.getElementById("pad");
    var chatEl = document.getElementById("chat");

    var view = null;          // the drawing: { layout, overlays }
    var drawnKey = null;      // layout + finish the drawing shows
    var wanted = null;        // layout + finish the server asked for
    var scale = 1;
    var lastState = null;
    var lastNotice = "";
    var noticeSince = 0;

    // ── Drawing ──

    function redraw(layoutKey, finish) {
        var key = layoutKey + "|" + finish;
        wanted = key;
        if (key === drawnKey) return;
        PadForgeLayoutView.fetchLayout(layoutKey, finish, function (layout) {
            if (wanted !== key) return; // a newer slot message won
            drawnKey = key;
            view = PadForgeLayoutView.draw(padEl, layout);
            refit();
        });
    }

    function refit() {
        if (!view) return;
        scale = PadForgeLayoutView.fit(padEl, view.layout, padBox.clientWidth, padBox.clientHeight);
        if (lastState) applyState(lastState);
    }

    window.addEventListener("resize", refit);

    // Lights the press art, fills the triggers and moves the stick caps,
    // the way the controller page does (controller_client.js:
    // setOverlayActive, setTriggerFill, moveStickOverlay).
    function applyState(s) {
        lastState = s;
        if (!view) return;
        var lit = {};
        (s.lit || []).forEach(function (t) { lit[t] = true; });
        var overlays = view.overlays;
        Object.keys(overlays).forEach(function (target) {
            var img = overlays[target];
            if (img.classList.contains("pf-press")) img.classList.toggle("active", !!lit[target]);
        });
        fill(overlays.LeftTrigger, s.lt);
        fill(overlays.RightTrigger, s.rt);
        move(overlays.LeftThumbRing, s.lx, s.ly);
        move(overlays.RightThumbRing, s.rx, s.ry);
    }

    function fill(img, fraction) {
        if (!img || !img.classList.contains("pf-trigger")) return;
        var f = Math.max(0, Math.min(1, fraction || 0));
        img.style.clipPath = "inset(" + ((1 - f) * 100) + "% 0 0 0)";
    }

    function move(img, x, y) {
        if (!img || !view) return;
        var travel = view.layout.stickMaxTravel * scale;
        // Up is positive on the wire and negative on the screen.
        img.style.transform = "translate(" + ((x || 0) * travel) + "px, " + (-(y || 0) * travel) + "px)";
    }

    // ── Chat ──

    function line(el, parts) {
        el.textContent = "";
        parts.forEach(function (p) {
            if (typeof p === "string") { el.appendChild(document.createTextNode(p)); return; }
            var span = document.createElement("span");
            span.className = p.cls;
            span.textContent = p.text;
            el.appendChild(span);
        });
    }

    function clock(seconds) {
        var m = Math.floor(seconds / 60), s = seconds % 60;
        return m + ":" + (s < 10 ? "0" : "") + s;
    }

    function applyChat(c) {
        if (!c.on) {
            if (chatEl.style.display !== "none") { chatEl.style.display = "none"; refit(); }
            return;
        }
        var playing = document.getElementById("playing");
        if (c.user) {
            line(playing, [{ cls: "who", text: c.user }, " ", { cls: "cmd", text: "!" + c.text }]);
        } else if (c.paused) {
            line(playing, [{ cls: "paused", text: "Chat control is paused." }]);
        } else if (c.state === "reading") {
            line(playing, ["Type a command like ", { cls: "cmd", text: "!a" }, " in chat to play."]);
        } else if (c.state === "nochannel") {
            line(playing, []);
        } else {
            line(playing, ["Connecting to " + (c.channel ? c.channel + "'s " : "") + "chat…"]);
        }

        var next = c.next || [];
        var parts = [];
        if (next.length) {
            parts.push("Next: ");
            next.forEach(function (n, i) {
                if (i) parts.push(", ");
                parts.push({ cls: "who", text: n.user }, " ", { cls: "cmd", text: "!" + n.text });
            });
            if (c.waiting > next.length) parts.push(" and " + (c.waiting - next.length) + " more");
        }
        line(document.getElementById("line"), parts);

        var claim = [];
        if (c.owner) {
            claim.push({ cls: "who", text: "@" + c.owner }, " has control, " + clock(c.ownerSeconds) + " left.");
        } else if ((c.requests || []).length) {
            c.requests.forEach(function (r, i) {
                if (i) claim.push(" ");
                claim.push({ cls: "who", text: "@" + r.user },
                    " asked for control: " + r.votes + " of " + c.votesNeeded + ", " + r.seconds + " s.");
            });
        }
        line(document.getElementById("claim"), claim);

        // A notice shows for 15 seconds after it changes.
        if (c.notice !== lastNotice) { lastNotice = c.notice; noticeSince = Date.now(); }
        var fresh = lastNotice && Date.now() - noticeSince < 15000;
        document.getElementById("notice").textContent = fresh ? lastNotice : "";

        if (chatEl.style.display !== "block") { chatEl.style.display = "block"; refit(); }
    }

    // ── Socket ──

    function connect() {
        var proto = location.protocol === "https:" ? "wss:" : "ws:";
        var url = proto + "//" + location.host + "/ws?view=" + encodeURIComponent(slotParam);
        if (code) url += "&code=" + encodeURIComponent(code);
        var ws = new WebSocket(url);
        ws.onmessage = function (ev) {
            var msg;
            try { msg = JSON.parse(ev.data); } catch (e) { return; }
            if (msg.type === "slot") redraw(layoutParam || msg.layout, finishParam || msg.finish || "");
            else if (msg.type === "state") applyState(msg);
            else if (msg.type === "chat") applyChat(msg);
        };
        ws.onclose = function () { setTimeout(connect, 3000); };
        ws.onerror = function () { try { ws.close(); } catch (e) { } };
    }

    connect();
})();
