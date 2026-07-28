using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// A mapping row's device subtitle must follow its own stored GUID.
    ///
    /// <para>Owner report, Workshop import: rows whose stored source was
    /// <c>DeviceGuid="" Descriptor="Gamepad ButtonA"</c> (verified in the
    /// saved XML) rendered the subtitle of the OUTGOING profile's controller
    /// instead of "(Any device)", on a slot with no device assigned at all.
    /// The label was an independently assigned string that every writer kept
    /// in step with the GUID by hand, so any path that set one without the
    /// other left the pair disagreeing and the wrong device on screen.</para>
    ///
    /// <para>It is derived now. These tests pin that a stale stored label can
    /// never win over the GUID, which is the property that makes the whole
    /// divergence class impossible rather than fixing one writer.</para>
    /// </summary>
    public class AnyDeviceLabelTests : System.IDisposable
    {
        private const string SteamGuid = "11111111-2222-3333-4444-555555555555";
        private readonly System.Func<string, string> _priorPrimary = MappingItem.DeviceLabelResolver;
        private readonly System.Func<string, string> _priorSource = MappingSourceItem.DeviceLabelResolver;

        public AnyDeviceLabelTests()
        {
            System.Func<string, string> resolver = guid =>
                string.IsNullOrEmpty(guid) ? "(Any device)" : "Steam Controller";
            MappingItem.DeviceLabelResolver = resolver;
            MappingSourceItem.DeviceLabelResolver = resolver;
        }

        public void Dispose()
        {
            MappingItem.DeviceLabelResolver = _priorPrimary;
            MappingSourceItem.DeviceLabelResolver = _priorSource;
        }

        private static MappingItem Row() =>
            new MappingItem("A", "ButtonA", MappingCategory.Buttons);

        [Fact]
        public void EmptyGuidRow_ReadsAnyDevice_EvenAfterAStaleLabelWasStamped()
        {
            var m = Row();
            // Exactly the shipped state: a concrete label left behind by the
            // outgoing profile, over an empty GUID from the import.
            m.PrimarySourceDeviceLabel = "Steam Controller";
            m.PrimarySourceDeviceGuid = "";
            m.SourceDescriptor = "Gamepad ButtonA";

            Assert.Equal("(Any device)", m.PrimarySourceDeviceLabel);
        }

        [Fact]
        public void ConcreteGuidRow_StillReadsItsDevice()
        {
            // The positive control: deriving must not blank out a real
            // device's name.
            var m = Row();
            m.PrimarySourceDeviceGuid = SteamGuid;
            Assert.Equal("Steam Controller", m.PrimarySourceDeviceLabel);
        }

        [Fact]
        public void ClearingTheGuid_FlipsTheLabelBackToAnyDevice()
        {
            var m = Row();
            m.PrimarySourceDeviceGuid = SteamGuid;
            Assert.Equal("Steam Controller", m.PrimarySourceDeviceLabel);

            m.PrimarySourceDeviceGuid = "";
            Assert.Equal("(Any device)", m.PrimarySourceDeviceLabel);
        }

        [Fact]
        public void SecondarySource_FollowsTheSameRule()
        {
            // The twin surface. A Workshop import makes empty-guid
            // secondaries the common case, which is how #222 shipped.
            var src = new MappingSourceItem { DeviceLabel = "Steam Controller", DeviceGuid = "" };
            Assert.Equal("(Any device)", src.DeviceLabel);

            src.DeviceGuid = SteamGuid;
            Assert.Equal("Steam Controller", src.DeviceLabel);
        }

        [Fact]
        public void WithNoResolverWired_TheStoredValueStillShows()
        {
            // Early startup and tests have no resolver; the stored string
            // remains the fallback rather than rendering empty.
            MappingItem.DeviceLabelResolver = null;
            var m = Row();
            m.PrimarySourceDeviceLabel = "Stored Name";
            Assert.Equal("Stored Name", m.PrimarySourceDeviceLabel);
        }
    }
}
