using System;
using System.IO;
using System.Reflection;
using System.Xml.Serialization;
using PadForge.Common.Input;
using PadForge.Services;

namespace PadForge.Tests;

public partial class ProfileServiceToggleTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void LegacyHeadTrackingGlobalsKeepTheirEffectiveInputs(bool master, bool freeTrack)
    {
        var (vm, settings) = Arrange();
        LoadHeadGlobals(settings, new AppSettingsData { HeadTrackingEnabled = master, HeadTrackingFreeTrack = freeTrack });
        Assert.Equal(master, vm.Dashboard.HeadTrackingEnabled);
        Assert.Equal(master && freeTrack, vm.Dashboard.HeadTrackingFreeTrack);
        Assert.Equal(master, HeadTrackingRuntime.AnyEnabled);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void IndependentHeadTrackingGlobalsSurviveTheActualSaveAndLoad(bool udp, bool freeTrack)
    {
        var (vm, settings) = Arrange();
        vm.Dashboard.HeadTrackingEnabled = udp;
        vm.Dashboard.HeadTrackingFreeTrack = freeTrack;
        var built = (AppSettingsData)typeof(SettingsService).GetMethod("BuildAppSettings", BindingFlags.Instance | BindingFlags.NonPublic)
            .Invoke(settings, null);
        Assert.True(built.HeadTrackingIndependentInputs);
        Assert.Equal(udp, built.HeadTrackingEnabled);
        Assert.Equal(freeTrack, built.HeadTrackingFreeTrack);
        var serializer = new XmlSerializer(typeof(AppSettingsData));
        using var writer = new StringWriter();
        serializer.Serialize(writer, built);
        using var reader = new StringReader(writer.ToString());
        var loaded = (AppSettingsData)serializer.Deserialize(reader);
        vm.Dashboard.HeadTrackingEnabled = !udp;
        vm.Dashboard.HeadTrackingFreeTrack = !freeTrack;
        LoadHeadGlobals(settings, loaded);
        Assert.Equal(udp, vm.Dashboard.HeadTrackingEnabled);
        Assert.Equal(freeTrack, vm.Dashboard.HeadTrackingFreeTrack);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData(null, true)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void LegacyProfileOpinionsMigrateOnceWithoutInventingANullOpinion(bool? enabled, bool freeTrack)
    {
        var profile = new ProfileData { EnableHeadTracking = enabled };
        profile.MigrateHeadTrackingInputs(freeTrack);
        Assert.True(profile.HeadTrackingIndependentInputs);
        Assert.Equal(enabled, profile.EnableHeadTracking);
        bool? expectedFree = enabled.HasValue ? enabled.Value && freeTrack : null;
        Assert.Equal(expectedFree, profile.EnableHeadTrackingFreeTrack);
        profile.MigrateHeadTrackingInputs(!freeTrack);
        Assert.Equal(expectedFree, profile.EnableHeadTrackingFreeTrack);
    }

    [Fact]
    public void ColdLoadUsesTheLegacyPreferenceBeforeTheGlobalMasterGate()
    {
        var (vm, settings) = Arrange();
        var data = new AppSettingsData { HeadTrackingEnabled = false, HeadTrackingFreeTrack = true };
        LoadHeadGlobals(settings, data);
        Assert.False(vm.Dashboard.HeadTrackingFreeTrack);
        var on = new ProfileData { Id = "head-on", Name = "Head On", EnableHeadTracking = true };
        var off = new ProfileData { Id = "head-off", Name = "Head Off", EnableHeadTracking = false };
        var inherit = new ProfileData { Id = "head-inherit", Name = "Head Inherit" };
        settings.LoadProfiles(new[] { on, off, inherit }, data);
        Assert.True(on.EnableHeadTrackingFreeTrack);
        Assert.False(off.EnableHeadTrackingFreeTrack);
        Assert.Null(inherit.EnableHeadTrackingFreeTrack);
        settings.ApplyProfileServiceToggles(on);
        Assert.True(vm.Dashboard.HeadTrackingEnabled);
        Assert.True(vm.Dashboard.HeadTrackingFreeTrack);
        settings.ApplyProfileServiceToggles(off);
        Assert.False(HeadTrackingRuntime.AnyEnabled);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EditingOneTrackingInputAuthorsOnlyThatProfileOpinion(bool udp)
    {
        var (vm, settings) = Arrange();
        var profile = new ProfileData { Id = "head", Name = "Head" };
        SettingsManager.Profiles.Add(profile);
        SettingsManager.ActiveProfileId = profile.Id;
        if (udp) vm.Dashboard.HeadTrackingEnabled = true;
        else vm.Dashboard.HeadTrackingFreeTrack = true;
        Assert.True(profile.HeadTrackingIndependentInputs);
        Assert.Equal(udp ? true : (bool?)null, profile.EnableHeadTracking);
        Assert.Equal(udp ? (bool?)null : true, profile.EnableHeadTrackingFreeTrack);
        settings.UpdateActiveProfileSnapshot();
        Assert.Equal(udp ? true : (bool?)null, profile.EnableHeadTracking);
        Assert.Equal(udp ? (bool?)null : true, profile.EnableHeadTrackingFreeTrack);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void ProfilesRestoreIndependentTrackingInputs(bool udp, bool freeTrack)
    {
        var (vm, settings) = Arrange();
        var profile = new ProfileData
        {
            HeadTrackingIndependentInputs = true,
            EnableHeadTracking = udp, EnableHeadTrackingFreeTrack = freeTrack,
        };
        settings.ApplyProfileServiceToggles(profile);
        Assert.Equal(udp, vm.Dashboard.HeadTrackingEnabled);
        Assert.Equal(freeTrack, vm.Dashboard.HeadTrackingFreeTrack);
        Assert.Equal(udp || freeTrack, HeadTrackingRuntime.AnyEnabled);
    }

    [Fact]
    public void NullTrackingOpinionsKeepTheCurrentInputValues()
    {
        var (vm, settings) = Arrange();
        vm.Dashboard.HeadTrackingEnabled = true;
        vm.Dashboard.HeadTrackingFreeTrack = true;
        settings.ApplyProfileServiceToggles(new ProfileData
        {
            HeadTrackingIndependentInputs = true, EnableHeadTracking = false,
        });
        Assert.False(vm.Dashboard.HeadTrackingEnabled);
        Assert.True(vm.Dashboard.HeadTrackingFreeTrack);
        settings.ApplyProfileServiceToggles(new ProfileData
        {
            HeadTrackingIndependentInputs = true, EnableHeadTrackingFreeTrack = false,
        });
        Assert.False(vm.Dashboard.HeadTrackingEnabled);
        Assert.False(vm.Dashboard.HeadTrackingFreeTrack);
    }

    private static void LoadHeadGlobals(SettingsService settings, AppSettingsData data)
        => typeof(SettingsService).GetMethod("LoadAppSettings", BindingFlags.Instance | BindingFlags.NonPublic)
            .Invoke(settings, new object[] { data });

    [Theory]
    [InlineData(null, null)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(null, true)]
    public void IndependentOpinionsSurviveProfileExportAndImport(bool? udp, bool? freeTrack)
    {
        var profile = new ProfileData
        {
            Id = "head-export", Name = "Head Export", HeadTrackingIndependentInputs = true,
            EnableHeadTracking = udp, EnableHeadTrackingFreeTrack = freeTrack,
        };
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".pfprofile");
        try
        {
            PadForge.Common.ProfileTransfer.Export(profile, path);
            var imported = PadForge.Common.ProfileTransfer.Import(path, out _);
            Assert.True(imported.HeadTrackingIndependentInputs);
            Assert.Equal(udp, imported.EnableHeadTracking);
            Assert.Equal(freeTrack, imported.EnableHeadTrackingFreeTrack);
            imported.MigrateHeadTrackingInputs(true);
            Assert.Equal(freeTrack, imported.EnableHeadTrackingFreeTrack);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void ApplyingFreshDefaultsStopsBothInputs()
    {
        var (vm, settings) = Arrange();
        vm.Dashboard.HeadTrackingEnabled = true;
        vm.Dashboard.HeadTrackingFreeTrack = true;
        LoadHeadGlobals(settings, new AppSettingsData());
        Assert.False(vm.Dashboard.HeadTrackingEnabled);
        Assert.False(vm.Dashboard.HeadTrackingFreeTrack);
        Assert.False(HeadTrackingRuntime.AnyEnabled);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LegacyStandaloneProfileUsesTheCurrentFreeTrackPreference(bool freeTrack)
    {
        var (vm, settings) = Arrange();
        vm.Dashboard.HeadTrackingFreeTrack = freeTrack;
        var imported = new ProfileData { EnableHeadTracking = true };
        settings.ApplyProfileServiceToggles(imported);
        Assert.True(vm.Dashboard.HeadTrackingEnabled);
        Assert.Equal(freeTrack, vm.Dashboard.HeadTrackingFreeTrack);
        Assert.Equal(freeTrack, imported.EnableHeadTrackingFreeTrack);
        Assert.True(imported.HeadTrackingIndependentInputs);
    }

    [Fact]
    public void ColdStartupPreservesFreeTrackWhenNamedProfileHasNoOpinionOnIt()
    {
        var (vm, settings) = Arrange();
        var global = new AppSettingsData
        {
            HeadTrackingIndependentInputs = true, HeadTrackingEnabled = true, HeadTrackingFreeTrack = true,
            ActiveProfileId = "udp-off",
        };
        LoadHeadGlobals(settings, global);
        var profile = new ProfileData
        {
            Id = "udp-off", Name = "UDP Off", HeadTrackingIndependentInputs = true,
            EnableHeadTracking = false, EnableHeadTrackingFreeTrack = null,
        };
        settings.LoadProfiles(new[] { profile }, global);
        Assert.False(vm.Dashboard.HeadTrackingEnabled);
        Assert.True(vm.Dashboard.HeadTrackingFreeTrack);
        Assert.Null(profile.EnableHeadTrackingFreeTrack);
    }
}
