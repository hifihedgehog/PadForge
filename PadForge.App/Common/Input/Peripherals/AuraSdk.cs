using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace PadForge.Common.Input.Peripherals
{
    /// <summary>
    /// The native seam of the ASUS Aura SDK worker (#557). Production is
    /// <see cref="AuraSdkNative"/>, the test bench scripts a fake. Every
    /// call is made on the worker's thread.
    /// </summary>
    internal interface IAuraSdkNative
    {
        /// <summary>Creates the SDK's object, takes control of the devices
        /// (SwitchMode) and reads them (Enumerate). False with a reason when
        /// it cannot.</summary>
        bool Open(out string detail);

        /// <summary>The devices the open session read, in the SDK's
        /// order.</summary>
        IReadOnlyList<AuraDevice> Devices { get; }

        /// <summary>Sets the light at <paramref name="index"/> of the
        /// device's Lights list to a 0xRRGGBB color.</summary>
        void SetLight(int device, int index, int rgb);

        /// <summary>Sets the key with the Aura key code
        /// <paramref name="code"/> of a keyboard lit key by key.</summary>
        void SetKey(int device, ushort code, int rgb);

        /// <summary>Sends what was set to the device.</summary>
        void Apply(int device);

        /// <summary>Hands every device back to Armoury Crate
        /// (ReleaseControl) and lets the SDK go. Never throws.</summary>
        void Close();
    }

    /// <summary>
    /// The Aura SDK through the COM server Armoury Crate's LightingService
    /// hosts, reached the way RGB.NET.Devices.Asus reaches it: the AuraSdk
    /// class made and cast to IAuraSdk2, SwitchMode to take the devices,
    /// Enumerate(0) for every device (AsusDeviceProvider.cs:61-74), each
    /// light colored through its Red, Green and Blue and each device sent
    /// with Apply (AsusUpdateQueue.cs:158-202), and ReleaseControl(0) to give
    /// them back (AsusDeviceProvider.cs:103). The device collection is held
    /// for the session's life, as RGB.NET holds it against an access
    /// violation inside the SDK (AsusDeviceProvider.cs:37).
    ///
    /// <para>The interfaces are declared here, so PadForge ships no interop
    /// assembly of ASUS's. Their IIDs, the class ID and the order of their
    /// methods are those of Interop.AuraServiceLib.dll, the type library
    /// import RGB.NET, Aurora and Artemis carry (one file, SHA-256
    /// B82FC09C363FF94B...), read through its metadata alone. Every
    /// interface is dual (TypeLibType 0x10C0), so its methods follow
    /// IDispatch's in the order the import lists them.</para>
    /// </summary>
    internal sealed class AuraSdkNative : IAuraSdkNative
    {
        /// <summary>AuraServiceLib.AuraSdkClass.</summary>
        internal static readonly Guid AuraSdkClsid = new("05921124-5057-483E-A037-E9497B523590");

        private IAuraSdk2 _sdk;
        private IAuraSyncDeviceCollection _collection;
        private readonly List<IAuraSyncDevice> _devices = new();
        private readonly List<IAuraRgbLight[]> _lights = new();
        private readonly List<Dictionary<ushort, IAuraRgbLight>> _keys = new();
        private readonly List<IAuraSyncKeyboard> _keyboards = new();
        private IReadOnlyList<AuraDevice> _found = Array.Empty<AuraDevice>();

        public IReadOnlyList<AuraDevice> Devices => _found;

        /// <summary>Whether the SDK's class is registered, in either
        /// registry view, which is how Armoury Crate's LightingService makes
        /// it known.</summary>
        internal static bool Registered()
        {
            string key = @"CLSID\{" + AuraSdkClsid.ToString("D").ToUpperInvariant() + "}";
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                try
                {
                    using var root = RegistryKey.OpenBaseKey(RegistryHive.ClassesRoot, view);
                    using var sub = root.OpenSubKey(key);
                    if (sub != null) return true;
                }
                catch { }
            }
            return false;
        }

        public bool Open(out string detail)
        {
            if (PadForge.Engine.Common.VendorProgramGate.Refuses)
            {
                detail = "the test host opens no vendor SDK";
                return false;
            }
            Close();
            try
            {
                var type = Type.GetTypeFromCLSID(AuraSdkClsid, throwOnError: false);
                if (type == null)
                {
                    detail = "AuraSdk class not registered";
                    return false;
                }
                if (Activator.CreateInstance(type) is not IAuraSdk2 sdk)
                {
                    detail = "AuraSdk object answers no IAuraSdk2";
                    return false;
                }
                _sdk = sdk;
                _sdk.SwitchMode();
                _collection = _sdk.Enumerate(0);
                int count = _collection.get_Count();
                var found = new List<AuraDevice>(count);
                for (int i = 0; i < count; i++)
                {
                    var device = _collection.get_Item(i);
                    var lightList = device.get_Lights();
                    int lightCount = lightList.get_Count();
                    var lights = new IAuraRgbLight[lightCount];
                    for (int l = 0; l < lightCount; l++) lights[l] = lightList.get_Item(l);
                    uint deviceType = device.get_Type();
                    var codes = new List<ushort>();
                    IAuraSyncKeyboard keyboard = null;
                    if (AuraLights.PerKey(deviceType) && device is IAuraSyncKeyboard kb)
                    {
                        keyboard = kb;
                        var keys = kb.get_Keys();
                        int keyCount = keys.get_Count();
                        for (int k = 0; k < keyCount; k++) codes.Add(keys.get_Item(k).get_Code());
                    }
                    _devices.Add(device);
                    _lights.Add(lights);
                    _keys.Add(new Dictionary<ushort, IAuraRgbLight>());
                    _keyboards.Add(keyboard);
                    found.Add(new AuraDevice(i, deviceType, device.get_Name(), lightCount, codes));
                }
                _found = found;
                detail = $"{count} devices";
                return true;
            }
            catch (Exception ex)
            {
                detail = "AuraSdk " + ex.GetType().Name + " 0x" + ex.HResult.ToString("X8");
                Close();
                return false;
            }
        }

        public void SetLight(int device, int index, int rgb) => Color(_lights[device][index], rgb);

        public void SetKey(int device, ushort code, int rgb)
        {
            var keys = _keys[device];
            if (!keys.TryGetValue(code, out var light))
                keys[code] = light = _keyboards[device].get_Key(code);
            Color(light, rgb);
        }

        private static void Color(IAuraRgbLight light, int rgb)
        {
            light.set_Red((byte)(rgb >> 16));
            light.set_Green((byte)(rgb >> 8));
            light.set_Blue((byte)rgb);
        }

        public void Apply(int device) => _devices[device].Apply();

        public void Close()
        {
            var sdk = _sdk;
            _sdk = null;
            if (sdk != null)
            {
                try { sdk.ReleaseControl(0); } catch { }
            }
            _devices.Clear();
            _lights.Clear();
            _keys.Clear();
            _keyboards.Clear();
            _found = Array.Empty<AuraDevice>();
            _collection = null;
            if (sdk != null)
            {
                try { Marshal.FinalReleaseComObject(sdk); } catch { }
            }
        }

        // ── AuraServiceLib, as Interop.AuraServiceLib.dll declares it ──
        // Every method returns an HRESULT the runtime turns into an
        // exception, so a server that went away fails the call.

        [ComImport, Guid("EE69DBAE-33FF-4E45-B378-01797A59852D"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
        internal interface IAuraSdk2
        {
            [DispId(1)] [return: MarshalAs(UnmanagedType.Interface)] IAuraSyncDeviceCollection Enumerate(uint devType);
            [DispId(2)] void SwitchMode();
            [DispId(3)] void ReleaseControl(uint reserve);
        }

        [ComImport, Guid("87FC56AB-99CA-4FD3-B561-2EEDD719DA57"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
        internal interface IAuraSyncDeviceCollection
        {
            [DispId(-4)] [return: MarshalAs(UnmanagedType.IUnknown)] object GetEnumerator();
            [DispId(2)] int get_Count();
            [DispId(0)] [return: MarshalAs(UnmanagedType.Interface)] IAuraSyncDevice get_Item(int index);
        }

        [ComImport, Guid("6A30D789-F5DA-4F26-BF09-6DFB9DEDF91E"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
        internal interface IAuraSyncDevice
        {
            [DispId(7)] [return: MarshalAs(UnmanagedType.Interface)] IAuraRgbLightCollection get_Lights();
            [DispId(8)] uint get_Type();
            [DispId(9)] [return: MarshalAs(UnmanagedType.BStr)] string get_Name();
            [DispId(13)] uint get_Width();
            [DispId(14)] uint get_Height();
            [DispId(1)] void Apply();
        }

        [ComImport, Guid("87B03DD6-59E6-44F5-8772-56A7494F9900"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
        internal interface IAuraSyncKeyboard
        {
            [DispId(7)] [return: MarshalAs(UnmanagedType.Interface)] IAuraRgbLightCollection get_Lights();
            [DispId(8)] uint get_Type();
            [DispId(9)] [return: MarshalAs(UnmanagedType.BStr)] string get_Name();
            [DispId(13)] uint get_Width();
            [DispId(14)] uint get_Height();
            [DispId(1)] void Apply();
            [DispId(15)] [return: MarshalAs(UnmanagedType.Interface)] IAuraRgbLight get_Key(ushort keyCode);
            [DispId(16)] [return: MarshalAs(UnmanagedType.Interface)] IAuraRgbKeyCollection get_Keys();
        }

        [ComImport, Guid("003065BC-B562-454E-ADCB-A9B70042D486"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
        internal interface IAuraRgbLightCollection
        {
            [DispId(-4)] [return: MarshalAs(UnmanagedType.IUnknown)] object GetEnumerator();
            [DispId(2)] int get_Count();
            [DispId(0)] [return: MarshalAs(UnmanagedType.Interface)] IAuraRgbLight get_Item(int index);
        }

        [ComImport, Guid("9AF6260E-4311-417D-B3EF-B85A34CF3244"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
        internal interface IAuraRgbLight
        {
            [DispId(1)] byte get_Red();
            [DispId(1)] void set_Red(byte value);
            [DispId(2)] byte get_Green();
            [DispId(2)] void set_Green(byte value);
            [DispId(3)] byte get_Blue();
            [DispId(3)] void set_Blue(byte value);
            [DispId(4)] [return: MarshalAs(UnmanagedType.BStr)] string get_Name();
            [DispId(5)] uint get_Color();
            [DispId(5)] void set_Color(uint value);
        }

        [ComImport, Guid("81C61F82-4DA0-4081-BDDE-108E1FCDDA7F"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
        internal interface IAuraRgbKeyCollection
        {
            [DispId(-4)] [return: MarshalAs(UnmanagedType.IUnknown)] object GetEnumerator();
            [DispId(2)] int get_Count();
            [DispId(0)] [return: MarshalAs(UnmanagedType.Interface)] IAuraRgbKey get_Item(int index);
        }

        [ComImport, Guid("95E6C828-6DE4-441E-873A-C44AE850F3A2"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
        internal interface IAuraRgbKey
        {
            [DispId(1)] byte get_Red();
            [DispId(1)] void set_Red(byte value);
            [DispId(2)] byte get_Green();
            [DispId(2)] void set_Green(byte value);
            [DispId(3)] byte get_Blue();
            [DispId(3)] void set_Blue(byte value);
            [DispId(4)] [return: MarshalAs(UnmanagedType.BStr)] string get_Name();
            [DispId(5)] uint get_Color();
            [DispId(5)] void set_Color(uint value);
            [DispId(6)] ushort get_Code();
            [DispId(7)] int get_X();
            [DispId(8)] int get_Y();
        }
    }
}
