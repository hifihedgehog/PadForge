using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Serialization;
using PadForge.Common.ConfigImport;
using PadForge.Engine.Data;
using PadForge.Services;
using PadForge.SteamWorkshop.Translation;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>DS4Windows loads a profile through ProfileDTO
    /// (ScpUtil.LoadProfileNew, 5306-5313), so an element a file leaves out
    /// takes the DTO field's initializer, or the DTO's own default where it
    /// has none, and MapTo copies it on. A file that leaves an element out
    /// imports as one that writes that value.</summary>
    [Collection("SettingsManagerStatics")]
    public class Ds4WindowsDefaultsTests
    {
        private static ForeignImportResult Import(string body)
            => ForeignConfigImporter.Translate(Encoding.UTF8.GetBytes(
                "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<DS4Windows app_version=\"3.2.19\" config_version=\"5\">" + body + "</DS4Windows>"),
                "C:/DS4Windows/Profiles/Test.xml");

        private static string Shape(ForeignImportResult r)
        {
            Assert.False(r.Refused, r.RefusedKey);
            var serializer = new XmlSerializer(typeof(ProfileData));
            using var writer = new StringWriter(CultureInfo.InvariantCulture);
            serializer.Serialize(writer, r.Profile);
            string lines = string.Join("\n", r.Report.Entries
                .Where(e => e.Status != TranslationStatus.Clean)
                .Select(e => e.Status + " " + e.ReasonKey + " " + string.Join(",", e.ReasonArgs)));
            // Ids are fresh per import.
            string shape = System.Text.RegularExpressions.Regex.Replace(writer.ToString(),
                @"\b(\w*Id)=""[0-9a-fA-F-]{16,}""", "$1=\"\"");
            shape = System.Text.RegularExpressions.Regex.Replace(shape,
                @"<(\w*Id)>[0-9a-fA-F-]{16,}</\1>", "<$1/>");
            return shape + "\n" + lines;
        }

        private const string Touch = "<TouchpadOutputMode>Controls</TouchpadOutputMode>";
        private const string GyroStick = Touch + "<GyroOutputMode>MouseJoystick</GyroOutputMode>";
        private const string Battery = Touch + "<ledAsBatteryIndicator>True</ledAsBatteryIndicator>";

        // Element, the value ProfileDTO gives it when the file leaves it out,
        // and the rest of the file. A {0} in the rest marks where the element
        // goes, for one nested in another.
        [Theory]
        // Changed: StickDeadZoneInfo.DEFAULT_DEADZONE and DEFAULT_ANTIDEADZONE
        // (ProfileDTO.cs:453, 461, 469, 477).
        [InlineData("LSDeadZone", "10", Touch)]
        [InlineData("RSDeadZone", "10", Touch)]
        [InlineData("LSAntiDeadZone", "20", Touch)]
        [InlineData("RSAntiDeadZone", "20", Touch)]
        // Changed: auto-properties with no initializer, copied by MapTo
        // (1018-1028, 2229-2230): enum 0, None.
        [InlineData("GyroMouseStickOutputStick", "None", GyroStick)]
        [InlineData("GyroMouseStickOutputStickAxes", "None", GyroStick)]
        // Changed: FlickStickSettings' properties have no initializer, and
        // the DTO builds the settings when it is made (3033-3070, 1456,
        // MapTo 2309-2316).
        [InlineData("RealWorldCalibration", "0", Touch + "<RSOutputMode>FlickStick</RSOutputMode><RSOutputSettings><FlickStickSettings>{0}<FlickThreshold>0.9</FlickThreshold><FlickTime>0.1</FlickTime></FlickStickSettings></RSOutputSettings>")]
        [InlineData("FlickThreshold", "0", Touch + "<RSOutputMode>FlickStick</RSOutputMode><RSOutputSettings><FlickStickSettings><RealWorldCalibration>5.3</RealWorldCalibration>{0}<FlickTime>0.1</FlickTime></FlickStickSettings></RSOutputSettings>")]
        [InlineData("FlickTime", "0", Touch + "<RSOutputMode>FlickStick</RSOutputMode><RSOutputSettings><FlickStickSettings><RealWorldCalibration>5.3</RealWorldCalibration><FlickThreshold>0.9</FlickThreshold>{0}</FlickStickSettings></RSOutputSettings>")]
        // Changed: a DS4Color that starts black and only Color or the older
        // Red, Green and Blue set (98, 224, PostProcessXml 2569-2622).
        [InlineData("Color", "0,0,0", Touch)]
        [InlineData("LowColor", "0,0,0", Battery + "<Color>0,0,255</Color>")]
        // Unchanged, and held here: the importer's fallback already agrees.
        [InlineData("LSMaxZone", "100", Touch)]
        [InlineData("LSMaxOutput", "100", Touch)]
        [InlineData("LSVerticalScale", "100", Touch)]
        [InlineData("LSRotation", "0", Touch)]
        [InlineData("LSOuterBindDead", "75", Touch)]
        [InlineData("LSDeadZoneType", "Radial", Touch)]
        [InlineData("LSOutputCurveMode", "linear", Touch)]
        [InlineData("LSOutputMode", "Controls", Touch)]
        [InlineData("LeftTriggerMiddle", "0", Touch)]
        [InlineData("L2AntiDeadZone", "0", Touch)]
        [InlineData("L2MaxZone", "100", Touch)]
        [InlineData("L2MaxOutput", "100", Touch)]
        [InlineData("L2HipFireTime", "100", Touch + "<L2TwoStageMode>HipFire</L2TwoStageMode>")]
        [InlineData("L2TwoStageMode", "Disabled", Touch)]
        [InlineData("SXDeadZone", "0.25", Touch)]
        [InlineData("SXMaxZone", "100", Touch)]
        [InlineData("GyroOutputMode", "Controls", Touch)]
        [InlineData("GyroMouseStickDeadZone", "30", GyroStick)]
        [InlineData("GyroMouseStickMaxZone", "830", GyroStick)]
        [InlineData("GyroMouseStickAntiDeadX", "0.4", GyroStick)]
        [InlineData("GyroMouseStickVerticalScale", "100", GyroStick)]
        [InlineData("GyroMouseDeadZone", "10", Touch + "<GyroOutputMode>Mouse</GyroOutputMode>")]
        [InlineData("GyroSensitivity", "100", Touch + "<GyroOutputMode>Mouse</GyroOutputMode>")]
        [InlineData("TouchpadOutputMode", "Mouse", "")]
        [InlineData("touchSensitivity", "100", "")]
        [InlineData("TrackballFriction", "10", "<TrackballMode>True</TrackballMode>")]
        [InlineData("OutputContDevice", "X360", Touch)]
        [InlineData("LightbarMode", "DS4Win", Touch + "<Color>1,2,3</Color>")]
        [InlineData("MaxSatRainbow", "100", Touch + "<Rainbow>5</Rainbow>")]
        [InlineData("RumbleBoost", "100", Touch)]
        [InlineData("SASteeringWheelEmulationRange", "360", Touch + "<SASteeringWheelEmulationAxis>LX</SASteeringWheelEmulationAxis>")]
        [InlineData("ButtonMouseSensitivity", "25", Touch)]
        public void AnElementTheFileLeavesOut_ImportsAsItsProfileDtoDefault(string element, string value, string rest)
        {
            string written = $"<{element}>{value}</{element}>";
            string With(string e) => rest.Contains("{0}", StringComparison.Ordinal) ? rest.Replace("{0}", e, StringComparison.Ordinal) : rest + e;
            Assert.Equal(Shape(Import(With(written))), Shape(Import(With(""))));
        }

        private static MappingRow Row(ForeignImportResult r, string target)
            => r.Profile.SlotMappingSets.SelectMany(s => s.Rows).FirstOrDefault(x => x.Target == target && x.LayerMask == "Base");

        // The values those defaults land on, as Mapping.cs plays them. A gyro
        // or touchpad stick output of None writes no stick (Mapping.cs:3036),
        // and its axes are XY, X, Y or None (ProfilePropGroups.cs:346-352,
        // OutputHorizontal and OutputVertical 514-524).
        [Theory]
        [InlineData("<GyroOutputMode>MouseJoystick</GyroOutputMode><GyroMouseStickOutputStick>None</GyroMouseStickOutputStick><GyroMouseStickOutputStickAxes>XY</GyroMouseStickOutputStickAxes>", false, false)]
        [InlineData("<GyroOutputMode>MouseJoystick</GyroOutputMode><GyroMouseStickOutputStick>RightStick</GyroMouseStickOutputStick><GyroMouseStickOutputStickAxes>None</GyroMouseStickOutputStickAxes>", false, false)]
        [InlineData("<GyroOutputMode>MouseJoystick</GyroOutputMode><GyroMouseStickOutputStick>RightStick</GyroMouseStickOutputStick><GyroMouseStickOutputStickAxes>X</GyroMouseStickOutputStickAxes>", true, false)]
        [InlineData("<GyroOutputMode>MouseJoystick</GyroOutputMode><GyroMouseStickOutputStick>RightStick</GyroMouseStickOutputStick><GyroMouseStickOutputStickAxes>Y</GyroMouseStickOutputStickAxes>", false, true)]
        [InlineData("<GyroOutputMode>MouseJoystick</GyroOutputMode><GyroMouseStickOutputStick>RightStick</GyroMouseStickOutputStick><GyroMouseStickOutputStickAxes>XY</GyroMouseStickOutputStickAxes>", true, true)]
        [InlineData("<TouchpadOutputMode>MouseJoystick</TouchpadOutputMode><TouchpadMouseStick><OutputStick>None</OutputStick></TouchpadMouseStick>", false, false)]
        [InlineData("<TouchpadOutputMode>MouseJoystick</TouchpadOutputMode><TouchpadMouseStick><OutputStick>RightStick</OutputStick><OutputStickAxes>Y</OutputStickAxes></TouchpadMouseStick>", false, true)]
        [InlineData("<TouchpadOutputMode>MouseJoystick</TouchpadOutputMode><TouchpadMouseStick><OutputStick>RightStick</OutputStick></TouchpadMouseStick>", true, true)]
        public void AGyroOrTouchpadStick_WritesTheStickAndAxesItNames(string body, bool x, bool y)
        {
            var r = Import((body.Contains("TouchpadOutputMode", StringComparison.Ordinal) ? "" : Touch) + body);
            bool Writes(string target) => Row(r, target)?.Sources.Any(s => s.Descriptor.StartsWith("Gyro", StringComparison.Ordinal)
                || s.Descriptor.StartsWith("Touchpad", StringComparison.Ordinal)) == true;
            Assert.Equal(x, Writes("RightThumbAxisX"));
            Assert.Equal(y, Writes("RightThumbAxisY"));
            Assert.False(Writes("LeftThumbAxisX") || Writes("LeftThumbAxisY"));
        }

        // A flick stick's settings at 0: the calibration turns nothing
        // (Mapping.cs:3327, 3334), the flick time is spent at once (3398-3399),
        // and a threshold of 0 keeps the stick past it even at rest (3359-3361),
        // which a flick row cannot hold, so the report names it.
        [Fact]
        public void AFlickStickAtZero_PlaysAsMappingCsDoes()
        {
            const string Flick = Touch + "<RSOutputMode>FlickStick</RSOutputMode><RSOutputSettings><FlickStickSettings>{0}</FlickStickSettings></RSOutputSettings>";
            var noTurn = Import(Flick.Replace("{0}", "<RealWorldCalibration>0</RealWorldCalibration><FlickThreshold>0.9</FlickThreshold><FlickTime>0.1</FlickTime>", StringComparison.Ordinal));
            Assert.DoesNotContain(noTurn.Profile.SlotMappingSets.SelectMany(s => s.Rows).SelectMany(x => x.Sources), s => s.Descriptor == "Flick Stick Right");
            var instant = Import(Flick.Replace("{0}", "<RealWorldCalibration>5.3</RealWorldCalibration><FlickThreshold>0.9</FlickThreshold><FlickTime>0</FlickTime>", StringComparison.Ordinal));
            var src = instant.Profile.SlotMappingSets.SelectMany(s => s.Rows).SelectMany(x => x.Sources).Single(s => s.Descriptor == "Flick Stick Right");
            Assert.InRange(src.ParamFlickTime, 0.0, 0.002);
            Assert.True(src.ParamFlickTime > 0);
            var atRest = Import(Flick.Replace("{0}", "<RealWorldCalibration>5.3</RealWorldCalibration><FlickThreshold>0</FlickThreshold><FlickTime>0.1</FlickTime>", StringComparison.Ordinal));
            Assert.Contains(atRest.Report.Entries, e => e.Status == TranslationStatus.Partial && e.ReasonArgs.Contains("FlickThreshold"));
        }

        // Color and LowColor as PostProcessXml reads them when the file
        // writes only the older Red, Green and Blue (ProfileDTO.cs:2578-2597):
        // it parses Red for all three channels it finds.
        [Fact]
        public void TheOlderColorElements_ReadAsProfileDtoReadsThem()
        {
            var r = Import(Touch + "<Red>10</Red><Green>20</Green><Blue>30</Blue>");
            var bar = Assert.Single(r.Profile.Macros, m => m.Name == "Lightbar");
            var set = Assert.Single(bar.Actions);
            Assert.Equal((byte)10, set.LightbarR);
            Assert.Equal((byte)10, set.LightbarG);
            Assert.Equal((byte)10, set.LightbarB);
        }
    }
}
