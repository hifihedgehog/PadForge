// PadForge Web Controller: the phone's home page (#574). The layout this
// phone used last is one button, four common layouts sit under it, and the
// rest open under More Layouts. A tile picks a layout, the dots under the
// tiles pick its finish, and Continue opens it. The viewer's name (#555) is
// kept here for the controller, Browser Gamepad and builder pages to send.
(function () {
    "use strict";

    var NAME_KEY = "padforge_viewer_name";
    var LAST_KEY = "padforge_last_layout";
    var WLG = "linear-gradient(135deg,#25242b 62%,#7b2fbe 62%)";

    // Each layout's finishes, the stock art first: the server's key (empty
    // for the stock art), the app's name and the dot's color. The keys are
    // the server's (WebControllerServer.LayoutDefs, Finishes) in its order,
    // and the names are Models2D/Controller2DColorways.cs's. WLG is Yuriy's
    // finish from ForgePlay (#555).
    var FINISHES = {
        xbox360: [["", "White", "#f3f5f8"], ["WLG", "WLG", WLG]],
        xboxseries: [["", "Robot White", "#f3f5f8"], ["Carbon", "Carbon Black", "#1b1f26"],
            ["DeepPink", "Deep Pink", "#f06aa0"], ["ElectricVolt", "Electric Volt", "#d9e63a"],
            ["PulseRed", "Pulse Red", "#c93030"], ["ShockBlue", "Shock Blue", "#2f6fd6"],
            ["WLG", "WLG", WLG]],
        ds4: [["", "Jet Black", "#1b1f26"], ["GlacierWhite", "Glacier White", "#e8e8e8"],
            ["Gold", "Gold", "#c9a227"], ["MagmaRed", "Magma Red", "#c0392b"],
            ["MidnightBlue", "Midnight Blue", "#1f3a93"]],
        dualsense: [["", "White", "#f3f5f8"], ["CosmicRed", "Cosmic Red", "#b0233a"],
            ["GalacticPurple", "Galactic Purple", "#7b6bb7"], ["Midnight", "Midnight Black", "#23272f"],
            ["NovaPink", "Nova Pink", "#f7a8c4"], ["StarlightBlue", "Starlight Blue", "#7fb2d9"],
            ["WLG", "WLG", WLG]]
    };

    // A custom pad's picture: the builder's card icon (#296 phase 4).
    var CUSTOM_GLYPH = '<svg class="glyph" viewBox="0 0 32 32" fill="none" stroke="currentColor" stroke-width="1.87" aria-hidden="true">' +
        '<rect x="4" y="7" width="24" height="18" rx="4"/><circle cx="22" cy="14" r="2" fill="currentColor"/>' +
        '<circle cx="10" cy="18" r="3"/></svg>';

    function finishesOf(key) {
        return Object.prototype.hasOwnProperty.call(FINISHES, key) ? FINISHES[key] : [];
    }

    // The finish when the layout has it, else "" (the stock art).
    function knownFinish(key, finish) {
        var list = finishesOf(key);
        for (var i = 0; i < list.length; i++)
            if (list[i][0] === finish) return finish;
        return "";
    }

    // Where Continue goes: the tile's page, with the finish when it is not
    // the stock art, as the cards' color dots linked before.
    function continueHref(href, key, finish) {
        finish = knownFinish(key, finish);
        return finish ? href + "&finish=" + encodeURIComponent(finish) : href;
    }

    // The choice this phone made last, or null. Junk reads as none.
    function parseLast(raw) {
        if (!raw) return null;
        var v;
        try { v = JSON.parse(raw); } catch (e) { return null; }
        if (!v || typeof v !== "object" || typeof v.key !== "string" || !v.key) return null;
        return {
            key: v.key,
            finish: typeof v.finish === "string" ? v.finish : "",
            name: typeof v.name === "string" ? v.name : ""
        };
    }

    // What Continue keeps, or null for Build Your Own: an unsaved pad has
    // nothing to come back to.
    function lastRecord(key, finish, name) {
        if (key === "build") return null;
        return JSON.stringify({ key: key, finish: knownFinish(key, finish), name: name });
    }

    // The More Layouts line: the first four names and how many follow.
    function summaryText(names) {
        if (names.length <= 4) return names.join(", ");
        return names.slice(0, 4).join(", ") + " and " + (names.length - 4) + " more";
    }

    // ── The page ──
    var el = {};
    var tiles = [];        // every tile, a custom pad's included
    var chosen = null;     // the pick: { key, href, name, tile }
    var last = null;       // the stored choice, while it still names a tile
    var finishFor = {};    // a layout's finish, picked while the page is open
    var views = {};        // a preview's drawing, by layout key
    var resize = window.ResizeObserver ? new ResizeObserver(refitAll) : null;

    function itemOf(tile) {
        return {
            key: tile.getAttribute("data-key"),
            href: tile.getAttribute("data-href"),
            name: tile.querySelector(".name").textContent,
            tile: tile
        };
    }

    function tileFor(key) {
        for (var i = 0; i < tiles.length; i++)
            if (tiles[i].getAttribute("data-key") === key) return tiles[i];
        return null;
    }

    function motion() {
        return window.matchMedia && window.matchMedia("(prefers-reduced-motion: reduce)").matches
            ? "auto" : "smooth";
    }

    // A layout tile's controller at rest in its finish, drawn by
    // layout_view.js as the cards were since #555. A newer finish wins over
    // an older answer, as on the picture page (display_client.js redraw).
    function draw(tile) {
        var box = tile.querySelector(".preview");
        if (!box) return;
        var key = box.getAttribute("data-layout");
        var finish = finishFor[key] || "";
        var want = key + "|" + finish;
        if (box.getAttribute("data-want") === want) return;
        box.setAttribute("data-want", want);
        PadForgeLayoutView.fetchLayout(key, finish, function (layout) {
            if (box.getAttribute("data-want") !== want) return;
            var pad = document.createElement("span");
            box.innerHTML = "";
            box.appendChild(pad);
            PadForgeLayoutView.draw(pad, layout);
            views[key] = { box: box, pad: pad, layout: layout };
            refit(views[key]);
            if (resize) resize.observe(box);
        });
    }

    // A hidden box has no size: its drawing is fitted once it shows.
    function refit(view) {
        if (view.box.clientWidth && view.box.clientHeight)
            PadForgeLayoutView.fit(view.pad, view.layout, view.box.clientWidth, view.box.clientHeight);
    }

    function refitAll() {
        for (var key in views)
            if (Object.prototype.hasOwnProperty.call(views, key)) refit(views[key]);
    }

    // The pick on the page: the tile's edge, Continue's words, the dots.
    function show(rebuildDots) {
        for (var i = 0; i < tiles.length; i++)
            tiles[i].setAttribute("aria-pressed", tiles[i] === chosen.tile ? "true" : "false");
        el.continueName.textContent = "Continue with " + chosen.name;
        el.continueNote.hidden = !(last && last.key === chosen.key);
        if (rebuildDots) buildDots();
    }

    function buildDots() {
        var list = finishesOf(chosen.key);
        var current = finishFor[chosen.key] || "";
        el.finishGroup.innerHTML = "";
        el.finishSection.hidden = list.length === 0;
        list.forEach(function (f) {
            var dot = document.createElement("button");
            dot.type = "button";
            dot.className = "dotb";
            dot.title = f[1];
            dot.setAttribute("aria-label", f[1]);
            dot.setAttribute("aria-pressed", f[0] === current ? "true" : "false");
            dot.setAttribute("data-finish", f[0]);
            var swatch = document.createElement("span");
            swatch.style.background = f[2];
            dot.appendChild(swatch);
            dot.addEventListener("click", function () { pickFinish(f[0]); });
            el.finishGroup.appendChild(dot);
        });
    }

    // The dots stay where they are, so the one under a finger or the
    // keyboard keeps its focus.
    function pickFinish(finish) {
        finishFor[chosen.key] = finish;
        var dots = el.finishGroup.querySelectorAll(".dotb");
        for (var i = 0; i < dots.length; i++)
            dots[i].setAttribute("aria-pressed", dots[i].getAttribute("data-finish") === finish ? "true" : "false");
        if (chosen.tile) draw(chosen.tile);
    }

    function pick(tile) {
        var same = chosen && chosen.tile === tile;
        chosen = itemOf(tile);
        show(!same);
        reveal();
    }

    // Scrolls a part to the top of the screen, 20 px down, and below the
    // finish dots where they stay on screen (a phone on its side).
    function bringToTop(part) {
        var cover = 0;
        if (!el.finishSection.hidden && window.getComputedStyle(el.finishSection).position === "sticky")
            cover = el.finishSection.getBoundingClientRect().height;
        var y = window.pageYOffset + part.getBoundingClientRect().top - cover - 20;
        window.scrollTo({ top: Math.max(0, y), behavior: motion() });
    }

    // After a pick made further down the page, Continue comes into view.
    function reveal() {
        var r = el.continueButton.getBoundingClientRect();
        if (r.top < 0 || r.bottom > window.innerHeight) bringToTop(el.continueButton);
    }

    function setMore(open, scroll) {
        el.moreGrid.hidden = !open;
        el.moreSummary.hidden = open;
        el.moreButton.setAttribute("aria-expanded", open ? "true" : "false");
        if (!open) return;
        var inside = el.moreGrid.querySelectorAll(".lay");
        for (var i = 0; i < inside.length; i++) draw(inside[i]);
        if (scroll) bringToTop(el.moreButton);
    }

    function go() {
        var finish = finishFor[chosen.key] || "";
        var record = lastRecord(chosen.key, finish, chosen.name);
        if (record) {
            try { localStorage.setItem(LAST_KEY, record); } catch (e) { }
        }
        location.href = continueHref(chosen.href, chosen.key, finish);
    }

    function summarize() {
        var names = [];
        var inside = el.moreGrid.querySelectorAll(".lay .name");
        for (var i = 0; i < inside.length; i++) names.push(inside[i].textContent);
        el.moreSummary.textContent = summaryText(names);
    }

    function wireTile(tile) {
        tiles.push(tile);
        tile.addEventListener("click", function () { pick(tile); });
    }

    // A custom pad built on this server (#296 phase 4), a tile under More.
    function addCustomTile(pad) {
        var tile = document.createElement("button");
        tile.type = "button";
        tile.className = "lay";
        tile.setAttribute("aria-pressed", "false");
        tile.setAttribute("data-key", "custom:" + pad.id);
        tile.setAttribute("data-href", "/custom.html?id=" + encodeURIComponent(pad.id));
        tile.innerHTML = CUSTOM_GLYPH + '<span class="name"></span><span class="line"></span>';
        var count = pad.widgets ? pad.widgets.length : 0;
        tile.querySelector(".name").textContent = pad.name || "Custom Pad";
        tile.querySelector(".line").textContent = "Custom pad (" + count + (count === 1 ? " widget)" : " widgets)");
        el.moreGrid.appendChild(tile);
        wireTile(tile);
    }

    // The custom pads, then the stored pick when it was one of them.
    function loadCustomPads(pending) {
        var xhr = new XMLHttpRequest();
        xhr.open("GET", "/api/custom-layouts", true);
        xhr.onload = function () {
            if (xhr.status !== 200) return;
            var list;
            try { list = JSON.parse(xhr.responseText); } catch (e) { return; }
            if (!Array.isArray(list)) return;
            list.forEach(function (pad) { if (pad && pad.id) addCustomTile(pad); });
            summarize();
            if (!pending) return;
            var tile = tileFor(pending);
            if (!tile) {
                // The pad was deleted: forget it, and offer the first layout
                // unless another one was picked meanwhile.
                try { localStorage.removeItem(LAST_KEY); } catch (e) { }
                last = null;
                var fallback = chosen.key === pending;
                if (fallback) chosen = itemOf(tiles[0]);
                show(fallback);
            } else if (chosen.key === pending) {
                chosen = itemOf(tile);
                show(true);
                setMore(true, false);
            }
        };
        xhr.send();
    }

    function wireName() {
        var input = el.viewerName;
        try { input.value = localStorage.getItem(NAME_KEY) || ""; } catch (e) { }
        input.addEventListener("input", function () {
            try { localStorage.setItem(NAME_KEY, input.value.trim()); } catch (e) { }
        });
        // The keyboard's Done closes it. The name is already kept.
        input.addEventListener("keydown", function (e) {
            if (e.key === "Enter") input.blur();
        });
    }

    function start() {
        ["viewerName", "continueButton", "continueName", "continueNote", "layoutGrid",
            "finishSection", "finishGroup", "moreButton", "moreSummary", "moreGrid"].forEach(function (id) {
            el[id] = document.getElementById(id);
        });
        wireName();
        var stock = document.querySelectorAll(".lay");
        for (var i = 0; i < stock.length; i++) wireTile(stock[i]);
        summarize();

        var raw = null;
        try { raw = localStorage.getItem(LAST_KEY); } catch (e) { }
        last = parseLast(raw);
        var pending = null;
        var tile = last ? tileFor(last.key) : null;
        if (tile) {
            finishFor[last.key] = knownFinish(last.key, last.finish);
            chosen = itemOf(tile);
        } else if (last && last.key.indexOf("custom:") === 0) {
            // A custom pad, named as it was stored until the server's list
            // says it is still there.
            pending = last.key;
            chosen = { key: last.key, href: "/custom.html?id=" + encodeURIComponent(last.key.slice(7)),
                name: last.name || "Custom Pad", tile: null };
        } else {
            last = null;
            chosen = itemOf(tiles[0]);
        }
        show(true);

        var common = el.layoutGrid.querySelectorAll(".lay");
        for (var j = 0; j < common.length; j++) draw(common[j]);
        if (chosen.tile && el.moreGrid.contains(chosen.tile)) setMore(true, false);

        el.moreButton.addEventListener("click", function () { setMore(el.moreGrid.hidden, true); });
        el.continueButton.addEventListener("click", go);
        loadCustomPads(pending);
    }

    document.addEventListener("DOMContentLoaded", start);
})();
