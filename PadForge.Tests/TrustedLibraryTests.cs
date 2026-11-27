using System;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using PadForge.Engine.Common;
using Xunit;
using Level = PadForge.Engine.Common.ChangeRule.Level;

namespace PadForge.Tests
{
    /// <summary>
    /// A vendor library loads into PadForge, which runs with rights, only
    /// when only administrators could have put it in place and its vendor
    /// signed it, held open from the check through the load (#597).
    /// </summary>
    public class TrustedLibraryTests
    {
        private const string Admins = "S-1-5-32-544", System = "S-1-5-18", Users = "S-1-5-32-545", AuthUsers = "S-1-5-11",
            TrustedInstaller = "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464", CreatorOwner = "S-1-3-0";

        private static readonly FileSystemRights ReadRun = FileSystemRights.ReadAndExecute | FileSystemRights.Synchronize;

        /// <summary>One allow entry, as a test writes it.</summary>
        private readonly record struct Entry(string Sid, FileSystemRights Rights, bool InheritOnly);

        /// <summary>The self-relative descriptor Windows would give for that
        /// owner and those allow entries, an inherit-only one inherited by
        /// files and folders alike.</summary>
        internal static byte[] Descriptor(string owner, params (string Sid, int Rights, bool InheritOnly)[] entries)
        {
            var acl = new RawAcl(GenericAcl.AclRevision, entries.Length);
            foreach (var (sid, rights, inheritOnly) in entries)
                acl.InsertAce(acl.Count, new CommonAce(
                    inheritOnly ? AceFlags.InheritOnly | AceFlags.ObjectInherit | AceFlags.ContainerInherit : AceFlags.None,
                    AceQualifier.AccessAllowed, rights, new SecurityIdentifier(sid), false, null));
            var sd = new RawSecurityDescriptor(ControlFlags.DiscretionaryAclPresent | ControlFlags.SelfRelative,
                owner == null ? null : new SecurityIdentifier(owner), null, null, acl);
            byte[] bytes = new byte[sd.BinaryLength];
            sd.GetBinaryForm(bytes, 0);
            return bytes;
        }

        /// <summary>The one rule on that descriptor at a vendor's
        /// level.</summary>
        private static bool OthersMayChange(string owner, Entry[] entries, Level level)
            => ChangeRule.Judge(Descriptor(owner, Array.ConvertAll(entries, e => (e.Sid, (int)e.Rights, e.InheritOnly))), level)
               != ChangeRule.Verdict.Sound;

        [Fact]
        public void ProgramFiles_PassesTheRule()
        {
            // As Program Files and a vendor folder in it stand: owned by
            // TrustedInstaller or Administrators, Users read and run, and
            // CREATOR OWNER's entry for what is made inside.
            var folder = new[]
            {
                new Entry(TrustedInstaller, FileSystemRights.FullControl, false),
                new Entry(System, FileSystemRights.FullControl, false),
                new Entry(Admins, FileSystemRights.FullControl, false),
                new Entry(Users, ReadRun, false),
                new Entry(CreatorOwner, FileSystemRights.FullControl, true),
            };
            Assert.False(OthersMayChange(TrustedInstaller, folder, Level.VendorFile));
            Assert.False(OthersMayChange(Admins, folder, Level.VendorFolder));
            Assert.False(OthersMayChange(System, folder, Level.VendorAbove));
        }

