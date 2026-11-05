using System.Collections;
using System.IO;
using System.Reflection;
using HIDMaestro;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Data;
using PadForge.Engine.RemoteLink;
using PadForge.ViewModels;

namespace PadForge.Tests;

/// <summary>
/// A Sony pad shared over Remote Link. The consumer ships its effects and its
/// sound, and the owner plays them on the real pad: the effect bytes in the
/// shape SDL's driver for that pad copies, the sound on the route the owner
/// sets, and the master volume where the pad's report does not carry it.
/// </summary>
[Collection("AudioProfileLifecycle")]
public sealed class SharedSonyPadTests : IDisposable
{
    const BindingFlags Static = BindingFlags.Static | BindingFlags.NonPublic;
    static readonly Type Audio = typeof(AudioPassthroughService);
    readonly object gate;
    readonly IDictionary sinks;
    readonly Thread priorWorker;
    readonly Thread worker = Thread.CurrentThread;
    readonly DeviceCollection devices = SettingsManager.UserDevices;
    readonly SettingsCollection settings = SettingsManager.UserSettings;
    readonly bool[] created = SettingsManager.SlotCreated;
    readonly bool[] enabled = SettingsManager.SlotEnabled;
    readonly Func<int, bool> demand = AudioPassthroughService.SlotWantsMacroAudioProvider;
    readonly Func<int, IEnumerable<(Guid Device, bool PassthroughOn, string MirrorSource)>> config = AudioPassthroughService.PassthroughConfigProvider;
    readonly Func<int, Guid, int> outputPath = AudioPassthroughService.DeviceAudioOutputPathProvider;
    readonly string dir = Path.Combine(Path.GetTempPath(), "PadForge-SharedPad-" + Guid.NewGuid().ToString("N"));

    public SharedSonyPadTests()
    {
        AudioPassthroughService.Shutdown();
        foreach (string name in new[] { "_workerThread", "_btThread" })
            if (Audio.GetField(name, Static)!.GetValue(null) is Thread prior && prior.IsAlive)
                Assert.True(prior.Join(6000));
        gate = Audio.GetField("_lock", Static)!.GetValue(null)!;
        sinks = (IDictionary)Audio.GetField("_sinks", Static)!.GetValue(null)!;
        priorWorker = Audio.GetField("_workerThread", Static)!.GetValue(null) as Thread;
        lock (gate)
        {
            Audio.GetField("_workerThread", Static)!.SetValue(null, worker);
            Audio.GetField("_running", Static)!.SetValue(null, true);
        }
        SettingsManager.UserDevices = new DeviceCollection();
        SettingsManager.UserSettings = new SettingsCollection();
        SettingsManager.SlotCreated = new bool[16];
        SettingsManager.SlotEnabled = new bool[16];
        AudioPassthroughService.SlotWantsMacroAudioProvider = _ => false;
        AudioPassthroughService.PassthroughConfigProvider = _ => Array.Empty<(Guid, bool, string)>();
        AudioPassthroughService.DeviceAudioOutputPathProvider = null;
        Directory.CreateDirectory(dir);
    }

    public void Dispose()
    {
        var watches = (IDictionary)Audio.GetField("_jackWatch", Static)!.GetValue(null)!;
        var jackGate = Audio.GetField("_jackLock", Static)!.GetValue(null)!;
        Thread[] readers;
        lock (jackGate) readers = watches.Values.Cast<object>().Select(w => (Thread)w.GetType().GetField("Thread")!.GetValue(w)!).ToArray();
        AudioPassthroughService.Shutdown();
        foreach (var reader in readers) if (reader.IsAlive) Assert.True(reader.Join(2000));
        Audio.GetField("_workerThread", Static)!.SetValue(null, priorWorker);
        SettingsManager.UserDevices = devices;
        SettingsManager.UserSettings = settings;
        SettingsManager.SlotCreated = created;
        SettingsManager.SlotEnabled = enabled;
        AudioPassthroughService.SlotWantsMacroAudioProvider = demand;
        AudioPassthroughService.PassthroughConfigProvider = config;
        AudioPassthroughService.DeviceAudioOutputPathProvider = outputPath;
        try { Directory.Delete(dir, true); } catch { }
    }

    static void Reconcile(Thread on) => Audio.GetMethod("ReconcileOnWorker", Static)!.Invoke(null, new object[] { on });

    /// <summary>What the consumer ships for a report through the writer.</summary>
    static byte[] Shipped(HMProfile profile, Dictionary<string, object> fields)
    {
        string path = "peer://shared-" + Guid.NewGuid();
        var sender = RemoteLinkOutputRouter.SendOutput;
        byte[] body = null;
        try
        {
            RemoteLinkOutputRouter.Register(path, "shared", 0);
            RemoteLinkOutputRouter.SendOutput = (_, _, bytes) =>
            {
                Assert.True(OutputEffectCodec.TryDecode(bytes, out var effect));
                body = effect.SonyBody;
            };
            Assert.True(PlayStationEffectWriter.Write(path, profile, fields));
            return body;
        }
        finally
        {
            RemoteLinkOutputRouter.Unregister(path);
            RemoteLinkOutputRouter.SendOutput = sender;
        }
    }

    /// <summary>SDL's PS4 driver sets the validity flags itself and copies the
    /// body after them, to byte 4 of a USB report and byte 6 of a Bluetooth
    /// one, where DS4EffectsState_t starts: right motor, left motor, red,
    /// green, blue, flash on, flash off (SDL_hidapi_ps4.c,
    /// HIDAPI_DriverPS4_InternalSendJoystickEffect and
    /// HIDAPI_DriverPS4_SendJoystickEffect).</summary>
    [Theory]
    [InlineData("dualshock-4-v1")]
    [InlineData("dualshock-4-v2")]
    public void ASharedDualShock4_ShipsWhatSdlsPs4DriverCopies(string profileId)
    {
        var profile = HMaestroProfileCatalog.GetProfileById(profileId);
        Assert.NotNull(profile);
        var fields = Ds4EffectSynthesizer.BuildFields(new DeviceSlotConfig(), 0f, 0, 0, 0, 0f, 0x11, 0x22);
        fields["lightbar"] = new byte[] { 0x33, 0x44, 0x55 };

        byte[] body = Shipped(profile, fields);

        Assert.Equal(new byte[] { 0x11, 0x22, 0x33, 0x44, 0x55, 0, 0 }, body.Take(7).ToArray());
        Assert.Equal(32 - 4, body.Length);
    }

    /// <summary>SDL's PS5 driver reads the validity flags from the body, so a
    /// DualSense ships everything after the report ID.</summary>
    [Fact]
    public void ASharedDualSense_ShipsTheValidityFlagsFirst()
    {
        var profile = HMaestroProfileCatalog.GetProfileById("dualsense");
        var fields = Ds5EffectSynthesizer.BuildFields(new DeviceSlotConfig());
        byte[] body = Shipped(profile, fields);
        Assert.Equal((byte)fields["validFlag0"], body[0]);
        Assert.Equal((byte)fields["validFlag1"], body[1]);
    }

    /// <summary>A DualSense's report carries the master volume in its speaker
    /// byte and the owner replays it. PadForge drives a DualShock 4's speaker
    /// at one level on either link, so its macro sounds carry the master in
    /// their samples, on a peer's copy as on a local pad.</summary>
    [Theory]
    [InlineData(0x09CC, false)]
    [InlineData(0x05C4, false)]
    [InlineData(0x0BA0, false)]
    [InlineData(0x0CE6, true)]
    [InlineData(0x0DF2, true)]
    public void APeersMacroSounds_CarryTheMaster_WhereTheReportDoesNot(int productId, bool sinkOwnsMaster)
    {
        var id = Guid.NewGuid();
        SettingsManager.UserDevices.Items.Add(new UserDevice
        {
            InstanceGuid = id, VendorId = 0x054C, ProdId = (ushort)productId, IsOnline = true, IsEnabled = true,
            DevicePath = "peer://shared-" + id,
        });
        SettingsManager.UserSettings.Items.Add(new UserSetting { InstanceGuid = id, MapTo = 14, IsEnabled = true });
        AudioPassthroughService.SlotWantsMacroAudioProvider = slot => slot == 14;

        Reconcile(worker);

        var mixers = AudioPassthroughService.GetSlotSinkMixers(14, out bool pending);
        Assert.False(pending);
        Assert.Equal(sinkOwnsMaster, Assert.Single(mixers).OwnsMaster);
    }

    static RemotePeerDeviceInfo Listed(string id, string firmware, string revision) => new()
    {
        Slot = 0,
        Online = true,
        PeerLocalDeviceId = id,
        Name = "pad",
        VendorId = 0x054C,
        ProductId = 0x0CE6,
        NumAxes = 6,
        NumButtons = 22,
        RawButtonCount = 22,
        InputDeviceType = InputDeviceType.Gamepad,
        FirmwareText = firmware,
        ProductRevision = revision,
    };

    static int TailStart(IEnumerable<RemotePeerDeviceInfo> list) => LinkConnection.EncodeDeviceList(list.Select(d =>
    {
        var bare = d.CloneForSlot(d.Slot);
        bare.FirmwareText = bare.ProductRevision = null;
        return bare;
    }).ToList(), "").Length;

