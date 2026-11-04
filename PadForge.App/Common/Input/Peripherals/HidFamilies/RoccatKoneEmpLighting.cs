using System;
using System.Collections.Generic;
using PadForge.Common.Input;

namespace PadForge.Common.Input.Peripherals.HidFamilies
{
    /// <summary>
    /// The Roccat Kone EMP (1E7D:2E24), which only libratbag lights
    /// (src/driver-roccat-kone-emp.c, #557).
    ///
    /// <para>The mouse has no way to take a color but its profile's settings, report 06.
    /// A write reads the active profile from report 05 (roccat_current_profile, lines
    /// 425-439), asks for that profile's settings by writing 04 with the profile and 80
    /// (roccat_set_config_profile, lines 484-505), waits for report 04 to say ready
    /// (roccat_is_ready, lines 378-396), and reads report 06. Each of the four LEDs takes
    /// color index 1E for a color of its own, the status FF, no color flow and the fixed
    /// effect (roccat_write_profile, lines 568-590), then the sum of the report's bytes as
    /// its last two (roccat_compute_crc, lines 334-347).</para>
    ///
    /// <para>Every picture is stored in the mouse, so the bounds are those of a stored write
    /// (<see cref="LenovoLegionLighting"/>), and hand-back writes the settings the session
    /// read, so the profile goes back to what it held.</para>
    /// </summary>
    internal sealed class RoccatKoneEmpLighting : IHidLightingFamily
    {
        public static readonly RoccatKoneEmpLighting Instance = new();

        internal const ushort VendorId = 0x1E7D, ProductId = 0x2E24;

        internal const byte ReportConfigure = 0x04, ReportProfile = 0x05, ReportSettings = 0x06;

        /// <summary>ROCCAT_CONFIG_SETTINGS (line 75).</summary>
        internal const byte ConfigSettings = 0x80;

        /// <summary>sizeof(struct roccat_settings_report) (lines 112-131).</summary>
        internal const int SettingsLength = 41;

        internal const int Leds = 4;

        private const int StatusAt = 19, FlowAt = 20, EffectAt = 21, LedsAt = 23;
        private const byte UserColor = 0x1E, EffectFixed = 0x01;
        private const int ReadyPolls = 50, ReadyWaitMs = 10;

        public string Name => "Roccat Kone EMP";

        private HidLightingModel _model;

        public HidLightingModel Match(HidCollectionInfo collection)
        {
            if (collection == null || collection.VendorId != VendorId || collection.ProductId != ProductId) return null;
            if (collection.FeatureReportLength < SettingsLength) return null;
            return _model ??= new HidLightingModel
            {
                Family = this,
                Name = Name,
                Map = new LightMap(new[]
                {
                    new LightInfo(0, LightKind.Edge, 0, LightKeySource.None),
                    new LightInfo(1, LightKind.Edge, 0, LightKeySource.None),
                    new LightInfo(2, LightKind.Edge, 0, LightKeySource.None),
                    new LightInfo(3, LightKind.Edge, 0, LightKeySource.None),
                }, "libratbag src/driver-roccat-kone-emp.c") { Product = "roccat:kone-emp" },
                Programs = new[] { VendorPrograms.RoccatSwarm },
                HoldMs = LenovoLegionLighting.HoldMs,
                RepaintMs = LenovoLegionLighting.RepaintMs,
                Begin = Begin,
            };
        }

        /// <summary>The sum of the report's bytes but the last two, low byte first.</summary>
        internal static void PutChecksum(byte[] report)
        {
            int sum = 0;
            for (int i = 0; i < SettingsLength - 2; i++) sum += report[i];
            report[SettingsLength - 2] = (byte)sum;
            report[SettingsLength - 1] = (byte)(sum >> 8);
        }

        /// <summary>The settings with the colors in them.</summary>
        internal static byte[] Written(byte[] settings, IReadOnlyList<LightInfo> lights, IReadOnlyList<int> colors)
        {
            var report = (byte[])settings.Clone();
            report[StatusAt] = 0xFF;
            report[FlowAt] = 0x00;
            report[EffectAt] = EffectFixed;
            int n = Math.Min(lights?.Count ?? 0, colors?.Count ?? 0);
            for (int i = 0; i < n; i++)
            {
                int at = lights[i].Address;
                if (at < 0 || at >= Leds) continue;
                report[LedsAt + at * 4] = UserColor;
                report[LedsAt + at * 4 + 1] = (byte)(colors[i] >> 16);
                report[LedsAt + at * 4 + 2] = (byte)(colors[i] >> 8);
                report[LedsAt + at * 4 + 3] = (byte)colors[i];
            }
            PutChecksum(report);
            return report;
        }

        /// <summary>roccat_wait_ready (lines 398-421). Report 04's second byte: 01 ready, 02 an
        /// error, anything else not yet.</summary>
        internal static bool WaitReady(IHidReportChannel io, Action<int> wait)
        {
            var answer = new byte[Math.Max(3, io.FeatureReportLength)];
            wait?.Invoke(ReadyWaitMs);
            for (int i = 0; i < ReadyPolls; i++)
            {
                if (!io.GetFeature(ReportConfigure, answer)) return false;
                if (answer[1] == 0x01) return true;
                if (answer[1] == 0x02) return false;
                wait?.Invoke(ReadyWaitMs);
            }
            return false;
        }

        /// <summary>The active profile's settings, or null.</summary>
        internal static byte[] ReadSettings(IHidReportChannel io, Action<int> wait)
        {
            var profile = new byte[Math.Max(3, io.FeatureReportLength)];
            if (!io.GetFeature(ReportProfile, profile)) return null;
            if (!io.SetFeature(new[] { ReportConfigure, profile[2], ConfigSettings })) return null;
            if (!WaitReady(io, wait)) return null;
            var buffer = new byte[Math.Max(SettingsLength, io.FeatureReportLength)];
            if (!io.GetFeature(ReportSettings, buffer)) return null;
            var settings = new byte[SettingsLength];
            Array.Copy(buffer, settings, SettingsLength);
            return settings;
        }

        internal static HidLightingSession Begin(IHidReportChannel io, Action<int> wait)
        {
            if (io == null) return null;
            var settings = ReadSettings(io, wait);
            return settings == null ? null : new Session(settings);
        }

        internal sealed class Session : HidLightingSession
        {
            private readonly byte[] _original;

            public Session(byte[] original) => _original = original;

            public override bool Feature => true;

            public override byte[][] CrashFrames => new[] { _original };

            public override HidLightingPaint Paint(IHidReportChannel io, Action<int> wait,
                IReadOnlyList<LightInfo> lights, IReadOnlyList<int> colors)
                => io.SetFeature(Written(_original, lights, colors)) && WaitReady(io, wait)
                    ? HidLightingPaint.Sent : HidLightingPaint.Failed;

            public override bool HandBack(IHidReportChannel io, Action<int> wait)
                => io.SetFeature(_original) && WaitReady(io, wait);
        }
    }
}
