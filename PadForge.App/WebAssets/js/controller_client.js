// PadForge Web Controller Client — 2D Controller Overlay Mode
// Renders Xbox 360 or DS4 controller using PNG overlays from PadForge's 2D asset pack.
// Touch input for buttons, triggers, D-pad, and dual analog sticks via nipplejs.

// iOS Safari: WebSocket upgrades fail after page navigation but work
// after reload.  Auto-reload once on first navigation to work around this.
(function() {
    var nav = performance.getEntriesByType && performance.getEntriesByType('navigation')[0];
    if (nav && nav.type === 'navigate' && !sessionStorage.getItem('_ctrl_reloaded')) {
        sessionStorage.setItem('_ctrl_reloaded', '1');
        location.reload();
        return;
    }
    sessionStorage.removeItem('_ctrl_reloaded');
})();

(function () {
    "use strict";

    // ── Config ──
    var params = new URLSearchParams(location.search);
    var layoutType = params.get("layout") || "xbox360";
    var finish = params.get("finish") || "";

    // ── Client identity (per-tab AND per-layout-type so switching pages doesn't collide) ──
    var clientIdKey = "padforge_client_id_" + layoutType;
    var clientId = sessionStorage.getItem(clientIdKey);
    if (!clientId) {
        clientId = crypto.randomUUID ? crypto.randomUUID() : Math.random().toString(36).slice(2);
        sessionStorage.setItem(clientIdKey, clientId);
    }

    // ── Haptic ──
    var vibrate = navigator.vibrate || navigator.webkitVibrate || navigator.mozVibrate;
    function haptic() {
        if (vibrate) vibrate.call(navigator, 30);
    }

    // ── WebSocket ──
    var ws = null;
    var resyncFns = [];        // reset per-socket duplicate-suppression caches on (re)connect
    var releaseFns = [];       // force-neutral everything currently held (page hidden)
    var reconnectPending = false;

    function send(obj) {
        if (ws && ws.readyState === WebSocket.OPEN) {
            ws.send(JSON.stringify(obj));
        }
    }

    function connect() {
        // One socket at a time: a reconnect timer racing another connect path
        // must not stack a second loop.
        if (ws && (ws.readyState === WebSocket.OPEN || ws.readyState === WebSocket.CONNECTING)) return;
        var proto = location.protocol === "https:" ? "wss:" : "ws:";
        var hasTouchpad = layout && layout.overlays && layout.overlays.some(function(o) {
            // Only generic touch surfaces make this a touchpad device. A
            // surface with an inputKind is repurposed (Steam Controller: the
            // left pad IS the d-pad, the right pad IS the right stick).
            return o.type === "touchpad" && !o.inputKind;
        });
        var wsUrl = proto + "//" + location.host + "/ws?id=" + encodeURIComponent(clientId) + "&layout=" + encodeURIComponent(layoutType);
        if (hasTouchpad) wsUrl += "&touchpad=1";
        ws = new WebSocket(wsUrl);

        ws.onopen = function () {
            console.log("[PadForge] WebSocket connected");
            // Say what this browser can actually do, so the pad is not
            // advertised with capabilities it cannot deliver. iOS Safari has
            // no Vibration API at all, and a pad that claims rumble it never
            // plays is a mapping the user sets and then wonders about.
            send({ type: "caps", vibrate: !!vibrate });
            // A reconnected server-side device starts neutral, so every
            // duplicate-suppression cache from the old socket must be dropped
            // or the first post-reconnect state is silently swallowed.
            for (var ri = 0; ri < resyncFns.length; ri++) resyncFns[ri]();
            document.getElementById("controller-viewport").style.display = "";
            document.getElementById("disconnect-message").style.display = "none";
            setStatus("Connected");
        };

        ws.onmessage = function (ev) {
            var msg;
            try { msg = JSON.parse(ev.data); } catch (e) { return; }

            if (msg.type === "connected") {
                setStatus(msg.name);
            } else if (msg.type === "rumble") {
                if (vibrate && (msg.left > 0 || msg.right > 0)) {
                    var intensity = Math.max(msg.left, msg.right) / 65535;
                    vibrate.call(navigator, Math.round(intensity * 200));
                }
            } else if (msg.type === "led") {
                // Lightbar feedback (#296): the strip glows in the color the
                // slot drives, exactly like a DualShock's bar.
                setLedColor(msg.r | 0, msg.g | 0, msg.b | 0);
            } else if (msg.type === "player") {
                setPlayerPips(msg.index | 0);
            }
        };

        ws.onclose = function (ev) {
            console.log("[PadForge] WebSocket closed, code=" + ev.code);
            document.getElementById("controller-viewport").style.display = "none";
            document.getElementById("disconnect-message").style.display = "block";
            scheduleReconnect();
        };

        ws.onerror = function (ev) {
            console.error("[PadForge] WebSocket error", ev);
            ws.close();
        };
    }

    function scheduleReconnect() {
        // A backgrounded tab reconnects when it returns to the foreground
        // instead of burning a socket attempt every 3 s while invisible.
        if (document.hidden) { reconnectPending = true; return; }
        setTimeout(connect, 3000);
    }

    function releaseAll() {
        for (var i = 0; i < releaseFns.length; i++) releaseFns[i]();
    }

    document.addEventListener("visibilitychange", function () {
        if (document.hidden) {
            // The browser stops delivering touch events to a hidden page, but
            // the server would keep the last state latched: let go of
            // everything before going quiet.
            releaseAll();
            if (motionOn) send({ type: "motion", gx: 0, gy: 0, gz: 0, ax: 0, ay: 0, az: 0 });
        } else if (reconnectPending) {
            reconnectPending = false;
            connect();
        }
    });

    // The status bar announces, then fades (owner report 2026-08-19): kept
    // up permanently it overlaid the layout's topmost controls, and on
    // layouts whose triggers ride the top edge the visible target shrank
    // enough that blind taps from muscle memory missed. Transient statuses
    // fade after a few seconds. Sticky ones (errors that need reading)
    // stay until replaced. The bar was always pointer-events: none, so
    // this changes what the eye sees, never hit-testing.
    var statusEl, statusFadeTimer;
    function setStatus(text, sticky) {
        if (!statusEl) return;
        statusEl.textContent = text;
        statusEl.classList.remove("faded");
        if (statusFadeTimer) { clearTimeout(statusFadeTimer); statusFadeTimer = null; }
        if (!sticky) {
            statusFadeTimer = setTimeout(function () {
                statusEl.classList.add("faded");
            }, 3000);
        }
    }

    // ── Layout state ──
    var layout = null;
    var container, touchLayer;
    var overlayImages = {};   // target name → img element
    var scaleFactor = 1;      // current container width / layout base width

    // ── Init ──
    document.addEventListener("DOMContentLoaded", function () {
        document.oncontextmenu = function (e) { e.preventDefault(); return false; };
        statusEl = document.getElementById("statusBar");
        container = document.getElementById("controller-container");
        touchLayer = document.getElementById("touch-layer");

        // Reconnect on tap when disconnected.
        document.getElementById("disconnect-message").addEventListener("click", function () {
            location.reload();
        });

        setupMotionButton();
        fetchLayoutAndBuild();
    });

    // ── Phone motion (#296 phase 1) ──
    // DeviceMotionEvent exists only in a secure context (the HTTPS lane), and
    // iOS additionally gates it behind requestPermission() from a user
    // gesture. The button is that gesture. Samples stream as
    // {type:"motion"}: gyro rad/s + accel m/s², rotated from the DEVICE frame
    // into the current screen frame (devicemotion axes do not follow screen
    // rotation) and delivered in the SDL controller-frame convention the
    // whole gyro pipeline expects (X right, Y up, Z toward the player).
    var motionOn = false, motionBtn = null;
    // rotationRate is degrees per second in EVERY current browser: the W3C
    // spec mandates deg/s, Firefox and Safari always followed it, and Chrome
    // switched from radians in Chrome 66 (2018, treated as a bug fix). So the
    // conversion below is unconditional, and any "some browsers send radians"
    // special case would be wrong on all of them.
    var D2R = Math.PI / 180;

    function setupMotionButton() {
        var isTouchpadPage = !!document.getElementById("touchpad-zone-page");
        if (isTouchpadPage) return;
        if (!window.isSecureContext || typeof DeviceMotionEvent === "undefined") return;
        motionBtn = document.createElement("button");
        motionBtn.textContent = "⟳ Motion";
        motionBtn.style.cssText = "position:fixed;bottom:10px;right:10px;z-index:45;" +
            "background:#16213e;color:#9aa;border:1px solid #0f3460;border-radius:8px;" +
            "padding:6px 12px;font:600 12px 'Segoe UI',sans-serif;opacity:0.85";
        motionBtn.addEventListener("click", toggleMotion);
        document.body.appendChild(motionBtn);
    }

    function toggleMotion() {
        if (motionOn) {
            window.removeEventListener("devicemotion", onMotion);
            motionOn = false;
            // Park the slot's motion state at rest: without this the last
            // sample stays latched server-side and a mid-motion toggle leaves
            // the gyro pipeline seeing a permanent rotation.
            send({ type: "motion", gx: 0, gy: 0, gz: 0, ax: 0, ay: 0, az: 0 });
            motionBtn.style.color = "#9aa";
            motionBtn.style.borderColor = "#0f3460";
            return;
        }
        // iOS 13+: permission prompt, must run inside this click.
        if (typeof DeviceMotionEvent.requestPermission === "function") {
            DeviceMotionEvent.requestPermission().then(function (res) {
                if (res === "granted") armMotion();
            }).catch(function () { });
        } else {
            armMotion();
        }
    }

    function armMotion() {
        window.addEventListener("devicemotion", onMotion);
        motionOn = true;
        motionBtn.style.color = "#7CFC00";
        motionBtn.style.borderColor = "#7CFC00";
        haptic();
    }

    var lastMotionTs = 0;
    function onMotion(e) {
        // ~60 Hz cap: browsers may fire faster on some Androids.
        var now = (window.performance && performance.now) ? performance.now() : Date.now();
        if (now - lastMotionTs < 15) return;
        lastMotionTs = now;

        var rr = e.rotationRate || {};
        var ag = e.accelerationIncludingGravity || {};
        // Device frame: beta = about device X, gamma = about device Y,
        // alpha = about device Z. Same axes for the accelerometer.
        var gx = rr.beta || 0, gy = rr.gamma || 0, gz = rr.alpha || 0;
        var ax = ag.x || 0, ay = ag.y || 0, az = ag.z || 0;

        // Rotate device X/Y into the current screen frame. Z is the rotation
        // axis, unchanged. Angle is how far the SCREEN is rotated from the
        // device's natural orientation.
        var angle = (screen.orientation && screen.orientation.angle) || window.orientation || 0;
        var g = rotateToScreen(gx, gy, angle);
        var a = rotateToScreen(ax, ay, angle);

        send({
            type: "motion",
            gx: g.x * D2R, gy: g.y * D2R, gz: gz * D2R,
            ax: a.x, ay: a.y, az: az
        });
    }

    function rotateToScreen(x, y, angle) {
        switch (((angle % 360) + 360) % 360) {
            case 90:  return { x: y,  y: -x };
            case 180: return { x: -x, y: -y };
            case 270: return { x: -y, y: x };
            default:  return { x: x,  y: y };
        }
    }

    // Lighting lives ON THE CONTROLLER, on the controllers that have it.
    //
    // This used to be a six-pixel strip across the top of the PAGE plus four
    // dots in the corner, drawn for every layout: an Xbox 360 and a Switch Pro
    // both got a lightbar and player pips they have no hardware for. The
    // server now sends geometry only for the pads that own the feature, using
    // the Lighting tab's own rule (a bar for the DualSense family and the
    // DualShock 4, indicator LEDs for the DualSense family alone) and the same
    // mask art the tab's preview draws. A layout with no lighting sends none
    // and nothing is created here, so nothing can appear.
    var ledBars = [], pipEls = [], pipOnColor = "#7CFC00";

    function buildLighting(layout, container) {
        ledBars = []; pipEls = [];
        var pct = function (v, base) { return (v / base * 100) + "%"; };

        var bars = layout.lightbar || [];
        for (var i = 0; i < bars.length; i++) {
            var b = bars[i];
            var el = document.createElement("div");
            // The mask carries the bar's shape, so the element is a plain
            // rectangle of color that the mask cuts to the real outline.
            var url = "url('/img/" + b.image + "')";
            el.style.cssText =
                "position:absolute;pointer-events:none;z-index:3;opacity:0;" +
                "transition:background-color 0.15s,opacity 0.15s;" +
                "left:" + pct(b.x, layout.baseWidth) + ";top:" + pct(b.y, layout.baseHeight) + ";" +
                "width:" + pct(b.w, layout.baseWidth) + ";height:" + pct(b.h, layout.baseHeight) + ";" +
                "-webkit-mask-image:" + url + ";mask-image:" + url + ";" +
                "-webkit-mask-size:100% 100%;mask-size:100% 100%;" +
                "-webkit-mask-repeat:no-repeat;mask-repeat:no-repeat";
            container.appendChild(el);
            ledBars.push(el);
        }

        var ind = layout.indicatorLeds;
        if (ind) {
            // Five LEDs in the DualSense's row, centered under the touchpad.
            // Spacing is in base units so it scales with the art like
            // everything else.
            var count = 5, gap = 16, dot = 10;
            var total = count * dot + (count - 1) * gap;
            for (var k = 0; k < count; k++) {
                var d = document.createElement("div");
                d.style.cssText =
                    "position:absolute;pointer-events:none;z-index:4;border-radius:50%;" +
                    "transition:background-color 0.15s,box-shadow 0.15s;background:#2a2a2a;" +
                    "left:" + pct(ind.cx - total / 2 + k * (dot + gap), layout.baseWidth) + ";" +
                    "top:" + pct(ind.y, layout.baseHeight) + ";" +
                    "width:" + pct(dot, layout.baseWidth) + ";height:" + pct(dot, layout.baseHeight);
                container.appendChild(d);
                pipEls.push(d);
            }
        }
    }

    function setLedColor(r, g, b) {
        if (!ledBars.length) return;
        var c = "rgb(" + r + "," + g + "," + b + ")";
        var dark = (r + g + b) < 24;
        for (var i = 0; i < ledBars.length; i++) {
            ledBars[i].style.backgroundColor = c;
            ledBars[i].style.opacity = dark ? "0" : "1";
        }
        pipOnColor = c;
        refreshPips();
    }

    // The DualSense lights a SYMMETRIC pattern rather than one dot per
    // player, which is what the pad itself does: one center LED for player 1,
    // the outer pair for player 2, and so on outward.
    var PIP_PATTERNS = [
        [0, 0, 1, 0, 0],
        [0, 1, 0, 1, 0],
        [1, 0, 1, 0, 1],
        [1, 1, 0, 1, 1],
        [1, 1, 1, 1, 1]
    ];
    var pipIndex = 0;
    function setPlayerPips(index) { pipIndex = index | 0; refreshPips(); }
    function refreshPips() {
        if (!pipEls.length) return;
        var pat = PIP_PATTERNS[Math.min(Math.max(pipIndex, 1), 5) - 1];
        for (var i = 0; i < pipEls.length; i++) {
            var on = pipIndex > 0 && pat[i];
            pipEls[i].style.background = on ? pipOnColor : "#2a2a2a";
            pipEls[i].style.boxShadow = on ? "0 0 6px " + pipOnColor : "none";
        }
    }

    function fetchLayoutAndBuild() {
        var xhr = new XMLHttpRequest();
        var layoutUrl = "/api/layout?type=" + encodeURIComponent(layoutType);
        if (finish) layoutUrl += "&finish=" + encodeURIComponent(finish);
        xhr.open("GET", layoutUrl, true);
        xhr.onload = function () {
            if (xhr.status !== 200) {
                setStatus("Failed to load layout", true);
                return;
            }
            try { layout = JSON.parse(xhr.responseText); }
            catch (e) { setStatus("Failed to load layout", true); return; }
            connect();
            buildController();
            setupTouchZones();
            setupSticks();
            onResize();
        };
        xhr.onerror = function () {
            setStatus("Failed to load layout", true);
        };
        xhr.send();
    }

    // ── Build controller overlays ──
    function buildController() {
        var baseImg = document.getElementById("base-image");
        baseImg.src = "/img/" + layout.basePath;
        baseImg.onload = onResize;

        // Layouts that ship only ACTIVE trigger art (Steam Deck, Steam
        // Controller) have no TriggerBase element, and a trigger whose only
        // image is clipped to the live pull is INVISIBLE at rest (owner
        // report 2026-08-12). Note which triggers have a real base so a rest
        // silhouette can be synthesized for the ones that do not.
        var baseFor = {};
        for (var b = 0; b < layout.overlays.length; b++) {
            var bo = layout.overlays[b];
            if (bo.type === "triggerBase")
                baseFor[bo.target.replace("TriggerBase", "Trigger")] = true;
        }
        // The Valve layouts ship no TriggerBase because their BASE render
        // already draws the trigger at rest, and their trigger art is the
        // pack's press blue. Synthesizing a rest copy from that art painted
        // a dim blue trigger on an idle controller, which no other surface
        // shows. The server says which layouts draw their own rest state.
        var baseDrawsTriggers = !!layout.baseDrawsTriggers;

        for (var i = 0; i < layout.overlays.length; i++) {
            var ov = layout.overlays[i];
            if (ov.type === "touchpad") continue; // no image — handled by setupTouchpadZone
            if (!ov.image || ov.image.endsWith("/")) continue; // no image — touch-zone only

            if (ov.type === "trigger" && !baseFor[ov.target] && !baseDrawsTriggers) {
                // Synthetic rest state: the same art, dimmed, unclipped, and
                // behind the live copy, exactly the role a TriggerBase plays.
                var rest = document.createElement("img");
                rest.src = "/img/" + ov.image;
                rest.className = "overlay trigger-base";
                rest.style.opacity = "0.45";
                rest.style.zIndex = "1";
                rest.style.left = (ov.x / layout.baseWidth * 100) + "%";
                rest.style.top = (ov.y / layout.baseHeight * 100) + "%";
                rest.style.width = (ov.w / layout.baseWidth * 100) + "%";
                rest.style.height = (ov.h / layout.baseHeight * 100) + "%";
                container.appendChild(rest);
            }

            var img = document.createElement("img");
            img.src = "/img/" + ov.image;
            img.dataset.target = ov.target;

            // Position as percentage of base dimensions.
            img.style.left = (ov.x / layout.baseWidth * 100) + "%";
            img.style.top = (ov.y / layout.baseHeight * 100) + "%";
            img.style.width = (ov.w / layout.baseWidth * 100) + "%";
            img.style.height = (ov.h / layout.baseHeight * 100) + "%";

            if (ov.type === "trigger") {
                img.className = "overlay trigger";
            } else if (ov.type === "triggerBase") {
                img.className = "overlay trigger-base";
            } else if (ov.type === "stickRing") {
                img.className = "overlay stick-ring";
            } else if (ov.type === "decal") {
                img.className = "overlay decal";
            } else {
                img.className = "overlay";
            }

            container.appendChild(img);
            overlayImages[ov.target] = img;
        }

        // Lighting goes on last so the bar and the indicator row sit above
        // the pad art, the way they do on the hardware.
        buildLighting(layout, container);
    }

    // ── Responsive scaling ──
    window.addEventListener("resize", onResize);

    function onResize() {
        if (!layout) return;
        var vw = window.innerWidth;
        var vh = window.innerHeight;
        var ar = layout.baseWidth / layout.baseHeight;

        var w, h;
        if (vw / vh > ar) {
            h = vh;
            w = h * ar;
        } else {
            w = vw;
            h = w / ar;
        }

        container.style.width = w + "px";
        container.style.height = h + "px";
        scaleFactor = w / layout.baseWidth;

        // Position the touch layer to match the container.
        var offsetX = (vw - w) / 2;
        var offsetY = (vh - h) / 2;
        touchLayer.style.left = offsetX + "px";
        touchLayer.style.top = offsetY + "px";
        touchLayer.style.width = w + "px";
        touchLayer.style.height = h + "px";
    }

    // ── Touch zones ──
    // Small meta-buttons that should always be on top of d-pad/trigger zones.
    var smallButtons = ["ButtonBack", "ButtonStart", "ButtonGuide", "TouchpadClick"];

    function setupTouchZones() {
        var dpadOverlays = [];

        for (var i = 0; i < layout.overlays.length; i++) {
            var ov = layout.overlays[i];

            if (ov.type === "stickRing" || ov.type === "stickClick") continue;
            // Printed markings explain a control, they are not one.
            if (ov.type === "decal") continue;
            // Explicitly disabled for this layout (e.g. the Steam Controller's
            // trackpad click zones, which would steal touches from the
            // repurposed pad surfaces underneath).
            if (ov.inputKind === "none") continue;

            // Touchpad zone. A surface with an inputKind is repurposed:
            // the 2015 Steam Controller's left pad acts as the D-PAD and its
            // right pad as the RIGHT STICK (that is how SDL maps the real
            // hardware). Everything else is the generic multi-touch surface.
            if (ov.type === "touchpad") {
                if (ov.inputKind === "pov") bindDpadSurface(ov);
                else if (ov.inputKind === "stick") bindStickSurface(ov);
                else setupTouchpadZone(ov, layout);
                continue;
            }

            // Collect D-pad overlays for unified zone.
            if (ov.target.indexOf("DPad") === 0) {
                dpadOverlays.push(ov);
                continue;
            }

            var zone = document.createElement("div");
            zone.className = "touch-zone";

            // Touchpad CLICK: a visible labeled pill sitting just above the
            // touch surface, so the user has an obvious place to tap for the
            // click that is distinct from the touch-drag surface. The DS4
            // already read well because its click zone extended above the
            // touchpad; this makes that same affordance explicit and gives
            // the DualSense and Steam Deck (whose click and touch areas fully
            // overlap) the same obvious target (owner report 2026-08-12).
            if (ov.target === "TouchpadClick"
                || ov.target === "LeftTouchpadClick"
                || ov.target === "RightTouchpadClick") {
                setupTouchpadClickPill(ov);
                continue;
            }

            // Enlarge touch target by ~40% for mobile fat-finger tolerance.
            var padX = ov.w * 0.2;
            var padY = ov.h * 0.2;
            zone.style.left = ((ov.x - padX) / layout.baseWidth * 100) + "%";
            zone.style.top = ((ov.y - padY) / layout.baseHeight * 100) + "%";
            zone.style.width = ((ov.w + padX * 2) / layout.baseWidth * 100) + "%";
            zone.style.height = ((ov.h + padY * 2) / layout.baseHeight * 100) + "%";

            // Z-index priority: triggers < buttons < small meta-buttons.
            // This ensures bumpers are preferred over triggers when zones overlap,
            // and small buttons (Back/Start/Guide/Share) are preferred over d-pad.
            if (ov.type === "trigger" && ov.inputKind === "axis") {
                zone.style.zIndex = "12";
                bindTriggerZone(zone, ov);
            } else if (ov.type === "button" && ov.inputKind === "button") {
                zone.style.zIndex = smallButtons.indexOf(ov.target) >= 0 ? "15" : "14";
                bindButtonZone(zone, ov);
            }

            touchLayer.appendChild(zone);
        }

        if (dpadOverlays.length > 0) {
            setupDpadZone(dpadOverlays);
        }
    }

    function bindButtonZone(zone, ov) {
        var code = ov.inputCode;
        var target = ov.target;
        var engaged = false;

        function down(e) {
            e.preventDefault();
            engaged = true;
            var img = overlayImages[target];
            if (img) img.classList.add("active");
            send({ type: "input", kind: "button", code: code, value: 1 });
            haptic();
        }
        function up(e) {
            if (e && e.preventDefault) e.preventDefault();
            // mouseleave fires on every pass-over: only a held button releases.
            if (!engaged) return;
            engaged = false;
            var img = overlayImages[target];
            if (img) img.classList.remove("active");
            send({ type: "input", kind: "button", code: code, value: 0 });
        }
        releaseFns.push(function () { up(null); });

        zone.addEventListener("touchstart", down, { passive: false });
        zone.addEventListener("touchend", up, { passive: false });
        zone.addEventListener("touchcancel", up, { passive: false });
        zone.addEventListener("mousedown", down);
        zone.addEventListener("mouseup", up);
        zone.addEventListener("mouseleave", up);
    }

    function bindTriggerZone(zone, ov) {
        var axisCode = ov.inputCode;
        var target = ov.target;

        // Analog trigger slider (#296, requested by eVenent, in the spirit of
        // reWASD's mobile slider): a tap is still a full pull, but KEEPING the
        // finger down and dragging vertically feathers the pull like a racing
        // throttle. Drag down from the touch point to ease off, slide back up
        // for full. The overlay art fills to the live value, so partial pulls
        // are visible.
        var startY = null, engaged = false, lastSent = -1, lastTs = 0;
        var RANGE = 140; // css px of drag = the full analog range
        resyncFns.push(function () { lastSent = -1; lastTs = 0; });

        function sendValue(frac) {
            var v = Math.max(0, Math.min(1, frac));
            var raw = Math.round(v * 65535);
            var now = (window.performance && performance.now) ? performance.now() : Date.now();
            if (raw === lastSent) return;
            // Rate-limit mid-range updates; endpoints always go out.
            if (now - lastTs < 16 && raw !== 0 && raw !== 65535) return;
            lastSent = raw; lastTs = now;
            setTriggerFill(target, v);
            send({ type: "input", kind: "axis", code: axisCode, value: raw });
        }
        function pointY(e) {
            // changedTouches, NOT touches: e.touches is every contact on the
            // SCREEN, so a finger held on a face button became touches[0] and
            // the trigger read its drag position from the wrong finger (same
            // defect the d-pad zone below documents and fixes).
            var t = (e.changedTouches && e.changedTouches.length) ? e.changedTouches[0]
                  : (e.touches && e.touches.length) ? e.touches[0]
                  : e;
            return t.clientY;
        }
        function down(e) {
            e.preventDefault();
            engaged = true;
            startY = pointY(e);
            sendValue(1.0);
            haptic();
        }
        function move(e) {
            if (!engaged) return;
            e.preventDefault();
            sendValue(1.0 - Math.max(0, pointY(e) - startY) / RANGE);
        }
        function up(e) {
            if (e && e.preventDefault) e.preventDefault();
            if (!engaged) return;
            engaged = false; startY = null; lastSent = -1;
            setTriggerFill(target, 0.0);
            send({ type: "input", kind: "axis", code: axisCode, value: 0 });
        }
        releaseFns.push(function () { up(null); });

        zone.addEventListener("touchstart", down, { passive: false });
        zone.addEventListener("touchmove", move, { passive: false });
        zone.addEventListener("touchend", up, { passive: false });
        zone.addEventListener("touchcancel", up, { passive: false });
        zone.addEventListener("mousedown", down);
        zone.addEventListener("mousemove", move);
        zone.addEventListener("mouseup", up);
        zone.addEventListener("mouseleave", up);
    }

    function setTriggerFill(target, fraction) {
        var img = overlayImages[target];
        if (!img) return;
        var topClip = (1.0 - fraction) * 100;
        img.style.clipPath = "inset(" + topClip + "% 0 0 0)";
    }

    // A visible, TEXT-FREE click pill beside its touch surface. Placement is
    // obvious from adjacency alone (owner direction 2026-08-12): a dual-pad
    // layout gets a slim VERTICAL pill hugging the outboard edge of each pad
    // (left pad's on its left, right pad's on its right), and a single-pad
    // layout keeps a slim horizontal bar above the pad. The pill never
    // overlaps the touch surface or any other control; if the preferred spot
    // is occupied, the remaining sides are tried in order.
    function setupTouchpadClickPill(ov) {
        var surfaceTarget = ov.target.replace("Click", "");
        var surface = null;
        for (var si = 0; si < layout.overlays.length; si++)
            if (layout.overlays[si].target === surfaceTarget) { surface = layout.overlays[si]; break; }
        var ref = surface || ov;
        var gap = layout.baseHeight * 0.012;

        // Sizing follows the CONTROLLER's geometry, not arbitrary fractions
        // (owner direction): the vertical pills run the full height of the
        // pad they serve and sit flush against it, and where the layout's
        // click element extends beyond the touch surface (DS4 / DualSense,
        // whose click rect is drawn to match the controller's clickable
        // surround), the bar fills that strip exactly so it looks native.
        var vw = layout.baseWidth * 0.035;
        var vh = ref.h;                       // full pad height
        var hw = Math.min(ref.w * 0.6, layout.baseWidth * 0.16);
        var hh = layout.baseHeight * 0.05;

        // The native strip: the click element's own rect above the surface.
        var strip = null;
        if (surface && ov.y < surface.y - 2 && ov.w > 0) {
            var stripH = surface.y - ov.y - gap;
            if (stripH > layout.baseHeight * 0.02)
                strip = { x: ov.x, y: ov.y, w: ov.w, h: stripH };
        }

        function collides(cx, cy, cw, ch) {
            if (surface
                && cx < surface.x + surface.w && cx + cw > surface.x
                && cy < surface.y + surface.h && cy + ch > surface.y)
                return true;
            for (var k = 0; k < layout.overlays.length; k++) {
                var o = layout.overlays[k];
                if (o === ov || o.target === surfaceTarget || o.target === ov.target) continue;
                var padX = o.w * 0.2, padY = o.h * 0.2;
                if (cx < o.x + o.w + padX && cx + cw > o.x - padX
                    && cy < o.y + o.h + padY && cy + ch > o.y - padY)
                    return true;
            }
            return false;
        }
        function onCanvas(cx, cy, cw, ch) {
            return cx >= 0 && cy >= 0
                && cx + cw <= layout.baseWidth && cy + ch <= layout.baseHeight;
        }

        // Flush against the pad: no gap on the shared edge.
        var left  = { x: ref.x - vw,               y: ref.y,                    w: vw, h: vh };
        var right = { x: ref.x + ref.w,            y: ref.y,                    w: vw, h: vh };
        var above = { x: ref.x + (ref.w - hw) / 2, y: ref.y - hh - gap,         w: hw, h: hh };
        var below = { x: ref.x + (ref.w - hw) / 2, y: ref.y + ref.h + gap,      w: hw, h: hh };

        var candidates;
        if (ov.target === "LeftTouchpadClick") candidates = [left, right, below, above];
        else if (ov.target === "RightTouchpadClick") candidates = [right, left, below, above];
        else if (strip) candidates = [strip, above, below, left, right];
        else candidates = [above, below, left, right];

        var chosen = candidates[0];
        for (var c = 0; c < candidates.length; c++) {
            if (onCanvas(candidates[c].x, candidates[c].y, candidates[c].w, candidates[c].h)
                && !collides(candidates[c].x, candidates[c].y, candidates[c].w, candidates[c].h)) {
                chosen = candidates[c];
                break;
            }
        }

        var pill = document.createElement("div");
        pill.className = "touch-zone touchpad-click-pill";
        pill.style.left = (chosen.x / layout.baseWidth * 100) + "%";
        pill.style.top = (chosen.y / layout.baseHeight * 100) + "%";
        pill.style.width = (chosen.w / layout.baseWidth * 100) + "%";
        pill.style.height = (chosen.h / layout.baseHeight * 100) + "%";
        pill.style.zIndex = "16";
        bindTouchpadClickZone(pill, ov);
        touchLayer.appendChild(pill);
    }

    function bindTouchpadClickZone(zone, ov) {
        // Touchpad click rides Buttons[16] on the server side
        // (SDL_GAMEPAD_BUTTON_TOUCHPAD's canonical slot — between paddles
        // and Misc2-Misc6 per SDL's enum order). Sent as a standard
        // button-press, same shape as every other web-controller button —
        // no bespoke {type:"touchpad", click:bool} wire format anymore.
        var code = (ov && typeof ov.inputCode === "number") ? ov.inputCode : 16;
        var engaged = false;
        function down(e) {
            e.preventDefault();
            engaged = true;
            zone.classList.add("pressed");
            send({ type: "input", kind: "button", code: code, value: 1 });
            haptic();
        }
        function up(e) {
            if (e && e.preventDefault) e.preventDefault();
            if (!engaged) return;
            engaged = false;
            zone.classList.remove("pressed");
            send({ type: "input", kind: "button", code: code, value: 0 });
        }
        releaseFns.push(function () { up(null); });
        zone.addEventListener("touchstart", down, { passive: false });
        zone.addEventListener("touchend", up, { passive: false });
        zone.addEventListener("touchcancel", up, { passive: false });
        zone.addEventListener("mousedown", down);
        zone.addEventListener("mouseup", up);
        zone.addEventListener("mouseleave", up);
    }

    // ── Repurposed touch surfaces (Steam Controller 2015) ──

    function makeSurfaceZone(ov) {
        var zone = document.createElement("div");
        zone.className = "touch-zone";
        zone.style.left = (ov.x / layout.baseWidth * 100) + "%";
        zone.style.top = (ov.y / layout.baseHeight * 100) + "%";
        zone.style.width = (ov.w / layout.baseWidth * 100) + "%";
        zone.style.height = (ov.h / layout.baseHeight * 100) + "%";
        zone.style.zIndex = "10";
        touchLayer.appendChild(zone);
        return zone;
    }

    function surfacePoint(zone, e) {
        // changedTouches, NOT touches: the screen-global list hands the pad a
        // finger resting on some other control (see the d-pad zone comment).
        var t = (e.changedTouches && e.changedTouches.length) ? e.changedTouches[0]
              : (e.touches && e.touches.length) ? e.touches[0]
              : e;
        var r = zone.getBoundingClientRect();
        return {
            x: ((t.clientX - r.left) / r.width - 0.5) * 2,
            y: ((t.clientY - r.top) / r.height - 0.5) * 2
        };
    }

    // The left pad as an 8-way D-pad: touch position picks the direction,
    // release centers the hat.
    // The wedges cut out of this pad, in the order a hat's 45-degree steps
    // visit them. Index i of a 4500-unit hat value lights the entries this
    // names, so a diagonal lights BOTH of its cardinals, the way the desktop
    // preview renders one.
    var DPAD_WEDGES = [
        ["DPadUp"], ["DPadUp", "DPadRight"], ["DPadRight"], ["DPadRight", "DPadDown"],
        ["DPadDown"], ["DPadDown", "DPadLeft"], ["DPadLeft"], ["DPadLeft", "DPadUp"]
    ];

    function lightDpadWedges(pov) {
        var lit = pov < 0 ? [] : DPAD_WEDGES[(pov / 4500) % 8];
        for (var i = 0; i < 4; i++) {
            var name = ["DPadUp", "DPadRight", "DPadDown", "DPadLeft"][i];
            setOverlayActive(name, lit.indexOf(name) >= 0);
        }
    }

    function bindDpadSurface(ov) {
        var zone = makeSurfaceZone(ov);
        var active = false;
        function update(e) {
            var p = surfacePoint(zone, e);
            if (Math.sqrt(p.x * p.x + p.y * p.y) < 0.22) {
                send({ type: "input", kind: "pov", code: 0, value: -1 });
                lightDpadWedges(-1);
                return;
            }
            var deg = Math.atan2(p.x, -p.y) * 180 / Math.PI;
            if (deg < 0) deg += 360;
            var pov = (Math.round(deg / 45) % 8) * 4500;
            send({ type: "input", kind: "pov", code: 0, value: pov });
            // The pad IS the d-pad here, so nothing else on the page can say
            // a direction was pressed. Every other zone type lights its own
            // overlay on the way down; the two repurposed pad surfaces sent
            // their input and lit nothing at all.
            lightDpadWedges(pov);
        }
        function down(e) { e.preventDefault(); active = true; update(e); haptic(); }
        function move(e) { if (active) { e.preventDefault(); update(e); } }
        function up(e) {
            if (!active) return;
            if (e && e.preventDefault) e.preventDefault();
            active = false;
            send({ type: "input", kind: "pov", code: 0, value: -1 });
            lightDpadWedges(-1);
        }
        releaseFns.push(function () { lightDpadWedges(-1); });
        releaseFns.push(function () { up(null); });
        zone.addEventListener("touchstart", down, { passive: false });
        zone.addEventListener("touchmove", move, { passive: false });
        zone.addEventListener("touchend", up, { passive: false });
        zone.addEventListener("touchcancel", up, { passive: false });
        zone.addEventListener("mousedown", down);
        zone.addEventListener("mousemove", move);
        zone.addEventListener("mouseup", up);
        zone.addEventListener("mouseleave", up);
    }

    // The right pad as a stick: absolute touch position is the deflection,
    // release recenters. ov.inputCode is the base axis (3 = RX/RY).
    function bindStickSurface(ov) {
        var zone = makeSurfaceZone(ov);
        var baseAxis = ov.inputCode || 3;
        // The stand-in stick standing on this pad, named by the axes the
        // surface drives: 0 is the left stick's pair, 3 the right's. It
        // leans with the drag, which is the only thing on the page that
        // shows where the stick is.
        var ringTarget = baseAxis === 0 ? "LeftThumbRing" : "RightThumbRing";
        var active = false, lastTs = 0;
        function update(e) {
            var now = (window.performance && performance.now) ? performance.now() : Date.now();
            if (now - lastTs < 16) return;
            lastTs = now;
            var p = surfacePoint(zone, e);
            var mag = Math.sqrt(p.x * p.x + p.y * p.y);
            if (mag > 1) { p.x /= mag; p.y /= mag; }
            send({ type: "input", kind: "axis", code: baseAxis, value: Math.round((p.x * 0.5 + 0.5) * 65535) });
            send({ type: "input", kind: "axis", code: baseAxis + 1, value: Math.round((p.y * 0.5 + 0.5) * 65535) });
            moveStickOverlay(ringTarget, p.x, p.y);
        }
        function down(e) { e.preventDefault(); active = true; lastTs = 0; update(e); }
        function move(e) { if (active) { e.preventDefault(); update(e); } }
        function up(e) {
            if (!active) return;
            if (e && e.preventDefault) e.preventDefault();
            active = false;
            send({ type: "input", kind: "axis", code: baseAxis, value: 32767 });
            send({ type: "input", kind: "axis", code: baseAxis + 1, value: 32767 });
            moveStickOverlay(ringTarget, 0, 0);
        }
        releaseFns.push(function () { moveStickOverlay(ringTarget, 0, 0); });
        releaseFns.push(function () { up(null); });
        zone.addEventListener("touchstart", down, { passive: false });
        zone.addEventListener("touchmove", move, { passive: false });
        zone.addEventListener("touchend", up, { passive: false });
        zone.addEventListener("touchcancel", up, { passive: false });
        zone.addEventListener("mousedown", down);
        zone.addEventListener("mousemove", move);
        zone.addEventListener("mouseup", up);
        zone.addEventListener("mouseleave", up);
    }

    // ── Touchpad: multi-touch zone for DS4 touchpad ──
    function setupTouchpadZone(ov, lay) {
        var zone = document.createElement("div");
        zone.className = "touch-zone";
        zone.style.left = (ov.x / lay.baseWidth * 100) + "%";
        zone.style.top = (ov.y / lay.baseHeight * 100) + "%";
        zone.style.width = (ov.w / lay.baseWidth * 100) + "%";
        zone.style.height = (ov.h / lay.baseHeight * 100) + "%";
        zone.style.zIndex = "15";
        zone.style.borderRadius = "8px";
        zone.style.border = "2px solid rgba(255,255,255,0.5)";
        zone.style.background = "rgba(100,149,237,0.15)";
        touchLayer.appendChild(zone);

        // Finger preview dots
        var dot0 = document.createElement("div");
        dot0.className = "touchpad-dot f0";
        zone.appendChild(dot0);

        var dot1 = document.createElement("div");
        dot1.className = "touchpad-dot f1";
        zone.appendChild(dot1);

        var finger0Id = null, finger1Id = null;

        function normXY(touch) {
            var rect = zone.getBoundingClientRect();
            return {
                x: Math.max(0, Math.min(1, (touch.clientX - rect.left) / rect.width)),
                y: Math.max(0, Math.min(1, (touch.clientY - rect.top) / rect.height))
            };
        }

        function updateDot(dot, pos, show) {
            if (show) {
                dot.style.display = "block";
                dot.style.left = (pos.x * 100) + "%";
                dot.style.top = (pos.y * 100) + "%";
            } else {
                dot.style.display = "none";
            }
        }

        zone.addEventListener("touchstart", function(e) {
            e.preventDefault();
            for (var i = 0; i < e.changedTouches.length; i++) {
                var t = e.changedTouches[i];
                var p = normXY(t);
                if (finger0Id === null) {
                    finger0Id = t.identifier;
                    send({ type: "touchpad", finger: 0, x: p.x, y: p.y, down: true });
                    updateDot(dot0, p, true);
                } else if (finger1Id === null) {
                    finger1Id = t.identifier;
                    send({ type: "touchpad", finger: 1, x: p.x, y: p.y, down: true });
                    updateDot(dot1, p, true);
                }
            }
        }, { passive: false });

        zone.addEventListener("touchmove", function(e) {
            e.preventDefault();
            for (var i = 0; i < e.changedTouches.length; i++) {
                var t = e.changedTouches[i];
                var p = normXY(t);
                if (t.identifier === finger0Id) {
                    send({ type: "touchpad", finger: 0, x: p.x, y: p.y, down: true });
                    updateDot(dot0, p, true);
                } else if (t.identifier === finger1Id) {
                    send({ type: "touchpad", finger: 1, x: p.x, y: p.y, down: true });
                    updateDot(dot1, p, true);
                }
            }
        }, { passive: false });

        function onTouchEnd(e) {
            e.preventDefault();
            for (var i = 0; i < e.changedTouches.length; i++) {
                var t = e.changedTouches[i];
                if (t.identifier === finger0Id) {
                    send({ type: "touchpad", finger: 0, x: 0, y: 0, down: false });
                    finger0Id = null;
                    updateDot(dot0, null, false);
                } else if (t.identifier === finger1Id) {
                    send({ type: "touchpad", finger: 1, x: 0, y: 0, down: false });
                    finger1Id = null;
                    updateDot(dot1, null, false);
                }
            }
        }
        zone.addEventListener("touchend", onTouchEnd, { passive: false });
        zone.addEventListener("touchcancel", onTouchEnd, { passive: false });
        releaseFns.push(function () {
            if (finger0Id !== null) {
                send({ type: "touchpad", finger: 0, x: 0, y: 0, down: false });
                finger0Id = null; updateDot(dot0, null, false);
            }
            if (finger1Id !== null) {
                send({ type: "touchpad", finger: 1, x: 0, y: 0, down: false });
                finger1Id = null; updateDot(dot1, null, false);
            }
        });
    }

    // ── D-Pad: single zone with angle-based 8-way detection ──
    function setupDpadZone(dpadOverlays) {
        var minX = Infinity, minY = Infinity, maxX = 0, maxY = 0;
        for (var i = 0; i < dpadOverlays.length; i++) {
            var ov = dpadOverlays[i];
            minX = Math.min(minX, ov.x);
            minY = Math.min(minY, ov.y);
            maxX = Math.max(maxX, ov.x + ov.w);
            maxY = Math.max(maxY, ov.y + ov.h);
        }

        var padX = (maxX - minX) * 0.15;
        var padY = (maxY - minY) * 0.15;

        var zone = document.createElement("div");
        zone.className = "touch-zone";
        zone.style.left = ((minX - padX) / layout.baseWidth * 100) + "%";
        zone.style.top = ((minY - padY) / layout.baseHeight * 100) + "%";
        zone.style.width = ((maxX - minX + padX * 2) / layout.baseWidth * 100) + "%";
        zone.style.height = ((maxY - minY + padY * 2) / layout.baseHeight * 100) + "%";
        zone.style.zIndex = "13"; // Above stick zones (11), below buttons (14).

        var currentPov = -1;
        // -2 forces the next computed value (even neutral) onto the fresh
        // socket, whose server-side device knows nothing of this cache.
        resyncFns.push(function () { currentPov = -2; });

        function updateDpad(e) {
            e.preventDefault();
            var rect = zone.getBoundingClientRect();
            // changedTouches, NOT touches. e.touches lists every contact point
            // on the SCREEN, so with a face button already held that finger is
            // touches[0] and the d-pad computed its direction from a position
            // outside its own rect, emitting a direction the user never
            // pressed. Holding a button while working the d-pad is ordinary
            // gamepad use, so this fired constantly.
            //
            // Touch events dispatch to the element the touch STARTED on, so on
            // this zone changedTouches only ever holds d-pad touches. The
            // touchpad zone above already tracks identifiers for the same
            // reason. Mouse events carry neither list and fall through to e.
            var touch = (e.changedTouches && e.changedTouches.length) ? e.changedTouches[0]
                      : (e.touches && e.touches.length) ? e.touches[0]
                      : e;
            if (!touch) return;
            var dx = (touch.clientX - rect.left) / rect.width - 0.5;
            var dy = (touch.clientY - rect.top) / rect.height - 0.5;

            var dirs = { up: false, down: false, left: false, right: false };
            var deadzone = 0.15;

            if (Math.abs(dx) > deadzone || Math.abs(dy) > deadzone) {
                var angle = Math.atan2(dy, dx) * 180 / Math.PI;
                if (angle >= -67.5 && angle < 67.5) dirs.right = true;
                if (angle >= 22.5 && angle < 157.5) dirs.down = true;
                if (angle >= 112.5 || angle < -112.5) dirs.left = true;
                if (angle >= -157.5 && angle < -22.5) dirs.up = true;
            }

            var pov = computePov(dirs);
            if (pov !== currentPov) {
                currentPov = pov;
                send({ type: "input", kind: "pov", code: 0, value: pov });
            }

            // Show/hide directional overlays.
            setOverlayActive("DPadUp", dirs.up);
            setOverlayActive("DPadDown", dirs.down);
            setOverlayActive("DPadLeft", dirs.left);
            setOverlayActive("DPadRight", dirs.right);
        }

        function releaseDpad(e) {
            if (e && e.preventDefault) e.preventDefault();
            if (currentPov !== -1) {
                currentPov = -1;
                send({ type: "input", kind: "pov", code: 0, value: -1 });
            }
            setOverlayActive("DPadUp", false);
            setOverlayActive("DPadDown", false);
            setOverlayActive("DPadLeft", false);
            setOverlayActive("DPadRight", false);
        }
        releaseFns.push(function () { releaseDpad(null); });

        zone.addEventListener("touchstart", updateDpad, { passive: false });
        zone.addEventListener("touchmove", updateDpad, { passive: false });
        zone.addEventListener("touchend", releaseDpad, { passive: false });
        zone.addEventListener("touchcancel", releaseDpad, { passive: false });
        zone.addEventListener("mousedown", updateDpad);
        zone.addEventListener("mousemove", function (e) {
            if (e.buttons === 1) updateDpad(e);
        });
        zone.addEventListener("mouseup", releaseDpad);
        zone.addEventListener("mouseleave", releaseDpad);

        touchLayer.appendChild(zone);
    }

    function computePov(dirs) {
        if (dirs.up && dirs.right) return 4500;
        if (dirs.down && dirs.right) return 13500;
        if (dirs.down && dirs.left) return 22500;
        if (dirs.up && dirs.left) return 31500;
        if (dirs.up) return 0;
        if (dirs.right) return 9000;
        if (dirs.down) return 18000;
        if (dirs.left) return 27000;
        return -1;
    }

    function setOverlayActive(target, active) {
        var img = overlayImages[target];
        if (!img) return;
        if (active) img.classList.add("active");
        else img.classList.remove("active");
    }

    // ── Analog sticks via nipplejs ──
    function setupSticks() {
        setupOneStick("left-stick-zone", "LeftThumbRing", "LeftThumbButton", 0, 1, 8);
        setupOneStick("right-stick-zone", "RightThumbRing", "RightThumbButton", 3, 4, 9);
    }

    function setupOneStick(zoneId, ringTarget, clickTarget, axisX, axisY, clickCode) {
        var zone = document.getElementById(zoneId);
        if (!zone || !layout) return;

        // Find stick ring overlay in layout data.
        var stickOv = null;
        for (var i = 0; i < layout.overlays.length; i++) {
            if (layout.overlays[i].target === ringTarget) {
                stickOv = layout.overlays[i];
                break;
            }
        }
        if (!stickOv) return;
        // A ring the layout marks "none" is a DRAWING, not a control: the
        // 2015 Steam Controller's right-pad ghost stick is bound by the pad
        // surface underneath, and building a nipplejs zone here would lay a
        // relative joystick twice the pad's size over that surface (the
        // stick zone's z-index outranks the touch zones') and swallow it.
        if (stickOv.inputKind === "none") return;

        // Position zone centered on stick area, enlarged 2x for comfortable thumb use.
        var enlargeFactor = 2.0;
        var cx = stickOv.x + stickOv.w / 2;
        var cy = stickOv.y + stickOv.h / 2;
        var zoneW = stickOv.w * enlargeFactor;
        var zoneH = stickOv.h * enlargeFactor;

        zone.style.left = ((cx - zoneW / 2) / layout.baseWidth * 100) + "%";
        zone.style.top = ((cy - zoneH / 2) / layout.baseHeight * 100) + "%";
        zone.style.width = (zoneW / layout.baseWidth * 100) + "%";
        zone.style.height = (zoneH / layout.baseHeight * 100) + "%";

        var lastX = 32767, lastY = 32767;
        var touchStartTime = 0;
        var touchStartDist = 0;
        // -1 is outside the axis range, so the first post-reconnect sample
        // always goes out even when the stick sits at center.
        resyncFns.push(function () { lastX = -1; lastY = -1; });
        releaseFns.push(function () {
            if (lastX !== 32767 || lastY !== 32767) {
                send({ type: "input", kind: "axis", code: axisX, value: 32767 });
                send({ type: "input", kind: "axis", code: axisY, value: 32767 });
                lastX = 32767; lastY = 32767;
            }
            moveStickOverlay(ringTarget, 0, 0);
        });

        var joystick = nipplejs.create({
            zone: zone,
            mode: "static",
            color: "rgba(255,255,255,0.3)",
            position: { left: "50%", top: "50%" },
            multitouch: true
        });

        joystick.on("start", function () {
            touchStartTime = Date.now();
            touchStartDist = 0;
        });

        joystick.on("move", function (evt, data) {
            var maxDist = 50;
            var norm = Math.min(data.distance / maxDist, 1.0);
            var rad = data.angle.radian;
            var dx = Math.cos(rad) * norm;
            var dy = -Math.sin(rad) * norm;
            var x = Math.round(32767 + dx * 32767);
            var y = Math.round(32767 + dy * 32767);
            x = Math.max(0, Math.min(65535, x));
            y = Math.max(0, Math.min(65535, y));

            touchStartDist = Math.max(touchStartDist, data.distance);

            if (x !== lastX || y !== lastY) {
                send({ type: "input", kind: "axis", code: axisX, value: x });
                send({ type: "input", kind: "axis", code: axisY, value: y });
                lastX = x;
                lastY = y;
            }

            // Visually move stick overlay.
            moveStickOverlay(ringTarget, dx, dy);
        });

        joystick.on("end", function () {
            // Reset axes.
            if (lastX !== 32767 || lastY !== 32767) {
                send({ type: "input", kind: "axis", code: axisX, value: 32767 });
                send({ type: "input", kind: "axis", code: axisY, value: 32767 });
                lastX = 32767;
                lastY = 32767;
            }
            moveStickOverlay(ringTarget, 0, 0);

            // Stick click detection: quick tap with minimal movement.
            var elapsed = Date.now() - touchStartTime;
            if (elapsed < 200 && touchStartDist < 10) {
                send({ type: "input", kind: "button", code: clickCode, value: 1 });
                setOverlayActive(clickTarget, true);
                haptic();
                setTimeout(function () {
                    send({ type: "input", kind: "button", code: clickCode, value: 0 });
                    setOverlayActive(clickTarget, false);
                }, 100);
            }
        });
    }

    function moveStickOverlay(target, normX, normY) {
        var img = overlayImages[target];
        if (!img || !layout) return;
        var travel = layout.stickMaxTravel * scaleFactor;
        var tx = normX * travel;
        var ty = normY * travel;
        img.style.transform = "translate(" + tx + "px, " + ty + "px)";
    }

})();