        [Fact]
        public void AFolderOthersCanAddTo_OrAnOwnerOfTheirs_Fails()
        {
            // C:\SteamVR on this PC: Authenticated Users modify, from C:\.
            var steamVr = new[] { new Entry(Admins, FileSystemRights.FullControl, false), new Entry(AuthUsers, FileSystemRights.Modify, false) };
            Assert.True(OthersMayChange(Admins, steamVr, Level.VendorFolder));
            Assert.True(OthersMayChange(Admins, steamVr, Level.VendorFile));
            Assert.True(OthersMayChange(Admins, steamVr, Level.VendorAbove));
            var readOnly = new[] { new Entry(Users, ReadRun, false) };
            Assert.True(OthersMayChange("S-1-5-21-1-2-3-1001", readOnly, Level.VendorFile));
            Assert.True(OthersMayChange(null, readOnly, Level.VendorFile));
            Assert.True(OthersMayChange(Admins, new[] { new Entry(Users, FileSystemRights.AppendData, false) }, Level.VendorFolder));
            Assert.True(OthersMayChange(Admins, new[] { new Entry(Users, FileSystemRights.ChangePermissions, false) }, Level.VendorAbove));
            Assert.True(OthersMayChange(Admins, new[] { new Entry(Users, FileSystemRights.Delete, false) }, Level.VendorAbove));
            Assert.True(OthersMayChange(Admins, new[] { new Entry(Users, (FileSystemRights)0x40000000, false) }, Level.VendorFile));
        }

        /// <summary>A folder above may let others make new folders, as the
        /// system drive's root does, and not delete or rename one.</summary>
        [Fact]
        public void AFolderAbove_MayLetOthersMakeFolders_ButNotDeleteThem()
        {
            var root = new[]
            {
                new Entry(Admins, FileSystemRights.FullControl, false),
                new Entry(AuthUsers, FileSystemRights.AppendData, false),
                new Entry(AuthUsers, FileSystemRights.Modify, true),
                new Entry(Users, ReadRun, false),
            };
            Assert.False(OthersMayChange(System, root, Level.VendorAbove));
            Assert.True(OthersMayChange(System, new[] { new Entry(AuthUsers, FileSystemRights.DeleteSubdirectoriesAndFiles, false) }, Level.VendorAbove));
        }

        [Fact]
        public void TheSigner_IsTheVendors()
        {
            Assert.True(TrustedLibrary.SignedBy("Logitech Inc", TrustedLibrary.Logitech));
            Assert.True(TrustedLibrary.SignedBy("Logitech Europe S.A.", TrustedLibrary.Logitech));
            Assert.True(TrustedLibrary.SignedBy("Corsair Memory, Inc.", TrustedLibrary.Corsair));
            Assert.True(TrustedLibrary.SignedBy("Micro-Star International CO., LTD.", TrustedLibrary.MicroStar));
            Assert.True(TrustedLibrary.SignedBy("Razer USA Ltd.", TrustedLibrary.Razer));
            Assert.False(TrustedLibrary.SignedBy("Micro-Star", TrustedLibrary.MicroStar));
            Assert.False(TrustedLibrary.SignedBy("Not Logitech", TrustedLibrary.Logitech));
            Assert.False(TrustedLibrary.SignedBy(null, TrustedLibrary.Logitech));
        }

        /// <summary>On this PC: the runtime's own library, signed by
        /// Microsoft in a folder of Program Files, passes for its signer and
        /// fails for another, and the same file in a folder the user can
        /// write fails before any signature is read. The hold refuses writes
        /// while it stands.</summary>
        [Fact]
        public void OnThisPc_TheRuleReadsRealAccessListsAndSignatures()
        {
            string library = typeof(object).Assembly.Location;
            Assert.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), library, StringComparison.OrdinalIgnoreCase);
            using (var held = TrustedLibrary.Hold(library, new[] { ".NET" }, out string reason))
            {
                Assert.True(held != null, reason);
                Assert.Null(reason);
            }
            Assert.Null(TrustedLibrary.Hold(library, TrustedLibrary.Logitech, out string other));
            // The runtime's signer is named ".NET".
            Assert.Equal("signed by .NET", other);

