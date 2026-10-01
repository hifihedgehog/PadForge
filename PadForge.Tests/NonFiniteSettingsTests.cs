using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Data;
using PadForge.Common;
using PadForge.Common.Input;
using PadForge.Engine.Common.Mapping;
using PadForge.Services;
using PadForge.ViewModels;
using PadForge.Views;
using Xunit;

namespace PadForge.Tests;

/// <summary>
/// NaN and infinity in a stored setting read as unreadable, as
/// ConstantForceEvaluator.ParseNorm already read them. Math.Clamp passes NaN
/// through, so one hand-edited or imported value reached every clamped setter
/// and the engine. Found reviewing #474.
/// </summary>
[Collection("SettingsManagerStatics")]
public class NonFiniteSettingsTests
{
    private static T Call<T>(Type type, string name, params object[] args)
        => (T)type.GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, args);

    [Theory]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("-Infinity")]
    [InlineData("1e400")]
    public void EveryStoredSettingParseReadsANonFiniteValueAsUnreadable(string stored)
    {
        Assert.Equal(5.0, Call<double>(typeof(InputService), "TryParseDouble", stored, 5.0));
        Assert.Equal(5f, Call<float>(typeof(InputService), "TryParseFloatPs", stored, 5f));
        Assert.Equal(5.0, Call<double>(typeof(SettingsService), "TryParseDouble", stored, 5.0));
        Assert.Equal(5.0, Call<double>(typeof(PadViewModel), "ParseSteerDouble", stored, 5.0));
        Assert.Equal(0.0, Call<double>(typeof(InputManager), "ParseConstantForceComponent", stored));
        Assert.Equal(5f, Call<float>(typeof(InputManager), "TryParseFloat", stored, 5f));
        Assert.Equal(5.0, Call<double>(typeof(InputManager), "TryParseDoubleStatic", stored, 5.0));
    }

    [Fact]
    public void AFiniteValueStillParses()
    {
        Assert.Equal(2.5, Call<double>(typeof(InputService), "TryParseDouble", "2.5", 5.0));
        Assert.Equal(2.5f, Call<float>(typeof(InputService), "TryParseFloatPs", "2.5", 5f));
        Assert.Equal(2.5, Call<double>(typeof(SettingsService), "TryParseDouble", "2.5", 5.0));
        Assert.Equal(2.5, Call<double>(typeof(PadViewModel), "ParseSteerDouble", "2.5", 5.0));
        Assert.Equal(0.25, Call<double>(typeof(InputManager), "ParseConstantForceComponent", "0.25"));
        Assert.Equal(2.5f, Call<float>(typeof(InputManager), "TryParseFloat", "2.5", 5f));
        Assert.Equal(2.5, Call<double>(typeof(InputManager), "TryParseDoubleStatic", "2.5", 5.0));
    }

    /// <summary>The five local parsers SettingsService builds mapping
    /// sources with share the rule.</summary>
    [Fact]
    public void SettingsServicesLocalParsersShareTheRule()
    {
        string text = File.ReadAllText(Path.Combine(RepoRoot(), "PadForge.App", "Services", "SettingsService.cs"));
        Assert.Equal(5, CountOf(text, "out double v) && double.IsFinite(v) ? v : dflt;"));
        Assert.Equal(0, CountOf(text, "out double v) ? v : dflt;"));
    }

    [Fact]
    public void ACurveWithANonFinitePointReadsWithoutIt()
    {
        Assert.Equal(new List<(double, double)> { (0, 0), (1, 1) }, CurveLut.Parse("NaN"));
        Assert.Equal(new List<(double, double)> { (0, 0), (1, 1) }, CurveLut.Parse("Infinity"));
        var points = CurveLut.Parse("0,0;0.5,NaN;0.6,0.4;1,1");
        Assert.All(points, p => Assert.True(double.IsFinite(p.X) && double.IsFinite(p.Y)));
        Assert.Contains((0.6, 0.4), points);
    }

    [Fact]
    public void AnEqBandWithANonFiniteValueNeverReachesTheFilter()
    {
        var bands = EqBandCodec.Decode("PK:NaN:0:1|PK:1000:NaN:NaN|PK:2000:3:2");
        Assert.Equal(2, bands.Count);
        Assert.Equal(1000f, bands[0].FrequencyHz);
        Assert.Equal(0f, bands[0].GainDb);
        Assert.Equal(0.707f, bands[0].Q, 3);
        Assert.Equal(3f, bands[1].GainDb);

        string huge = new string('9', 60);
        var (parsed, preamp) = AutoEqProfile.Parse(
            $"Preamp: {huge} dB\nFilter 1: ON PK Fc 1000 Hz Gain {huge} dB Q {huge}\nFilter 2: ON PK Fc {huge} Hz Gain 1 dB Q 1");
        Assert.True(float.IsFinite(preamp));
        var band = Assert.Single(parsed);
        Assert.Equal(0f, band.GainDb);
        Assert.Equal(0.707f, band.Q, 3);
    }

    [Theory]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    public void APercentBoxIgnoresANonFiniteEntry(string typed)
    {
        var converter = new FractionToPercentConverter();
        Assert.Same(Binding.DoNothing, converter.ConvertBack(typed, typeof(double), null, CultureInfo.InvariantCulture));
        Assert.Same(Binding.DoNothing, converter.ConvertBack(double.NaN, typeof(double), null, CultureInfo.InvariantCulture));
        Assert.Equal(0.25, converter.ConvertBack("25", typeof(double), null, CultureInfo.InvariantCulture));
    }

    [Fact]
    public void AFormulaNumberPastDoublesRangeIsInvalid()
    {
        Assert.False(MappingExpression.Compile(new string('9', 400)).IsValid);
        Assert.True(MappingExpression.Compile("1.5").IsValid);
    }

    /// <summary>A NaN typed into a numeric box leaves the property as it was,
    /// for every double and float setter in a view model.</summary>
    [Fact]
    public void ANaNLeavesAClampedSettingAsItWas()
    {
        var vm = new MainViewModel().Pads[0];
        vm.GyroTiltRangeDeg = 40;
        vm.GyroTiltRangeDeg = double.NaN;
        Assert.Equal(40, vm.GyroTiltRangeDeg);
        // A setter with its own rule keeps it.
        vm.GyroSimulationSmoothingMs = double.NaN;
        Assert.Equal(100, vm.GyroSimulationSmoothingMs);
        // Infinity clamps as before.
        vm.GyroTiltRangeDeg = double.PositiveInfinity;
        Assert.Equal(90, vm.GyroTiltRangeDeg);
    }

    private static int CountOf(string text, string value)
    {
        int count = 0, at = 0;
        while ((at = text.IndexOf(value, at, StringComparison.Ordinal)) >= 0) { count++; at += value.Length; }
        return count;
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "PadForge.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("PadForge.sln not found");
    }
}
