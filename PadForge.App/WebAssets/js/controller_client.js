// PadForge Web Controller Client — 2D Controller Overlay Mode
// Renders Xbox 360 or DS4 controller using PNG overlays from PadForge's 2D asset pack.
// Touch input for buttons, triggers, D-pad, and dual analog sticks via nipplejs.

(function () {
    "use strict";

    // ── Config ──
    var params = new URLSearchParams(location.search);
    var layoutType = params.get("layout") || "xbox360";

    // ── Client identity ──
    var clientId = localStorage.getItem("padforge_client_id");
    if (!clientId) {
        clientId = crypto.randomUUID ? crypto.randomUUID() : Math.random().toString(36).slice(2);
        localStorage.setItem("padforge_client_id", clientId);
    }

    // ── Haptic ──
    var vibrate = navigator.vibrate || navigator.webkitVibrate || navigator.mozVibrate;
    function haptic() {
        if (vibrate) vibrate.call(navigator, 30);
    }

    // ── WebSocket ──
    var ws = null;

    function send(obj) {
        if (ws && ws.readyState === WebSocket.OPEN) {
            ws.send(JSON.stringify(obj));
        }
    }

    function connect() {
        var proto = location.protocol === "https:" ? "wss:" : "ws:";
        var hasTouchpad = layout && layout.overlays && layout.overlays.some(function(o) { return o.type === "touchpad"; });
        var wsUrl = proto + "//" + location.host + "/ws?id=" + encodeURIComponent(clientId);
        if (hasTouchpad) wsUrl += "&touchpad=1";
        ws = new WebSocket(wsUrl);

        ws.onopen = function () {
            console.log("[PadForge] WebSocket connected");
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
            }
        };

        ws.onclose = function (ev) {
            console.log("[PadForge] WebSocket closed, code=" + ev.code);
            document.getElementById("controller-viewport").style.display = "none";
            document.getElementById("disconnect-message").style.display = "block";
            setTimeout(connect, 3000);
        };

        ws.onerror = function (ev) {
            console.error("[PadForge] WebSocket error", ev);
            ws.close();
        };
    }

    var statusEl;
    function setStatus(text) {
        if (statusEl) statusEl.textContent = text;
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

        fetchLayoutAndBuild();
    });

    function fetchLayoutAndBuild() {
        var xhr = new XMLHttpRequest();
        xhr.open("GET", "/api/layout?type=" + encodeURIComponent(layoutType), true);
        xhr.onload = function () {
            if (xhr.status !== 200) {
                setStatus("Failed to load layout");
                return;
            }
            layout = JSON.parse(xhr.responseText);
            buildController();
            setupTouchZones();
            setupSticks();
            onResize();
            connect();
        };
        xhr.onerror = function () {
            setStatus("Failed to load layout");
        };
        xhr.send();
    }

    // ── Build controller overlays ──
    function buildController() {
        var baseImg = document.getElementById("base-image");
        baseImg.src = "/img/" + layout.basePath;
        baseImg.onload = onResize;

        for (var i = 0; i < layout.overlays.length; i++) {
            var ov = layout.overlays[i];
            if (ov.type === "touchpad") continue; // no image — handled by setupTouchpadZone
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
            } else if (ov.type === "stickRing") {
                img.className = "overlay stick-ring";
            } else {
                img.className = "overlay";
            }

            container.appendChild(img);
            overlayImages[ov.target] = img;
        }
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
    // Track which touch identifier is on which zone to prevent cross-finger bugs.
    var activeTouches = {}; // touch.identifier → { zone, cleanup }

    // Small meta-buttons that should always be on top of d-pad/trigger zones.
    var smallButtons = ["ButtonBack", "ButtonStart", "ButtonGuide"];

    function setupTouchZones() {
        var dpadOverlays = [];

        for (var i = 0; i < layout.overlays.length; i++) {
            var ov = layout.overlays[i];

            if (ov.type === "stickRing" || ov.type === "stickClick") continue;

            // Touchpad zone: multi-touch surface for DS4 touchpad emulation.
            if (ov.type === "touchpad") {
                setupTouchpadZone(ov, layout);
                continue;
            }

            // Collect D-pad overlays for unified zone.
            if (ov.target.indexOf("DPad") === 0) {
                dpadOverlays.push(ov);
                continue;
            }

            var zone = document.createElement("div");
            zone.className = "touch-zone";

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

        function down(e) {
            e.preventDefault();
            var img = overlayImages[target];
            if (img) img.classList.add("active");
            send({ type: "input", kind: "button", code: code, value: 1 });
            haptic();
        }
        function up(e) {
            e.preventDefault();
            var img = overlayImages[target];
            if (img) img.classList.remove("active");
            send({ type: "input", kind: "button", code: code, value: 0 });
        }

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

        function down(e) {
            e.preventDefault();
            setTriggerFill(target, 1.0);
            send({ type: "input", kind: "axis", code: axisCode, value: 65535 });
            haptic();
        }
        function up(e) {
            e.preventDefault();
            setTriggerFill(target, 0.0);
            send({ type: "input", kind: "axis", code: axisCode, value: 0 });
        }

        zone.addEventListener("touchstart", down, { passive: false });
        zone.addEventListener("touchend", up, { passive: false });
        zone.addEventListener("touchcancel", up, { passive: false });
        zone.addEventListener("mousedown", down);
        zone.addEventListener("mouseup", up);
        zone.addEventListener("mouseleave", up);
    }

    function setTriggerFill(target, fraction) {
        var img = overlayImages[target];
        if (!img) return;
        var topClip = (1.0 - fraction) * 100;
        img.style.clipPath = "inset(" + topClip + "% 0 0 0)";
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
        zone.style.border = "2px solid rgba(255,255,255,0.3)";
        zone.style.background = "rgba(255,255,255,0.05)";
        container.appendChild(zone);

        var finger0Id = null, finger1Id = null;

        function normXY(touch) {
            var rect = zone.getBoundingClientRect();
            return {
                x: Math.max(0, Math.min(1, (touch.clientX - rect.left) / rect.width)),
                y: Math.max(0, Math.min(1, (touch.clientY - rect.top) / rect.height))
            };
        }

        zone.addEventListener("touchstart", function(e) {
            e.preventDefault();
            for (var i = 0; i < e.changedTouches.length; i++) {
                var t = e.changedTouches[i];
                var p = normXY(t);
                if (finger0Id === null) {
                    finger0Id = t.identifier;
                    send({ type: "touchpad", finger: 0, x: p.x, y: p.y, down: true });
                } else if (finger1Id === null) {
                    finger1Id = t.identifier;
                    send({ type: "touchpad", finger: 1, x: p.x, y: p.y, down: true });
                }
            }
        }, { passive: false });

        zone.addEventListener("touchmove", function(e) {
            e.preventDefault();
            for (var i = 0; i < e.changedTouches.length; i++) {
                var t = e.changedTouches[i];
                var p = normXY(t);
                if (t.identifier === finger0Id)
                    send({ type: "touchpad", finger: 0, x: p.x, y: p.y, down: true });
                else if (t.identifier === finger1Id)
                    send({ type: "touchpad", finger: 1, x: p.x, y: p.y, down: true });
            }
        }, { passive: false });

        function onTouchEnd(e) {
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
        zone.addEventListener("touchend", onTouchEnd, { passive: false });
        zone.addEventListener("touchcancel", onTouchEnd, { passive: false });
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

        function updateDpad(e) {
            e.preventDefault();
            var rect = zone.getBoundingClientRect();
            var touch = e.touches ? e.touches[0] : e;
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
            e.preventDefault();
            if (currentPov !== -1) {
                currentPov = -1;
                send({ type: "input", kind: "pov", code: 0, value: -1 });
            }
            setOverlayActive("DPadUp", false);
            setOverlayActive("DPadDown", false);
            setOverlayActive("DPadLeft", false);
            setOverlayActive("DPadRight", false);
        }

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

    // ─── Touchpad zone (DS4 controller page) ───

})();