            string folder = Path.Combine(Path.GetTempPath(), "PadForgeTests", "trusted-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            try
            {
                string planted = Path.Combine(folder, "LogitechLcd.dll");
                File.Copy(library, planted);
                Assert.Null(TrustedLibrary.Hold(planted, new[] { ".NET" }, out string why));
                Assert.StartsWith("others may change", why, StringComparison.Ordinal);
                Assert.Equal(IntPtr.Zero, TrustedLibrary.Load(planted, new[] { ".NET" }, _ => throw new InvalidOperationException("loaded"), out why));
                Assert.Null(TrustedLibrary.Hold(Path.Combine(folder, "none.dll"), TrustedLibrary.Logitech, out why));
                Assert.Equal("missing", why);

                // The hold leaves the load room: Windows' loader opens the
                // image for reading and running, which the hold shares.
                string native = Path.Combine(folder, "padforge-hold-probe.dll");
                File.Copy(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "version.dll"), native);
                TrustedLibrary.CheckSeam = (_, _) => null;
                IntPtr module = TrustedLibrary.Load(native, TrustedLibrary.Logitech,
                    p => global::System.Runtime.InteropServices.NativeLibrary.TryLoad(p, out IntPtr m) ? m : IntPtr.Zero, out why);
                Assert.True(module != IntPtr.Zero, why);
                global::System.Runtime.InteropServices.NativeLibrary.Free(module);

                // While held, the file cannot be written or deleted.
                using (var held = TrustedLibrary.Hold(planted, TrustedLibrary.Logitech, out _))
                {
                    Assert.NotNull(held);
                    Assert.Throws<IOException>(() => File.WriteAllText(planted, "x"));
                    Assert.Throws<IOException>(() => File.Delete(planted));
                }
            }
            finally
            {
                TrustedLibrary.CheckSeam = null;
                try { Directory.Delete(folder, true); } catch { }
            }
        }

        /// <summary>A library signed through a catalog, as Windows' own files
        /// are, passes on its catalog's signer: the check is the one reader
        /// of signatures (AuthenticodeSigner), run on the held handle. A
        /// one-byte change in a copy leaves no signer, and the copy fails on
        /// its signature once its place is let pass.</summary>
        [Fact]
        public void ACatalogSignedLibrary_PassesOnItsCatalogsSigner_ReadOnTheHeldFile()
        {
            string system = Environment.GetFolderPath(Environment.SpecialFolder.System);
            string cmd = Path.Combine(system, "cmd.exe");
            using (var held = TrustedLibrary.Hold(cmd, new[] { "Microsoft Windows" }, out string reason))
            {
                Assert.True(held != null, reason);
                Assert.Equal("Microsoft Windows", AuthenticodeSigner.OfHeld(cmd, ((FileStream)held).SafeFileHandle));
            }
            Assert.Null(TrustedLibrary.Hold(cmd, TrustedLibrary.Razer, out string other));
            Assert.Equal("signed by Microsoft Windows", other);

            string folder = Path.Combine(Path.GetTempPath(), "PadForgeTests", "trusted-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            try
            {
                string changed = Path.Combine(folder, "changed.exe");
                byte[] bytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "testhost.exe"));
                bytes[bytes.Length / 2] ^= 0xFF;
                File.WriteAllBytes(changed, bytes);
                using (var stream = new FileStream(changed, FileMode.Open, FileAccess.Read, FileShare.Read))
                    Assert.Null(AuthenticodeSigner.OfHeld(changed, stream.SafeFileHandle));
                Assert.Null(AuthenticodeSigner.OfHeld(changed, null));
            }
            finally
            {
                try { Directory.Delete(folder, true); } catch { }
            }
        }

        /// <summary>Every vendor library loaded into PadForge by path goes
        /// through the rule with its vendor, and Razer's runtime is held
        /// while the provider loads it.</summary>
        [Fact]
        public void EveryVendorLoadInPadForge_GoesThroughTheRule()
        {
            string Text(params string[] path) => AuditDelta20261002Tests.RepoText(path);
            Assert.Contains("TrustedLibrary.Load(candidate, TrustedLibrary.Logitech,", Text("PadForge.Engine", "Common", "Logitech", "LogitechLcdLibrary.cs"), StringComparison.Ordinal);
            Assert.Contains("TrustedLibrary.Load(candidate, TrustedLibrary.Logitech,", Text("PadForge.Engine", "Common", "Logitech", "LogitechArxLibrary.cs"), StringComparison.Ordinal);
            Assert.Contains("TrustedLibrary.Load(candidate, TrustedLibrary.Logitech,", Text("PadForge.Engine", "Common", "Logitech", "LogitechGKeySource.cs"), StringComparison.Ordinal);
            Assert.Contains("TrustedLibrary.Load(path, PadForge.Engine.Common.TrustedLibrary.Logitech,", Text("PadForge.App", "Services", "LogiLedEngineNative.cs"), StringComparison.Ordinal);
            Assert.Contains("TrustedLibrary.Load(path, PadForge.Engine.Common.TrustedLibrary.Corsair,", Text("PadForge.App", "Services", "IcueSdkNative.cs"), StringComparison.Ordinal);
            string msi = Text("PadForge.App", "Common", "Input", "Peripherals", "MysticLightSdk.cs");
            Assert.Contains("TrustedLibrary.Load(path, PadForge.Engine.Common.TrustedLibrary.MicroStar,", msi, StringComparison.Ordinal);
            Assert.Contains("exe = PadForge.Engine.Common.MachineEnvironment.Expand(exe);", msi, StringComparison.Ordinal);
            Assert.DoesNotContain("Environment.ExpandEnvironmentVariables", msi, StringComparison.Ordinal);
            string sensa = Text("PadForge.App", "Services", "SensaHapticsService.cs");
            Assert.Contains("if (runtime != null) providerUp = Provider.ProviderInit();", sensa, StringComparison.Ordinal);
        }

        /// <summary>Microsoft ships the MIDI App SDK runtime unsigned, so its
        /// place alone decides: on this PC the machine's registration names
        /// it in Program Files, and it passes. A copy the user could change
        /// fails.</summary>
        [Fact]
        public void TheMidiRuntime_IsJudgedByItsPlace()
        {
            using (var held = PadForge.Common.Input.MidiBackendAppSdk.HoldRuntime(out string why))
            {
                if (held == null) Assert.Equal("the runtime is not registered for this machine", why);
                else Assert.Null(why);
            }
            string folder = Path.Combine(Path.GetTempPath(), "PadForgeTests", "midi-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            try
            {
                string copy = Path.Combine(folder, "Microsoft.Windows.Devices.Midi2.dll");
                File.WriteAllText(copy, "x");
                PadForge.Common.Input.MidiBackendAppSdk.RuntimePathSeam = () => copy;
                Assert.Null(PadForge.Common.Input.MidiBackendAppSdk.HoldRuntime(out string why));
                Assert.StartsWith("others may change", why, StringComparison.Ordinal);
            }
            finally
            {
                PadForge.Common.Input.MidiBackendAppSdk.RuntimePathSeam = null;
                try { Directory.Delete(folder, true); } catch { }
            }
        }

        /// <summary>The provider is started only for a runtime in an
        /// InstallDir, in the provider's order. With none, it would look
        /// beside PadForge.exe, so it is not started.</summary>
        [Fact]
        public void RazersRuntime_IsTakenOnlyFromItsInstallDir()
        {
            string folder = Path.Combine(Path.GetTempPath(), "PadForgeTests", "razer-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            try
            {
                PadForge.Services.SensaHapticsService.InstallDirSeam = key => key == PadForge.Services.SensaHapticsService.RuntimeKeys[1] ? folder : null;
                Assert.Null(PadForge.Services.SensaHapticsService.RuntimePath());
                File.WriteAllText(Path.Combine(folder, PadForge.Services.SensaHapticsService.RuntimeName), "x");
                Assert.Equal(Path.Combine(folder, "RzInterHaptics.dll"), PadForge.Services.SensaHapticsService.RuntimePath());
            }
            finally
            {
                PadForge.Services.SensaHapticsService.InstallDirSeam = null;
                try { Directory.Delete(folder, true); } catch { }
            }
        }
    }
}
