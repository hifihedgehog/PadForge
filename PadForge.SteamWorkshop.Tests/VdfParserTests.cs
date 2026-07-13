using System;
using System.Text;
using PadForge.SteamWorkshop.Vdf;

namespace PadForge.SteamWorkshop.Tests
{
    public class VdfParserTests
    {
        private const string WellFormed = @"
""controller_mappings""
{
    ""version""    ""3""
    ""title""      ""Test \""Quoted\"" Title""
    ""group"" { ""id"" ""0"" ""mode"" ""four_buttons"" }
    ""group"" { ""id"" ""1"" ""mode"" ""dpad"" }
    ""empty""      """"
    ""obj"" { }
    // a line comment that should be ignored
    ""path""       ""C:\\Users\\x""
    ""url""        ""https://example.com//not-a-comment""
    ""num""        ""3.14""
}";

        [Fact]
        public void Parses_scalars_objects_and_nested_structure()
        {
            var root = VdfParser.Parse(WellFormed);
            var m = root["controller_mappings"];

            Assert.True(m.IsObject);
            Assert.Equal("3", m["version"].AsString);
            Assert.Equal(3, m["version"].AsInt);
        }

        [Fact]
        public void Processes_escape_sequences_in_quoted_strings()
        {
            var m = VdfParser.Parse(WellFormed)["controller_mappings"];
            Assert.Equal("Test \"Quoted\" Title", m["title"].AsString);
            // Escaped backslashes collapse to single backslashes.
            Assert.Equal(@"C:\Users\x", m["path"].AsString);
        }

        [Fact]
        public void Does_not_treat_double_slash_inside_a_quoted_string_as_a_comment()
        {
            var m = VdfParser.Parse(WellFormed)["controller_mappings"];
            Assert.Equal("https://example.com//not-a-comment", m["url"].AsString);
        }

        [Fact]
        public void Preserves_duplicate_keys_via_Multi()
        {
            var m = VdfParser.Parse(WellFormed)["controller_mappings"];
            var groups = m.Multi("group");
            Assert.Equal(2, groups.Count);
            Assert.Equal("0", groups[0]["id"].AsString);
            Assert.Equal("1", groups[1]["id"].AsString);
            // Indexer returns the first match.
            Assert.Equal("0", m["group"]["id"].AsString);
        }

        [Fact]
        public void Handles_empty_value_and_empty_object()
        {
            var m = VdfParser.Parse(WellFormed)["controller_mappings"];
            Assert.True(m["empty"].IsValue);
            Assert.Equal(string.Empty, m["empty"].AsString);
            Assert.True(m["obj"].IsObject);
            Assert.Equal(0, m["obj"].ChildCount);
        }

        [Fact]
        public void Parses_numbers_with_invariant_culture()
        {
            var m = VdfParser.Parse(WellFormed)["controller_mappings"];
            Assert.Equal(3.14, m["num"].AsDouble.Value, 5);
        }

        [Fact]
        public void Missing_key_returns_null_safe_sentinel()
        {
            var m = VdfParser.Parse(WellFormed)["controller_mappings"];
            Assert.True(m["nope"].IsMissing);
            // Chained navigation through a missing node does not throw.
            Assert.True(m["nope"]["deeper"].IsMissing);
            Assert.Empty(m["nope"].Multi("x"));
            Assert.Null(m["nope"].AsString);
        }

        [Fact]
        public void Skips_line_and_block_comments_outside_strings()
        {
            var text = "\"a\" \"1\" /* block */ \"b\" \"2\" // trailing\n\"c\" \"3\"";
            var root = VdfParser.Parse(text);
            Assert.Equal("1", root["a"].AsString);
            Assert.Equal("2", root["b"].AsString);
            Assert.Equal("3", root["c"].AsString);
        }

        [Fact]
        public void Handles_newline_and_tab_escapes_and_passes_unknown_escapes_through()
        {
            var root = VdfParser.Parse("\"nl\" \"a\\nb\" \"unknown\" \"a\\zb\"");
            Assert.Equal("a\nb", root["nl"].AsString);
            Assert.Equal("a\\zb", root["unknown"].AsString);
        }

        [Fact]
        public void Preserves_embedded_unicode()
        {
            var root = VdfParser.Parse("\"jp\" \"ゲームパッド\" \"ru\" \"Геймпад\"");
            Assert.Equal("ゲームパッド", root["jp"].AsString);
            Assert.Equal("Геймпад", root["ru"].AsString);
        }

        [Fact]
        public void Strips_leading_utf8_bom()
        {
            var root = VdfParser.Parse("\uFEFF\"a\" \"1\"");
            Assert.Equal("1", root["a"].AsString);
        }

        [Fact]
        public void Rejects_unbalanced_braces()
        {
            Assert.Throws<VdfSyntaxException>(() => VdfParser.Parse("\"a\" { \"b\" \"c\""));
        }

        [Fact]
        public void Rejects_unexpected_closing_brace_at_top_level()
        {
            Assert.Throws<VdfSyntaxException>(() => VdfParser.Parse("\"a\" \"b\" }"));
        }

        [Fact]
        public void Rejects_truncated_quoted_string()
        {
            Assert.Throws<VdfSyntaxException>(() => VdfParser.Parse("\"a\" \"unterminated"));
        }

        [Fact]
        public void Rejects_key_with_no_value_at_eof()
        {
            Assert.Throws<VdfSyntaxException>(() => VdfParser.Parse("\"lonelyKey\""));
        }

        [Fact]
        public void Rejects_binary_vbkv_magic()
        {
            var ex = Assert.Throws<VdfSyntaxException>(() => VdfParser.Parse("\0VBKV"));
            Assert.Contains("Binary", ex.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Rejects_leading_nul_binary_content()
        {
            Assert.Throws<VdfSyntaxException>(() => VdfParser.Parse("\0random"));
        }

        [Fact]
        public void Rejects_nesting_beyond_depth_cap()
        {
            var sb = new StringBuilder();
            sb.Append("\"root\"");
            for (var i = 0; i < 40; i++) sb.Append(" { \"k\"");
            var ex = Assert.Throws<VdfSyntaxException>(() => VdfParser.Parse(sb.ToString(), maxDepth: 32));
            Assert.Contains("depth", ex.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Allows_nesting_up_to_depth_cap()
        {
            // 5 levels deep is well within the default cap.
            var root = VdfParser.Parse("\"a\" { \"b\" { \"c\" { \"d\" { \"e\" \"1\" } } } }");
            Assert.Equal("1", root["a"]["b"]["c"]["d"]["e"].AsString);
        }

        [Fact]
        public void Rejects_input_over_ten_megabytes()
        {
            var big = new string(' ', VdfParser.MaxInputBytes + 16);
            var ex = Assert.Throws<VdfSyntaxException>(() => VdfParser.Parse(big));
            Assert.Contains("10 MB", ex.Message);
        }

        [Fact]
        public void TryParse_reports_failure_without_throwing()
        {
            var ok = VdfParser.TryParse("\"a\" { \"b\"", out var root, out var error);
            Assert.False(ok);
            Assert.Null(root);
            Assert.NotNull(error);
        }

        [Fact]
        public void TryParse_succeeds_on_valid_input()
        {
            var ok = VdfParser.TryParse("\"a\" \"1\"", out var root, out var error);
            Assert.True(ok);
            Assert.NotNull(root);
            Assert.Null(error);
            Assert.Equal("1", root["a"].AsString);
        }

        [Fact]
        public void Parses_a_real_fixture_config()
        {
            var root = VdfParser.Parse(TestFixtures.Read(TestFixtures.SkyrimDs4));
            var m = root["controller_mappings"];
            Assert.Equal("Dualshock 4 Skyrim", m["title"].AsString);
            Assert.Equal(3, m["version"].AsInt);
            Assert.True(m.Multi("group").Count >= 20);
            Assert.Equal("controller_ps4", m["controller_type"].AsString);
        }

        [Fact]
        public void Parses_every_committed_fixture_without_error()
        {
            var count = 0;
            foreach (var path in TestFixtures.AllVdfPaths())
            {
                var text = System.IO.File.ReadAllText(path);
                var root = VdfParser.Parse(text);
                Assert.True(root["controller_mappings"].IsObject, $"controller_mappings missing in {path}");
                count++;
            }
            Assert.Equal(27, count);
        }
    }
}
