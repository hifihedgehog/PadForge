using System.Text.Json;
using PadForge.Services;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The custom-controller layout store (#296 phase 4). These pin the
    /// upsert / find / delete contract and, specifically, the JsonElement id
    /// bug: a Dictionary&lt;string,object&gt; deserialized from
    /// System.Text.Json holds JsonElement values, so a plain (v as string)
    /// cast returns null and every id match silently missed.
    /// </summary>
    [Collection("WebCustomLayoutStore")]
    public class WebCustomLayoutStoreTests
    {
        private static string Layout(string name, int widgetCode = 12) =>
            $"{{\"name\":\"{name}\",\"widgets\":[" +
            $"{{\"kind\":\"button\",\"x\":0.5,\"y\":0.5,\"w\":0.1,\"h\":0.1,\"code\":{widgetCode}}}]}}";

        [Fact]
        public void Upsert_ThenFindAndDelete_MatchById_AcrossAReload()
        {
            WebCustomLayoutStore.LoadFrom("[]");
            var id = WebCustomLayoutStore.Upsert(Layout("Alpha"));
            Assert.NotNull(id);

            // Find must resolve after the round-trip through the JSON store,
            // which is where the JsonElement-vs-string bug lived.
            var found = WebCustomLayoutStore.Find(id);
            Assert.NotNull(found);
            using (var doc = JsonDocument.Parse(found))
                Assert.Equal("Alpha", doc.RootElement.GetProperty("name").GetString());

            // Persistence round-trip: reload the serialized store, still found.
            WebCustomLayoutStore.LoadFrom(WebCustomLayoutStore.Json);
            Assert.NotNull(WebCustomLayoutStore.Find(id));

            Assert.True(WebCustomLayoutStore.Delete(id));
            Assert.Null(WebCustomLayoutStore.Find(id));
            Assert.False(WebCustomLayoutStore.Delete(id)); // already gone
        }

        [Fact]
        public void Upsert_WithSameId_Replaces_DoesNotDuplicate()
        {
            WebCustomLayoutStore.LoadFrom("[]");
            var id = WebCustomLayoutStore.Upsert(Layout("First"));
            var again = WebCustomLayoutStore.Upsert(
                $"{{\"id\":\"{id}\",\"name\":\"Second\",\"widgets\":[" +
                "{\"kind\":\"stick\",\"x\":0.1,\"y\":0.1,\"w\":0.2,\"h\":0.2,\"code\":0}]}");
            Assert.Equal(id, again);

            using var doc = JsonDocument.Parse(WebCustomLayoutStore.Json);
            Assert.Equal(1, doc.RootElement.GetArrayLength());
            Assert.Equal("Second", doc.RootElement[0].GetProperty("name").GetString());
        }

        [Fact]
        public void Upsert_WhitelistsTheSchema_AndClampsOutOfRange()
        {
            WebCustomLayoutStore.LoadFrom("[]");
            // Junk fields, an out-of-range coordinate, an unknown widget kind,
            // and an evil oversized code must all be sanitized.
            var id = WebCustomLayoutStore.Upsert(
                "{\"name\":\"Dirty\",\"evil\":\"drop table\",\"widgets\":[" +
                "{\"kind\":\"button\",\"x\":9.9,\"y\":-3,\"w\":0.1,\"h\":0.1,\"code\":9999,\"junk\":1}," +
                "{\"kind\":\"nonsense\",\"x\":0.5,\"y\":0.5}]}");
            Assert.NotNull(id);

            using var doc = JsonDocument.Parse(WebCustomLayoutStore.Find(id));
            var root = doc.RootElement;
            Assert.False(root.TryGetProperty("evil", out _));
            var widgets = root.GetProperty("widgets");
            Assert.Equal(1, widgets.GetArrayLength()); // the nonsense widget dropped
            var w0 = widgets[0];
            Assert.Equal(1.0, w0.GetProperty("x").GetDouble());       // clamped to max 1
            Assert.Equal(0.0, w0.GetProperty("y").GetDouble());       // clamped to min 0
            Assert.Equal(127, w0.GetProperty("code").GetInt32());     // clamped to max 127
            Assert.False(w0.TryGetProperty("junk", out _));
        }

        /// <summary>A pad keeps the shape of the screen it was arranged on,
        /// and a second arrangement for the other way of holding the screen
        /// (#574). The page needs the shape to keep a control's own shape
        /// on any other screen. The whitelist dropped both as unknown.</summary>
        [Fact]
        public void Upsert_KeepsTheScreensShape_AndTheSecondArrangement()
        {
            WebCustomLayoutStore.LoadFrom("[]");
            var id = WebCustomLayoutStore.Upsert(
                "{\"name\":\"Both\",\"aspect\":2.16410256,\"aspect2\":0.46208531,\"widgets\":[" +
                "{\"kind\":\"stick\",\"x\":0.05,\"y\":0.5,\"w\":0.16,\"h\":0.35,\"code\":0," +
                "\"x2\":0.06,\"y2\":7,\"w2\":0.001,\"h2\":0.1}," +
                "{\"kind\":\"button\",\"x\":0.8,\"y\":0.5,\"w\":0.07,\"h\":0.16,\"code\":1}]}");
            Assert.NotNull(id);

            using var doc = JsonDocument.Parse(WebCustomLayoutStore.Find(id));
            var root = doc.RootElement;
            Assert.Equal(2.1641, root.GetProperty("aspect").GetDouble());
            Assert.Equal(0.4621, root.GetProperty("aspect2").GetDouble());
            var stick = root.GetProperty("widgets")[0];
            Assert.Equal(0.06, stick.GetProperty("x2").GetDouble());
            Assert.Equal(1.0, stick.GetProperty("y2").GetDouble());    // clamped to max 1
            Assert.Equal(0.02, stick.GetProperty("w2").GetDouble());   // clamped to min 0.02
            Assert.Equal(0.1, stick.GetProperty("h2").GetDouble());
            Assert.Equal(0.05, stick.GetProperty("x").GetDouble());
            // A control that came with no second place has none. The page gives it one.
            Assert.False(root.GetProperty("widgets")[1].TryGetProperty("x2", out _));
            Assert.Equal(1, root.GetProperty("widgets")[1].GetProperty("code").GetInt32());

            // It survives the round trip through the settings file's text.
            WebCustomLayoutStore.LoadFrom(WebCustomLayoutStore.Json);
            using var again = JsonDocument.Parse(WebCustomLayoutStore.Find(id));
            Assert.Equal(0.4621, again.RootElement.GetProperty("aspect2").GetDouble());
        }

        /// <summary>A shape no screen has is not kept, a second arrangement
        /// counts only beside a first and only for the other way of holding
        /// the screen, and a pad of the first builder, which sends no shape,
        /// is kept as it always was.</summary>
        [Theory]
        [InlineData("\"aspect\":9,\"aspect2\":0.5,", false, false)]
        [InlineData("\"aspect\":0.05,", false, false)]
        [InlineData("\"aspect\":\"wide\",\"aspect2\":0.5,", false, false)]
        [InlineData("\"aspect2\":0.5,", false, false)]
        [InlineData("\"aspect\":2,\"aspect2\":1.5,", true, false)]
        [InlineData("\"aspect\":0.5,\"aspect2\":0.6,", true, false)]
        [InlineData("\"aspect\":2,\"aspect2\":44,", true, false)]
        [InlineData("\"aspect\":2,", true, false)]
        [InlineData("\"aspect\":0.5,\"aspect2\":2,", true, true)]
        [InlineData("\"aspect\":1,\"aspect2\":0.99,", true, true)]
        [InlineData("", false, false)]
        public void Upsert_KeepsOnlyAShapeAScreenHas(string shape, bool first, bool second)
        {
            WebCustomLayoutStore.LoadFrom("[]");
            var id = WebCustomLayoutStore.Upsert(
                "{\"name\":\"Shape\"," + shape + "\"widgets\":[" +
                "{\"kind\":\"button\",\"x\":0.5,\"y\":0.5,\"w\":0.1,\"h\":0.1,\"code\":2," +
                "\"x2\":0.3,\"y2\":0.3,\"w2\":0.2,\"h2\":0.2}]}");
            Assert.NotNull(id);

            using var doc = JsonDocument.Parse(WebCustomLayoutStore.Find(id));
            Assert.Equal(first, doc.RootElement.TryGetProperty("aspect", out _));
            Assert.Equal(second, doc.RootElement.TryGetProperty("aspect2", out _));
            var button = doc.RootElement.GetProperty("widgets")[0];
            Assert.Equal(second, button.TryGetProperty("x2", out _));
            Assert.Equal(second, button.TryGetProperty("h2", out _));
            Assert.Equal(0.5, button.GetProperty("x").GetDouble());
            Assert.Equal(2, button.GetProperty("code").GetInt32());
        }

        [Fact]
        public void Upsert_RejectsEmptyOrOversizedWidgetSets()
        {
            WebCustomLayoutStore.LoadFrom("[]");
            Assert.Null(WebCustomLayoutStore.Upsert("{\"name\":\"Empty\",\"widgets\":[]}"));
            Assert.Null(WebCustomLayoutStore.Upsert("{\"name\":\"NoWidgets\"}"));
            Assert.Null(WebCustomLayoutStore.Upsert("not json at all"));
        }

        [Fact]
        public void LoadFrom_InvalidInput_ResetsToEmptyList()
        {
            WebCustomLayoutStore.LoadFrom("{\"not\":\"an array\"}");
            Assert.Equal("[]", WebCustomLayoutStore.Json);
            WebCustomLayoutStore.LoadFrom(null);
            Assert.Equal("[]", WebCustomLayoutStore.Json);
        }

        [Fact]
        public void Find_And_Delete_RejectUnsafeIds()
        {
            WebCustomLayoutStore.LoadFrom("[]");
            Assert.Null(WebCustomLayoutStore.Find("../etc/passwd"));
            Assert.Null(WebCustomLayoutStore.Find("has spaces"));
            Assert.False(WebCustomLayoutStore.Delete("has-dashes"));
        }
    }
}
