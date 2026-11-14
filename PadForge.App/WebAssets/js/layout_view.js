// PadForge Web Controller: one controller drawing at rest (issue #555),
// shared by the home page's cards and the controller picture page. It draws
// what buildController in controller_client.js draws before any touch: the
// base, the trigger bases behind it, the stick caps, the decals, and a dimmed
// trigger where a layout has no trigger base and its base does not draw one.
// The press art sits hidden on top, for a caller that lights it.
(function (global) {
    "use strict";

    function pct(v, base) { return (v / base * 100) + "%"; }

    function place(img, ov, layout) {
        img.style.left = pct(ov.x, layout.baseWidth);
        img.style.top = pct(ov.y, layout.baseHeight);
        img.style.width = pct(ov.w, layout.baseWidth);
        img.style.height = pct(ov.h, layout.baseHeight);
    }

    var CLASS_FOR = {
        trigger: "pf-ov pf-trigger",
        triggerBase: "pf-ov pf-trigger-base",
        stickRing: "pf-ov pf-stick",
        decal: "pf-ov pf-decal"
    };

    // Draws the layout into container, replacing what it held. Returns the
    // overlays by target, so a caller can light a press, fill a trigger or
    // move a stick.
    function draw(container, layout) {
        container.innerHTML = "";
        container.classList.add("pf-pad");

        var base = document.createElement("img");
        base.className = "pf-base";
        base.alt = "";
        base.src = "/img/" + layout.basePath;
        container.appendChild(base);

        // Which triggers ship a base of their own (controller_client.js's
        // baseFor), and whether the base render draws the triggers itself.
        var baseFor = {};
        layout.overlays.forEach(function (o) {
            if (o.type === "triggerBase") baseFor[o.target.replace("TriggerBase", "Trigger")] = true;
        });
        var baseDrawsTriggers = !!layout.baseDrawsTriggers;

        var overlays = {};
        layout.overlays.forEach(function (ov) {
            if (ov.type === "touchpad") return;                  // no image
            if (!ov.image || /\/$/.test(ov.image)) return;      // touch zone only
            if (ov.type === "trigger" && !baseFor[ov.target] && !baseDrawsTriggers) {
                // The rest silhouette: the same art, dimmed and unclipped,
                // in front of the base and behind the live copy.
                var rest = document.createElement("img");
                rest.className = "pf-ov pf-trigger-base";
                rest.alt = "";
                rest.src = "/img/" + ov.image;
                rest.style.opacity = "0.45";
                rest.style.zIndex = "1";
                place(rest, ov, layout);
                container.appendChild(rest);
            }
            var img = document.createElement("img");
            img.className = CLASS_FOR[ov.type] || "pf-ov pf-press";
            img.alt = "";
            img.src = "/img/" + ov.image;
            place(img, ov, layout);
            container.appendChild(img);
            overlays[ov.target] = img;
        });
        return { layout: layout, overlays: overlays };
    }

    // Sizes container to the largest drawing that fits maxW by maxH.
    function fit(container, layout, maxW, maxH) {
        var ar = layout.baseWidth / layout.baseHeight;
        var w = maxW, h = maxW / ar;
        if (h > maxH) { h = maxH; w = maxH * ar; }
        container.style.width = Math.floor(w) + "px";
        container.style.height = Math.floor(h) + "px";
        return w / layout.baseWidth;
    }

    // GET /api/layout for a layout key and an optional finish. A request
    // that gives no layout calls failed, where the caller gives one: the
    // server refused, the answer was no JSON, or it never came.
    function fetchLayout(type, finish, done, failed) {
        var xhr = new XMLHttpRequest();
        var url = "/api/layout?type=" + encodeURIComponent(type);
        if (finish) url += "&finish=" + encodeURIComponent(finish);
        function fail() { if (failed) failed(); }
        xhr.open("GET", url, true);
        xhr.onload = function () {
            if (xhr.status !== 200) { fail(); return; }
            var layout;
            try { layout = JSON.parse(xhr.responseText); } catch (e) { fail(); return; }
            done(layout);
        };
        xhr.onerror = fail;
        xhr.ontimeout = fail;
        xhr.onabort = fail;
        xhr.send();
    }

    global.PadForgeLayoutView = { draw: draw, fit: fit, fetchLayout: fetchLayout };
})(window);
