using System;
using System.Linq;
using System.Text.Json;
using Jint;
using PadForge.Services;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// A build-your-own pad keeps its controls' shapes on a screen of any
    /// shape (owner, 2026-11-14: the page "stretches the elements and does
    /// not keep shape proportional", worst on an upright screen). The first
    /// builder kept each control as shares of the screen's width and of its
    /// height and drew it that way, so a round button was round on one shape
    /// of screen only. js/pad_fit.js now decides where each control goes,
    /// and these run it in Jint on pads and screens as the page does.
    /// </summary>
    [Collection("WebCustomLayoutStore")]
    public class CustomPadFitTests
    {
        private static string Web(params string[] parts)
            => ExtrasPageTests.RepoText(new[] { "PadForge.App", "WebAssets" }.Concat(parts).ToArray());

        // Pads as PadForge keeps them, the screens of phones and tablets held
        // both ways, and what a test asks of a pad drawn on one.
        private const string Bench = """
            function shares(W, H, list) {
                return list.map(function (c) { return { kind: c[0], label: c[1], code: 0, x: c[2] / W, y: c[3] / H, w: c[4] / W, h: c[5] / H }; });
            }
            // Built on a phone held sideways, 844 by 390: sticks and triggers at the sides, a Back and Start pair and a touch area at the middle.
            var FULL = [
                ["stick", "LS", 40, 210, 140, 140], ["dpad", "DP", 210, 240, 110, 110],
                ["button", "Y", 700, 150, 62, 62], ["button", "X", 640, 210, 62, 62], ["button", "B", 760, 210, 62, 62], ["button", "A", 700, 270, 62, 62],
                ["slider", "LT", 30, 20, 50, 130], ["slider", "RT", 764, 20, 50, 130],
                ["button", "LB", 100, 30, 110, 44], ["button", "RB", 634, 30, 110, 44],
                ["button", "Bk", 366, 40, 50, 44], ["button", "St", 428, 40, 50, 44],
                ["touch", "T", 322, 110, 200, 110]];
            var full = { name: "Full", aspect: 844 / 390, widgets: shares(844, 390, FULL) };
            // As the first builder saved one: no screen shape, its controls at the sizes that builder gave a new one.
            var old = { name: "Old", widgets: [
                { kind: "stick", code: 0, label: "LS", x: 0.05, y: 0.55, w: 0.2, h: 0.32 },
                { kind: "dpad", code: 0, label: "DP", x: 0.28, y: 0.6, w: 0.22, h: 0.34 },
                { kind: "button", code: 0, label: "A", x: 0.80, y: 0.78, w: 0.1, h: 0.16 },
                { kind: "button", code: 1, label: "B", x: 0.88, y: 0.60, w: 0.1, h: 0.16 },
                { kind: "button", code: 2, label: "X", x: 0.72, y: 0.60, w: 0.1, h: 0.16 },
                { kind: "button", code: 3, label: "Y", x: 0.80, y: 0.42, w: 0.1, h: 0.16 },
                { kind: "slider", code: 2, label: "LT", x: 0.03, y: 0.05, w: 0.08, h: 0.4 },
                { kind: "slider", code: 5, label: "RT", x: 0.89, y: 0.05, w: 0.08, h: 0.33 },
                { kind: "button", code: 6, label: "Bk", x: 0.42, y: 0.06, w: 0.07, h: 0.11 },
                { kind: "button", code: 7, label: "St", x: 0.51, y: 0.06, w: 0.07, h: 0.11 }] };
            // Built on a phone held upright, 390 by 844.
            var tall = { name: "Tall", aspect: 390 / 844, widgets: shares(390, 844, [
                ["touch", "T", 30, 60, 330, 190], ["button", "LB", 30, 290, 150, 56], ["button", "RB", 210, 290, 150, 56],
                ["dpad", "DP", 24, 600, 150, 150], ["button", "Y", 272, 560, 62, 62], ["button", "X", 216, 616, 62, 62],
                ["button", "B", 328, 616, 62, 62], ["button", "A", 272, 672, 62, 62], ["button", "Bk", 120, 420, 60, 44], ["button", "St", 210, 420, 60, 44]]) };
            var SCREENS = [[844, 390], [844, 340], [1100, 420], [1280, 720], [1024, 768], [600, 600], [390, 844], [412, 915], [768, 1024], [320, 568], [2400, 1080]];

            function meet(a, b) {
                return Math.min(a.x + a.w, b.x + b.w) - Math.max(a.x, b.x) > 0.02 && Math.min(a.y + a.h, b.y + b.h) - Math.max(a.y, b.y) > 0.02;
            }
            // A pad drawn on a screen: how many controls leave it, how many pairs meet, the worst change of a control's shape.
            function drawn(stored, W, H) {
                var pad = PadFit.read(stored), out = PadFit.place(pad, W, H);
                var home = PadFit.place(pad, 1000 * pad.faces[out.face], 1000);
                var off = 0, meets = 0, shape = 0, unsquare = 0;
                out.rects.forEach(function (r, i) {
                    if (r.x < -0.02 || r.y < -0.02 || r.x + r.w > W + 0.02 || r.y + r.h > H + 0.02) off++;
                    if ((pad.widgets[i].kind === "stick" || pad.widgets[i].kind === "dpad") && Math.abs(r.w - r.h) > 0.01) unsquare++;
                    shape = Math.max(shape, Math.abs(r.w / r.h - home.rects[i].w / home.rects[i].h));
                    for (var j = i + 1; j < out.rects.length; j++) if (meet(r, out.rects[j])) meets++;
                });
                return { off: off, meets: meets, shape: shape, unsquare: unsquare, scale: out.scale / out.natural, own: out.own, face: out.face, rects: out.rects, pad: pad };
            }
            function apart(a, b) {
                var worst = 0;
                a.forEach(function (r, i) { ["x", "y", "w", "h"].forEach(function (k) { worst = Math.max(worst, Math.abs(r[k] - b[i][k])); }); });
                return worst;
            }
            function named(d, label) {
                for (var i = 0; i < d.pad.widgets.length; i++) if (d.pad.widgets[i].label === label) return d.rects[i];
            }
            """;

        private static Jint.Engine Page()
        {
            var engine = new Jint.Engine(o => { o.TimeoutInterval(TimeSpan.FromSeconds(30)); o.MaxStatements(200_000_000); });
            engine.Execute(Web("js", "pad_fit.js"), "pad_fit.js");
            engine.Execute(Bench);
            return engine;
        }

        private static double N(Jint.Engine engine, string expression) => engine.Evaluate(expression).AsNumber();
        private static bool B(Jint.Engine engine, string expression) => engine.Evaluate(expression).AsBoolean();

        /// <summary>The report itself. On every screen, a stick and a D-pad
        /// are as wide as they are tall, and every control has the shape it
        /// had on the screen it was built on: a round button stays round and
        /// a pill stays that pill, upright as sideways.</summary>
        [Fact]
        public void AControlKeepsItsShapeOnEveryScreen()
        {
            using var page = Page();
            foreach (string pad in new[] { "old", "full", "tall" })
                for (int s = 0; s < 11; s++)
                {
                    string d = $"drawn({pad}, SCREENS[{s}][0], SCREENS[{s}][1])";
                    Assert.Equal(0, N(page, d + ".unsquare"));
                    Assert.True(N(page, d + ".shape") < 1e-9, pad + " changes a control's shape on screen " + s);
                }

            // A pad of the first builder, on the upright phone of the report:
            // its buttons are circles and its stick is one. That builder
            // drew a button there 39 wide and 135 tall, a tenth of the
            // screen's width by sixteen hundredths of its height.
            Assert.Equal(N(page, "named(drawn(old, 390, 844), 'A').w"), N(page, "named(drawn(old, 390, 844), 'A').h"), 6);
            Assert.Equal(N(page, "named(drawn(old, 390, 844), 'LS').w"), N(page, "named(drawn(old, 390, 844), 'LS').h"), 6);
            Assert.True(N(page, "named(drawn(old, 390, 844), 'A').w") > 44, "a button is too small to press");

            // A control is the same size held either way: sizes are measured
            // against the screen's shorter side, which is the same edge.
            using var again = Page();
            double sideways = N(again, "named(drawn(PadFit.write(PadFit.starter(844, 390)), 844, 390), '').w");
            double upright = N(again, "named(drawn(PadFit.write(PadFit.starter(390, 844)), 390, 844), '').w");
            Assert.Equal(sideways, upright, 6);
            Assert.Equal(34 * 3.9, sideways, 6);
        }

        /// <summary>On the screen a pad was built on it is drawn as it was
        /// built, to the pixel and at its full size.</summary>
        [Fact]
        public void APadOnItsOwnScreen_IsDrawnAsItWasBuilt()
        {
            using var page = Page();
            Assert.Equal(1, N(page, "drawn(full, 844, 390).scale"), 9);
            Assert.True(B(page, "drawn(full, 844, 390).own"));
            Assert.True(N(page, "apart(drawn(full, 844, 390).rects, FULL.map(function (c) { return { x: c[2], y: c[3], w: c[4], h: c[5] }; }))") < 1e-6);
            Assert.Equal(1, N(page, "drawn(tall, 390, 844).scale"), 9);
            Assert.Equal(60, N(page, "named(drawn(tall, 390, 844), 'T').y"), 6);
        }

        /// <summary>On a wider screen a cluster on the left keeps its
        /// distance from the left edge, one on the right from the right
        /// edge, and what was at the middle stays at the middle, with the
        /// pair of buttons there still a pair. RetroArch moves an overlay's
        /// halves to the edges of a wider screen the same way. An earlier
        /// form of this grouped controls by their overlap along the screen
        /// alone and drew Back and Start apart.</summary>
        [Fact]
        public void OnAWiderScreen_ClustersKeepTheirEdges_AndTheMiddleStaysAtTheMiddle()
        {
            using var page = Page();
            page.Execute("var wide = drawn(full, 1200, 390);");
            Assert.Equal(1, N(page, "wide.scale"), 9);
            Assert.Equal(40, N(page, "named(wide, 'LS').x"), 6);
            Assert.Equal(30, N(page, "named(wide, 'LT').x"), 6);
            Assert.Equal(22, N(page, "1200 - named(wide, 'B').x - named(wide, 'B').w"), 6);
            Assert.Equal(30, N(page, "1200 - named(wide, 'RT').x - named(wide, 'RT').w"), 6);
            // The pair: the same 12 apart, about the screen's middle.
            Assert.Equal(12, N(page, "named(wide, 'St').x - named(wide, 'Bk').x - named(wide, 'Bk').w"), 6);
            Assert.Equal(600, N(page, "(named(wide, 'Bk').x + named(wide, 'St').x + named(wide, 'St').w) / 2"), 6);
            Assert.Equal(600, N(page, "named(wide, 'T').x + named(wide, 'T').w / 2"), 6);
            // The diamond is the diamond it was.
            Assert.Equal(60, N(page, "named(wide, 'Y').x - named(wide, 'X').x"), 6);
            Assert.Equal(60, N(page, "named(wide, 'B').x - named(wide, 'Y').x"), 6);

            // The first builder's pad, whose Back and Start stand either
            // side of the middle: together, and at the middle.
            page.Execute("var o = drawn(old, 844, 390);");
            Assert.Equal(422, N(page, "(named(o, 'Bk').x + named(o, 'St').x + named(o, 'St').w) / 2"), 6);
            Assert.True(N(page, "named(o, 'St').x - named(o, 'Bk').x - named(o, 'Bk').w") < 20);

            // Two buttons that sit together are one cluster, and it has one
            // side. Each taken alone, the left one of this pair is on the
            // left and the right one at the middle, and a wider screen
            // would part them.
            page.Execute("""
                var pair = drawn({ aspect: 844 / 390, widgets: shares(844, 390, [
                    ["button", "P1", 330, 40, 44, 44], ["button", "P2", 380, 40, 44, 44], ["stick", "S", 700, 220, 120, 120]]) }, 1200, 390);
                """);
            Assert.Equal(6, N(page, "named(pair, 'P2').x - named(pair, 'P1').x - named(pair, 'P1').w"), 6);
            Assert.Equal(330, N(page, "named(pair, 'P1').x"), 6);
            Assert.Equal(24, N(page, "1200 - named(pair, 'S').x - named(pair, 'S').w"), 6);
        }

        /// <summary>Held upright, a pad built sideways sits at the bottom of
        /// the screen, where the thumbs are, whole and on the screen. A pad
        /// with room to spare is drawn at its full size, and a crowded one
        /// is drawn smaller in one piece.</summary>
        [Fact]
        public void HeldUpright_ASidewaysPadSitsAtTheBottom_Whole()
        {
            using var page = Page();
            // The starter pad a new one begins as: full size upright.
            page.Execute("var start = PadFit.write(PadFit.starter(844, 390)); var up = drawn(start, 390, 844);");
            Assert.Equal(1, N(page, "up.scale"), 9);
            Assert.False(B(page, "up.own"));
            Assert.Equal(0, N(page, "up.off + up.meets"));
            // Each control is as far from the bottom as it was built.
            Assert.True(N(page, "apart(up.rects.map(function (r) { return { x: 0, y: 844 - r.y, w: r.w, h: r.h }; }), " +
                "drawn(start, 844, 390).rects.map(function (r) { return { x: 0, y: 390 - r.y, w: r.w, h: r.h }; }))") < 1e-6);

            // Thirteen controls built across a wide screen do not fit across
            // a narrow one at full size. They are drawn smaller, together.
            page.Execute("var crowded = drawn(full, 390, 844);");
            Assert.InRange(N(page, "crowded.scale"), 0.6, 0.7);
            Assert.Equal(0, N(page, "crowded.off"));
            Assert.Equal(N(page, "drawn(full, 844, 390).meets"), N(page, "crowded.meets"));
            // Every control is in the lower half of the screen.
            Assert.True(N(page, "Math.min.apply(null, crowded.rects.map(function (r) { return r.y; }))") > 422);
            Assert.Equal(N(page, "(390 - 350) * crowded.scale"), N(page, "844 - named(crowded, 'LS').y - named(crowded, 'LS').h"), 6);
        }

        /// <summary>On no screen does a control leave it, and no two
        /// controls meet that did not meet as the pad was built.</summary>
        [Fact]
        public void NothingLeavesTheScreen_AndNoTwoControlsMeetThatDidNotAsBuilt()
        {
            using var page = Page();
            foreach (var (pad, w, h) in new[] { ("old", 1600, 1000), ("full", 844, 390), ("tall", 390, 844) })
            {
                double asBuilt = N(page, $"drawn({pad}, {w}, {h}).meets");
                for (int s = 0; s < 11; s++)
                {
                    string d = $"drawn({pad}, SCREENS[{s}][0], SCREENS[{s}][1])";
                    Assert.Equal(0, N(page, d + ".off"));
                    Assert.Equal(asBuilt, N(page, d + ".meets"));
                    Assert.InRange(N(page, d + ".scale"), 0.4, 1.0000001);
                }
            }
        }

        /// <summary>A pad of the first builder has no screen shape. Its round
        /// controls give one: each was sized to look round on the screen it
        /// was built on. A pad built upright is read as an upright one.</summary>
        [Fact]
        public void APadOfTheFirstBuilder_GetsItsScreensShapeFromItsRoundControls()
        {
            using var page = Page();
            // The stick's 0.32 over 0.2 and the D-pad's 0.34 over 0.22.
            Assert.Equal((0.32 / 0.2 + 0.34 / 0.22) / 2, N(page, "PadFit.read(old).faces[0]"), 9);
            Assert.Equal(0, N(page, "PadFit.read(old).faces[1]"));
            // Buttons alone, when there is no stick or D-pad.
            Assert.Equal(1.6, N(page, "PadFit.read({ widgets: [{ kind: 'button', x: 0.1, y: 0.1, w: 0.1, h: 0.16 }] }).faces[0]"), 9);
            // No round control at all.
            Assert.Equal(16.0 / 9, N(page, "PadFit.read({ widgets: [{ kind: 'touch', x: 0.1, y: 0.1, w: 0.5, h: 0.3 }] }).faces[0]"), 9);
            // Built upright: round controls wider than tall as shares.
            Assert.Equal(0.5, N(page, "PadFit.read({ widgets: [{ kind: 'stick', x: 0.1, y: 0.6, w: 0.4, h: 0.2 }] }).faces[1]"), 9);
            Assert.Equal(0, N(page, "PadFit.read({ widgets: [{ kind: 'stick', x: 0.1, y: 0.6, w: 0.4, h: 0.2 }] }).faces[0]"));
            // A pad that has its screen's shape keeps it.
            Assert.Equal(844.0 / 390, N(page, "PadFit.read(full).faces[0]"), 9);
        }

        /// <summary>The first edit on a screen held the other way gives the
        /// pad an arrangement of its own for it: what is drawn there is kept
        /// as it is drawn, and the arrangement it was fitted from is not
        /// touched. Both go to PadForge and come back the same.</summary>
        [Fact]
        public void TheFirstEditTakesTheScreen_AndTheOtherArrangementStays()
        {
            using var page = Page();
            page.Execute("""
                var pad = PadFit.read(full), fitted = PadFit.place(pad, 390, 844), home = PadFit.place(pad, 844, 390);
                PadFit.adopt(pad, fitted.rects, 390, 844);
                var mine = PadFit.place(pad, 390, 844), stillHome = PadFit.place(pad, 844, 390);
                var kept = PadFit.write(pad), back = PadFit.read(JSON.parse(JSON.stringify(kept)));
                """);
            Assert.False(B(page, "fitted.own"));
            Assert.True(B(page, "mine.own"));
            Assert.Equal(1, N(page, "mine.scale / mine.natural"), 9);
            Assert.True(N(page, "apart(mine.rects, fitted.rects)") < 1e-6);
            Assert.True(N(page, "apart(stillHome.rects, home.rects)") < 1e-9);
            Assert.Equal(844.0 / 390, N(page, "kept.aspect"), 9);
            Assert.Equal(390.0 / 844, N(page, "kept.aspect2"), 9);
            Assert.True(B(page, "kept.widgets.every(function (w) { return w.w2 > 0 && w.h2 > 0; })"));
            Assert.True(N(page, "apart(PadFit.place(back, 390, 844).rects, mine.rects)") < 1e-6);
            Assert.True(N(page, "apart(PadFit.place(back, 844, 390).rects, home.rects)") < 1e-6);

            // A pad with one arrangement is kept with one.
            Assert.True(B(page, "PadFit.write(PadFit.read(full)).aspect2 === undefined && PadFit.write(PadFit.read(full)).widgets[0].x2 === undefined"));
            Assert.Equal(390.0 / 844, N(page, "PadFit.write(PadFit.read(tall)).aspect"), 9);
        }

        /// <summary>An arrangement made for one way of holding the screen can
        /// be given up: the pad is fitted there again from the other. The
        /// last arrangement a pad has cannot be.</summary>
        [Fact]
        public void GivingUpAnArrangement_FitsThePadFromTheOtherAgain()
        {
            using var page = Page();
            page.Execute("""
                var pad = PadFit.read(full), fitted = PadFit.place(pad, 390, 844);
                PadFit.adopt(pad, fitted.rects.map(function (r) { return { x: r.x, y: r.y - 300, w: r.w, h: r.h }; }), 390, 844);
                var moved = PadFit.place(pad, 390, 844);
                var gave = PadFit.drop(pad, 390, 844), after = PadFit.place(pad, 390, 844), gaveLast = PadFit.drop(pad, 844, 390);
                """);
            Assert.True(N(page, "apart(moved.rects, fitted.rects)") > 299);
            Assert.True(B(page, "gave"));
            Assert.False(B(page, "after.own"));
            Assert.True(N(page, "apart(after.rects, fitted.rects)") < 1e-9);
            Assert.False(B(page, "gaveLast"));
            Assert.True(B(page, "PadFit.place(pad, 844, 390).own"));
        }

        /// <summary>A new control goes to the free place nearest the screen's
        /// middle, at its kind's size, and a stick or a D-pad is square. In
        /// a pad arranged both ways it has a place in both.</summary>
        [Fact]
        public void ANewControl_GoesToTheFreePlaceNearestTheMiddle()
        {
            using var page = Page();
            page.Execute("""
                var pad = PadFit.read(full);
                var first = PadFit.add(pad, "dpad", 0, "", 844, 390), second = PadFit.add(pad, "button", 3, "", 844, 390);
                var all = PadFit.place(pad, 844, 390), last = all.rects.length - 1;
                var meets = 0;
                for (var i = 0; i < last - 1; i++) { if (meet(all.rects[last - 1], all.rects[i])) meets++; if (meet(all.rects[last], all.rects[i])) meets++; }
                if (meet(all.rects[last], all.rects[last - 1])) meets++;
                var empty = PadFit.starter(844, 390); empty.widgets = [];
                var alone = PadFit.add(empty, "stick", 0, "", 844, 390), only = PadFit.place(empty, 844, 390).rects[0];
                var two = PadFit.read(full);
                PadFit.adopt(two, PadFit.place(two, 390, 844).rects, 390, 844);
                var both = PadFit.add(two, "button", 9, "", 390, 844);
                """);
            Assert.Equal(0, N(page, "meets"));
            Assert.Equal(34 * 3.9, N(page, "all.rects[last - 1].w"), 6);
            Assert.Equal(N(page, "all.rects[last - 1].w"), N(page, "all.rects[last - 1].h"), 6);
            Assert.Equal(16 * 3.9, N(page, "all.rects[last].w"), 6);
            Assert.Equal(0, N(page, "PadFit.place(pad, 844, 390).rects.filter(function (r) { return r.x < 0 || r.y < 0 || r.x + r.w > 844 || r.y + r.h > 390; }).length"));
            // With nothing there, the middle itself.
            Assert.Equal(422, N(page, "only.x + only.w / 2"), 6);
            Assert.Equal(195, N(page, "only.y + only.h / 2"), 6);
            Assert.True(B(page, "both.at[0] !== null && both.at[1] !== null"));
            Assert.Equal(N(page, "both.at[1].w * 390"), N(page, "both.at[0].w * 844"), 6);
        }

        /// <summary>The pad a new one starts as is on the screen whole, held
        /// either way, at its full size.</summary>
        [Fact]
        public void TheStarterPad_FitsAScreenHeldEitherWay()
        {
            using var page = Page();
            foreach (var (w, h) in new[] { (844, 390), (390, 844), (1024, 768), (320, 568), (600, 600) })
            {
                string d = $"drawn(PadFit.write(PadFit.starter({w}, {h})), {w}, {h})";
                Assert.Equal(4, N(page, d + ".rects.length"));
                Assert.Equal(0, N(page, d + ".off + " + d + ".meets + " + d + ".unsquare"));
                Assert.Equal(1, N(page, d + ".scale"), 9);
                Assert.True(B(page, d + ".own"));
            }
        }

        /// <summary>What the page keeps goes through PadForge's store and
        /// comes back a pad that is drawn the same, with its second
        /// arrangement. The store rounds a share to four places, a twelfth
        /// of a pixel across a phone.</summary>
        [Fact]
        public void ThePagesPad_ComesBackFromTheStoreTheSame()
        {
            using var page = Page();
            page.Execute("""
                var pad = PadFit.read(full);
                PadFit.adopt(pad, PadFit.place(pad, 390, 844).rects, 390, 844);
                var sent = JSON.stringify(PadFit.write(pad));
                """);
            string before = WebCustomLayoutStore.Json;
            try
            {
                WebCustomLayoutStore.LoadFrom("[]");
                string id = WebCustomLayoutStore.Upsert(page.Evaluate("sent").AsString());
                Assert.NotNull(id);
                string stored = WebCustomLayoutStore.Find(id);
                using (var doc = JsonDocument.Parse(stored))
                {
                    Assert.Equal(844.0 / 390, doc.RootElement.GetProperty("aspect").GetDouble(), 4);
                    Assert.Equal(390.0 / 844, doc.RootElement.GetProperty("aspect2").GetDouble(), 4);
                    Assert.Equal(13, doc.RootElement.GetProperty("widgets").GetArrayLength());
                    Assert.True(doc.RootElement.GetProperty("widgets")[0].TryGetProperty("w2", out _));
                }
                page.SetValue("stored", stored);
                page.Execute("var back = PadFit.read(JSON.parse(stored));");
                Assert.True(N(page, "apart(PadFit.place(back, 844, 390).rects, PadFit.place(pad, 844, 390).rects)") < 0.1);
                Assert.True(N(page, "apart(PadFit.place(back, 390, 844).rects, PadFit.place(pad, 390, 844).rects)") < 0.1);
                Assert.True(B(page, "PadFit.place(back, 390, 844).own && PadFit.place(back, 844, 390).own"));
            }
            finally { WebCustomLayoutStore.LoadFrom(before); }
        }

        /// <summary>The page draws through pad_fit.js: a control's box is set
        /// in the screen's pixels, the pad is drawn again when the screen
        /// changes, and nothing is a share of the screen's width or height
        /// any more.</summary>
        [Fact]
        public void ThePageDrawsThroughPadFit()
        {
            string html = Web("custom.html");
            int fit = html.IndexOf("<script src=\"/js/pad_fit.js\"></script>", StringComparison.Ordinal);
            int client = html.IndexOf("<script src=\"/js/custom_client.js\"></script>", StringComparison.Ordinal);
            Assert.True(fit > 0 && client > fit, "pad_fit.js is not loaded before the page's script");
            Assert.Contains(".w-button { border-radius: 9999px;", html);
            Assert.Contains("#toolbar.moved { transform: none; }", html);
            Assert.Contains("<span id=\"barGrip\" title=\"Move these tools\"></span>", html);
            Assert.Contains("<button id=\"autoBtn\" hidden", html);

            string script = Web("js", "custom_client.js");
            Assert.Contains("shown = PadFit.place(layout, screenW, screenH);", script);
            Assert.Contains("el.style.width = r.w + \"px\";", script);
            Assert.Contains("el.style.height = r.h + \"px\";", script);
            Assert.Contains("window.addEventListener(\"resize\", screenChanged);", script);
            Assert.Contains("window.addEventListener(\"orientationchange\", screenChanged);", script);
            Assert.Contains("PadFit.adopt(layout, shown.rects, screenW, screenH);", script);
            Assert.Contains("var body = PadFit.write(layout);", script);
            Assert.Contains("layout = PadFit.read(found);", script);
            Assert.Contains("if (PadFit.isSquare(d.wd.kind)) {", script);
            // The first builder's lines.
            Assert.DoesNotContain("wd.x * 100", script);
            Assert.DoesNotContain("wd.w * 100", script);
            Assert.DoesNotContain("Math.round(v * 50) / 50", script);
        }

        /// <summary>The page and the store agree on the narrowest and the
        /// widest screen an arrangement is kept for. A shape the page keeps
        /// and the store drops would lose the pad's arrangement on save.</summary>
        [Fact]
        public void ThePageAndTheStoreKeepTheSameShapes()
        {
            var limits = System.Text.RegularExpressions.Regex.Match(Web("js", "pad_fit.js"),
                @"var ASPECT_LEAST = ([0-9.]+), ASPECT_MOST = ([0-9.]+);");
            Assert.True(limits.Success);
            Assert.Equal(WebCustomLayoutStore.AspectLeast, double.Parse(limits.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture));
            Assert.Equal(WebCustomLayoutStore.AspectMost, double.Parse(limits.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture));

            using var page = Page();
            // A screen at each limit: the pad made on it is kept by the store.
            foreach (var (w, h) in new[] { (500, 100), (100, 500), (3000, 100) })
            {
                page.Execute($"var edge = JSON.stringify(PadFit.write(PadFit.starter({w}, {h})));");
                string before = WebCustomLayoutStore.Json;
                try
                {
                    WebCustomLayoutStore.LoadFrom("[]");
                    string id = WebCustomLayoutStore.Upsert(page.Evaluate("edge").AsString());
                    using var doc = JsonDocument.Parse(WebCustomLayoutStore.Find(id));
                    Assert.True(doc.RootElement.TryGetProperty("aspect", out var aspect), $"{w} by {h} lost its shape");
                    Assert.InRange(aspect.GetDouble(), WebCustomLayoutStore.AspectLeast, WebCustomLayoutStore.AspectMost);
                }
                finally { WebCustomLayoutStore.LoadFrom(before); }
            }
        }
    }
}
