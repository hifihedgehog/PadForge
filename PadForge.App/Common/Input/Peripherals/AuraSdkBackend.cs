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
    /// path is claimed the SDK holds them all. A path nobody claims while
    /// another is claimed keeps the last picture it was sent, since the SDK
    /// has no call that hands one device back, and a kind no claim has
    /// painted in the session shows whatever SwitchMode leaves on it, which
    /// no reference records. When the last claim goes,
    /// the program stops or a call fails, ReleaseControl hands every device
    /// back to Armoury Crate's own lighting.</para>
    ///
    /// <para>Each device is sent with one Apply after its lights are set,
    /// as RGB.NET sends it (AsusUpdateQueue.cs:90). A device whose lights
    /// did not change is not sent. Within a device only the lights whose
    /// color changed are set again, and the first picture of a session
    /// sets every one.</para>
    /// </summary>
    internal sealed class AuraSdkBackend : SdkCategoryBackend
    {
        private readonly IAuraSdkNative _native;

        public AuraSdkBackend() : this(null) { }

        internal AuraSdkBackend(IAuraSdkNative native, int pollMs = 50, int retryMs = 30000, int stopWaitMs = 3000)
            : base(pollMs, retryMs, stopWaitMs)
        {
            _native = native ?? new AuraSdkNative();
        }

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

        /// <summary>The SDK hands every device back at once, never one kind
        /// alone, so a path that leaves while others stay keeps its last
        /// picture until the session ends.</summary>
        protected override void Release(string category) { }

        protected override void Close() => _native.Close();
    }
}
