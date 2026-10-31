using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Serialization;
using PadForge.Common.Input;
using PadForge.Common.Input.Peripherals;
using PadForge.Common.Input.WinUsbKeyboards;
using PadForge.Engine;
using PadForge.Engine.Common;
using PadForge.Engine.Data;
using PadForge.Services;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>Report descriptors the keyboard tests read.</summary>
    internal static class KeyboardDescriptors
    {
        /// <summary>The boot keyboard of USB HID 1.11 Appendix E.6: eight
        /// modifier bits, a reserved byte, LED outputs, and a six-key array
        /// of usages 0 to 0x65.</summary>
        public static readonly byte[] Boot =
        {
            0x05, 0x01, 0x09, 0x06, 0xA1, 0x01, 0x05, 0x07, 0x19, 0xE0, 0x29, 0xE7, 0x15, 0x00, 0x25, 0x01,
            0x75, 0x01, 0x95, 0x08, 0x81, 0x02, 0x95, 0x01, 0x75, 0x08, 0x81, 0x01, 0x95, 0x05, 0x75, 0x01,
            0x05, 0x08, 0x19, 0x01, 0x29, 0x05, 0x91, 0x02, 0x95, 0x01, 0x75, 0x03, 0x91, 0x01, 0x95, 0x06,
            0x75, 0x08, 0x15, 0x00, 0x25, 0x65, 0x05, 0x07, 0x19, 0x00, 0x29, 0x65, 0x81, 0x00, 0xC0,
        };

        /// <summary>A keyboard with report IDs: 1 is a boot-style report,
        /// 2 an N-key rollover bitmap of usages 0 to 0x77, 3 a 16-bit
        /// Consumer page array.</summary>
        public static readonly byte[] WithReportIds =
        {
            0x05, 0x01, 0x09, 0x06, 0xA1, 0x01, 0x85, 0x01,
            0x05, 0x07, 0x19, 0xE0, 0x29, 0xE7, 0x15, 0x00, 0x25, 0x01, 0x75, 0x01, 0x95, 0x08, 0x81, 0x02,
            0x95, 0x01, 0x75, 0x08, 0x81, 0x01,
            0x95, 0x06, 0x75, 0x08, 0x15, 0x00, 0x25, 0xFF, 0x05, 0x07, 0x19, 0x00, 0x29, 0xFF, 0x81, 0x00,
            0xC0,
            0x05, 0x01, 0x09, 0x06, 0xA1, 0x01, 0x85, 0x02,
            0x05, 0x07, 0x19, 0x00, 0x29, 0x77, 0x15, 0x00, 0x25, 0x01, 0x75, 0x01, 0x95, 0x78, 0x81, 0x02,
            0xC0,
            0x05, 0x0C, 0x09, 0x01, 0xA1, 0x01, 0x85, 0x03,
            0x15, 0x01, 0x26, 0x9C, 0x02, 0x19, 0x01, 0x2A, 0x9C, 0x02, 0x75, 0x10, 0x95, 0x01, 0x81, 0x00,
            0xC0,
        };

        /// <summary>A three-button mouse: no key at all.</summary>
        public static readonly byte[] Mouse =
        {
            0x05, 0x01, 0x09, 0x02, 0xA1, 0x01, 0x09, 0x01, 0xA1, 0x00, 0x05, 0x09, 0x19, 0x01, 0x29, 0x03,
            0x15, 0x00, 0x25, 0x01, 0x95, 0x03, 0x75, 0x01, 0x81, 0x02, 0x95, 0x01, 0x75, 0x05, 0x81, 0x01,
            0x05, 0x01, 0x09, 0x30, 0x09, 0x31, 0x15, 0x81, 0x25, 0x7F, 0x75, 0x08, 0x95, 0x02, 0x81, 0x06,
            0xC0, 0xC0,
        };

        /// <summary>A configuration descriptor with a mouse on interface 0
        /// (report descriptor 0x40 bytes) and a keyboard on interface 1
        /// (0x3F bytes), the shape of a receiver.</summary>
        public static readonly byte[] TwoInterfaceConfiguration =
        {
            0x09, 0x02, 0x3B, 0x00, 0x02, 0x01, 0x00, 0xA0, 0x32,
            0x09, 0x04, 0x00, 0x00, 0x01, 0x03, 0x01, 0x02, 0x00,
            0x09, 0x21, 0x11, 0x01, 0x00, 0x01, 0x22, 0x40, 0x00,
            0x07, 0x05, 0x81, 0x03, 0x08, 0x00, 0x0A,
            0x09, 0x04, 0x01, 0x00, 0x01, 0x03, 0x01, 0x01, 0x00,
            0x09, 0x21, 0x11, 0x01, 0x00, 0x01, 0x22, 0x3F, 0x00,
            0x07, 0x05, 0x82, 0x03, 0x08, 0x00, 0x0A,
        };

        public static uint Key(ushort usage) => 0x00070000u | usage;
        public static uint Consumer(ushort usage) => 0x000C0000u | usage;
    }

    /// <summary>
    /// The report decoder of a keyboard read over WinUSB (#549), against
    /// the rules of Linux's HID core it follows (hid-core.c): usages and
    /// their pages, field offsets by report ID, padding, variable bits,
    /// array values within the logical range, and ErrorRollOver.
    /// </summary>
    public class HidKeyboardReportsTests
    {
        private static HashSet<uint> Decode(HidKeyboardReports reports, params byte[] report)
        {
            var pressed = new HashSet<uint>();
            Assert.True(reports.Decode(report, pressed));
            return pressed;
        }

        [Fact]
        public void TheBootKeyboard_HasItsModifierBitsAndItsKeyArray()
        {
            var reports = HidKeyboardReports.Parse(KeyboardDescriptors.Boot);
            Assert.NotNull(reports);
            Assert.False(reports.UsesReportIds);
            // The reserved byte has no usage, so it is padding, and the LED
            // outputs are no input field.
            Assert.Equal(2, reports.Fields.Count);
            var modifiers = reports.Fields[0];
            Assert.True(modifiers.IsVariable);
            Assert.Equal((0, 1, 8), (modifiers.BitOffset, modifiers.Size, modifiers.Count));
            Assert.Equal(KeyboardDescriptors.Key(0xE0), modifiers.Usages[0]);
            Assert.Equal(KeyboardDescriptors.Key(0xE7), modifiers.Usages[7]);
            var keys = reports.Fields[1];
            Assert.False(keys.IsVariable);
            Assert.Equal((16, 8, 6), (keys.BitOffset, keys.Size, keys.Count));
            Assert.Equal((0, 0x65), (keys.LogicalMinimum, keys.LogicalMaximum));
            Assert.Equal(0x66, keys.Usages.Length);
        }

        [Fact]
        public void ABootReport_PressesItsModifiersAndKeys_AndAnEmptyOneReleasesThem()
        {
            var reports = HidKeyboardReports.Parse(KeyboardDescriptors.Boot);
            // Left Shift (bit 1), Enter (0x28) and F1 (0x3A).
            var pressed = Decode(reports, 0x02, 0x00, 0x28, 0x3A, 0x00, 0x00, 0x00, 0x00);
            Assert.Equal(new[] { 0x000700E1u, 0x00070028u, 0x0007003Au }.OrderBy(u => u), pressed.OrderBy(u => u));

            Assert.Empty(Decode(reports, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00));
        }

        [Fact]
        public void ErrorRollOver_LeavesTheKeysAsTheyWere()
        {
            var reports = HidKeyboardReports.Parse(KeyboardDescriptors.Boot);
            Decode(reports, 0x00, 0x00, 0x28, 0x00, 0x00, 0x00, 0x00, 0x00);
            // Too many keys: every slot says ErrorRollOver. The modifiers
            // still read, and the array keeps Enter.
            var pressed = Decode(reports, 0x01, 0x00, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01);
            Assert.Contains(KeyboardDescriptors.Key(0x28), pressed);
            Assert.Contains(KeyboardDescriptors.Key(0xE0), pressed);
            Assert.DoesNotContain(HidKeyboardReports.ErrorRollOver, pressed);
        }

        [Fact]
        public void AShortReport_ReadsZerosPastItsEnd()
        {
            var reports = HidKeyboardReports.Parse(KeyboardDescriptors.Boot);
            var pressed = Decode(reports, 0x00, 0x00, 0x28);
            Assert.Equal(new[] { KeyboardDescriptors.Key(0x28) }, pressed);
        }

        [Fact]
        public void ReportIds_RouteEachReportToItsOwnFields_AndTheKeysHeldAddUp()
        {
            var reports = HidKeyboardReports.Parse(KeyboardDescriptors.WithReportIds);
            Assert.NotNull(reports);
            Assert.True(reports.UsesReportIds);
            Assert.Equal(new byte[] { 1, 1, 2, 3 }, reports.Fields.Select(f => f.ReportId));

            // Report 1 presses Escape (0x29).
            var pressed = Decode(reports, 0x01, 0x00, 0x00, 0x29, 0, 0, 0, 0, 0);
            Assert.Equal(new[] { KeyboardDescriptors.Key(0x29) }, pressed);

            // Report 2's bitmap presses usage 0x04 (bit 4) and keeps report 1's Escape.
            var bitmap = new byte[1 + 15];
            bitmap[0] = 0x02;
            bitmap[1] = 0x10;
            pressed = Decode(reports, bitmap);
            Assert.Equal(new[] { KeyboardDescriptors.Key(0x04), KeyboardDescriptors.Key(0x29) }.OrderBy(u => u), pressed.OrderBy(u => u));

            // Report 3 presses Volume Up, 0x00E9 little-endian.
            pressed = Decode(reports, 0x03, 0xE9, 0x00);
            Assert.Contains(KeyboardDescriptors.Consumer(0xE9), pressed);
            Assert.Equal(3, pressed.Count);

            // An unknown report ID changes nothing and says so.
            var none = new HashSet<uint> { 42 };
            Assert.False(reports.Decode(new byte[] { 0x07, 0x01 }, none));
            Assert.Equal(new uint[] { 42 }, none);
        }

        [Fact]
        public void AUsagePageDeclaredAfterTheUsages_IsTheirs()
        {
            byte[] descriptor =
            {
                0x05, 0x01, 0x09, 0x06, 0xA1, 0x01,
                0x09, 0xE0, 0x09, 0xE1, 0x05, 0x07,
                0x15, 0x00, 0x25, 0x01, 0x75, 0x01, 0x95, 0x02, 0x81, 0x02,
                0xC0,
            };
            var reports = HidKeyboardReports.Parse(descriptor);
            Assert.NotNull(reports);
            Assert.Equal(new[] { KeyboardDescriptors.Key(0xE0), KeyboardDescriptors.Key(0xE1) }, reports.Fields[0].Usages);
        }

        [Fact]
        public void AnExcessValue_RepeatsTheLastUsage()
        {
            // Two usages for four bits: bits 2 and 3 read as the second usage.
            byte[] descriptor =
            {
                0x05, 0x07, 0x09, 0x04, 0x09, 0x05,
                0x15, 0x00, 0x25, 0x01, 0x75, 0x01, 0x95, 0x04, 0x81, 0x02,
            };
            var reports = HidKeyboardReports.Parse(descriptor);
            Assert.Equal(new[] { 0x00070004u, 0x00070005u, 0x00070005u, 0x00070005u }, reports.Fields[0].Usages);
            var pressed = Decode(reports, 0x08);
            Assert.Equal(new[] { KeyboardDescriptors.Key(0x05) }, pressed);
        }

        [Theory]
        [InlineData(new byte[] { 0x05 })]                     // an item cut short
        [InlineData(new byte[] { 0xB4 })]                     // Pop with nothing pushed
        [InlineData(new byte[] { 0x85, 0x00 })]               // report ID 0
        [InlineData(new byte[] { 0xFE, 0x05, 0x00, 0x01 })]   // a long item cut short
        public void AMalformedDescriptor_IsRefused(byte[] descriptor)
            => Assert.Null(HidKeyboardReports.Parse(descriptor));

        [Fact]
        public void ADescriptorWithNoKeys_IsRefused()
        {
            Assert.Null(HidKeyboardReports.Parse(KeyboardDescriptors.Mouse));
            Assert.Null(HidKeyboardReports.Parse(Array.Empty<byte>()));
            Assert.Null(HidKeyboardReports.Parse(new byte[HidKeyboardReports.MaxDescriptorLength + 1]));
        }

        [Fact]
        public void ARangePastTheBound_IsCutToFit()
        {
            // Usage Minimum 0, Usage Maximum 0xFFFF on the Consumer page.
            byte[] descriptor =
            {
                0x05, 0x0C, 0x19, 0x00, 0x2A, 0xFF, 0xFF,
                0x15, 0x00, 0x26, 0xFF, 0x00, 0x75, 0x10, 0x95, 0x01, 0x81, 0x00,
            };
            var reports = HidKeyboardReports.Parse(descriptor);
            Assert.NotNull(reports);
            Assert.Equal(HidKeyboardReports.MaxUsages, reports.Fields[0].Usages.Length);
        }

        [Theory]
        [InlineData(new byte[] { 0xFF }, 0, 8, false, 255)]
        [InlineData(new byte[] { 0xFF }, 0, 8, true, -1)]
        [InlineData(new byte[] { 0x80 }, 4, 4, true, -8)]
        [InlineData(new byte[] { 0x34, 0x12 }, 0, 16, false, 0x1234)]
        [InlineData(new byte[] { 0x01 }, 8, 8, false, 0)]
        public void Extract_ReadsLeastSignificantFirst_AndZerosPastTheEnd(byte[] data, int offset, int size, bool signed, int expected)
            => Assert.Equal(expected, HidKeyboardReports.Extract(data, offset, size, signed));
    }

    /// <summary>A scripted WinUSB interface for the keyboard channel.</summary>
    internal sealed class FakeKeyboardUsb : IKeyboardUsb
    {
        public byte Number = 1, Class = 0x03, InPipe = 0x82;
        public ushort PacketSize = 8;
        public byte[] Configuration = KeyboardDescriptors.TwoInterfaceConfiguration;
        public byte[] Descriptor = KeyboardDescriptors.Boot;
        public readonly ConcurrentQueue<string> Journal = new();
        public readonly ConcurrentQueue<(int Got, int Error, byte[] Data)> Reads = new();
        public int Disposals;

        public bool DescribeInterface(out byte number, out byte interfaceClass, out byte interruptIn, out ushort packetSize)
        {
            (number, interfaceClass, interruptIn, packetSize) = (Number, Class, InPipe, PacketSize);
            return true;
        }

        public int ReadConfiguration(byte[] buffer)
        {
            int n = Math.Min(buffer.Length, Configuration.Length);
            Array.Copy(Configuration, buffer, n);
            return n;
        }

        public bool ControlIn(byte[] setup, byte[] buffer, out int length)
        {
            Journal.Enqueue("in " + BitConverter.ToString(setup));
            length = Math.Min(buffer.Length, Descriptor.Length);
            Array.Copy(Descriptor, buffer, length);
            return true;
        }

        public bool ControlOut(byte[] setup, byte[] data)
        {
            Journal.Enqueue("out " + BitConverter.ToString(setup));
            return true;
        }

        public int ReadPipe(byte pipe, byte[] buffer, out int error)
        {
            if (!Reads.TryDequeue(out var read))
            {
                error = 0;
                return 0;
            }
            error = read.Error;
            if (read.Data != null) Array.Copy(read.Data, buffer, Math.Min(read.Data.Length, buffer.Length));
            return read.Got;
        }

        public void SetTimeout(byte pipe, int milliseconds) => Journal.Enqueue($"timeout {pipe:X2} {milliseconds}");
        public void CancelIo() => Journal.Enqueue("cancel");
        public void Dispose() => Interlocked.Increment(ref Disposals);
    }

    /// <summary>A scripted open keyboard channel for the row and the lane.</summary>
    internal sealed class FakeKeyboardChannel : IKeyboardChannel
    {
        private int _closed;

        public FakeKeyboardChannel(string path, byte[] descriptor = null)
        {
            Path = path;
            Reports = HidKeyboardReports.Parse(descriptor ?? KeyboardDescriptors.Boot);
        }

        public string Path { get; }
        public bool IsOpen => Volatile.Read(ref _closed) == 0;
        public HidKeyboardReports Reports { get; }
        public int ReadLength => 8;

        public bool TryRead(Span<byte> report, out int length)
        {
            length = 0;
            if (!IsOpen) return false;
            Thread.Sleep(5);
            return IsOpen;
        }

        public void Dispose() => Interlocked.Exchange(ref _closed, 1);
    }

    /// <summary>
    /// The channel of a keyboard bound to WinUSB (#549): what its open
    /// asks of the interface, and on which interface.
    /// </summary>
    public class WinUsbKeyboardChannelTests
    {
        private const string Path = @"\\?\usb#vid_046d&pid_c54d&mi_01#8&340f50e3&0&0001#{fd826c66-3556-4845-9436-0fe7580ded02}";

        [Fact]
        public void TheReportDescriptorLength_IsTheChosenInterfaces()
        {
            Assert.Equal(0x40, WinUsbKeyboardProtocol.ReportDescriptorLength(KeyboardDescriptors.TwoInterfaceConfiguration, 0));
            Assert.Equal(0x3F, WinUsbKeyboardProtocol.ReportDescriptorLength(KeyboardDescriptors.TwoInterfaceConfiguration, 1));
            Assert.Equal(0, WinUsbKeyboardProtocol.ReportDescriptorLength(KeyboardDescriptors.TwoInterfaceConfiguration, 2));
            Assert.Equal(0, WinUsbKeyboardProtocol.ReportDescriptorLength(new byte[] { 0x01 }, 0));
        }

        [Fact]
        public void TheRequests_NameTheInterfaceInWIndex()
        {
            Assert.Equal(new byte[] { 0x21, 0x0A, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00 }, WinUsbKeyboardProtocol.SetIdleSetup(1));
            Assert.Equal(new byte[] { 0x81, 0x06, 0x00, 0x22, 0x01, 0x00, 0x3F, 0x00 }, WinUsbKeyboardProtocol.ReportDescriptorSetup(1, 0x3F));
            // Interface 0 asks as the TouchSense open asks.
            Assert.Equal(TouchSenseProtocol.SetIdleSetup(), WinUsbKeyboardProtocol.SetIdleSetup(0));
            Assert.Equal(TouchSenseProtocol.ReportDescriptorSetup(0x4A), WinUsbKeyboardProtocol.ReportDescriptorSetup(0, 0x4A));
        }

        [Fact]
        public void TheOpen_ReadsTheInterfacesDescriptor_AndSendsSetIdleToIt_Once()
        {
            var usb = new FakeKeyboardUsb { Descriptor = KeyboardDescriptors.Boot.Concat(new byte[0x3F - 63]).ToArray() };
            var channel = WinUsbKeyboardChannel.Open(Path, usb);
            Assert.NotNull(channel);
            Assert.Equal(1, channel.InterfaceNumber);
            Assert.Equal(8, channel.ReadLength);
            Assert.NotNull(channel.Reports);
            var journal = usb.Journal.ToArray();
            Assert.Contains("in 81-06-00-22-01-00-3F-00", journal);
            Assert.Single(journal, j => j == "out 21-0A-00-00-01-00-00-00");
            Assert.Contains($"timeout 82 {WinUsbKeyboardChannel.ReadTimeoutMs}", journal);
            Assert.Equal(0, usb.Disposals);
            Assert.True(channel.IsOpen);
        }

        [Fact]
        public void AnInterfaceThatIsNoKeyboard_IsNotOpened_AndItsHandleIsClosed()
        {
            var notHid = new FakeKeyboardUsb { Class = 0xFF };
            Assert.Null(WinUsbKeyboardChannel.Open(Path, notHid));
            Assert.Equal(1, notHid.Disposals);

            var noPipe = new FakeKeyboardUsb { InPipe = 0 };
            Assert.Null(WinUsbKeyboardChannel.Open(Path, noPipe));
            Assert.Equal(1, noPipe.Disposals);

            // A mouse's descriptor declares no key.
            var mouse = new FakeKeyboardUsb { Number = 0, Descriptor = KeyboardDescriptors.Mouse.Concat(new byte[0x40 - KeyboardDescriptors.Mouse.Length]).ToArray() };
            Assert.Null(WinUsbKeyboardChannel.Open(Path, mouse));
            Assert.Equal(1, mouse.Disposals);
            Assert.DoesNotContain(mouse.Journal, j => j.StartsWith("out "));
        }

        [Fact]
        public void AReadLength_IsThePipesPacket_WithinItsBounds()
        {
            byte[] padded = KeyboardDescriptors.Boot.Concat(new byte[0x3F - 63]).ToArray();
            Assert.Equal(64, WinUsbKeyboardChannel.Open(Path, new FakeKeyboardUsb { Descriptor = padded, PacketSize = 64 }).ReadLength);
            Assert.Equal(WinUsbKeyboardChannel.DefaultReadLength,
                WinUsbKeyboardChannel.Open(Path, new FakeKeyboardUsb { Descriptor = padded, PacketSize = 0 }).ReadLength);
            Assert.Equal(WinUsbKeyboardChannel.MaxReadLength,
                WinUsbKeyboardChannel.Open(Path, new FakeKeyboardUsb { Descriptor = padded, PacketSize = 4096 }).ReadLength);
        }

        [Fact]
        public void AReadThatSaysTheKeyboardLeft_EndsTheChannel_AndACloseFreesTheInterfaceOnce()
        {
            var usb = new FakeKeyboardUsb { Descriptor = KeyboardDescriptors.Boot.Concat(new byte[0x3F - 63]).ToArray() };
            var channel = WinUsbKeyboardChannel.Open(Path, usb, maxReadFaults: 3, readFaultPauseMs: 0);
            var report = new byte[8];
            usb.Reads.Enqueue((8, 0, new byte[] { 0, 0, 0x28, 0, 0, 0, 0, 0 }));
            Assert.True(channel.TryRead(report, out int length));
            Assert.Equal((8, (byte)0x28), (length, report[2]));
            Assert.True(channel.TryRead(report, out length));
            Assert.Equal(0, length);
            usb.Reads.Enqueue((-1, 1167, null));   // ERROR_DEVICE_NOT_CONNECTED
            Assert.False(channel.TryRead(report, out _));

            channel.Dispose();
            channel.Dispose();
            Assert.False(channel.IsOpen);
            Assert.Equal(1, usb.Disposals);
            Assert.False(channel.TryRead(report, out _));
        }
    }

    /// <summary>
    /// The row of a keyboard bound to WinUSB (#549): a keyboard row with
    /// the ID its takeover recorded, whose keys come from its reports.
    /// Only keys whose key index no layout changes are pressed here, so the
    /// test reads the same on any PC.
    /// </summary>
    public class WinUsbKeyboardDeviceTests
    {
        internal const string Path = @"\\?\usb#vid_048d&pid_c197#7&14999c31&0&4#{fd826c66-3556-4845-9436-0fe7580ded02}";
        private static readonly Guid RowId = new("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");

        private static WinUsbKeyboardDevice Row(FakeKeyboardChannel channel, Guid rowId = default, string name = "Macro Pad")
            => new(new WinUsbKeyboard(Path, 0x048D, 0xC197, rowId, name), channel, start: false, layout: () => IntPtr.Zero);

        [Fact]
        public void TheRow_IsAKeyboardRow_WithTheRecordedIdAndName()
        {
            using var row = Row(new FakeKeyboardChannel(Path), RowId);
            Assert.Equal(InputDeviceType.Keyboard, row.GetInputDeviceType());
            Assert.Equal(RowId, row.InstanceGuid);
            Assert.Equal("Macro Pad", row.Name);
            Assert.Equal(((ushort)0x048D, (ushort)0xC197, Path), (row.VendorId, row.ProductId, row.DevicePath));
            // Every key, densely, with the keyboard rows' key names.
            Assert.Equal(row.NumButtons, row.SupportedButtonIndices.Length);
            Assert.Equal(Enumerable.Range(0, row.NumButtons), row.SupportedButtonIndices);
            Assert.Equal(SdlKeyboardWrapper.KeyObjects(row.SupportedButtonIndices).Select(o => o.Name),
                row.GetDeviceObjects().Select(o => o.Name));
            Assert.Equal(0, row.NumAxes);
            Assert.False(row.HasRumble);
        }

        [Fact]
        public void WithNoRecordedId_TheRowsIdIsItsPortsInOneCase()
        {
            using var row = Row(new FakeKeyboardChannel(Path));
            Assert.Equal(WinUsbKeyboardDevice.IdentityFor(Path), row.InstanceGuid);
            Assert.Equal(WinUsbKeyboardDevice.IdentityFor(Path.ToUpperInvariant()), row.InstanceGuid);
            Assert.NotEqual(TouchSenseMouseDevice.IdentityFor(Path), row.InstanceGuid);
            using var unnamed = Row(new FakeKeyboardChannel(Path), name: " ");
            Assert.Equal("Keyboard 048D:C197", unnamed.Name);
        }

        [Fact]
        public void Reports_PressAndReleaseTheRowsKeys()
        {
            using var row = Row(new FakeKeyboardChannel(Path));
            row.Feed(new byte[] { 0x02, 0x00, 0x28, 0x3A, 0x29, 0, 0, 0 });
            var state = row.GetCurrentState();
            Assert.True(state.Buttons[0xA0]);   // Left Shift
            Assert.True(state.Buttons[0x0D]);   // Enter
            Assert.True(state.Buttons[0x70]);   // F1
            Assert.True(state.Buttons[0x1B]);   // Escape
            Assert.Equal(4, state.Buttons.Take(256).Count(b => b));

            row.Feed(new byte[] { 0x00, 0x00, 0x3A, 0, 0, 0, 0, 0 });
            state = row.GetCurrentState();
            Assert.Equal(new[] { 0x70 }, Enumerable.Range(0, 256).Where(i => state.Buttons[i]));
        }

        [Fact]
        public void AKeyTwoReportsHold_IsReleasedOnlyWhenBothLetGo()
        {
            using var row = Row(new FakeKeyboardChannel(Path, KeyboardDescriptors.WithReportIds));
            // Report 1's array and report 2's bitmap both hold Enter (0x28).
            row.Feed(new byte[] { 0x01, 0x00, 0x00, 0x28, 0, 0, 0, 0, 0 });
            var bitmap = new byte[16];
            bitmap[0] = 0x02;
            bitmap[1 + 0x28 / 8] = (byte)(1 << (0x28 % 8));
            row.Feed(bitmap);
            Assert.True(row.GetCurrentState().Buttons[0x0D]);
            row.Feed(new byte[] { 0x01, 0x00, 0x00, 0x00, 0, 0, 0, 0, 0 });
            Assert.True(row.GetCurrentState().Buttons[0x0D]);
            row.Feed(new byte[16] { 0x02, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 });
            Assert.False(row.GetCurrentState().Buttons[0x0D]);
        }

        [Fact]
        public void AMediaKey_IsTheRowsMediaKey()
        {
            using var row = Row(new FakeKeyboardChannel(Path, KeyboardDescriptors.WithReportIds));
            row.Feed(new byte[] { 0x03, 0xE9, 0x00 });
            Assert.True(row.GetCurrentState().Buttons[0xAF]);   // Volume Up
            row.Feed(new byte[] { 0x03, 0x00, 0x00 });
            Assert.False(row.GetCurrentState().Buttons[0xAF]);
        }

        [Fact]
        public void APressResolvesItsKeyOnce_SoALayoutChangeWhileHeldStrandsNothing()
        {
            int reads = 0;
            using var row = new WinUsbKeyboardDevice(new WinUsbKeyboard(Path, 1, 2, Guid.Empty, "K"),
                new FakeKeyboardChannel(Path), start: false, layout: () => { reads++; return IntPtr.Zero; });
            row.Feed(new byte[] { 0x00, 0x00, 0x28, 0, 0, 0, 0, 0 });
            row.Feed(new byte[] { 0x00, 0x00, 0x28, 0, 0, 0, 0, 0 });
            row.Feed(new byte[] { 0x00, 0x00, 0x00, 0, 0, 0, 0, 0 });
            Assert.Equal(1, reads);
        }

        [Fact]
        public void AClosedChannel_TakesTheRowOffline()
        {
            var channel = new FakeKeyboardChannel(Path);
            using var row = Row(channel);
            Assert.True(row.IsAttached);
            Assert.NotNull(row.GetCurrentState());
            channel.Dispose();
            Assert.False(row.IsAttached);
            Assert.Null(row.GetCurrentState());
        }

        [Fact]
        public void TheReader_ClosesTheChannelWhenTheRowIsClosed()
        {
            var channel = new FakeKeyboardChannel(Path);
            var row = new WinUsbKeyboardDevice(new WinUsbKeyboard(Path, 1, 2, Guid.Empty, "K"), channel, start: true,
                layout: () => IntPtr.Zero);
            Assert.True(row.IsAttached);
            row.Close();
            Assert.False(channel.IsOpen);
            Assert.False(row.IsAttached);
        }
    }

    /// <summary>
    /// What the Devices page offers a keyboard row, which bound keyboards
    /// the device pass finds, and what else leaves Windows with a keyboard
    /// (#549). Paths, nodes and collections are the test's.
    /// </summary>
    public class WinUsbKeyboardBindingTests
    {
        private const string HidPath = @"\\?\HID#VID_046D&PID_C54D&MI_01&Col01#9&372bce97&0&0000#{884b96c3-56ef-11d1-bc8c-00a0c91405dd}";
        private const string NodeId = @"USB\VID_046D&PID_C54D&MI_01\8&340F50E3&0&0001";
        private const string BoundPath = @"\\?\usb#vid_046d&pid_c54d&mi_01#8&340f50e3&0&0001#{fd826c66-3556-4845-9436-0fe7580ded02}";

        private static VendorUsbDriverInstaller.UsbNode Node(string id, string service, int mi = 1)
            => new(id, 0x046D, 0xC54D, mi, service,
                new[] { mi < 0 ? @"USB\VID_046D&PID_C54D" : $@"USB\VID_046D&PID_C54D&MI_{mi:X2}" }, Array.Empty<string>());

        [Fact]
        public void AKeyboardOnWindowsHidDriver_IsOfferedTheSwitch_OfItsOwnInterface()
        {
            var nodes = new[] { Node(NodeId, "HidUsb") };
            var offer = WinUsbKeyboardBinding.QueryOffer(HidPath, nodes, parentOf: p => p == HidPath ? NodeId : null, ours: _ => false);
            Assert.NotNull(offer);
            Assert.Equal(VendorUsbDriverInstaller.OptInKind.Keyboard, offer.Kind);
            Assert.False(offer.Restore);
            Assert.Equal(NodeId, offer.NodeId);
            Assert.Equal(@"USB\VID_046D&PID_C54D&MI_01", offer.Bind.Value.BindId);
            Assert.Equal(VendorUsbDriverInstaller.BindDriver.WinUsb, offer.Bind.Value.Driver);
            Assert.Equal(new[] { "HidUsb" }, offer.From);
        }

        [Fact]
        public void AKeyboardOnAnotherDriver_OrWithNoUsbNode_IsOfferedNothing()
        {
            Assert.Null(WinUsbKeyboardBinding.QueryOffer(HidPath, new[] { Node(NodeId, "LGBusEnum") }, _ => NodeId, _ => false));
            Assert.Null(WinUsbKeyboardBinding.QueryOffer(HidPath, new[] { Node(NodeId, "HidUsb") }, _ => null, _ => false));
            Assert.Null(WinUsbKeyboardBinding.QueryOffer(HidPath, new[] { Node(NodeId, "HidUsb") }, _ => @"BTHENUM\{x}\y", _ => false));
            Assert.Null(WinUsbKeyboardBinding.QueryOffer(HidPath, new[] { Node(NodeId, "HidUsb") }, _ => throw new InvalidOperationException(), _ => false));
            Assert.Null(WinUsbKeyboardBinding.QueryOffer(null, new[] { Node(NodeId, "HidUsb") }, _ => NodeId, _ => false));
        }

        [Fact]
        public void ABoundKeyboardsRow_IsOfferedTheRestore_OnlyWhenPadForgesInterfaceServesIt()
        {
            var offer = WinUsbKeyboardBinding.QueryOffer(BoundPath, new[] { Node(NodeId, "WINUSB") }, _ => null, id => id == NodeId);
            Assert.NotNull(offer);
            Assert.True(offer.Restore);
            Assert.Null(offer.Bind);
            Assert.Equal(VendorUsbDriverInstaller.OptInKind.Keyboard, offer.Kind);

            Assert.Null(WinUsbKeyboardBinding.QueryOffer(BoundPath, new[] { Node(NodeId, "WINUSB") }, _ => null, _ => false));
            Assert.Null(WinUsbKeyboardBinding.QueryOffer(BoundPath, Array.Empty<VendorUsbDriverInstaller.UsbNode>(), _ => null, _ => true));
        }

        [Fact]
        public void AnInterfacePath_ParsesToItsNode_OnlyWhenItIsPadForgesInterface()
        {
            Assert.True(WinUsbKeyboardBinding.TryParseInterfacePath(BoundPath, out string id));
            Assert.Equal(NodeId, id, StringComparer.OrdinalIgnoreCase);
            Assert.False(WinUsbKeyboardBinding.TryParseInterfacePath(HidPath, out _));
            Assert.False(WinUsbKeyboardBinding.TryParseInterfacePath(BoundPath.Replace("fd826c66", "fd826c67"), out _));
            Assert.False(WinUsbKeyboardBinding.TryParseInterfacePath(null, out _));
            Assert.True(VendorUsbDriverInstaller.IsOwnInterfacePath(BoundPath));
            Assert.False(VendorUsbDriverInstaller.IsOwnInterfacePath(HidPath));
            Assert.False(VendorUsbDriverInstaller.IsOwnInterfacePath("web://pad"));
        }

        [Fact]
        public void TheBoundKeyboards_AreTheInterfacePathsATakeoverNames()
        {
            var takeovers = new[]
            {
                new KeyboardTakeoverEntry { NodeId = NodeId, BindId = @"USB\VID_046D&PID_C54D&MI_01", RowId = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee", Name = "Pad", VendorId = 0x046D, ProductId = 0xC54D },
            };
            const string mouse = @"\\?\usb#vid_046d&pid_c030#5&1&0&2#{fd826c66-3556-4845-9436-0fe7580ded02}";
            var found = WinUsbKeyboardBinding.BoundKeyboards(new[] { mouse, BoundPath }, takeovers);
            var keyboard = Assert.Single(found);
            Assert.Equal((BoundPath, (ushort)0x046D, (ushort)0xC54D, "Pad"), (keyboard.Path, keyboard.VendorId, keyboard.ProductId, keyboard.Name));
            Assert.Equal(new Guid("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"), keyboard.RowId);
            Assert.Empty(WinUsbKeyboardBinding.BoundKeyboards(new[] { BoundPath }, Array.Empty<KeyboardTakeoverEntry>()));
        }

        [Fact]
        public void WhatLeaves_IsReadFromTheCollectionsOnTheNode()
        {
            HidCollectionInfo C(string path, ushort page, ushort usage) => new() { Path = path, UsagePage = page, Usage = usage };
            var collections = new[]
            {
                C("k", 0x01, 0x06), C("m", 0x01, 0x02), C("c", 0x0C, 0x01), C("s", 0x01, 0x80), C("v", 0xFF00, 0x01),
                C("elsewhere", 0x01, 0x05),
            };
            var takes = WinUsbKeyboardBinding.WhatLeaves(NodeId, collections, p => p == "elsewhere" ? "other" : NodeId);
            Assert.Equal(WinUsbKeyboardBinding.Takes.Mouse | WinUsbKeyboardBinding.Takes.MediaKeys
                         | WinUsbKeyboardBinding.Takes.SystemKeys | WinUsbKeyboardBinding.Takes.VendorControls, takes);
            Assert.Equal(WinUsbKeyboardBinding.Takes.None, WinUsbKeyboardBinding.WhatLeaves(NodeId, new[] { C("k", 1, 6) }, _ => NodeId));
            Assert.Equal(WinUsbKeyboardBinding.Takes.GameController, WinUsbKeyboardBinding.Classify(0x01, 0x05));
            Assert.Equal(WinUsbKeyboardBinding.Takes.Other, WinUsbKeyboardBinding.Classify(0x0D, 0x01));
        }

        [Fact]
        public void TheSwitch_IsRefused_WhileAnotherKeyboardOfTheModelIsPluggedIn()
        {
            var plan = new VendorUsbDriverInstaller.BindPlan(@"USB\VID_046D&PID_C54D&MI_01", "Keyboard read by PadForge");
            var other = @"USB\VID_046D&PID_C54D&MI_01\8&99999999&0&0002";
            var nodes = new[] { Node(NodeId, "HidUsb"), Node(other, "HidUsb") };
            Assert.Equal(VendorUsbDriverInstaller.BindRefusal.OthersPluggedIn,
                VendorUsbDriverInstaller.WhyNotBind(plan, NodeId, nodes, WinUsbKeyboardBinding.From, out _, VendorUsbDriverInstaller.OptInKind.Keyboard));
            // Its ID belongs to no listed opt-in, so without the kind no rule
            // by kind applies: the caller always names it.
            Assert.Equal(VendorUsbDriverInstaller.BindRefusal.None,
                VendorUsbDriverInstaller.WhyNotBind(plan, NodeId, nodes, WinUsbKeyboardBinding.From, out _));
            // Alone, it may be switched.
            Assert.Equal(VendorUsbDriverInstaller.BindRefusal.None,
                VendorUsbDriverInstaller.WhyNotBind(plan, NodeId, new[] { Node(NodeId, "HidUsb") }, WinUsbKeyboardBinding.From, out _, VendorUsbDriverInstaller.OptInKind.Keyboard));
            Assert.True(VendorUsbDriverInstaller.BindsOneDeviceAtATime(VendorUsbDriverInstaller.OptInKind.Keyboard));
        }

        [Fact]
        public void AKeyboardWindowsPutBack_IsFound_AndOneStillBoundIsNot()
        {
            var entry = new KeyboardTakeoverEntry { NodeId = NodeId, BindId = "x", RowId = Guid.NewGuid().ToString() };
            Assert.Single(WinUsbKeyboardBinding.MovedBack(new[] { entry }, new[] { Node(NodeId, "HidUsb") }));
            Assert.Empty(WinUsbKeyboardBinding.MovedBack(new[] { entry }, new[] { Node(NodeId, "WINUSB") }));
            Assert.Empty(WinUsbKeyboardBinding.MovedBack(new[] { entry }, Array.Empty<VendorUsbDriverInstaller.UsbNode>()));
        }

        [Fact]
        public void TheConfirm_NamesWhatLeaves_AndWarnsOfTheOnlyKeyboardOrMouse()
        {
            var s = PadForge.Resources.Strings.Strings.Instance;
            string plain = PadForge.MainWindow.KeyboardDriverCost(WinUsbKeyboardBinding.Takes.None);
            Assert.StartsWith(s.Devices_DriverConfirm_Keyboard, plain);
            Assert.Contains(string.Format(s.Devices_WarnHide_Format, s.Devices_DeviceKind_Keyboard), plain);
            Assert.Contains(string.Format(s.Devices_DriverKeepNote_Format, PadForge.MainWindow.KeepDriverSeconds), plain);
            Assert.DoesNotContain(s.Devices_DriverTakes_Mouse, plain);

            string withMouse = PadForge.MainWindow.KeyboardDriverCost(WinUsbKeyboardBinding.Takes.Mouse | WinUsbKeyboardBinding.Takes.MediaKeys);
            Assert.Contains(string.Format(s.Devices_DriverTakes_Format, s.Devices_DriverTakes_Mouse + ", " + s.Devices_DriverTakes_MediaKeys), withMouse);
            Assert.Contains(string.Format(s.Devices_WarnHide_Format, s.Devices_DriverKind_KeyboardOrMouse), withMouse);
            Assert.Equal(15, PadForge.MainWindow.KeepDriverSeconds);
        }

        [Fact]
        public void TheKeepPromptsCountdown_EndsAtZero()
        {
            Assert.Equal(14, PadForge.Views.KeepDriverDialog.CountDown(15));
            Assert.Equal(0, PadForge.Views.KeepDriverDialog.CountDown(1));
            Assert.Equal(0, PadForge.Views.KeepDriverDialog.CountDown(0));
        }

        /// <summary>The Keep prompt's way out is the undo. Give It Back is
        /// the default and the cancel button and takes the focus, so Enter,
        /// Esc, the close button and the countdown's end all give the
        /// keyboard back. Only a Keep click made while the countdown runs
        /// keeps the switch.</summary>
        [Fact]
        public void TheKeepPrompt_GivesTheKeyboardBackUnlessKeepIsClicked()
        {
            string xaml = AuditDelta20261002Tests.RepoText("PadForge.App", "Views", "KeepDriverDialog.xaml");
            string code = AuditDelta20261002Tests.RepoText("PadForge.App", "Views", "KeepDriverDialog.xaml.cs");
            static string Tag(string text, string start)
            {
                int at = text.IndexOf(start, StringComparison.Ordinal);
                Assert.True(at >= 0, start);
                return text.Substring(at, text.IndexOf("/>", at, StringComparison.Ordinal) - at);
            }
            string giveBack = Tag(xaml, "<Button x:Name=\"GiveBackButton\"");
            string keep = Tag(xaml, "<Button x:Name=\"KeepButton\"");
            Assert.Contains("IsCancel=\"True\"", giveBack);
            Assert.Contains("IsDefault=\"True\"", giveBack);
            Assert.DoesNotContain("Click=", giveBack);
            Assert.DoesNotContain("IsCancel", keep);
            Assert.DoesNotContain("IsDefault", keep);
            Assert.Contains("Click=\"KeepButton_Click\"", keep);
            Assert.Equal(2, xaml.Split("<Button ").Length - 1);
            Assert.Contains("GiveBackButton.Focus();", code);

            // The countdown's end closes the prompt unanswered.
            int tick = code.IndexOf("_remaining = CountDown(_remaining);", StringComparison.Ordinal);
            int ends = code.IndexOf("Close();", tick, StringComparison.Ordinal);
            Assert.True(tick > 0 && ends > tick, $"{tick} {ends}");

            // The answer is set in one place, the Keep click, which does
            // nothing once the countdown has run out.
            Assert.Equal(1, code.Split("_kept = true;").Length - 1);
            int click = code.IndexOf("private void KeepButton_Click(object sender, RoutedEventArgs e)", StringComparison.Ordinal);
            int gate = code.IndexOf("if (_remaining <= 0) return;", click, StringComparison.Ordinal);
            int kept = code.IndexOf("_kept = true;", StringComparison.Ordinal);
            Assert.True(click > 0 && gate > click && kept > gate, $"{click} {gate} {kept}");
            Assert.Contains("return dlg._kept;", code);
        }

        [Fact]
        public void ATakeover_RoundTripsThroughTheSettingsFile()
        {
            var data = new AppSettingsData
            {
                KeyboardTakeovers = new[]
                {
                    new KeyboardTakeoverEntry { NodeId = NodeId, BindId = @"USB\VID_046D&PID_C54D&MI_01", RowId = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee", Name = "Pad & Co", VendorId = 0x046D, ProductId = 0xC54D },
                },
            };
            var serializer = new XmlSerializer(typeof(AppSettingsData));
            using var writer = new StringWriter();
            serializer.Serialize(writer, data);
            string xml = writer.ToString();
            Assert.Contains("<KeyboardTakeovers>", xml);
            var back = (AppSettingsData)serializer.Deserialize(new StringReader(xml));
            var entry = Assert.Single(back.KeyboardTakeovers);
            Assert.Equal((NodeId, "Pad & Co", (ushort)0x046D, (ushort)0xC54D), (entry.NodeId, entry.Name, entry.VendorId, entry.ProductId));
            Assert.Equal(new Guid("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"), entry.Row);
            Assert.True(KeyboardTakeovers.IsValid(entry));
            Assert.False(KeyboardTakeovers.IsValid(new KeyboardTakeoverEntry { NodeId = NodeId, BindId = "x", RowId = "not a guid" }));
        }
    }

    /// <summary>
    /// A keyboard PadForge's driver serves never feeds the input hooks or
    /// HidHide, whose rules key on keys and HID instances it no longer has
    /// (#549), and its row offers neither toggle. The takeover list is a
    /// static, so these run in the statics collection.
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public class KeyboardTakeoverGateTests : IDisposable
    {
        private static readonly Guid Taken = new("12345678-1234-1234-1234-123456789abc");

        public KeyboardTakeoverGateTests()
        {
            KeyboardTakeovers.Apply(new[]
            {
                new KeyboardTakeoverEntry { NodeId = @"USB\VID_1\1", BindId = @"USB\VID_1", RowId = Taken.ToString() },
                null,
                new KeyboardTakeoverEntry { NodeId = "", BindId = "x", RowId = Guid.NewGuid().ToString() },
            });
        }

        public void Dispose() => KeyboardTakeovers.Apply(null);

        [Fact]
        public void OnlyValidEntriesAreKept_AsCopies()
        {
            var entry = Assert.Single(KeyboardTakeovers.Current);
            Assert.Equal(Taken, entry.Row);
            Assert.True(KeyboardTakeovers.IsTakenOver(Taken));
            Assert.False(KeyboardTakeovers.IsTakenOver(Guid.NewGuid()));
            Assert.False(KeyboardTakeovers.IsTakenOver(Guid.Empty));
        }

        [Fact]
        public void ADeviceServedByPadForgesDriver_IsLeftOutOfTheHooksAndHidHide()
        {
            Assert.True(InputService.ServedByPadForgeDriver(new UserDevice { InstanceGuid = Taken }));
            Assert.True(InputService.ServedByPadForgeDriver(new UserDevice
            {
                InstanceGuid = Guid.NewGuid(),
                DevicePath = WinUsbKeyboardDeviceTests.Path,
            }));
            Assert.False(InputService.ServedByPadForgeDriver(new UserDevice
            {
                InstanceGuid = Guid.NewGuid(),
                DevicePath = @"\\?\HID#VID_048D&PID_C197&Col04#8&11b458cb&0&0003#{884b96c3-56ef-11d1-bc8c-00a0c91405dd}",
            }));
            Assert.False(InputService.ServedByPadForgeDriver(null));
        }

        [Fact]
        public void TheRowOfAKeyboardPadForgesDriverServes_OffersNeitherToggle()
        {
            var bound = new DeviceRowViewModel { DeviceTypeKey = "Keyboard", DevicePath = WinUsbKeyboardDeviceTests.Path };
            Assert.True(bound.IsPadForgeDriverRow);
            Assert.False(bound.ShowConsumeToggle);
            Assert.False(bound.ShowHidHideToggle);
            Assert.False(bound.ShowInputHidingSection);

            var hid = new DeviceRowViewModel
            {
                DeviceTypeKey = "Keyboard",
                DevicePath = @"\\?\HID#VID_048D&PID_C197&Col04#8&11b458cb&0&0003#{884b96c3-56ef-11d1-bc8c-00a0c91405dd}",
            };
            Assert.False(hid.IsPadForgeDriverRow);
            Assert.True(hid.ShowConsumeToggle);
            Assert.True(hid.ShowHidHideToggle);

            // A taken keyboard whose row still holds its HID path, before
            // the device pass opened it over WinUSB.
            var pending = new DeviceRowViewModel { DeviceTypeKey = "Keyboard", DevicePath = hid.DevicePath, InstanceGuid = Taken };
            Assert.True(pending.IsPadForgeDriverRow);
            Assert.False(pending.ShowConsumeToggle);
        }

        /// <summary>The switched keyboard keeps its row, whose path turns to
        /// its WinUSB interface: the page reads its offer again then, so the
        /// restore shows without selecting the row again. A row no longer
        /// selected is no longer followed.</summary>
        [Fact]
        public void TheSelectedRowsNewPath_ReadsTheOfferAgain()
        {
            var vm = new DevicesViewModel();
            var row = new DeviceRowViewModel { DeviceTypeKey = "Keyboard", DevicePath = @"\\?\HID#VID_1&PID_2#3&4#{884b96c3-56ef-11d1-bc8c-00a0c91405dd}" };
            vm.SelectedDevice = row;
            int reads = 0;
            vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(DevicesViewModel.ShowDriverRestore)) reads++; };

            row.DevicePath = WinUsbKeyboardDeviceTests.Path;
            Assert.Equal(1, reads);

            vm.SelectedDevice = null;
            row.DevicePath = @"\\?\HID#VID_1&PID_2#3&4#{884b96c3-56ef-11d1-bc8c-00a0c91405dd}";
            Assert.Equal(2, reads);   // the deselect's own read, and none for the path
        }
    }

    /// <summary>
    /// The device pass's phase for keyboards bound to WinUSB (#549), the
    /// TouchSense mice's phase for keyboards: a worker finds and opens the
    /// bound keyboards, and the poll thread registers each row under the ID
    /// its takeover recorded and retires a row whose channel closed. The
    /// look, the open, the clock and the worker are the test's.
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public class WinUsbKeyboardLaneTests : IDisposable
    {
        private const string Path = WinUsbKeyboardDeviceTests.Path;
        private static readonly Guid RowId = new("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
        private static readonly WinUsbKeyboard Pad = new(Path, 0x048D, 0xC197, RowId, "Macro Pad");

        private readonly DeviceCollection _devices = SettingsManager.UserDevices;
        private readonly SettingsCollection _settings = SettingsManager.UserSettings;

        public WinUsbKeyboardLaneTests()
        {
            SettingsManager.UserDevices = new DeviceCollection();
            SettingsManager.UserSettings = new SettingsCollection();
            KeyboardTakeovers.Apply(new[]
            {
                new KeyboardTakeoverEntry { NodeId = @"USB\VID_048D&PID_C197\7&14999c31&0&4", BindId = @"USB\VID_048D&PID_C197", RowId = RowId.ToString(), Name = "Macro Pad", VendorId = 0x048D, ProductId = 0xC197 },
            });
        }

        public void Dispose()
        {
            KeyboardTakeovers.Apply(null);
            SettingsManager.UserDevices = _devices;
            SettingsManager.UserSettings = _settings;
        }

        private sealed class Lane : IDisposable
        {
            private long _now = 1_000_000;
            private readonly ConcurrentQueue<Task> _sweeps = new();
            public readonly InputManager Manager = new();
            public readonly TouchSenseChannels Table = new();
            public readonly List<FakeKeyboardChannel> Opened = new();
            public volatile WinUsbKeyboard[] Bound = Array.Empty<WinUsbKeyboard>();
            public bool FailOpens;

            public Lane()
            {
                Manager.WinUsbKeyboardFind = () => Bound;
                Manager.WinUsbKeyboardOpen = keyboard =>
                {
                    if (FailOpens) return null;
                    var channel = new FakeKeyboardChannel(keyboard.Path);
                    lock (Opened) Opened.Add(channel);
                    return channel;
                };
                Manager.WinUsbKeyboardTable = Table;
                Manager.WinUsbKeyboardTicks = () => Interlocked.Read(ref _now);
                Manager.WinUsbKeyboardStartReaders = false;
                Manager.WinUsbKeyboardRun = sweep =>
                {
                    var task = new Task(sweep);
                    _sweeps.Enqueue(task);
                    return task;
                };
            }

            public void Advance(long ms) => Interlocked.Add(ref _now, ms);
            public bool Pass() => Manager.SyncWinUsbKeyboards();

            public bool RunSweep()
            {
                if (!_sweeps.TryDequeue(out var sweep)) return false;
                sweep.RunSynchronously();
                return true;
            }

            public bool Cycle()
            {
                bool changed = Pass();
                RunSweep();
                return Pass() | changed;
            }

            public UserDevice Record(Guid id)
            {
                lock (SettingsManager.UserDevices.SyncRoot)
                    return SettingsManager.UserDevices.Items.SingleOrDefault(d => d.InstanceGuid == id);
            }

            public void Dispose()
            {
                while (RunSweep()) { }
                Manager.RetireWinUsbKeyboards();
            }
        }

        [Fact]
        public void ABoundKeyboard_GetsItsRowBack_UnderTheIdItsTakeoverRecorded()
        {
            using var lane = new Lane();
            Assert.Same(TouchSenseChannels.Shared, new InputManager().WinUsbKeyboardTable);
            lane.Bound = new[] { Pad };
            Assert.True(lane.Cycle());
            var record = lane.Record(RowId);
            Assert.NotNull(record);
            Assert.True(record.IsOnline);
            var row = Assert.IsType<WinUsbKeyboardDevice>(record.Device);
            Assert.Equal(InputDeviceType.Keyboard, record.CapType);
            Assert.Equal(Path, record.DevicePath);
            Assert.Same(row, Assert.Single(lane.Manager.WinUsbKeyboardRows));
            // The table's lookup is the TouchSense backend's, for mice: a
            // keyboard's channel is listed but is no mouse's.
            Assert.Null(lane.Table.Find(Path));
            Assert.Equal(1, lane.Table.Count);

            // Its keys reach the record.
            row.Feed(new byte[] { 0x00, 0x00, 0x28, 0, 0, 0, 0, 0 });
            Assert.True(record.Device.GetCurrentState().Buttons[0x0D]);

            // Nothing changes while it stays, and it is opened once.
            lane.Advance(InputManager.WinUsbKeyboardSweepMs);
            Assert.False(lane.Cycle());
            Assert.Single(lane.Opened);
        }

        [Fact]
        public void ADriverSwitchsHold_ClosesTheKeyboardsChannel_AndTheRowRetires()
        {
            using var lane = new Lane();
            lane.Bound = new[] { Pad };
            Assert.True(lane.Cycle());
            var channel = lane.Opened[0];
            Assert.Null(lane.Table.Find(Path));   // no mouse's channel
            using (lane.Table.Hold(@"USB\VID_048D&PID_C197\7&14999c31&0&4"))
            {
                Assert.False(channel.IsOpen);
                Assert.True(lane.Pass());
                var record = lane.Record(RowId);
                Assert.False(record.IsOnline);
                Assert.Null(record.Device);
                Assert.Empty(lane.Manager.WinUsbKeyboardRows);
                // While held, the sweep opens nothing on the node.
                lane.Advance(InputManager.WinUsbKeyboardReopenMs);
                lane.Cycle();
                Assert.Single(lane.Opened);
            }
            lane.Advance(InputManager.WinUsbKeyboardReopenMs);
            Assert.True(lane.Cycle());
            Assert.Equal(2, lane.Opened.Count);
            Assert.True(lane.Record(RowId).IsOnline);
        }

        [Fact]
        public void APcWithNoTakeover_LooksForNoBoundKeyboard()
        {
            KeyboardTakeovers.Apply(null);
            using var lane = new Lane();
            int looks = 0;
            lane.Manager.WinUsbKeyboardFind = () => { looks++; return Array.Empty<WinUsbKeyboard>(); };
            Assert.False(lane.Cycle());
            Assert.Equal(0, looks);
        }

        [Fact]
        public void AKeyboardThatCannotBeOpened_IsTriedAgainAfterTheWait()
        {
            using var lane = new Lane();
            lane.Bound = new[] { Pad };
            lane.FailOpens = true;
            Assert.False(lane.Cycle());
            Assert.Null(lane.Record(RowId));
            lane.FailOpens = false;
            lane.Advance(InputManager.WinUsbKeyboardSweepMs);
            Assert.False(lane.Cycle());
            Assert.Empty(lane.Opened);
            lane.Advance(InputManager.WinUsbKeyboardReopenMs);
            Assert.True(lane.Cycle());
            Assert.Single(lane.Opened);
        }

        [Fact]
        public void TheShutdown_ClosesEveryKeyboard_AndNothingOpensAfterIt()
        {
            var lane = new Lane();
            lane.Bound = new[] { Pad };
            Assert.True(lane.Cycle());
            lane.Manager.RetireWinUsbKeyboards();
            Assert.False(lane.Opened[0].IsOpen);
            Assert.False(lane.Record(RowId).IsOnline);
            lane.Advance(InputManager.WinUsbKeyboardReopenMs);
            Assert.False(lane.Cycle());
            Assert.Single(lane.Opened);
            lane.Dispose();
        }
    }
}