    /// <summary>The Device Dossier's FIRMWARE and REV cross as SERIAL and SDL
    /// GUID do. An older owner stops before the tail, and a malformed record
    /// costs the tail alone.</summary>
    [Fact]
    public void TheDossiersFirmwareAndRevCrossTheWire()
    {
        var list = new[] { Listed("a", "1.2.3.4", "0100"), Listed("b", null, null), Listed("c", "", "0200") };
        byte[] payload = LinkConnection.EncodeDeviceList(list, "");
        var decoded = LinkConnection.DecodeDeviceList(payload);
        Assert.Equal("1.2.3.4", decoded[0].FirmwareText);
        Assert.Equal("0100", decoded[0].ProductRevision);
        Assert.Null(decoded[1].FirmwareText);
        Assert.Null(decoded[1].ProductRevision);
        Assert.Equal("", decoded[2].FirmwareText);
        Assert.Equal("0200", decoded[2].ProductRevision);

        int start = TailStart(list);
        Assert.Equal(0xEF, payload[start]);
        var older = LinkConnection.DecodeDeviceList(payload.Take(start).ToArray());
        Assert.Equal(3, older.Count);
        Assert.All(older, d => Assert.Null(d.FirmwareText));

        var broken = (byte[])payload.Clone();
        broken[start + 2] = 9;
        var refused = LinkConnection.DecodeDeviceList(broken);
        Assert.Equal(3, refused.Count);
        Assert.All(refused, d => { Assert.Null(d.FirmwareText); Assert.Null(d.ProductRevision); });

        var none = new[] { Listed("b", null, null) };
        Assert.Equal(TailStart(none), LinkConnection.EncodeDeviceList(none, "").Length);

        // A text past the limit goes as none, and the row's other text stays.
        var longText = new[] { Listed("a", new string('1', 65), "0100") };
        var trimmed = LinkConnection.DecodeDeviceList(LinkConnection.EncodeDeviceList(longText, ""));
        Assert.Equal("", trimmed[0].FirmwareText);
        Assert.Equal("0100", trimmed[0].ProductRevision);
    }

    /// <summary>The tail never pushes a list past what an old peer reads, and a
    /// full list's publication keeps it.</summary>
    [Fact]
    public void TheDossierTail_RidesOnlyWhileTheListFits()
    {
        var list = new[] { Listed("a", "1.2.3.4", "0100") };
        int bare = TailStart(list);
        Assert.Null(LinkConnection.DecodeDeviceList(LinkConnection.EncodeDeviceList(list, "", bare + 1))[0].FirmwareText);
        Assert.Equal("1.2.3.4", LinkConnection.DecodeDeviceList(LinkConnection.EncodeDeviceList(list, "", LinkConnection.MaxListPayload))[0].FirmwareText);
    }

    /// <summary>A peer's row reads the dossier's lines from its owner, and a
    /// refresh of the list reaches them.</summary>
    [Fact]
    public void APeersRow_ShowsItsOwnersFirmwareAndRev()
    {
        var info = Listed("a", "1.2.3.4", "0100");
        info.PeerFingerprintHex = "ab";
        using var copy = new RemotePeerDevice(info);
        var row = new UserDevice { Device = copy };
        Assert.Equal("1.2.3.4", PadForge.Services.InputService.DossierFirmware(row));
        Assert.Equal("0100", PadForge.Services.InputService.DossierRevision(row));
        info.FirmwareText = "1.2.3.5";
        Assert.Equal("1.2.3.5", PadForge.Services.InputService.DossierFirmware(row));
        info.FirmwareText = null;
        info.ProductRevision = null;
        Assert.Equal("", PadForge.Services.InputService.DossierFirmware(row));
        Assert.Equal("", PadForge.Services.InputService.DossierRevision(row));
    }

