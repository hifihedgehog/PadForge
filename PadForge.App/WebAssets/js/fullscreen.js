// A fullscreen toggle for the web controller pages (discussion #402).
//
// Added only where the browser reports element fullscreen as available and
// callable, which is Android Chrome and desktop browsers. iPhone Safari does
// not implement element fullscreen, so nothing is added there. The page
// provides the mount point (#fsMount) so the button sits in the page's own
// header or control strip and never over a control. Requested straight from
// the tap, since the browser requires the gesture, and the label follows
// fullscreenchange so a swipe-out by the browser is reflected.
(function () {
    "use strict";
    var doc = document;
    var root = doc.documentElement;
    var canFs = !!doc.fullscreenEnabled && typeof root.requestFullscreen === "function" && typeof doc.exitFullscreen === "function";
    if (!canFs) return;

    function mount() {
        var host = doc.getElementById("fsMount");
        if (!host) {
            host = doc.createElement("div");
            host.id = "pfTopControls";
            host.style.cssText = "position:fixed;top:4px;left:6px;z-index:46;display:flex;gap:6px;";
            doc.body.appendChild(host);
        }
        var btn = doc.createElement("button");
        btn.id = "fsBtn";
        btn.type = "button";
        // The look is css/padforge.css's chip, which every page that mounts
        // this button links.
        btn.className = "pf-chip";
        function label() { btn.textContent = doc.fullscreenElement ? "Exit fullscreen" : "Fullscreen"; }
        label();
        btn.addEventListener("click", function () {
            var p;
            try { p = doc.fullscreenElement ? doc.exitFullscreen() : root.requestFullscreen({ navigationUI: "hide" }); }
            catch (e) { return; }
            if (p && typeof p.catch === "function") p.catch(function () { });
        });
        doc.addEventListener("fullscreenchange", label);
        host.appendChild(btn);
    }

    if (doc.readyState === "loading") doc.addEventListener("DOMContentLoaded", mount);
    else mount();
})();
