using System.IO;
using System.Xml.Serialization;
using PadForge.Services;
using PadForge.SteamWorkshop.Translation;

namespace PadForge.Tests
{
    /// <summary>
    /// #9 Phase D: Steam Workshop provenance on imported profiles. Pins the
    /// XML round-trip (ProfileData child element SteamWorkshopSource), the
    /// materializer's stamp, and the clone/mirror sites that must not lose
    /// it. SnapshotCurrentProfile / SaveActiveProfileState /
    /// UpdateActiveProfileSnapshot need a live MainViewModel and stay
    /// code-audited rather than unit-tested: they preserve provenance by
    /// never touching identity members, the same way profile Name survives
    /// them.
    /// </summary>
    public class WorkshopProvenanceTests
    {
        private static SteamWorkshopSource SampleSource() => new SteamWorkshopSource
        {
            PublishedFileId = 2853328208,
            AppId = 570,
            GameName = "Dota 2",
            Title = "Gyro Aim Config",
            TimeUpdated = 1657600000,
            ImportedAt = new DateTime(2026, 7, 13, 9, 30, 0, DateTimeKind.Utc),
            TranslationSummary = "v1 rows:x12+k3 macros:2 layers:1 clean:15 partial:2 skipped:1 errors:0",
        };

        private static void AssertSourceEqual(SteamWorkshopSource expected, SteamWorkshopSource actual)
        {
            Assert.NotNull(actual);
            Assert.Equal(expected.PublishedFileId, actual.PublishedFileId);
            Assert.Equal(expected.AppId, actual.AppId);
            Assert.Equal(expected.GameName, actual.GameName);
            Assert.Equal(expected.Title, actual.Title);
            Assert.Equal(expected.TimeUpdated, actual.TimeUpdated);
            Assert.Equal(expected.ImportedAt, actual.ImportedAt);
            Assert.Equal(expected.TranslationSummary, actual.TranslationSummary);
        }

        [Fact]
        public void Provenance_RoundTripsThroughProfileXml()
        {
            var p = new ProfileData
            {
                Name = "Workshop Import",
                WorkshopSource = SampleSource(),
            };

            var serializer = new XmlSerializer(typeof(ProfileData));
            using var buffer = new MemoryStream();
            serializer.Serialize(buffer, p);
            buffer.Position = 0;
            var clone = (ProfileData)serializer.Deserialize(buffer);

            AssertSourceEqual(p.WorkshopSource, clone.WorkshopSource);

            // The on-disk element name is a contract (PadForge.xml and
            // .pfprofile archives carry it).
            buffer.Position = 0;
            string xml = new StreamReader(buffer).ReadToEnd();
            Assert.Contains("<SteamWorkshopSource>", xml);
        }

        [Fact]
        public void Provenance_AbsentElement_DeserializesNull()
        {
            var serializer = new XmlSerializer(typeof(ProfileData));
            using var reader = new StringReader("<ProfileData><Name>Plain</Name></ProfileData>");
            var p = (ProfileData)serializer.Deserialize(reader);
            Assert.Null(p.WorkshopSource);
        }

        [Fact]
        public void Provenance_RoundTripsThroughSettingsFile()
        {
            var data = new SettingsFileData
            {
                Profiles = new[]
                {
                    new ProfileData { Name = "Workshop Import", WorkshopSource = SampleSource() },
                    new ProfileData { Name = "Hand Made" },
                },
            };

            var serializer = new XmlSerializer(typeof(SettingsFileData));
            var sw = new StringWriter();
            serializer.Serialize(sw, data);
            using var reader = new StringReader(sw.ToString());
            var clone = (SettingsFileData)serializer.Deserialize(reader);

            AssertSourceEqual(SampleSource(), clone.Profiles[0].WorkshopSource);
            Assert.Null(clone.Profiles[1].WorkshopSource);
        }

        [Fact]
        public void Materializer_StampsProvenance()
        {
            var translated = new TranslatedProfile { Name = "Stamped" };
            translated.Report.XboxRowCount = 4;
            translated.Report.Add(TranslationStatus.Clean, TranslationReasons.RowEmitted, "Default/button_diamond");

            var source = new SteamWorkshopSource
            {
                PublishedFileId = 793611331,
                AppId = 620,
                GameName = "Portal 2",
                Title = "Puzzle Config",
                TimeUpdated = 1650000000,
            };

            var before = DateTime.UtcNow;
            var p = WorkshopProfileMaterializer.Materialize(translated, source);
            var after = DateTime.UtcNow;

            Assert.Same(source, p.WorkshopSource);
            Assert.Equal(793611331UL, p.WorkshopSource.PublishedFileId);
            Assert.InRange(p.WorkshopSource.ImportedAt, before, after);
            Assert.Equal(translated.Report.ToSummaryString(), p.WorkshopSource.TranslationSummary);
            Assert.Contains("clean:1", p.WorkshopSource.TranslationSummary);
            // The translator version rides the stored summary, so imports
            // from different translator generations stay distinguishable.
            Assert.StartsWith("v19 ", p.WorkshopSource.TranslationSummary);
        }

        [Fact]
        public void Materializer_NullSource_LeavesProvenanceNull()
        {
            var p = WorkshopProfileMaterializer.Materialize(new TranslatedProfile { Name = "NoSource" });
            Assert.Null(p.WorkshopSource);
        }

        [Fact]
        public void Compaction_PreservesProvenance()
        {
            int maxPads = PadForge.Common.Input.InputManager.MaxPads;
            var created = new bool[maxPads];
            created[1] = true;
            created[3] = true;

            var p = new ProfileData
            {
                Name = "Gappy Workshop Import",
                SlotCreated = created,
                SlotControllerTypes = new int[maxPads],
                WorkshopSource = SampleSource(),
            };

            var (map, needs) = InputService.BuildCompactionMap(p);
            Assert.True(needs);
            InputService.CompactProfileDataInPlace(p, map, maxPads);

            // Compaction really ran (slots shifted to 0 and 1) and the
            // provenance rode through untouched, same instance.
            Assert.True(p.SlotCreated[0]);
            Assert.True(p.SlotCreated[1]);
            Assert.False(p.SlotCreated[3]);
            AssertSourceEqual(SampleSource(), p.WorkshopSource);
        }
    }
}
