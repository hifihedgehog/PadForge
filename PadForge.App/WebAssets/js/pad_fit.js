// PadForge custom pad: where each control of a build-your-own pad goes on a
// screen of any shape.
//
// A pad is built on one screen and played on others: a wider phone, a
// tablet, the same phone turned upright or with the browser's bars showing.
// The first builder stored each control as shares of the screen's width and
// of its height and drew it that way, so a round button was round only on a
// screen of the shape it was built on, and a pad turned upright was a row of
// thin tall ovals.
//
// Here a control has one size on every screen and keeps its shape:
//
//   - Sizes are measured against the screen's shorter side, which is the
//     same edge of the phone however it is held. Eden's overlay sizes its
//     controls the same way (InputOverlay.kt, minScreenDimension).
//   - Controls that sit together are a cluster, and a cluster keeps its
//     shape: a diamond of face buttons, a Back and Start pair.
//   - A cluster on the left of the screen it was built on keeps its
//     distance from this screen's left edge, one on the right from the
//     right edge, and one at the middle stays at the middle. RetroArch
//     moves an overlay's two halves toward the edges of a wider screen the
//     same way (input_driver.c, x_separation), one control at a time.
//   - Every cluster keeps its distance from the bottom of the screen, where
//     the thumbs are. Room to spare is above the pad.
//   - Where two clusters would meet, or one would leave the screen, the
//     whole pad is drawn smaller.
//
// A pad keeps an arrangement for a screen held sideways, one for a screen
// held upright, or both. An arrangement is the shape of the screen it was
// made on (its width over its height) and each control's place as shares of
// that screen. Where a pad has no arrangement for the way the screen is
// held, the other one is fitted to it by the rules above. The first edit
// made on a screen gives the pad an arrangement of its own for that screen.
//
// As PadForge keeps it: aspect with each control's x, y, w, h is one
// arrangement, and aspect2 with x2, y2, w2, h2 the other, when there is one.
// A pad saved by the first builder has no aspect: read() works one out from
// its round controls.
var PadFit = (function () {
    "use strict";

    var SHORT = 100;         // a screen's shorter side, in the units sizes are kept in
    var NEAR = 5;            // controls no farther apart than this sit together
    var GAP_LEAST = 2;       // the least room kept between two clusters
    var MIDDLE = 0.05;       // a cluster this near the middle, as a share of the board's width, is at the middle
    var NEAR_SQUARE = 0.12;  // a button this near square is a round one
    var GRID = 2;            // the editor's grid, in units
    var SMALLEST = 6;        // the smallest a control's side can be made, in units
    var ASPECT_LEAST = 0.2, ASPECT_MOST = 5;
    var OLD_ASPECT = 16 / 9; // an old pad with no round control to judge by
    var SQUARE = { stick: true, dpad: true };
    // A new control's width and height, in units.
    var SIZES = { button: [16, 16], stick: [34, 34], dpad: [34, 34], slider: [14, 40], touch: [64, 36] };
    var SIDEWAYS = 0, UPRIGHT = 1;

    function clamp(v, lo, hi) { return v < lo ? lo : v > hi ? hi : v; }
    function num(v) { v = Number(v); return isFinite(v) ? v : 0; }
    function isAspect(a) { a = Number(a); return a >= ASPECT_LEAST && a <= ASPECT_MOST; }

    // The board of a screen of that shape, in units.
    function board(aspect) {
        return aspect >= 1 ? { w: SHORT * aspect, h: SHORT } : { w: SHORT, h: SHORT / aspect };
    }

    // Sideways or upright: the way a screen of that shape is held.
    function held(width, height) { return width >= height ? SIDEWAYS : UPRIGHT; }

    // A stick and a D-pad are as wide as they are tall. A button may be a
    // circle or a pill.
    function isSquare(kind) { return !!SQUARE[kind]; }

    function nearSquare(w, h) {
        var big = Math.max(w, h);
        return big > 0 && Math.abs(w - h) <= NEAR_SQUARE * big;
    }

    // The middle one of a control kind's heights over widths, or 0.
    function middleRatio(widgets, wanted) {
        var ratios = [];
        widgets.forEach(function (wd) {
            if (wanted(wd.kind) && num(wd.w) > 0 && num(wd.h) > 0) ratios.push(num(wd.h) / num(wd.w));
        });
        ratios.sort(function (p, q) { return p - q; });
        var n = ratios.length;
        return !n ? 0 : n % 2 ? ratios[(n - 1) / 2] : (ratios[n / 2 - 1] + ratios[n / 2]) / 2;
    }

    // The shape of the screen a pad of the first builder was built on. Its
    // round controls say what that was: each was sized to look round, so its
    // height over its width, as shares, is the screen's width over its
    // height. Sticks and D-pads are asked first, since a button may have
    // been drawn wide on purpose.
    function oldAspect(widgets) {
        var aspect = middleRatio(widgets, isSquare)
            || middleRatio(widgets, function (kind) { return kind === "button"; })
            || OLD_ASPECT;
        return clamp(aspect, 0.45, 2.6);
    }

    function box(x, y, w, h) { return { x: num(x), y: num(y), w: num(w), h: num(h) }; }

    // A pad as PadForge keeps it, made into the pad this page works on: its
    // controls, each with its place sideways and its place upright.
    function read(stored) {
        var list = (stored && stored.widgets) || [];
        var old = !isAspect(stored && stored.aspect);
        var aspect = old ? oldAspect(list) : Number(stored.aspect);
        var one = aspect >= 1 ? SIDEWAYS : UPRIGHT;
        var two = !old && isAspect(stored.aspect2) && (Number(stored.aspect2) >= 1 ? SIDEWAYS : UPRIGHT) !== one;
        var pad = { id: (stored && stored.id) || null, name: (stored && stored.name) || "", faces: [0, 0], widgets: [] };
        pad.faces[one] = aspect;
        if (two) pad.faces[1 - one] = Number(stored.aspect2);
        var b = board(aspect);
        list.forEach(function (wd) {
            var at = [null, null];
            at[one] = box(wd.x, wd.y, wd.w, wd.h);
            // A button of the first builder drawn nearly round was meant round.
            if (old && wd.kind === "button") {
                var w = at[one].w * b.w, h = at[one].h * b.h;
                if (w !== h && nearSquare(w, h)) {
                    var side = Math.min(w, h);
                    at[one] = box(at[one].x + (w - side) / 2 / b.w, at[one].y + (h - side) / 2 / b.h, side / b.w, side / b.h);
                }
            }
            if (two) {
                at[1 - one] = (wd.w2 > 0 && wd.h2 > 0) ? box(wd.x2, wd.y2, wd.w2, wd.h2)
                    : middleOf(pad.faces[1 - one], at[one], aspect);
            }
            pad.widgets.push({ kind: wd.kind, code: num(wd.code), label: wd.label || "", at: at });
        });
        return pad;
    }

    // The pad as PadForge keeps it.
    function write(pad) {
        var one = pad.faces[SIDEWAYS] ? SIDEWAYS : UPRIGHT;
        var two = pad.faces[SIDEWAYS] && pad.faces[UPRIGHT];
        var out = { name: pad.name, aspect: pad.faces[one], widgets: [] };
        if (pad.id) out.id = pad.id;
        if (two) out.aspect2 = pad.faces[UPRIGHT];
        pad.widgets.forEach(function (wd) {
            var a = wd.at[one];
            var w = { kind: wd.kind, x: a.x, y: a.y, w: a.w, h: a.h, code: wd.code, label: wd.label || "" };
            if (two) { var b = wd.at[UPRIGHT]; w.x2 = b.x; w.y2 = b.y; w.w2 = b.w; w.h2 = b.h; }
            out.widgets.push(w);
        });
        return out;
    }

    // A place at the middle of another board for a control of the same size.
    function middleOf(aspect, from, fromAspect) {
        var a = board(fromAspect), b = board(aspect);
        var w = Math.min(from.w * a.w, b.w), h = Math.min(from.h * a.h, b.h);
        return box((b.w - w) / 2 / b.w, (b.h - h) / 2 / b.h, w / b.w, h / b.h);
    }

    // Each control's box on one arrangement's board, in units.
    function boxes(pad, face) {
        var b = board(pad.faces[face]);
        return pad.widgets.map(function (wd) {
            var at = wd.at[face];
            var w = clamp(at.w, 0, 1) * b.w, h = clamp(at.h, 0, 1) * b.h;
            var x = clamp(at.x, 0, 1) * b.w, y = clamp(at.y, 0, 1) * b.h;
            if (isSquare(wd.kind)) {
                var side = Math.min(w, h);
                x += (w - side) / 2; y += (h - side) / 2;
                w = h = side;
            }
            return { x: clamp(x, 0, b.w - w), y: clamp(y, 0, b.h - h), w: w, h: h };
        });
    }

    // The room between two boxes along one axis. Below zero they overlap.
    function apart(aFrom, aSize, bFrom, bSize) {
        return Math.max(aFrom, bFrom) - Math.min(aFrom + aSize, bFrom + bSize);
    }

    // Which cluster each control is in: controls no farther apart than NEAR,
    // along and down, sit together, and so do the ones they sit with.
    function clusters(units) {
        var of = units.map(function (_, i) { return i; });
        function root(i) { while (of[i] !== i) { of[i] = of[of[i]]; i = of[i]; } return i; }
        for (var i = 0; i < units.length; i++)
            for (var j = i + 1; j < units.length; j++)
                if (apart(units[i].x, units[i].w, units[j].x, units[j].w) <= NEAR &&
                    apart(units[i].y, units[i].h, units[j].y, units[j].h) <= NEAR)
                    of[root(i)] = root(j);
        return units.map(function (_, k) { return root(k); });
    }

    // Each cluster's side of its board: -1 left, 0 the middle, 1 right, by
    // where the middle of its box is.
    function sides(units, cluster, width) {
        var from = {}, to = {}, side = {};
        units.forEach(function (u, i) {
            var c = cluster[i];
            from[c] = from[c] === undefined ? u.x : Math.min(from[c], u.x);
            to[c] = to[c] === undefined ? u.x + u.w : Math.max(to[c], u.x + u.w);
        });
        Object.keys(from).forEach(function (c) {
            var off = (from[c] + to[c]) / 2 - width / 2;
            side[c] = off < -MIDDLE * width ? -1 : off > MIDDLE * width ? 1 : 0;
        });
        return side;
    }

    // Where each control goes on a screen of that width and height, in the
    // screen's pixels. face is the arrangement drawn, own whether it was
    // made for a screen held this way, scale the pixels one unit is drawn
    // at and natural the pixels it is drawn at when nothing had to shrink.
    function place(pad, width, height) {
        var way = held(width, height);
        var face = pad.faces[way] ? way : 1 - way;
        var out = { face: face, own: face === way, scale: 0, natural: 0, rects: [] };
        if (!pad.widgets.length || !(width > 0) || !(height > 0) || !pad.faces[face]) return out;
        var b = board(pad.faces[face]), units = boxes(pad, face);
        var cluster = clusters(units), side = sides(units, cluster, b.w);

        // The pad at so many pixels a unit: each cluster from its own edge
        // of the screen, or from the middle, and from the bottom.
        function drawn(scale) {
            return units.map(function (u, i) {
                var s = side[cluster[i]];
                var x = s < 0 ? u.x * scale
                      : s > 0 ? width - (b.w - u.x) * scale
                      : width / 2 + (u.x - b.w / 2) * scale;
                return { x: x, y: height - (b.h - u.y) * scale, w: u.w * scale, h: u.h * scale };
            });
        }

        // Nothing off the screen, and no two clusters closer than they may be.
        function fits(rects, scale) {
            var slip = 0.01, room = GAP_LEAST * scale - slip;
            for (var i = 0; i < rects.length; i++) {
                var r = rects[i];
                if (r.x < -slip || r.y < -slip || r.x + r.w > width + slip || r.y + r.h > height + slip) return false;
                for (var j = i + 1; j < rects.length; j++) {
                    if (cluster[i] === cluster[j]) continue;
                    var q = rects[j];
                    if (apart(r.x, r.w, q.x, q.w) < room && apart(r.y, r.h, q.y, q.h) < room) return false;
                }
            }
            return true;
        }

        out.natural = Math.min(width, height) / SHORT;
        var scale = out.natural, rects = drawn(scale);
        if (!fits(rects, scale)) {
            // The largest the pad can be drawn. Clusters on different sides
            // draw apart as the pad is drawn smaller, so halving finds it.
            var least = 0, most = out.natural;
            for (var step = 0; step < 40; step++) {
                var mid = (least + most) / 2;
                if (fits(drawn(mid), mid)) least = mid; else most = mid;
            }
            scale = least;
            rects = drawn(scale);
            if (!(scale > 0) || !fits(rects, scale)) {
                // Clusters that cross each other. The pad is drawn as it was
                // built, in one piece, as large as the screen takes.
                scale = Math.min(out.natural, width / b.w, height / b.h);
                rects = units.map(function (u) {
                    return { x: (width - b.w * scale) / 2 + u.x * scale, y: height - (b.h - u.y) * scale,
                             w: u.w * scale, h: u.h * scale };
                });
            }
        }
        out.scale = scale;
        out.rects = rects;
        return out;
    }

    // Makes the pad as it is drawn on this screen the pad's own arrangement
    // for a screen held this way. An edit is made on the screen in hand, so
    // the first one takes this.
    function adopt(pad, rects, width, height) {
        var way = held(width, height);
        pad.faces[way] = clamp(width / height, ASPECT_LEAST, ASPECT_MOST);
        pad.widgets.forEach(function (wd, i) {
            var r = rects[i];
            wd.at[way] = r ? box(r.x / width, r.y / height, r.w / width, r.h / height) : wd.at[way];
        });
        return pad;
    }

    // Gives up the arrangement made for a screen held this way, when the
    // pad has the other one to be fitted from. True when it did.
    function drop(pad, width, height) {
        var way = held(width, height);
        if (!pad.faces[way] || !pad.faces[1 - way]) return false;
        pad.faces[way] = 0;
        pad.widgets.forEach(function (wd) { wd.at[way] = null; });
        return true;
    }

    // A new control on this screen, at the free place nearest its middle.
    // The pad's arrangement for this screen is its own by now. In the other
    // arrangement, when there is one, it goes at that board's middle at the
    // same size.
    function add(pad, kind, code, label, width, height) {
        var way = held(width, height), unit = Math.min(width, height) / SHORT;
        var size = SIZES[kind] || SIZES.button;
        var w = Math.min(size[0] * unit, width), h = Math.min(size[1] * unit, height);
        var step = GRID * unit, room = GAP_LEAST * unit;
        var there = pad.widgets.map(function (wd) {
            var a = wd.at[way];
            return a ? { x: a.x * width, y: a.y * height, w: a.w * width, h: a.h * height } : null;
        });
        function free(x, y) {
            return !there.some(function (o) {
                return o && apart(x, w, o.x, o.w) < room && apart(y, h, o.y, o.h) < room;
            });
        }
        // The middle when nothing is there, else the grid place nearest it.
        var x = (width - w) / 2, y = (height - h) / 2;
        if (step > 0 && !free(x, y)) {
            var best = Infinity;
            for (var gy = 0; gy + h <= height + 0.01; gy += step) {
                for (var gx = 0; gx + w <= width + 0.01; gx += step) {
                    var far = Math.pow(gx + w / 2 - width / 2, 2) + Math.pow(gy + h / 2 - height / 2, 2);
                    if (far < best && free(gx, gy)) { best = far; x = gx; y = gy; }
                }
            }
        }
        var at = [null, null];
        at[way] = box(x / width, y / height, w / width, h / height);
        if (pad.faces[1 - way]) at[1 - way] = middleOf(pad.faces[1 - way], at[way], pad.faces[way]);
        var wd = { kind: kind, code: num(code), label: label || "", at: at };
        pad.widgets.push(wd);
        return wd;
    }

    // The pad a new one starts as, on this screen: a stick at the lower
    // left, A and B at the lower right and the right trigger above them.
    function starter(width, height) {
        // A page with no size yet starts the pad for a phone held sideways.
        if (!(width > 0) || !(height > 0)) { width = 844; height = 390; }
        var unit = Math.min(width, height) / SHORT;
        var pad = { id: null, name: "Custom Pad", faces: [0, 0], widgets: [] };
        var way = held(width, height);
        pad.faces[way] = clamp(width / height, ASPECT_LEAST, ASPECT_MOST);
        // The screen in units, and a control put on it by its left, top,
        // width and height.
        var wide = width / unit, tall = height / unit;
        function put(kind, code, x, y, w, h) {
            var at = [null, null];
            at[way] = box(x / wide, y / tall, w / wide, h / tall);
            pad.widgets.push({ kind: kind, code: code, label: "", at: at });
        }
        put("stick", 0, 8, tall - 44, 34, 34);
        put("button", 0, wide - 38, tall - 28, 16, 16);
        put("button", 1, wide - 22, tall - 44, 16, 16);
        put("slider", 5, wide - 21, tall - 84, 14, 34);
        return pad;
    }

    return {
        SHORT: SHORT,
        GRID: GRID,
        SMALLEST: SMALLEST,
        SIDEWAYS: SIDEWAYS,
        UPRIGHT: UPRIGHT,
        board: board,
        held: held,
        isSquare: isSquare,
        nearSquare: nearSquare,
        read: read,
        write: write,
        place: place,
        adopt: adopt,
        drop: drop,
        add: add,
        starter: starter
    };
})();
