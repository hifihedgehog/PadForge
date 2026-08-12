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
