using System;
using System.Collections.Generic;

namespace PadForge.Common.Input.Peripherals
{
    /// <summary>
    /// The ASUS Aura SDK worker (#557 item 11). While Armoury Crate runs,
    /// the ASUS devices it lights are lit through its Aura SDK, by kind
    /// (<see cref="AuraLights"/>): the keyboard path key by key, the others
    /// light by light. The session opens only while one of its paths is
    /// claimed and Armoury Crate's processes run
    /// (<see cref="VendorPrograms.ArmouryCrate"/>), the check Aurora makes
    /// before it opens the SDK: "Aura or Armory Crate is not running!
    /// (LightingService.exe)" (Aurora AsusDevice.cs:17-21).
    ///
    /// <para>SwitchMode takes every Aura device at once and ReleaseControl
    /// gives every one back (AsusDeviceProvider.cs:65 and 103), so while any
    /// path is claimed the SDK holds them all. The SDK has no call that hands
    /// one device back, so a path nobody claims any more while another stays
    /// claimed ends the session and opens a new one in the same pass, which
    /// paints what stays, as <see cref="ChromaBackend"/> reopens when its
    /// categories change. PadForge's picture leaves the kind that was let go.
    /// A kind no claim paints in a session shows whatever SwitchMode leaves on
    /// it, which no reference records. When the last claim goes, the program
    /// stops or a call fails, ReleaseControl hands every device back to
    /// Armoury Crate's own lighting.</para>
    ///
    /// <para>Each device is sent with one Apply after its lights are set,
    /// as RGB.NET sends it (AsusUpdateQueue.cs:90). A device whose lights
    /// did not change is not sent. Within a device only the lights whose
    /// color changed are set again, and the first picture of a session
    /// sets every one. A per-key board RGB.NET gives extra lights by its name
    /// (<see cref="AuraLights.ExtraLightsOf"/>) takes them through its Lights
    /// list beside its keys (AsusUpdateQueue.cs:50-73).</para>
    /// </summary>
    internal sealed class AuraSdkBackend : SdkCategoryBackend
    {
        private readonly IAuraSdkNative _native;

        public AuraSdkBackend() : this(null) { }

        internal AuraSdkBackend(IAuraSdkNative native, int pollMs = 50, int retryMs = 30000, int stopWaitMs = 3000,
            int predecessorWaitMs = DefaultPredecessorWaitMs)
            : base(pollMs, retryMs, stopWaitMs, predecessorWaitMs)
        {
            _native = native ?? new AuraSdkNative();
        }

        private static readonly WorkerSuccession s_workers = new();

        protected override WorkerSuccession Workers => s_workers;

        public override OutputFamily Family => OutputFamily.AuraSdkType;

        protected override IReadOnlyList<string> Categories => AuraLights.Categories;

        protected override string LogName => "AURA SDK";

        protected override bool Installed(PeripheralPresence presence) => presence.AsusAuraSdk;

        protected override bool ProgramRuns(PeripheralPresence presence)
            => presence.Programs.FirstOf(ArmouryCrateList) != null;

        private static readonly IReadOnlyList<VendorProgram> ArmouryCrateList = new[] { VendorPrograms.ArmouryCrate };

        protected override LightMap StaticMap(string category) => category == "keyboard" ? AuraLights.KeyboardMap : null;

        protected override bool Open(out string detail) => _native.Open(out detail);

        protected override LightMap SessionMap(string category) => AuraLights.MapFor(category, _native.Devices);

        protected override bool Paint(string category, SdkPicture picture, SdkPicture last)
        {
            foreach (var device in _native.Devices)
            {
                if (AuraLights.CategoryOf(device.Type) != category) continue;
                bool changed = false;
                if (category == "keyboard" && AuraLights.PerKey(device.Type))
                {
                    foreach (ushort code in device.KeyCodes)
                    {
                        int rgb = picture.ColorAt(code);
                        if (last != null && last.ColorAt(code) == rgb) continue;
                        _native.SetKey(device.Index, code, rgb);
                        changed = true;
                    }
                    foreach (var extra in AuraLights.ExtraLightsOf(device.Name))
                    {
                        if (extra.Index >= device.LightCount) continue;
                        int address = AuraLights.ExtraBase + extra.Index;
                        int rgb = picture.ColorAt(address);
                        if (last != null && last.ColorAt(address) == rgb) continue;
                        _native.SetLight(device.Index, extra.Index, rgb);
                        changed = true;
                    }
                }
                else
                {
                    int zoneBase = category == "keyboard" ? AuraLights.ZoneBase : 0;
                    for (int i = 0; i < device.LightCount; i++)
                    {
                        int rgb = picture.ColorAt(zoneBase + i);
                        if (last != null && last.ColorAt(zoneBase + i) == rgb) continue;
                        _native.SetLight(device.Index, i, rgb);
                        changed = true;
                    }
                }
                if (changed) _native.Apply(device.Index);
            }
            return true;
        }

        /// <summary>ReleaseControl hands every device back at once, never one
        /// kind alone (AsusDeviceProvider.cs:103).</summary>
        protected override bool HandsBackAllAtOnce => true;

        protected override void Close() => _native.Close();
    }
}