    /// <summary>The owner fills both on every list it publishes, and the
    /// peer's reconcile takes them for a row it already has, or a version the
    /// owner learned after open would never reach the copy.</summary>
    [Fact]
    public void TheOwnerPublishesFirmwareAndRev_AndTheReconcileTakesThem()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "PadForge.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        string expose = File.ReadAllText(Path.Combine(dir.FullName, "PadForge.App", "Services", "InputService.cs"));
        Assert.Contains("FirmwareText = DossierFirmware(ud),", expose);
        Assert.Contains("ProductRevision = DossierRevision(ud),", expose);
        string reconcile = File.ReadAllText(Path.Combine(dir.FullName, "PadForge.Engine", "RemoteLink", "LinkServer.cs"));
        Assert.Contains("existing.Info.FirmwareText = info.FirmwareText;", reconcile);
        Assert.Contains("existing.Info.ProductRevision = info.ProductRevision;", reconcile);
    }

    /// <summary>The owner says which link a DualShock 4 is on. No answer, from
    /// an older owner or for another device, reads as null.</summary>
    [Fact]
    public void ADs4sLink_CrossesTheWire()
    {
        var bluetooth = Listed("a", null, null); bluetooth.OnBluetooth = true;
        var wired = Listed("b", null, null); wired.OnBluetooth = false;
        var list = new[] { bluetooth, wired, Listed("c", null, null) };
        byte[] payload = LinkConnection.EncodeDeviceList(list, "");
        var decoded = LinkConnection.DecodeDeviceList(payload);
        Assert.True(decoded[0].OnBluetooth);
        Assert.False(decoded[1].OnBluetooth);
        Assert.Null(decoded[2].OnBluetooth);

        var bare = list.Select(d => { var c = d.CloneForSlot(d.Slot); c.OnBluetooth = null; return c; }).ToList();
        int start = LinkConnection.EncodeDeviceList(bare, "").Length;
        var older = LinkConnection.DecodeDeviceList(payload.Take(start).ToArray());
        Assert.All(older, d => Assert.Null(d.OnBluetooth));

        var broken = (byte[])payload.Clone();
        broken[start + 3] = 9;
        Assert.All(LinkConnection.DecodeDeviceList(broken), d => Assert.Null(d.OnBluetooth));

        Assert.True(AudioPassthroughService.Ds4LinkToShare(new UserDevice
            { VendorId = 0x054C, ProdId = 0x05C4, DevicePath = @"\\?\HID#{00001124-0000-1000-8000-00805f9b34fb}_VID&0002054c_PID&05c4" }));
        Assert.False(AudioPassthroughService.Ds4LinkToShare(new UserDevice
            { VendorId = 0x054C, ProdId = 0x05C4, DevicePath = @"\\?\HID#VID_054C&PID_05C4" }));
        Assert.Null(AudioPassthroughService.Ds4LinkToShare(new UserDevice
            { VendorId = 0x054C, ProdId = 0x0CE6, DevicePath = @"\\?\HID#VID_054C&PID_0CE6" }));
    }

    /// <summary>The rows a peer's copy shows follow its owner's link. A first
    /// model has audio on Bluetooth alone, and a second model on both. An owner
    /// that says nothing leaves each row as a peer:// path always read.</summary>
    [Theory]
    [InlineData(0x05C4, true, true)]
    [InlineData(0x05C4, false, false)]
    [InlineData(0x05C4, null, false)]
    [InlineData(0x09CC, true, true)]
    [InlineData(0x09CC, false, true)]
    [InlineData(0x09CC, null, true)]
    public void APeersDs4_ShowsTheAudioRowsItsLinkAllows(int productId, bool? onBluetooth, bool shown)
    {
        var id = Guid.NewGuid();
        var info = Listed("a", null, null);
        info.ProductId = (ushort)productId;
        info.OnBluetooth = onBluetooth;
        info.PeerFingerprintHex = "ab";
        using var copy = new RemotePeerDevice(info);
        SettingsManager.UserDevices.Items.Add(new UserDevice
        {
            InstanceGuid = id, VendorId = 0x054C, ProdId = (ushort)productId, IsOnline = true, IsEnabled = true,
            DevicePath = "peer://shared-" + id, Device = copy,
        });
        var vm = new PadViewModel(0) { SelectedMappedDevice = new PadViewModel.MappedDeviceInfo { InstanceGuid = id } };
        Assert.Equal(shown, vm.SelectedDeviceHasSpeaker);
        Assert.Equal(!shown, vm.SelectedDeviceHasNoSpeaker);
        Assert.Equal(shown, vm.SelectedDeviceHasDspChain);
    }

    /// <summary>Sound shipped to a first model on the owner's cable plays
    /// nowhere, so the copy takes no sink and a macro's sound stays on this PC,
    /// as it does for the pad on its own PC. An owner that says nothing, or a
    /// link with audio, keeps the sink.</summary>
    [Theory]
    [InlineData(0x05C4, false, false)]
    [InlineData(0x05C4, null, true)]
    [InlineData(0x05C4, true, true)]
    [InlineData(0x09CC, false, true)]
    public void APeersDs4_TakesASink_OnlyWhereItsLinkHasAudio(int productId, bool? onBluetooth, bool sink)
    {
        var id = Guid.NewGuid();
        var info = Listed("a", null, null);
        info.ProductId = (ushort)productId;
        info.OnBluetooth = onBluetooth;
        info.PeerFingerprintHex = "ab";
        using var copy = new RemotePeerDevice(info);
        SettingsManager.UserDevices.Items.Add(new UserDevice
        {
            InstanceGuid = id, VendorId = 0x054C, ProdId = (ushort)productId, IsOnline = true, IsEnabled = true,
            DevicePath = "peer://shared-" + id, Device = copy,
        });
        SettingsManager.UserSettings.Items.Add(new UserSetting { InstanceGuid = id, MapTo = 14, IsEnabled = true });
        AudioPassthroughService.SlotWantsMacroAudioProvider = slot => slot == 14;
        Reconcile(worker);
        Assert.Equal(sink, sinks.Contains(id));
        var mixers = AudioPassthroughService.GetSlotSinkMixers(14, out bool pending);
        Assert.Equal(sink ? 1 : 0, mixers.Count);
        Assert.False(pending);
    }

    static System.Collections.IDictionary Marks =>
        (System.Collections.IDictionary)typeof(UserEffectsDispatcher).GetField("s_peerAudioMarks", Static)!.GetValue(null)!;

    /// <summary>A DualSense on the owner's PC with what a peer's frames left on
    /// it, reports landing in a temp file.</summary>
    sealed class SharedDualSense
    {
        public readonly Guid Id = Guid.NewGuid();
        public readonly string File;
        /// <summary>The profile of the pad's connection.</summary>
        public readonly HMProfile Wire;
        public readonly ushort Pid;
        /// <summary>Where the pre-gain sits (PlayStationEffectWriter.ApplyAudioControl2).</summary>
        public readonly int Control2At;
        public SharedDualSense(string dir, int slot = -1, bool usb = false, bool edge = false)
        {
            Pid = edge ? (ushort)0x0DF2 : (ushort)0x0CE6;
            Wire = HMaestroProfileCatalog.GetProfileById(edge
                ? (usb ? "dualsense-edge" : "dualsense-edge-bt")
                : (usb ? "dualsense" : "dualsense-bt-full"));
            Control2At = usb ? 38 : 40;
            File = Path.Combine(dir, (usb ? "USB-" : "BTHENUM-") + Id.ToString("N") + ".bin");
            System.IO.File.WriteAllBytes(File, []);
            SettingsManager.UserDevices.Items.Add(new UserDevice
            {
                InstanceGuid = Id, VendorId = 0x054C, ProdId = Pid, IsOnline = true, IsEnabled = true, DevicePath = File,
            });
            if (slot >= 0) SettingsManager.UserSettings.Items.Add(new UserSetting { InstanceGuid = Id, MapTo = slot });
        }
        public byte[] Read() => ReadShared(File);
        /// <summary>A frame that asserts only some of these.</summary>
        public void PeerAssertsOnly(string peer, int? path = null, int? percent = null, byte? control2 = null, byte? speaker = null)
        {
            var fields = Ds5EffectSynthesizer.BuildFields(new DeviceSlotConfig(),
                audioOutputPath: path ?? 1, assertAudioControl: path != null,
                headphoneVolumePercent: percent ?? 100, assertHeadphoneVolume: percent != null);
            Assert.True(RemoteLinkOutputRouter.ClaimOutput(File, peer));
            byte[] body = Shipped(HMaestroProfileCatalog.GetProfileById("dualsense"), fields);
            if (control2 is byte value)
            {
                body[1] |= 0x80;
                body[37] = value;
            }
            if (speaker is byte volume)
            {
                body[0] |= 0x20;
                body[5] = volume;
            }
            UserEffectsDispatcher.NotePeerEffectBody(Id, File, Pid, body, peer);
        }
        public void PeerAsserts(string peer, int path, bool lowered)
        {
            var fields = Ds5EffectSynthesizer.BuildFields(new DeviceSlotConfig(),
                audioOutputPath: path, assertAudioControl: true,
                headphoneVolumePercent: lowered ? 10 : 100, assertHeadphoneVolume: true);
            Assert.True(RemoteLinkOutputRouter.ClaimOutput(File, peer));
            UserEffectsDispatcher.NotePeerEffectBody(Id, File, Pid,
                Shipped(HMaestroProfileCatalog.GetProfileById("dualsense"), fields), peer);
        }
    }

    static int FieldOffset(HMProfile profile, string field)
    {
        byte[] with = HMOutputEncoder.Encode(profile, new Dictionary<string, object> { [field] = (byte)0x35 });
        byte[] without = HMOutputEncoder.Encode(profile, new Dictionary<string, object>());
        for (int i = 0; i < with.Length - 4; i++)
            if (with[i] != without[i]) return i;
        throw new InvalidOperationException(field);
    }

    void WithPeers(HashSet<string> connected, Action body)
    {
        var saved = RemoteLinkOutputRouter.IsPeerConnected;
        RemoteLinkOutputRouter.IsPeerConnected = connected.Contains;
        Routed.Clear();
        Marks.Clear();
        try { body(); }
        finally
        {
            RemoteLinkOutputRouter.IsPeerConnected = saved;
            Routed.Clear();
            Marks.Clear();
            typeof(UserEffectsDispatcher).GetField("s_headphoneLowered", Static)!.GetValue(null)!
                .GetType().GetMethod("Clear")!.Invoke(typeof(UserEffectsDispatcher).GetField("s_headphoneLowered", Static)!.GetValue(null), null);
        }
    }

    /// <summary>A peer that leaves with nothing else holding the pad: the owner
    /// writes the headset path, then full volume.</summary>
    [Fact]
    public void APeerLeaving_PutsBackWhatItLeft_WithNothingElseHoldingThePad()
    {
        var connected = new HashSet<string> { "a" };
        WithPeers(connected, () =>
        {
            var pad = new SharedDualSense(dir);
            pad.PeerAsserts("a", 4, lowered: true);
            UserEffectsDispatcher.LetGoOfPeerMarks();
            Assert.Empty(pad.Read());

            connected.Remove("a");
            RemoteLinkOutputRouter.ReleasePeer("a");
            UserEffectsDispatcher.LetGoOfPeerMarks();

            Assert.False(Routed.ContainsKey(pad.Id));
            Assert.Empty(Marks);
            byte[] last = pad.Read();
            Assert.Equal((byte)0x10, last[3]);
            Assert.Equal((byte)0x7F, last[FieldOffset(pad.Wire, "headphoneVolume")]);
        });
    }

    /// <summary>With the path alone, the frame the owner writes is the headset
    /// path.</summary>
    [Fact]
    public void APeerLeaving_GivesTheHeadsetBack()
    {
        var connected = new HashSet<string>();
        WithPeers(connected, () =>
        {
            var pad = new SharedDualSense(dir);
            connected.Add("a");
            pad.PeerAsserts("a", 4, lowered: false);
            connected.Clear();
            RemoteLinkOutputRouter.ReleasePeer("a");
            UserEffectsDispatcher.LetGoOfPeerMarks();
            byte[] last = pad.Read();
            Assert.Equal((byte)0x80, last[3]);
            Assert.Equal(0, last[FieldOffset(pad.Wire, "audioControlFlags")] & 0x30);
            Assert.False(Routed.ContainsKey(pad.Id));
        });
    }

    /// <summary>A peer that holds the pad and has asserted nothing does not
    /// own the path through its frames, which carry none. What the one that left
    /// put there goes, past that peer's lease, and its leaving owes nothing
    /// more.</summary>
    [Fact]
    public void APeerLeaving_GivesTheHeadsetBack_PastAPeerThatAssertedNothing()
    {
        var connected = new HashSet<string> { "a", "b" };
        WithPeers(connected, () =>
        {
            var pad = new SharedDualSense(dir);
            pad.PeerAsserts("a", 4, lowered: true);
            Assert.True(RemoteLinkOutputRouter.ClaimOutput(pad.File, "b"));

            connected.Remove("a");
            RemoteLinkOutputRouter.ReleasePeer("a");
            UserEffectsDispatcher.LetGoOfPeerMarks();
            byte[] last = pad.Read();
            Assert.Equal(0, last[FieldOffset(pad.Wire, "audioControlFlags")] & 0x30);
            Assert.Equal((byte)0x7F, last[FieldOffset(pad.Wire, "headphoneVolume")]);
            Assert.Empty(Marks);

            ClearShared(pad.File);
            connected.Remove("b");
            RemoteLinkOutputRouter.ReleasePeer("b");
            UserEffectsDispatcher.LetGoOfPeerMarks();
            Assert.Empty(pad.Read());
        });
    }

    /// <summary>Two peers play to one pad and the second writes over the first.
    /// When the second leaves, the pad takes the path and level the first last
    /// asserted, past its lease when it is still sending, since its frames assert
    /// them only when its setting changes. When the first leaves too, the pad
    /// goes back to the headset at full.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void APeerLeaving_PutsBackWhatTheConnectedPeerAsserted(bool theOtherWroteLast)
    {
        var connected = new HashSet<string> { "a", "b" };
        WithPeers(connected, () =>
        {
            var pad = new SharedDualSense(dir);
            pad.PeerAsserts("a", 4, lowered: true);
            pad.PeerAsserts("b", 2, lowered: false);
            if (theOtherWroteLast) Assert.True(RemoteLinkOutputRouter.ClaimOutput(pad.File, "a"));

            connected.Remove("b");
            RemoteLinkOutputRouter.ReleasePeer("b");
            UserEffectsDispatcher.LetGoOfPeerMarks();

            byte[] last = pad.Read();
            Assert.NotEmpty(last);
            Assert.Equal(0x30, last[FieldOffset(pad.Wire, "audioControlFlags")] & 0x30);
            Assert.Equal((byte)0x46, last[FieldOffset(pad.Wire, "headphoneVolume")]);
            Assert.Single(Marks);
            var resolved = Audio.GetMethod("ResolvedPathOf", Static)!;
            Assert.Equal(4, (int)resolved.Invoke(null, new object[] { 255, pad.Id })!);

            connected.Remove("a");
            RemoteLinkOutputRouter.ReleasePeer("a");
            UserEffectsDispatcher.LetGoOfPeerMarks();
            last = pad.Read();
            Assert.Equal(0, last[FieldOffset(pad.Wire, "audioControlFlags")] & 0x30);
            Assert.Equal((byte)0x7F, last[FieldOffset(pad.Wire, "headphoneVolume")]);
            Assert.Empty(Marks);
            Assert.Equal(0, (int)resolved.Invoke(null, new object[] { 255, pad.Id })!);
        });
    }

    /// <summary>The newest word of the peers still connected stands, whichever
    /// of them spoke first.</summary>
    [Theory]
    [InlineData(4, 3, 0x20)]
    [InlineData(3, 4, 0x30)]
    public void APeerLeaving_PutsBackTheNewestWordOfThoseConnected(int first, int second, int flags)
    {
        var connected = new HashSet<string> { "a", "c", "b" };
        WithPeers(connected, () =>
        {
            var pad = new SharedDualSense(dir);
            pad.PeerAsserts("a", first, lowered: false);
            pad.PeerAsserts("c", second, lowered: false);
            pad.PeerAsserts("b", 2, lowered: false);

            connected.Remove("b");
            RemoteLinkOutputRouter.ReleasePeer("b");
            UserEffectsDispatcher.LetGoOfPeerMarks();
            Assert.Equal(flags, pad.Read()[FieldOffset(pad.Wire, "audioControlFlags")] & 0x30);
        });
    }

    /// <summary>A peer says its level, its path, its pre-gain and its Speaker
    /// Volume in frames of their own, and the pad gets all of them back, on every
    /// wire a DualSense has. It does so once: the word stands after that, and
    /// another pass writes nothing.</summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void APeerLeaving_PutsBackAllTheConnectedPeerSaid_Once(bool usb, bool edge)
    {
        var connected = new HashSet<string> { "a", "b" };
        WithPeers(connected, () =>
        {
            var pad = new SharedDualSense(dir, usb: usb, edge: edge);
            pad.PeerAssertsOnly("a", percent: 10);
            pad.PeerAssertsOnly("a", path: 4);
            // A sink's frames say these two alone.
            pad.PeerAssertsOnly("a", control2: 3, speaker: 0x50);
            pad.PeerAsserts("b", 2, lowered: false);
            pad.PeerAssertsOnly("b", control2: 0, speaker: 0);

            connected.Remove("b");
            RemoteLinkOutputRouter.ReleasePeer("b");
            UserEffectsDispatcher.LetGoOfPeerMarks();
            byte[] last = pad.Read();
            Assert.Equal(0x30, last[FieldOffset(pad.Wire, "audioControlFlags")] & 0x30);
            Assert.Equal((byte)0x46, last[FieldOffset(pad.Wire, "headphoneVolume")]);
            Assert.Equal((byte)0x50, last[FieldOffset(pad.Wire, "speakerVolume")]);
            Assert.Equal((byte)3, last[pad.Control2At]);
            int valid = FieldOffset(pad.Wire, "validFlag0");
            Assert.Equal(0xB0, last[valid] & 0xB0);
            Assert.Equal(0x80, last[FieldOffset(pad.Wire, "validFlag1")] & 0x80);

            ClearShared(pad.File);
            UserEffectsDispatcher.LetGoOfPeerMarks();
            Assert.Empty(pad.Read());
        });
    }

    /// <summary>A frame that says the level alone takes the level, and clears the
    /// mark when that is full. The peer that asked for it leaving gives the pad back the
    /// lower level the one still connected asked for, with no other pad marked and
    /// with one.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void APeerLeaving_PutsBackALevelItsFrameRaised(bool anotherPadMarked)
    {
        var connected = new HashSet<string> { "a", "b" };
        WithPeers(connected, () =>
        {
            var pad = new SharedDualSense(dir);
            if (anotherPadMarked) new SharedDualSense(dir).PeerAsserts("a", 4, lowered: true);
            pad.PeerAssertsOnly("a", percent: 10);
            pad.PeerAssertsOnly("b", percent: 100);
            Assert.Equal(anotherPadMarked ? 1 : 0, Marks.Count);

            connected.Remove("b");
            RemoteLinkOutputRouter.ReleasePeer("b");
            UserEffectsDispatcher.NotePeerDropped("b");
            Assert.True(SpinWait.SpinUntil(() => pad.Read().Length > 0, 10000));
            Assert.Equal((byte)0x46, pad.Read()[FieldOffset(pad.Wire, "headphoneVolume")]);
            Assert.True(Marks.Contains(pad.Id));
        });
    }

    /// <summary>A path outlives no peer that set it because another peer said
    /// something else: with the one that set it gone, the path goes home and the
    /// level the other said stays as it is.</summary>
    [Fact]
    public void APeerLeaving_TakesItsPathWithIt_FromAPeerThatSaidOnlyTheLevel()
    {
        var connected = new HashSet<string> { "a", "b" };
        WithPeers(connected, () =>
        {
            var pad = new SharedDualSense(dir);
            pad.PeerAssertsOnly("a", path: 4);
            pad.PeerAssertsOnly("b", percent: 50);

            connected.Remove("a");
            RemoteLinkOutputRouter.ReleasePeer("a");
            UserEffectsDispatcher.LetGoOfPeerMarks();
            byte[] last = pad.Read();
            Assert.Equal(0, last[FieldOffset(pad.Wire, "audioControlFlags")] & 0x30);
            Assert.Equal(0x80, last[3] & 0x80);
            // The level's valid flag stays clear.
            Assert.Equal(0, last[3] & 0x10);
        });
    }

    /// <summary>Each field goes by its own newest word. One peer holds the path and
    /// another the level, and the peer that wrote over the path leaving moves the
    /// path alone.</summary>
    [Fact]
    public void APeerLeaving_PutsBackEachFieldFromItsOwnPeer()
    {
        var connected = new HashSet<string> { "a", "c", "b" };
        WithPeers(connected, () =>
        {
            var pad = new SharedDualSense(dir);
            pad.PeerAssertsOnly("a", path: 4);
            pad.PeerAssertsOnly("b", path: 2);
            pad.PeerAssertsOnly("c", percent: 10);

            connected.Remove("b");
            RemoteLinkOutputRouter.ReleasePeer("b");
            UserEffectsDispatcher.LetGoOfPeerMarks();
            byte[] last = pad.Read();
            Assert.Equal(0x30, last[FieldOffset(pad.Wire, "audioControlFlags")] & 0x30);
            Assert.Equal(0, last[3] & 0x10);
            Assert.Single(Marks);
        });
    }

    /// <summary>A slot of this PC writing to the pad with the peers quiet keeps it.
    /// A peer that is still connected and said a path long ago does not take the pad
    /// from that slot, and the slot asserts its route again.</summary>
    [Fact]
    public void APeerLeaving_WritesNoQuietPeersWord_OverASlotThatHoldsThePad()
    {
        var connected = new HashSet<string> { "a", "b" };
        WithPeers(connected, () =>
        {
            var pad = new SharedDualSense(dir, slot: 15);
            var saved = UserEffectsDispatcher.SlotPerDeviceConfigsProvider;
            UserEffectsDispatcher.SlotPerDeviceConfigsProvider = null;
            var cfg = new DeviceSlotConfig { LightbarMode = LightbarMode.Off, AudioOutputPath = AudioOutputPath.StereoHeadset };
            using var dispatcher = new UserEffectsDispatcher(15, cfg, startTimer: false);
            try
            {
                pad.PeerAsserts("b", 4, lowered: false);
                pad.PeerAsserts("a", 2, lowered: false);
                RemoteLinkOutputRouter.ReleaseDevice(pad.File);
                RemoteLinkOutputRouter.NoteLocalWrite(pad.File);
                ClearShared(pad.File);

                connected.Remove("a");
                RemoteLinkOutputRouter.ReleasePeer("a");
                UserEffectsDispatcher.LetGoOfPeerMarks();
                byte[] last = pad.Read();
                Assert.True(last.Length == 0 || (last[FieldOffset(pad.Wire, "audioControlFlags")] & 0x30) != 0x30,
                    "the quiet peer's speaker went out over the slot");
                Assert.Empty(Marks);
            }
            finally
            {
                UserEffectsDispatcher.SlotPerDeviceConfigsProvider = saved;
            }
        });
    }

    /// <summary>A put-back drops the mark, and the slot that maps the pad asserts
    /// its route again once it may: it wrote that route, and the pad no longer shows
    /// it.</summary>
    [Fact]
    public void APutBack_HasTheSlotsAssertTheirRouteAgain()
    {
        var connected = new HashSet<string> { "a", "b" };
        WithPeers(connected, () =>
        {
            var pad = new SharedDualSense(dir, slot: 15);
            var saved = UserEffectsDispatcher.SlotPerDeviceConfigsProvider;
            UserEffectsDispatcher.SlotPerDeviceConfigsProvider = null;
            var cfg = new DeviceSlotConfig { LightbarMode = LightbarMode.Off, AudioOutputPath = AudioOutputPath.StereoHeadset };
            using var dispatcher = new UserEffectsDispatcher(15, cfg, startTimer: false);
            try
            {
                dispatcher.ApplyOnce();
                const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
                var prior = (Dictionary<Guid, int>)typeof(UserEffectsDispatcher).GetField("_prevAudioOutputPath", Private)!.GetValue(dispatcher)!;
                Assert.True(prior.ContainsKey(pad.Id));

                pad.PeerAsserts("a", 4, lowered: true);
                Assert.True(RemoteLinkOutputRouter.ClaimOutput(pad.File, "b"));
                connected.Remove("a");
                RemoteLinkOutputRouter.ReleasePeer("a");
                UserEffectsDispatcher.LetGoOfPeerMarks();
                Assert.Empty(Marks);
                Assert.False(prior.ContainsKey(pad.Id));
            }
            finally
            {
                UserEffectsDispatcher.SlotPerDeviceConfigsProvider = saved;
            }
        });
    }

    /// <summary>A let-go that comes after the release writes nothing: the pads are
    /// the release's by then, and a frame past its guard can have said something
    /// since.</summary>
    [Fact]
    public void APeerLeaving_WritesNothing_AfterTheRelease()
    {
        var connected = new HashSet<string> { "a", "b" };
        WithPeers(connected, () =>
        {
            var pad = new SharedDualSense(dir);
            UserEffectsDispatcher.ReleaseAudioRoutes();
            try
            {
                pad.PeerAsserts("a", 4, lowered: false);
                pad.PeerAsserts("b", 2, lowered: false);
                ClearShared(pad.File);

                connected.Remove("b");
                RemoteLinkOutputRouter.ReleasePeer("b");
                UserEffectsDispatcher.LetGoOfPeerMarks();
                Assert.Empty(pad.Read());
            }
            finally
            {
                UserEffectsDispatcher.ResumeAudioRoutes();
            }
        });
    }

    /// <summary>A frame past its guard marks a pad after the release cleared what
    /// peers said. The mark is all there is, and it moves when its peer is gone.</summary>
    [Fact]
    public void AMarkWithNoWords_MovesOnceItsPeerIsGone()
    {
        var connected = new HashSet<string> { "a", "b" };
        WithPeers(connected, () =>
        {
            var pad = new SharedDualSense(dir);
            UserEffectsDispatcher.ReleaseAudioRoutes();
            try
            {
                pad.PeerAsserts("a", 4, lowered: true);
                Assert.True(RemoteLinkOutputRouter.ClaimOutput(pad.File, "b"));
                connected.Remove("a");
                RemoteLinkOutputRouter.ReleasePeer("a");
                UserEffectsDispatcher.LetGoOfPeerMarks();
                Assert.Single(Marks);
            }
            finally
            {
                UserEffectsDispatcher.ResumeAudioRoutes();
            }

            connected.Remove("b");
            RemoteLinkOutputRouter.ReleasePeer("b");
            UserEffectsDispatcher.LetGoOfPeerMarks();
            Assert.Empty(Marks);
            Assert.Equal(0, pad.Read()[FieldOffset(pad.Wire, "audioControlFlags")] & 0x30);
        });
    }

    /// <summary>The last peer's path goes with it even when it left no mark: a peer
    /// that asked for the headset leaves no sink mapping by what it asked.</summary>
    [Fact]
    public void TheLastPeerLeaving_ForgetsThePathItSaid_MarkOrNoMark()
    {
        var connected = new HashSet<string> { "a" };
        WithPeers(connected, () =>
        {
            var pad = new SharedDualSense(dir);
            pad.PeerAssertsOnly("a", path: 1);
            var body = Shipped(HMaestroProfileCatalog.GetProfileById("dualsense"),
                Ds5EffectSynthesizer.BuildFields(new DeviceSlotConfig(), audioOutputPath: 1, assertAudioControl: true));
            AudioPassthroughService.NoteRemoteSonyBody(pad.Id, pad.Pid, body);
            var resolved = Audio.GetMethod("ResolvedPathOf", Static)!;
            Assert.Equal(1, (int)resolved.Invoke(null, new object[] { 255, pad.Id })!);

            connected.Clear();
            RemoteLinkOutputRouter.ReleasePeer("a");
            UserEffectsDispatcher.LetGoOfPeerMarks();
            Assert.Equal(0, (int)resolved.Invoke(null, new object[] { 255, pad.Id })!);
        });
    }

    /// <summary>A peer that left and came back has said nothing to this session,
    /// so what it said before stays gone.</summary>
    [Fact]
    public void APeerThatCameBack_HasSaidNothingYet()
    {
        var connected = new HashSet<string> { "a", "b" };
        WithPeers(connected, () =>
        {
            var pad = new SharedDualSense(dir);
            pad.PeerAsserts("a", 4, lowered: true);
            pad.PeerAsserts("b", 2, lowered: false);

            connected.Remove("a");
            RemoteLinkOutputRouter.ReleasePeer("a");
            UserEffectsDispatcher.LetGoOfPeerMarks();
            connected.Add("a");

            connected.Remove("b");
            RemoteLinkOutputRouter.ReleasePeer("b");
            UserEffectsDispatcher.LetGoOfPeerMarks();
            Assert.Equal(0, pad.Read()[FieldOffset(pad.Wire, "audioControlFlags")] & 0x30);
            Assert.Empty(Marks);
        });
    }

    /// <summary>A pad this PC stopped sharing, and a release of every route, end
    /// what peers said of it, connected or not.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WhatPeersSaid_EndsWithTheShareAndTheRelease(bool release)
    {
        var connected = new HashSet<string> { "a", "b" };
        WithPeers(connected, () =>
        {
            var pad = new SharedDualSense(dir);
            pad.PeerAsserts("a", 4, lowered: true);
            if (release)
            {
                UserEffectsDispatcher.ReleaseAudioRoutes();
                UserEffectsDispatcher.ResumeAudioRoutes();
            }
            else
            {
                UserEffectsDispatcher.NoteDeviceUnshared(pad.Id);
                Assert.True(System.Threading.SpinWait.SpinUntil(() => Marks.Count == 0, 10000));
            }

            pad.PeerAsserts("b", 2, lowered: false);
            connected.Remove("b");
            RemoteLinkOutputRouter.ReleasePeer("b");
            UserEffectsDispatcher.LetGoOfPeerMarks();
            Assert.Equal(0, pad.Read()[FieldOffset(pad.Wire, "audioControlFlags")] & 0x30);
        });
    }

    /// <summary>What a peer asserted leaves with it: the first peer is gone when
    /// the second leaves, so nothing of the first's comes back. The second left
    /// the level full, so only the path goes back.</summary>
    [Fact]
    public void APeerThatLeftEarlier_IsNotPutBack()
    {
        var connected = new HashSet<string> { "a", "b" };
        WithPeers(connected, () =>
        {
            var pad = new SharedDualSense(dir);
            pad.PeerAsserts("a", 4, lowered: true);
            pad.PeerAsserts("b", 2, lowered: false);

            connected.Remove("a");
            RemoteLinkOutputRouter.ReleasePeer("a");
            UserEffectsDispatcher.NotePeerDropped("a");
            UserEffectsDispatcher.LetGoOfPeerMarks();
            Assert.Empty(pad.Read());

            connected.Remove("b");
            RemoteLinkOutputRouter.ReleasePeer("b");
            UserEffectsDispatcher.LetGoOfPeerMarks();
            byte[] last = pad.Read();
            Assert.NotEmpty(last);
            Assert.Equal(0, last[FieldOffset(pad.Wire, "audioControlFlags")] & 0x30);
            Assert.NotEqual((byte)0x46, last[FieldOffset(pad.Wire, "headphoneVolume")]);
            Assert.Empty(Marks);
        });
    }

    /// <summary>A slot of the owner's that holds the pad writes its own path and
    /// level again, and the sweep leaves the pad to it. A peer's path falls away
    /// from the channel map.</summary>
    [Fact]
    public void APeerLeaving_LeavesAPadASlotHolds_ToThatSlot()
    {
        var connected = new HashSet<string> { "a" };
        WithPeers(connected, () =>
        {
            var pad = new SharedDualSense(dir, slot: 15);
            var cfg = new DeviceSlotConfig { LightbarMode = LightbarMode.Off, AudioOutputPath = AudioOutputPath.StereoHeadset, HeadphoneVolume = 50 };
            var saved = UserEffectsDispatcher.SlotPerDeviceConfigsProvider;
            UserEffectsDispatcher.SlotPerDeviceConfigsProvider = null;
            using var dispatcher = new UserEffectsDispatcher(15, cfg, startTimer: false);
            try
            {
                dispatcher.ApplyOnce();
                var levelAt = FieldOffset(pad.Wire, "headphoneVolume");
                byte slotLevel = pad.Read()[levelAt];
                Assert.True(slotLevel is > 0 and < 0x7F);
                // The claim burst asserts everything on every frame, so it runs
                // out here: from now on the slot asserts only what it forgot.
                const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
                ((Dictionary<Guid, int>)typeof(UserEffectsDispatcher).GetField("_audioMuteBurstLeft", Private)!
                    .GetValue(dispatcher)!)[pad.Id] = 0;
                dispatcher.ApplyOnce();
                Assert.Equal(0, pad.Read()[3] & 0x90);

                pad.PeerAsserts("a", 4, lowered: true);
                AudioPassthroughService.NoteRemoteSonyBody(pad.Id, 0x0CE6, new byte[] { 0x80, 0, 0, 0, 0, 0, 0, 0x30 });
                ClearShared(pad.File);
                connected.Clear();
                RemoteLinkOutputRouter.ReleasePeer("a");
                UserEffectsDispatcher.LetGoOfPeerMarks();

                Assert.True(SpinWait.SpinUntil(() =>
                {
                    byte[] frame = pad.Read();
                    return frame.Length > 0 && !Routed.ContainsKey(pad.Id)
                        && (frame[3] & 0x90) == 0x90 && frame[levelAt] == slotLevel
                        && (frame[FieldOffset(pad.Wire, "audioControlFlags")] & 0x30) == 0;
                }, 5000));
                var resolved = Audio.GetMethod("ResolvedPathOf", Static)!;
                Assert.Equal(0, (int)resolved.Invoke(null, new object[] { 255, pad.Id })!);
            }
            finally { UserEffectsDispatcher.SlotPerDeviceConfigsProvider = saved; }
        });
    }

    /// <summary>This PC's exit lets go of a pad a peer still holds, past the
    /// peer's lease, and a pad it stops sharing goes back too.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ThisPcLettingGo_PutsBackWhatAConnectedPeerLeft(bool exit)
    {
        var connected = new HashSet<string> { "a" };
        WithPeers(connected, () =>
        {
            var pad = new SharedDualSense(dir);
            pad.PeerAsserts("a", 4, lowered: false);
            try
            {
                if (exit) UserEffectsDispatcher.ReleaseAudioRoutes();
                else
                {
                    RemoteLinkOutputRouter.ReleaseDevice(pad.File);
                    UserEffectsDispatcher.LetGoOfPeerMarks(pad.Id);
                }
                Assert.Equal((byte)0x80, pad.Read()[3]);
                Assert.Empty(Marks);
            }
            finally { UserEffectsDispatcher.ResumeAudioRoutes(); }
        });
    }

    /// <summary>Only what the frame asserts changes the mark. A frame back on
    /// the headset at full leaves nothing to put back. A DualShock 4's body
    /// marks nothing.</summary>
    [Fact]
    public void APeersFrames_KeepTheMarkToWhatTheyAssert()
    {
        var connected = new HashSet<string> { "a" };
        WithPeers(connected, () =>
        {
            var pad = new SharedDualSense(dir);
            pad.PeerAsserts("a", 4, lowered: false);
            Assert.Single(Marks);
            // Neither bit: the mark stands.
            UserEffectsDispatcher.NotePeerEffectBody(pad.Id, pad.File, 0x0CE6, new byte[8], "a");
            Assert.Single(Marks);
            pad.PeerAsserts("a", 1, lowered: false);
            Assert.Empty(Marks);
            UserEffectsDispatcher.NotePeerEffectBody(pad.Id, pad.File, 0x09CC, new byte[] { 0x90, 0, 0, 0, 0, 0, 0, 0x30 }, "a");
            Assert.Empty(Marks);
        });
    }

    /// <summary>A connected peer that asserted the path keeps the pad past its
    /// lease, so a sweep leaves a pad listed from this PC's own frames until
    /// the peer leaves.</summary>
    [Fact]
    public void ASweep_LeavesAPadAConnectedPeersMarkHolds()
    {
        var connected = new HashSet<string> { "a" };
        WithPeers(connected, () =>
        {
            var pad = new SharedDualSense(dir);
            Routed[pad.Id] = (pad.File, pad.Wire, true);
            pad.PeerAsserts("a", 4, lowered: false);
            RemoteLinkOutputRouter.ReleaseDevice(pad.File);

            typeof(UserEffectsDispatcher).GetMethod("SweepOrphanedRoutes", Static)!.Invoke(null, null);
            Assert.Empty(pad.Read());
            Assert.True(Routed.ContainsKey(pad.Id));

            connected.Clear();
            RemoteLinkOutputRouter.ReleasePeer("a");
            UserEffectsDispatcher.LetGoOfPeerMarks();
            Assert.Equal(0, pad.Read()[FieldOffset(pad.Wire, "audioControlFlags")] & 0x30);
            Assert.False(Routed.ContainsKey(pad.Id));
            Assert.Empty(Marks);
        });
    }

    /// <summary>A frame past its claim when the peer's last session drops marks
    /// the pad after the let-go found nothing. Once that frame is applied the
    /// mark takes the let-go. A connected peer's frame leaves its mark.</summary>
    [Fact]
    public void APeersFrame_ThatMarksAfterTheLetGo_IsLetGoOnceApplied()
    {
        var connected = new HashSet<string> { "a" };
        WithPeers(connected, () =>
        {
            var pad = new SharedDualSense(dir);
            var fields = Ds5EffectSynthesizer.BuildFields(new DeviceSlotConfig(), audioOutputPath: 4, assertAudioControl: true);
            byte[] body = Shipped(HMaestroProfileCatalog.GetProfileById("dualsense"), fields);
            Assert.True(RemoteLinkOutputRouter.ClaimOutput(pad.File, "a"));

            UserEffectsDispatcher.NotePeerEffectBody(pad.Id, pad.File, 0x0CE6, body, "a");
            UserEffectsDispatcher.NotePeerFrameApplied(pad.Id, pad.File, 0x0CE6, body, "a");
            Assert.Single(Marks);
            Assert.Empty(pad.Read());

            connected.Clear();
            RemoteLinkOutputRouter.ReleasePeer("a");
            UserEffectsDispatcher.LetGoOfPeerMarks();
            Assert.Empty(Marks);
            ClearShared(pad.File);

            // The let-go ran between this frame's note and its send, and took the note
            // with it. The frame says it again when it has landed.
            UserEffectsDispatcher.NotePeerFrameApplied(pad.Id, pad.File, 0x0CE6, body, "a");
            Assert.True(SpinWait.SpinUntil(() => Marks.Count == 0 && !Routed.ContainsKey(pad.Id) && pad.Read().Length > 0, 5000));
            Assert.Equal(0, pad.Read()[FieldOffset(pad.Wire, "audioControlFlags")] & 0x30);
        });
    }

    /// <summary>The owner asks about the mark after the frame's send, so a
    /// let-go it starts lands after that frame.</summary>
    [Fact]
    public void TheOwnerAsksAboutTheMark_AfterTheFramesSend()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "PadForge.sln"))) root = root.Parent;
        Assert.NotNull(root);
        string src = File.ReadAllText(Path.Combine(root.FullName, "PadForge.App", "Services", "InputService.cs"));
        int mark = src.IndexOf("UserEffectsDispatcher.NotePeerEffectBody(", StringComparison.Ordinal);
        int send = src.IndexOf("SDL3.SDL.SDL_SendGamepadEffect(handle, effect.SonyBody,", mark, StringComparison.Ordinal);
        int asked = src.IndexOf("UserEffectsDispatcher.NotePeerFrameApplied(ud.InstanceGuid, ud.DevicePath,", StringComparison.Ordinal);
        Assert.True(mark > 0 && send > mark && asked > send);
        // A let-go between the two puts back the path before this frame's, and this
        // frame's comes last.
        int path = src.IndexOf("AudioPassthroughService.NoteRemoteSonyBody(ud.InstanceGuid,", StringComparison.Ordinal);
        Assert.True(path > mark && path < send, $"{mark} {path} {send}");
    }

    /// <summary>The owner applies each relayed body to the marks, and a peer's
    /// drop runs the let-go after the router's release.</summary>
    [Fact]
    public void TheOwnerMarksEachBody_AndLetsGoAfterTheRelease()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "PadForge.sln"))) root = root.Parent;
        Assert.NotNull(root);
        string src = File.ReadAllText(Path.Combine(root.FullName, "PadForge.App", "Services", "InputService.cs")).Replace("\r\n", "\n");
        int release = src.IndexOf("_linkServer.PeerDropped += RemoteLinkOutputRouter.ReleasePeer;", StringComparison.Ordinal);
        int letGo = src.IndexOf("_linkServer.PeerDropped += UserEffectsDispatcher.NotePeerDropped;", StringComparison.Ordinal);
        Assert.True(release > 0 && letGo > release);
        Assert.Contains("UserEffectsDispatcher.NotePeerEffectBody(ud.InstanceGuid, ud.DevicePath, (ushort)ud.ProdId,", src);
        Assert.Contains("UserEffectsDispatcher.NoteDeviceUnshared(old.ud.InstanceGuid);", src);
        Assert.Contains("OnBluetooth = AudioPassthroughService.Ds4LinkToShare(ud),", src);
        string reconcile = File.ReadAllText(Path.Combine(root.FullName, "PadForge.Engine", "RemoteLink", "LinkServer.cs"));
        Assert.Contains("existing.Info.OnBluetooth = info.OnBluetooth;", reconcile);
    }

    /// <summary>A sink on a switched-off slot outlives its dispatcher. Its end
    /// is the last thing that happens to the pad, so the worker sweeps the
    /// route when it takes the sink down.</summary>
    [Fact]
    public void TakingASinkDown_SweepsAPadNoSlotHolds()
    {
        var id = Guid.NewGuid();
        string file = Path.Combine(dir, "BTHENUM-report.bin");
        File.WriteAllBytes(file, []);
        SettingsManager.UserDevices.Items.Add(new UserDevice
        {
            InstanceGuid = id, VendorId = 0x054C, ProdId = 0x0CE6, IsOnline = true, IsEnabled = true, DevicePath = file,
        });
        SettingsManager.UserSettings.Items.Add(new UserSetting { InstanceGuid = id, MapTo = 14, IsEnabled = true });
        var routed = (System.Collections.Concurrent.ConcurrentDictionary<Guid, (string Path, HMProfile Profile, bool Ds5)>)
            typeof(UserEffectsDispatcher).GetField("s_routedOffHeadset", Static)!.GetValue(null)!;
        routed.Clear();
        try
        {
            routed[id] = (file, HMaestroProfileCatalog.GetProfileById("dualsense-bt-full"), true);
            AudioPassthroughService.SlotWantsMacroAudioProvider = slot => slot == 14;
            Reconcile(worker);
            Assert.True(sinks.Contains(id));
            AudioPassthroughService.SlotWantsMacroAudioProvider = _ => false;
            Reconcile(worker);
            Assert.False(sinks.Contains(id));
            Assert.False(routed.ContainsKey(id));
            using var report = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            Assert.True(report.Length > 0);
        }
        finally
        {
            AudioPassthroughService.TryConsumeSpeakerPathCleared(id);
            routed.Clear();
        }
    }

    static System.Collections.Concurrent.ConcurrentDictionary<Guid, (string Path, HMProfile Profile, bool Ds5)> Routed =>
        (System.Collections.Concurrent.ConcurrentDictionary<Guid, (string Path, HMProfile Profile, bool Ds5)>)
            typeof(UserEffectsDispatcher).GetField("s_routedOffHeadset", Static)!.GetValue(null)!;

    static System.Collections.Concurrent.ConcurrentDictionary<Guid, long> Demand =>
        (System.Collections.Concurrent.ConcurrentDictionary<Guid, long>)Audio.GetField("_remoteAudioDemand", Static)!.GetValue(null)!;

    static void ClearShared(string file)
    {
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
        stream.SetLength(0);
    }

    static byte[] ReadShared(string file)
    {
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var bytes = new byte[stream.Length];
        stream.ReadExactly(bytes);
        return bytes;
    }

    Guid OwnersUsbDs4(out string file)
    {
        var id = Guid.NewGuid();
        file = Path.Combine(dir, "report-" + id.ToString("N") + ".bin");
        File.WriteAllBytes(file, []);
        SettingsManager.UserDevices.Items.Add(new UserDevice
        {
            InstanceGuid = id, VendorId = 0x054C, ProdId = 0x09CC, IsOnline = true, IsEnabled = true, DevicePath = file,
        });
        return id;
    }

    /// <summary>A peer plays to a DualShock 4 on the owner's cable, on no slot
    /// of the owner's. The speaker's volume goes on and the route moves to the
    /// speaker while it plays, as NFSU2-SplitScreen's UsbRoute does it, once,
    /// past the peer's lease, and the headset comes back when the sound
    /// ends.</summary>
    [Fact]
    public void AnOwnersUsbDs4_TakesTheSpeaker_WhileAPeerPlays_AndGivesTheHeadsetBack()
    {
        var id = OwnersUsbDs4(out string file);
        var reports = new List<byte[]>();
        Routed.Clear();
        PlayStationEffectWriter.RouteReportSeam = (_, report) => { reports.Add(report); return true; };
        try
        {
            Assert.True(RemoteLinkOutputRouter.ClaimOutput(file));
            Demand[id] = Environment.TickCount64;
            Reconcile(worker);
            Assert.True(sinks.Contains(id));

            Assert.Equal(new byte[] { 0xE0, 0x00, 0x03 }, Assert.Single(reports));
            byte[] volume = ReadShared(file);
            Assert.Equal(32, volume.Length);
            var expected = new byte[32];
            expected[0] = 0x05; expected[1] = 0x80; expected[22] = 0x4F; expected[23] = 0x85;
            Assert.Equal(expected, volume);
            Assert.True(Routed.ContainsKey(id));

            Demand[id] = Environment.TickCount64;
            Reconcile(worker);
            Assert.Single(reports);

            Demand.Clear();
            Reconcile(worker);
            Assert.False(sinks.Contains(id));
            Assert.Equal(2, reports.Count);
            Assert.Equal(new byte[] { 0xE0, 0x00, 0x00 }, reports[1]);
            Assert.False(Routed.ContainsKey(id));
        }
        finally
        {
            PlayStationEffectWriter.RouteReportSeam = null;
            RemoteLinkOutputRouter.ReleaseDevice(file);
            AudioPassthroughService.TryConsumeSpeakerPathCleared(id);
            Demand.Clear();
            Routed.Clear();
        }
    }

    /// <summary>A pad on one of the owner's slots with a peer's sound on it
    /// too has two entries in a pass, the slot's first. The route stays where
    /// it is from pass to pass, and the headset comes back once when the sound
    /// ends and the slot's sink stays.</summary>
    [Fact]
    public void AnOwnersUsbDs4OnASlot_KeepsThePeersRoute_PassAfterPass()
    {
        var id = OwnersUsbDs4(out string file);
        SettingsManager.UserSettings.Items.Add(new UserSetting { InstanceGuid = id, MapTo = 14, IsEnabled = true });
        AudioPassthroughService.SlotWantsMacroAudioProvider = slot => slot == 14;
        var reports = new List<byte[]>();
        Routed.Clear();
        PlayStationEffectWriter.RouteReportSeam = (_, report) => { reports.Add(report); return true; };
        try
        {
            Demand[id] = Environment.TickCount64;
            Reconcile(worker);
            Assert.Equal(new byte[] { 0xE0, 0x00, 0x03 }, Assert.Single(reports));

            Demand[id] = Environment.TickCount64;
            Reconcile(worker);
            Assert.Single(reports);

            Demand.Clear();
            Reconcile(worker);
            Assert.True(sinks.Contains(id));
            Assert.Equal(2, reports.Count);
            Assert.Equal(new byte[] { 0xE0, 0x00, 0x00 }, reports[1]);

            Reconcile(worker);
            Assert.Equal(2, reports.Count);
        }
        finally
        {
            PlayStationEffectWriter.RouteReportSeam = null;
            AudioPassthroughService.TryConsumeSpeakerPathCleared(id);
            Demand.Clear();
            Routed.Clear();
        }
    }

    /// <summary>A dispatcher on slot 15 for a DualShock 4 on the owner's cable,
    /// on a path off the headset, and what it does with the pad's first route
    /// when <paramref name="insideTheWrite"/> runs inside that route's
    /// write.</summary>
    void FirstRouteOfADispatcher(Guid id, Action insideTheWrite, List<byte[]> reports)
    {
        SettingsManager.UserSettings.Items.Add(new UserSetting { InstanceGuid = id, MapTo = 15 });
        var saved = UserEffectsDispatcher.SlotPerDeviceConfigsProvider;
        UserEffectsDispatcher.SlotPerDeviceConfigsProvider = null;
        var cfg = new DeviceSlotConfig { LightbarMode = LightbarMode.Off, AudioOutputPath = AudioOutputPath.SpeakerOnly };
        using var dispatcher = new UserEffectsDispatcher(15, cfg, startTimer: false);
        bool first = true;
        PlayStationEffectWriter.RouteReportSeam = (_, report) =>
        {
            lock (reports) reports.Add(report);
            if (first)
            {
                first = false;
                insideTheWrite();
            }
            return true;
        };
        try
        {
            dispatcher.ApplyOnce();
            (typeof(UserEffectsDispatcher).GetField("_animTimer", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(dispatcher) as Timer)?.Change(Timeout.Infinite, Timeout.Infinite);
        }
        finally
        {
            PlayStationEffectWriter.RouteReportSeam = null;
            UserEffectsDispatcher.SlotPerDeviceConfigsProvider = saved;
        }
    }

    /// <summary>The release waits for a frame inside its write, however empty
    /// the lists read before it, so the pad that frame lists goes back.</summary>
    [Fact]
    public void TheRelease_WaitsForAFrameInsideItsWrite()
    {
        var id = OwnersUsbDs4(out _);
        var reports = new List<byte[]>();
        var released = typeof(UserEffectsDispatcher).GetField("s_audioRoutesReleased", Static)!;
        Routed.Clear();
        Thread release = null;
        bool waited = false;
        try
        {
            FirstRouteOfADispatcher(id, () =>
            {
                release = new Thread(UserEffectsDispatcher.ReleaseAudioRoutes) { IsBackground = true };
                release.Start();
                SpinWait.SpinUntil(() => !release.IsAlive
                    || ((bool)released.GetValue(null)! && (release.ThreadState & ThreadState.WaitSleepJoin) != 0), 5000);
                waited = release.IsAlive;
            }, reports);
            Assert.True(release.Join(5000));
            Assert.True(waited);
            Assert.False(Routed.ContainsKey(id));
            Assert.Equal(new byte[] { 0xE0, 0x00, 0x00 }, reports[^1]);
        }
        finally
        {
            UserEffectsDispatcher.ResumeAudioRoutes();
            Routed.Clear();
        }
    }

    /// <summary>A frame whose write outlasts the release's wait lands after the
    /// release has gone, and nothing else would put its pad back, so the frame
    /// does.</summary>
    [Fact]
    public void AFrameThatOutlastsTheReleasesWait_PutsItsPadBackItself()
    {
        var id = OwnersUsbDs4(out _);
        var reports = new List<byte[]>();
        Routed.Clear();
        try
        {
            FirstRouteOfADispatcher(id, () =>
            {
                var release = new Thread(UserEffectsDispatcher.ReleaseAudioRoutes) { IsBackground = true };
                release.Start();
                Assert.True(release.Join(5000));
            }, reports);
            Assert.False(Routed.ContainsKey(id));
            Assert.Equal(new byte[] { 0xE0, 0x00, 0x00 }, reports[^1]);
        }
        finally
        {
            UserEffectsDispatcher.ResumeAudioRoutes();
            Routed.Clear();
        }
    }

    /// <summary>A release that could not wait for that write left every pad it
    /// lists off its headset, so the frame that held it up lets them all go.</summary>
    [Fact]
    public void AFrameThatOutlastsTheReleasesWait_PutsBackTheOtherListedPadsToo()
    {
        var id = OwnersUsbDs4(out _);
        var other = Guid.NewGuid();
        var reports = new List<byte[]>();
        Routed.Clear();
        Routed[other] = ("another-pad", null, false);
        try
        {
            FirstRouteOfADispatcher(id, () =>
            {
                var release = new Thread(UserEffectsDispatcher.ReleaseAudioRoutes) { IsBackground = true };
                release.Start();
                Assert.True(release.Join(5000));
            }, reports);
            Assert.False(Routed.ContainsKey(other));
            Assert.False(Routed.ContainsKey(id));
        }
        finally
        {
            UserEffectsDispatcher.ResumeAudioRoutes();
            Routed.Clear();
        }
    }

    /// <summary>A release that gave up on the gate is tried again, since the
    /// write that held it can fail and no later frame routes anything. The
    /// frame that held it is not the only way back.</summary>
    [Fact]
    public void AReleaseThatGaveUpOnTheGate_TriesAgain()
    {
        var gate = typeof(UserEffectsDispatcher).GetField("s_writeGate", Static)!.GetValue(null)!;
        var other = Guid.NewGuid();
        var reports = new List<byte[]>();
        Routed.Clear();
        Routed[other] = ("another-pad", null, false);
        PlayStationEffectWriter.RouteReportSeam = (_, report) =>
        {
            lock (reports) reports.Add(report);
            return true;
        };
        try
        {
            lock (gate)
            {
                var release = new Thread(UserEffectsDispatcher.ReleaseAudioRoutes) { IsBackground = true };
                release.Start();
                Assert.True(release.Join(5000));
                Assert.True(Routed.ContainsKey(other), "the release gave up");
            }
            Assert.True(SpinWait.SpinUntil(() => !Routed.ContainsKey(other), 10000), "the pad went back");
            lock (reports) Assert.Equal(new byte[] { 0xE0, 0x00, 0x00 }, reports[^1]);
        }
        finally
        {
            PlayStationEffectWriter.RouteReportSeam = null;
            UserEffectsDispatcher.ResumeAudioRoutes();
            Routed.Clear();
        }
    }

    /// <summary>The engine started again before the gate came free: the pads
    /// are its to route, so the try that was owed sends nothing.</summary>
    [Fact]
    public void AReleaseThatGaveUpOnTheGate_StandsDown_WhenTheEngineStartedAgain()
    {
        var gate = typeof(UserEffectsDispatcher).GetField("s_writeGate", Static)!.GetValue(null)!;
        var other = Guid.NewGuid();
        var reports = new List<byte[]>();
        Routed.Clear();
        Routed[other] = ("another-pad", null, false);
        PlayStationEffectWriter.RouteReportSeam = (_, report) =>
        {
            lock (reports) reports.Add(report);
            return true;
        };
        try
        {
            lock (gate)
            {
                var release = new Thread(UserEffectsDispatcher.ReleaseAudioRoutes) { IsBackground = true };
                release.Start();
                Assert.True(release.Join(5000));
                UserEffectsDispatcher.ResumeAudioRoutes();
            }
            Thread.Sleep(1500);
            Assert.True(Routed.ContainsKey(other));
            lock (reports) Assert.Empty(reports);
        }
        finally
        {
            PlayStationEffectWriter.RouteReportSeam = null;
            UserEffectsDispatcher.ResumeAudioRoutes();
            Routed.Clear();
        }
    }

    /// <summary>The worker's route for a peer's sound has the same window: it
    /// passes the released check, then writes and lists.</summary>
    [Fact]
    public void ARemoteFedRouteThatOutlastsTheReleasesWait_PutsItsPadBackItself()
    {
        var id = OwnersUsbDs4(out string file);
        var reports = new List<byte[]>();
        bool first = true;
        Routed.Clear();
        PlayStationEffectWriter.RouteReportSeam = (_, report) =>
        {
            lock (reports) reports.Add(report);
            if (first)
            {
                first = false;
                var release = new Thread(UserEffectsDispatcher.ReleaseAudioRoutes) { IsBackground = true };
                release.Start();
                Assert.True(release.Join(5000));
            }
            return true;
        };
        try
        {
            UserEffectsDispatcher.WriteRemoteFedRoute(id, file, Ds4UsbAudio.RouteSpeaker);
            Assert.False(Routed.ContainsKey(id));
            Assert.Equal(new byte[] { 0xE0, 0x00, 0x00 }, reports[^1]);
        }
        finally
        {
            PlayStationEffectWriter.RouteReportSeam = null;
            UserEffectsDispatcher.ResumeAudioRoutes();
            Routed.Clear();
        }
    }

    /// <summary>The owner's own Output Path picks the route, as it picks the
    /// render thread's channel map.</summary>
    [Theory]
    [InlineData(1, 0x00)]
    [InlineData(2, 0x01)]
    [InlineData(3, 0x02)]
    [InlineData(4, 0x03)]
    public void AnOwnersOutputPath_PicksThePeersRoute(int path, int route)
    {
        var id = OwnersUsbDs4(out string file);
        var reports = new List<byte[]>();
        Routed.Clear();
        AudioPassthroughService.DeviceAudioOutputPathProvider = (_, _) => path;
        PlayStationEffectWriter.RouteReportSeam = (_, report) => { reports.Add(report); return true; };
        try
        {
            Demand[id] = Environment.TickCount64;
            Reconcile(worker);
            Assert.Equal(new byte[] { 0xE0, 0x00, (byte)route }, Assert.Single(reports));
            Assert.Equal(Ds4UsbAudio.RouteHasSpeaker(route), ReadShared(file).Length == 32);
            Assert.Equal(route != 0, Routed.ContainsKey(id));
        }
        finally
        {
            PlayStationEffectWriter.RouteReportSeam = null;
            Demand.Clear();
            Routed.Clear();
        }
    }

    /// <summary>The release reaches a DualShock 4 a peer holds: its frames
    /// carry no route, so the route is the owner's to give back.</summary>
    [Fact]
    public void LettingGo_GivesAClaimedDs4TheHeadsetBack()
    {
        var id = OwnersUsbDs4(out string file);
        var reports = new List<byte[]>();
        Routed.Clear();
        PlayStationEffectWriter.RouteReportSeam = (_, report) => { reports.Add(report); return true; };
        try
        {
            Routed[id] = (file, null, false);
            Assert.True(RemoteLinkOutputRouter.ClaimOutput(file));
            UserEffectsDispatcher.ReleaseAudioRoutes();
            Assert.Equal(new byte[] { 0xE0, 0x00, 0x00 }, Assert.Single(reports));
            Assert.False(Routed.ContainsKey(id));
        }
        finally
        {
            PlayStationEffectWriter.RouteReportSeam = null;
            RemoteLinkOutputRouter.ReleaseDevice(file);
            UserEffectsDispatcher.ResumeAudioRoutes();
            Routed.Clear();
        }
    }

    /// <summary>A consumer's Output Path on a shared DualSense moves the pad's
    /// firmware through the effect body, so the owner's channel map follows it.
    /// Headphones (Stereo) played silence on the left and mono on the right
    /// before. A DualShock 4's body names no path.</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void APeersOutputPath_MovesTheOwnersChannelMap_OnADualSense(int path)
    {
        var profile = HMaestroProfileCatalog.GetProfileById("dualsense");
        byte[] body = Shipped(profile, Ds5EffectSynthesizer.BuildFields(new DeviceSlotConfig(),
            audioOutputPath: path, assertAudioControl: true));
        var id = Guid.NewGuid();
        AudioPassthroughService.DeviceAudioOutputPathProvider = (_, _) => 0;
        var resolved = Audio.GetMethod("ResolvedPathOf", Static)!;
        int Resolved(int slot) => (int)resolved.Invoke(null, new object[] { slot, id })!;

        Assert.Equal(0, Resolved(255));
        AudioPassthroughService.NoteRemoteSonyBody(id, 0x0CE6, body);
        Assert.Equal(path, Resolved(255));
        Assert.Equal(0, Resolved(3));

        var ds4 = Guid.NewGuid();
        AudioPassthroughService.NoteRemoteSonyBody(ds4, 0x09CC, body);
        id = ds4;
        Assert.Equal(0, Resolved(255));
    }

    /// <summary>A frame that asserts no path leaves the last one standing: the
    /// firmware holds it.</summary>
    [Fact]
    public void AFrameThatNamesNoPath_LeavesThePeersPathStanding()
    {
        var profile = HMaestroProfileCatalog.GetProfileById("dualsense");
        var id = Guid.NewGuid();
        var resolved = Audio.GetMethod("ResolvedPathOf", Static)!;
        AudioPassthroughService.NoteRemoteSonyBody(id, 0x0CE6, Shipped(profile, Ds5EffectSynthesizer.BuildFields(new DeviceSlotConfig(),
            audioOutputPath: 1, assertAudioControl: true)));
        AudioPassthroughService.NoteRemoteSonyBody(id, 0x0CE6, Shipped(profile, Ds5EffectSynthesizer.BuildFields(new DeviceSlotConfig(),
            audioOutputPath: 1, assertAudioControl: false)));
        Assert.Equal(1, (int)resolved.Invoke(null, new object[] { 255, id })!);
    }

    /// <summary>The Audio tab's Output Path shows for a DualShock 4 on this
    /// PC's cable, and not for a peer's copy of one, where choosing would reach
    /// nothing.</summary>
    [Theory]
    [InlineData(@"\\?\HID#VID_054C&PID_09CC#1", true)]
    [InlineData("peer://shared-ds4", false)]
    public void TheOutputPathRow_ShowsForALocalUsbDs4Alone(string path, bool shown)
    {
        var ud = new UserDevice { VendorId = 0x054C, ProdId = 0x09CC, DevicePath = path };
        var method = typeof(PadViewModel).GetMethod("IsUsbDs4WithAudio", Static)!;
        Assert.Equal(shown, (bool)method.Invoke(null, new object[] { ud })!);
    }

    /// <summary>The report at its own length goes first, the shape heard on a
    /// cable, and padded to the collection's length only if that is
    /// refused.</summary>
    [Fact]
    public void ARouteReport_GoesAtItsOwnLengthFirst_ThenPadded()
    {
        byte[] report = Ds4UsbAudio.BuildRouteReport(Ds4UsbAudio.RouteSpeaker);
        var sent = new List<byte[]>();

        Assert.True(RawHidOutput.SendOwnLengthFirst(report, 64, b => { sent.Add(b); return true; }));
        Assert.Equal(new byte[] { 0xE0, 0x00, 0x03 }, Assert.Single(sent));

        sent.Clear();
        Assert.True(RawHidOutput.SendOwnLengthFirst(report, 64, b => { sent.Add(b); return b.Length == 64; }));
        Assert.Equal(2, sent.Count);
        Assert.Equal(new byte[] { 0xE0, 0x00, 0x03 }, sent[0]);
        Assert.Equal(64, sent[1].Length);
        Assert.Equal(report, sent[1].Take(3).ToArray());
        Assert.All(sent[1].Skip(3), b => Assert.Equal(0, b));

        sent.Clear();
        Assert.False(RawHidOutput.SendOwnLengthFirst(report, 64, b => { sent.Add(b); return false; }));
        Assert.Equal(2, sent.Count);

        sent.Clear();
        Assert.False(RawHidOutput.SendOwnLengthFirst(report, 0, b => { sent.Add(b); return false; }));
        Assert.Single(sent);
    }

    /// <summary>A pad that refuses 0xE0 still takes its rumble and lights. The
    /// frame counts as delivered, nothing re-arms, and the route alone goes
    /// again after a second. It counts as written only once it lands.</summary>
    [Fact]
    public void ARefusedRoute_LeavesTheFrameDelivered_AndRetriesOnACooldown()
    {
        var id = OwnersUsbDs4(out string file);
        SettingsManager.UserSettings.Items.Add(new UserSetting { InstanceGuid = id, MapTo = 15 });
        const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        int attempts = 0;
        bool accept = false;
        Routed.Clear();
        PlayStationEffectWriter.RouteReportSeam = (_, _) => { attempts++; return accept; };
        var cfg = new DeviceSlotConfig { LightbarMode = LightbarMode.Off, AudioOutputPath = AudioOutputPath.SpeakerOnly };
        var saved = UserEffectsDispatcher.SlotPerDeviceConfigsProvider;
        UserEffectsDispatcher.SlotPerDeviceConfigsProvider = null;
        using var dispatcher = new UserEffectsDispatcher(15, cfg, startTimer: false);
        void Park() => (typeof(UserEffectsDispatcher).GetField("_animTimer", Private)!.GetValue(dispatcher) as Timer)?.Change(Timeout.Infinite, Timeout.Infinite);
        var retries = (System.Collections.Concurrent.ConcurrentDictionary<Guid, long>)typeof(UserEffectsDispatcher)
            .GetField("_usbRouteRetryAt", Private)!.GetValue(dispatcher)!;
        var failed = (IDictionary)typeof(UserEffectsDispatcher).GetField("_failedDeliveries", Private)!.GetValue(dispatcher)!;
        try
        {
            dispatcher.ApplyOnce();
            Park();
            Assert.Equal(1, attempts);
            Assert.NotEmpty(ReadShared(file));
            Assert.Empty(failed);
            Assert.False(Routed.ContainsKey(id));
            Assert.True(retries.ContainsKey(id));
            Assert.True((bool)typeof(UserEffectsDispatcher).GetField("_animTickActive", Private)!.GetValue(dispatcher)!);

            // Inside the cooldown the tick dispatches nothing, and a pass that
            // comes anyway sends no route.
            var tick = typeof(UserEffectsDispatcher).GetMethod("OnAnimTickCore", Private)!;
            tick.Invoke(dispatcher, new object[] { null });
            dispatcher.ApplyOnce();
            Park();
            Assert.Equal(1, attempts);

            accept = true;
            retries[id] = Environment.TickCount64 - 1;
            tick.Invoke(dispatcher, new object[] { null });
            Assert.Equal(2, attempts);
            Assert.True(Routed.ContainsKey(id));
            Assert.Empty(retries);
            var last = (Dictionary<Guid, int>)typeof(UserEffectsDispatcher).GetField("_prevDs4UsbRoute", Private)!.GetValue(dispatcher)!;
            Assert.Equal(3, last[id]);
        }
        finally
        {
            PlayStationEffectWriter.RouteReportSeam = null;
            UserEffectsDispatcher.SlotPerDeviceConfigsProvider = saved;
            Routed.Clear();
        }
    }
}
