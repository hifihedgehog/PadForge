using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using Microsoft.Win32;
using Nefarius.Utilities.DeviceManagement.Extensions;
using Nefarius.Utilities.DeviceManagement.PnP;

namespace PadForge.Services
{
    /// <summary>
    /// Installs and arms the Nefarius BthPS3 (L2CAP profile) + BthPS3PSM (BTHUSB lower
    /// class filter) drivers so a DualShock 3 can connect over the shared radio, and
    /// binds the docked pad to WinUSB so its magic sixpair report can be sent. Same
    /// eight-step, reboot-free sequence BthPS3's own MSI performs, but driven from the
    /// always-elevated app with the drivers embedded (no MSI, no DsHidMini). Every
    /// step is grounded in BthPS3's installer (BthPS3Installer/CustomActions.cs) and
    /// the drivers' own INFs.
    ///
    /// The critical detail vs. a naive install: the radio is re-enumerated with
    /// UsbPnPDevice.CyclePort() (IOCTL_USB_HUB_CYCLE_PORT), NOT Disable/Enable, which
    /// would leave the devnode flagged CM_PROB_NEED_RESTART (pending reboot). The
    /// profile driver ships RawPDO=0/ExclusivePDO=1; the DsHidMini-less raw reader
    /// needs RawPDO=1 (enumerate with no function driver) and ExclusivePDO=0 (shared
    /// open), so those are rewritten.
    /// </summary>
    internal static class Ds3DriverInstaller
    {
        // Bluetooth device setup class (BthPS3PSM registers as its lower filter).
        private static readonly Guid BluetoothClass = new Guid("e0cbf06c-cd8b-4647-bb8a-263b43f0f974");
        // Radio device-interface GUID (robust radio locate, HostRadio.cs:134).
        private static readonly Guid RadioInterface = new Guid("92383b0e-f90e-4ac9-8d44-8c2d0d0ebda2");
        // BTHPS3_SERVICE_GUID name (BthPS3.h:81) - advertising this service (via the
        // by-ref BthPs3ServiceGuidLocal below) spawns the profile PDO.
        private const string BthPs3ServiceName = "BthPS3Service";

        private const string BthPs3ParamsKey =
            @"SYSTEM\CurrentControlSet\Services\BthPS3\Parameters";

        // PSM filter control IOCTLs (BthPS3.h:400-405). Both take a 4-byte
        // { ULONG DeviceIndex } input; DeviceIndex is the plain index into the
        // filter's per-radio collection. A bad index completes with
        // STATUS_NO_SUCH_DEVICE (Sideband.c:317), surfaced as
        // ERROR_NO_SUCH_DEVICE, which ends the multi-radio sweep.
        private const uint IOCTL_BTHPS3PSM_ENABLE_PSM_PATCHING = 0x2AAC04;
        private const uint IOCTL_BTHPS3PSM_DISABLE_PSM_PATCHING = 0x2AAC08;
        private const int ERROR_NO_SUCH_DEVICE = 433;
        private const string PsmControlPath = @"\\.\BthPS3PSMControl";

        // ── public entry points used by Ds3PairingService ────────────────────────

        /// <summary><para>Installs + arms the BthPS3 stack if it isn't already
        /// present. Idempotent: when the service already runs it only
        /// reconciles the two consumer registry values. Returns true when the
        /// stack is operable.</para>
        ///
        /// <para>The stack is TWO drivers and the guard asks about both. It
        /// used to ask only whether the BthPS3 profile service existed, so a
        /// machine that got the profile driver and not the PSM filter stayed
        /// that way permanently: every later call short-circuited on the half
        /// that was present. That state is not hypothetical, it is what
        /// discussion #283's log shows, `patching=True` immediately followed
        /// by `PSM control device not present`. Without the filter nothing
        /// rewrites the DS3's reserved PSMs, so the pad can never reach the
        /// profile driver and Bluetooth silently never works.</para></summary>
        public static bool EnsureInstalled(Action<string> log)
        {
            try
            {
                if (IsServiceInstalled("BthPS3"))
                {
                    EnsureConsumerParams();       // keep RawPDO=1/ExclusivePDO=0 if a prior install set them wrong
                    if (!IsPsmFilterPresent())
                        RepairPsmFilter(log);
                    EnsurePsmPatch(log);
                    return true;
                }

                // Heal the shell an older build could leave behind: a
                // Services\BthPS3 key with no ImagePath, created as a side
                // effect of writing Parameters before the driver existed.
                // Machines already in that state cannot recover on their own,
                // because nothing else ever removes it and the install below
                // is what would have replaced it. Gated on the exact damaged
                // shape, so a real installed service is never touched.
                if (HasOrphanedBthPs3Key())
                {
                    log("Clearing an incomplete PlayStation Bluetooth registration.");
                    try
                    {
                        Registry.LocalMachine.DeleteSubKeyTree(
                            @"SYSTEM\CurrentControlSet\Services\BthPS3", throwOnMissingSubKey: false);
                    }
                    catch (Exception ex) { log("Could not clear it: " + ex.Message); }
                }

                log("Installing PlayStation Bluetooth drivers (one time)...");
                string dir = ExtractDrivers();

                // 1. filter INF (driver store + BthPS3PSM kernel service)
                InstallInf(Path.Combine(dir, "BthPS3PSM_x64", "BthPS3PSM.inf"), log);
                // 2. register it as the Bluetooth-class lower filter
                DeviceClassFilters.AddLower(BluetoothClass, "BthPS3PSM");
                log("Registered PSM filter.");
                // 3. reboot-free radio re-enumeration so the filter attaches
                CycleBluetoothRadio(log);
                // 4/5. profile driver + raw-PDO placeholder into the store
                InstallInf(Path.Combine(dir, "BthPS3_x64", "BthPS3.inf"), log);
                InstallInf(Path.Combine(dir, "BthPS3_x64", "BthPS3_PDO_NULL_Device.inf"), log);
                // 6. consumer registry (raw, shared)
                EnsureConsumerParams();
                // 7. advertise the profile service -> spawns the PDO, loads BthPS3.sys
                bool advertised = EnableBthPs3Service(log);
                if (!advertised)
                {
                    // One retry after a fresh cycle: the advertisement is the
                    // step that creates the profile driver's PDO, so skipping
                    // it silently leaves an install that can never carry a pad.
                    CycleBluetoothRadio(log);
                    advertised = EnableBthPs3Service(log);
                }
                // Params again, now that the service certainly exists. Step 6
                // lands them on this machine (proof: the INF's own defaults are
                // RawPDO 0 / ExclusivePDO 1 and a healthy install reads 1 / 0,
                // so our write came after the INF's AddReg), but that depends
                // on step 4 having created the service, and the raw-PDO reader
                // is dead if the override is ever missed. Idempotent, and now
                // gated on the service being real, so it cannot fabricate a key.
                EnsureConsumerParams();
                // 8. arm the PSM patch (belt-and-suspenders; AutoEnableFilter also does it)
                EnsurePsmPatch(log);

                // The BthPS3 SERVICE is created by PnP matching bthps3.inf to
                // the PDO the advertisement spawns, and that is ASYNCHRONOUS:
                // checking synchronously raced it and declared failure on an
                // install that was seconds from succeeding. Wait, and when the
                // PDO does not materialize at all, cycle the radio and wait
                // again, because on hardware whose port cycle was refused the
                // radio has not re-enumerated since the filter registration
                // and the advertisement (the arcade-PC MT7925 case: a manual
                // adapter toggle was what let the service appear).
                bool ok = WaitForCondition(() => IsServiceInstalled("BthPS3"), 10000, 500);
                if (!ok)
                {
                    log("Profile driver has not attached yet; re-enumerating the radio.");
                    CycleBluetoothRadio(log);
                    ok = WaitForCondition(() => IsServiceInstalled("BthPS3"), 15000, 500);
                }
                if (!ok)
                {
                    log(advertised
                        ? "Driver install did not register the service."
                        : "The profile service could not be advertised, so no PlayStation "
                          + "Bluetooth driver was created and the pad cannot connect.");
                    return false;
                }
                // NOW write the consumer params, and not before. This is the
                // first moment the service key genuinely exists: PnP creates it
                // when the advertised PDO matches the INF, which is
                // asynchronous, so both earlier calls hit the "service is not
                // installed" guard and wrote NOTHING on a clean machine. The
                // INF's own default is RawPDO=0, which means the child PDO
                // wants a function driver; the only INF that could serve it
                // matches the bare BTHPS3BUS GUID while the child reports
                // ...&Dev&VID_054C&PID_0268 with no compatible ID, so nothing
                // matches and the child dies at CM_PROB_FAILED_INSTALL. No
                // device interface, nothing for the reader to open, and the pad
                // flashes forever. A SECOND pairing worked only because by then
                // the service existed and this write finally landed.
                bool paramsChanged = EnsureConsumerParams();
                if (paramsChanged)
                {
                    // BthPS3 reads these when it builds the child, so the pad
                    // must arrive AFTER the override is in place. Re-enumerate
                    // so the running driver picks it up rather than carrying
                    // the INF defaults until something else cycles the radio.
                    log("Applying raw-PDO settings; re-enumerating the radio.");
                    CycleBluetoothRadio(log);
                }

                // The service key is not readiness. Patching is what routes
                // the pad's PSM to BthPS3, and it can only be armed once the
                // filter has re-attached after the install's own radio cycle.
                if (EnsurePsmPatch(log) == 0)
                {
                    log("PSM patching could not be armed, so the pad would be refused over Bluetooth.");
                    return false;
                }
                log("Bluetooth drivers installed.");
                return true;
            }
            catch (Exception ex) { log("Driver install failed: " + ex.Message); return false; }
        }

        /// <summary>True when the PSM filter's control device is open-able,
        /// which is the only proof that BthPS3PSM is both installed AND
        /// attached to a radio. The service key alone is not: the filter can
        /// be registered and still not loaded, and it is the loaded filter
        /// that rewrites the DS3's PSMs.</summary>
        public static bool IsPsmFilterPresent()
        {
            IntPtr h = CreateFile(PsmControlPath, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_RW,
                IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_NO_BUFFERING | FILE_FLAG_WRITE_THROUGH, IntPtr.Zero);
            if (h == INVALID_HANDLE) return false;
            CloseHandle(h);
            return true;
        }

        /// <summary><para>Re-runs the filter half of the install: driver
        /// package, class lower-filter registration, and the radio
        /// re-enumeration that makes it attach. Called only when the control
        /// device is absent, because the radio cycle drops every live
        /// Bluetooth device and must never run on a healthy machine.</para></summary>
        private static void RepairPsmFilter(Action<string> log)
        {
            log("PSM filter is missing from a half-installed stack; repairing.");
            try
            {
                string dir = ExtractDrivers();
                InstallInf(Path.Combine(dir, "BthPS3PSM_x64", "BthPS3PSM.inf"), log);
                DeviceClassFilters.AddLower(BluetoothClass, "BthPS3PSM");
                CycleBluetoothRadio(log);
                log(IsPsmFilterPresent()
                    ? "PSM filter repaired."
                    : "PSM filter still absent after repair; a reboot may be required.");
            }
            catch (Exception ex) { log("PSM filter repair failed: " + ex.Message); }
        }

        // ── WinUSB package signing (local machine, like HIDMaestro) ──────────
        //
        // The DS3's magic reports (0xF4 enable, 0xF2/0xF5 sixpair) are not in
        // its HID descriptor, so the inbox HID stack rejects them and the pad
        // has to be bound to inbox winusb.sys through an INF of ours. Windows
        // will not install a driver package whose catalog does not chain to a
        // root the machine trusts, and PadForge cannot buy or earn a code
        // signing certificate.
        //
        // So the package is signed ON THE MACHINE THAT INSTALLS IT, which is
        // exactly what HIDMaestro already does for its own drivers
        // (HIDMaestro.Internal.DriverBuilder.EnsureTestCertificate +
        // GenerateCatalogs). We own our own certificate rather than borrowing
        // HIDMaestro's, because its subject is that SDK's internal detail,
        // and we borrow only its extracted toolchain (Inf2Cat.exe,
        // signtool.exe), which is stable.
        //
        // Shipping a pre-signed catalog is what broke: the one in the repo
        // was signed by a prototype certificate that existed on exactly one
        // developer machine, so USB DualShock 3 and the whole pairing
        // ceremony had never worked for anybody else (discussion #283). The
        // catalog is now generated here, per machine, and no signed artifact
        // ships at all.

        private const string Ds3CertSubject = "CN=PadForge DS3 WinUSB";
        private const string Ds3CertFriendlyName = "PadForge DS3 WinUSB";

        /// <summary>Why the last <see cref="EnsureWinUsbBound"/> failed, so
        /// the pairing dialog reports the actual cause. Re-deriving it from
        /// IsWinUsbPackageTrusted blamed the certificate for every failure,
        /// including a missing tool or a rejected INF, which is the same
        /// class of wrong-cause reporting that made #283 undiagnosable.</summary>
        internal static string LastWinUsbFailure => _lastWinUsbFailure;
        private static volatile string _lastWinUsbFailure;

        /// <summary>Ensures a PadForge code-signing certificate exists in
        /// LocalMachine\My and is trusted in Root + TrustedPublisher, and
        /// returns its thumbprint. Ten-year validity, generated once per
        /// machine, private key never leaves it.</summary>
        internal static string EnsureSigningCertificate()
        {
            using (var my = new X509Store(StoreName.My, StoreLocation.LocalMachine))
            {
                my.Open(OpenFlags.ReadOnly);
                foreach (var c in my.Certificates.Find(
                             X509FindType.FindBySubjectDistinguishedName, Ds3CertSubject, false))
                    if (c.NotAfter > DateTime.Now.AddDays(30) && c.HasPrivateKey)
                        return c.Thumbprint;
            }

            using var rsa = RSA.Create(2048);
            var req = new CertificateRequest(Ds3CertSubject, rsa,
                HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            req.CertificateExtensions.Add(new X509KeyUsageExtension(
                X509KeyUsageFlags.DigitalSignature, critical: false));
            // Code Signing EKU: without it signtool will not use the cert and
            // Windows will not accept the catalog.
            req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
                new OidCollection { new Oid("1.3.6.1.5.5.7.3.3") }, critical: false));
            req.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(
                req.PublicKey, critical: false));

            using var fresh = req.CreateSelfSigned(
                DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(10));
            fresh.FriendlyName = Ds3CertFriendlyName;

            // Re-import with PersistKeySet + MachineKeySet so the private key
            // lands in the machine key store, which is where signtool reads it
            // from. A cert added straight from CreateSelfSigned has an
            // ephemeral key and signtool cannot use it.
            using var persisted = X509CertificateLoader.LoadPkcs12(
                fresh.Export(X509ContentType.Pfx, ""), "",
                X509KeyStorageFlags.PersistKeySet | X509KeyStorageFlags.MachineKeySet
                | X509KeyStorageFlags.Exportable);
            persisted.FriendlyName = Ds3CertFriendlyName;

            foreach (var name in new[] { StoreName.My, StoreName.Root, StoreName.TrustedPublisher })
            {
                using var store = new X509Store(name, StoreLocation.LocalMachine);
                store.Open(OpenFlags.ReadWrite);
                store.Add(persisted);
            }
            return persisted.Thumbprint;
        }

        // Signing writes into one shared staging directory, and two callers
        // can reach it at once: the DS3 monitor thread's auto-bind and the
        // pairing dialog's ceremony. The dialog suppresses the reader first,
        // but a monitor call already inside the bind keeps running, and
        // signing takes about a second. Unserialized, each run deletes the
        // other's catalog mid-sign.
        private static readonly object _signLock = new object();

        /// <summary>Generates and signs ds3_winusb.cat for the staged INF with
        /// this machine's certificate. ALWAYS regenerates: a catalog left in
        /// the staging directory by an earlier run is validly signed and still
        /// chains, so trusting its presence would skip regeneration after the
        /// INF changed and hand pnputil a package whose hashes no longer match
        /// it. That is the same shape as the shipped-catalog bug this replaced,
        /// one layer in.</summary>
        internal static bool SignWinUsbPackage(string dir, Action<string> log)
        {
            lock (_signLock)
            try
            {
                string thumb = EnsureSigningCertificate();
                string tools = HIDMaestro.Internal.DriverBuilder.EnsureExtracted();
                string inf2cat = Path.Combine(tools, "Inf2Cat.exe");
                string signtool = Path.Combine(tools, "signtool.exe");
                if (!File.Exists(inf2cat) || !File.Exists(signtool))
                {
                    log("Driver signing tools are unavailable; cannot prepare the USB driver.");
                    return false;
                }

                foreach (string stale in Directory.GetFiles(dir, "*.cat"))
                    try { File.Delete(stale); } catch { }

                var (rc, output) = RunTool(inf2cat, $"/driver:\"{dir}\" /os:10_X64", dir);
                if (rc != 0) { log("Catalog generation failed: " + output); return false; }

                string cat = Path.Combine(dir, "ds3_winusb.cat");
                if (!File.Exists(cat)) { log("Catalog generation produced no ds3_winusb.cat."); return false; }

                // /sha1 rather than /n: the thumbprint names exactly the cert
                // we just ensured, on a machine that may hold several
                // code-signing certs.
                var (rc2, out2) = RunTool(signtool,
                    $"sign /sm /s My /sha1 {thumb} /fd SHA256 \"{cat}\"", dir);
                if (rc2 != 0) { log("Catalog signing failed: " + out2); return false; }
                // Say so on SUCCESS too. This path's whole history is silent
                // failure, and a log that only speaks when something breaks
                // cannot distinguish "it worked" from "it never ran".
                log($"USB driver package signed with this PC's certificate ({thumb[..8]}).");
                return true;
            }
            catch (Exception ex) { log("Preparing the USB driver failed: " + ex.Message); return false; }
        }

        /// <summary>Runs a build tool and returns its exit code plus merged
        /// output. Async-drain, because a synchronous ReadToEnd on one stream
        /// before WaitForExit deadlocks the moment the child fills the OTHER
        /// stream's 4 KB pipe buffer: the child blocks writing stderr while we
        /// wait for a stdout EOF that only arrives at exit. signtool and
        /// inf2cat both write warnings to stderr as a matter of course. Same
        /// shape as HIDMaestro's DriverBuilder.Run, which documents the same
        /// trap. A timed-out child is killed rather than orphaned, or it keeps
        /// a handle on the catalog and fails the NEXT attempt's delete.</summary>
        private static (int Code, string Output) RunTool(string exe, string args, string workingDir)
        {
            var psi = new System.Diagnostics.ProcessStartInfo(exe, args)
            {
                WorkingDirectory = workingDir,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var p = System.Diagnostics.Process.Start(psi);
            var sb = new System.Text.StringBuilder();
            p.OutputDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
            p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            if (!p.WaitForExit(120000))
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                lock (sb) return (-1, (sb + "\n(timed out after 120 s)").Trim());
            }
            // The overload with no timeout flushes the async readers, so the
            // buffer is complete before it is read.
            p.WaitForExit();
            lock (sb) return (p.ExitCode, sb.ToString().Trim());
        }

        /// <summary>True when the WinUSB package's catalog chains to a root
        /// this machine trusts. Windows refuses a package whose catalog does
        /// not, and the refusal surfaces as a generic install error, so an
        /// unchecked bind reports nothing a user can act on. Checked after
        /// signing as the proof the signing worked.</summary>
        public static bool IsWinUsbPackageTrusted(out string signer)
        {
            signer = null;
            try
            {
                string cat = Path.Combine(ExtractDrivers(), "WinUSB", "ds3_winusb.cat");
                if (!File.Exists(cat)) return false;
                // X509CertificateLoader, the SYSLIB0057 replacement, loads a
                // certificate FILE. Reading the signer out of a signed file
                // is what this needs and CreateFromSignedFile is still the
                // only managed API that does it. Both handles are disposed:
                // the inner one is its own X509Certificate, and this runs up
                // to twice per bind attempt.
#pragma warning disable SYSLIB0057
                using var signed = X509Certificate2.CreateFromSignedFile(cat);
#pragma warning restore SYSLIB0057
                using var cert = new X509Certificate2(signed);
                signer = cert.Subject;
                using var chain = new X509Chain();
                chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                return chain.Build(cert);
            }
            catch { return false; }
        }

        /// <summary>Binds the docked DS3 to inbox WinUSB so its magic reports can be
        /// sent. No-op if the WinUSB interface is already present.</summary>
        public static bool EnsureWinUsbBound(Action<string> log, CancellationToken ct)
        {
            try
            {
                if (Devcon.FindByInterfaceGuid(new Guid("B35924D6-3E16-4A9E-9782-5524A4B79BAC"), out _))
                    return true;   // already bound

                string dir = ExtractDrivers();
                string winusb = Path.Combine(dir, "WinUSB");
                log("Preparing the controller over USB...");

                // Sign the package with this machine's own certificate before
                // installing it. No signed artifact ships, so this is not a
                // fallback: it is how the package comes to exist. Run
                // unconditionally rather than trusting a catalog an earlier
                // run left in the staging directory, which would still chain
                // happily while covering a previous version of the INF.
                if (!SignWinUsbPackage(winusb, log))
                {
                    _lastWinUsbFailure = "sign-failed";
                    return false;
                }
                if (!IsWinUsbPackageTrusted(out string signer))
                {
                    log($"WinUSB driver package is still untrusted (signer: {signer ?? "unknown"}); "
                        + "Windows would refuse the install, so the DS3 stays on the inbox HID driver.");
                    _lastWinUsbFailure = "driver-untrusted";
                    return false;
                }
                _lastWinUsbFailure = null;
                InstallInf(Path.Combine(winusb, "ds3_winusb.inf"), log);

                // The bind takes a moment to re-enumerate the USB node.
                for (int i = 0; i < 20 && !ct.IsCancellationRequested; i++)
                {
                    if (Devcon.FindByInterfaceGuid(new Guid("B35924D6-3E16-4A9E-9782-5524A4B79BAC"), out _))
                        return true;
                    Thread.Sleep(250);
                }
                return Devcon.FindByInterfaceGuid(new Guid("B35924D6-3E16-4A9E-9782-5524A4B79BAC"), out _);
            }
            catch (Exception ex) { log("WinUSB bind failed: " + ex.Message); return false; }
        }

        // Two radio cycles overlapping is a path into the BthPS3 freed-context BSOD
        // (0xD1, 2026-07-09). Ds3PairingService._radioGate serializes the pair/unpair
        // SEQUENCES; this lock serializes the cycle PRIMITIVE itself, so a caller
        // outside the gate (e.g. the one-time driver install) can't overlap a gated
        // cycle either.
        private static readonly object _cycleLock = new object();

        /// <summary><para>Every setup class a docked DS3 can occupy. A device node's
        /// class comes from the INF that OWNS it, not from the bus it hangs off, so
        /// the pad moves between these as its driver changes. Searching one class
        /// misses the pad in every state but that one.</para>
        ///
        /// <para>This list was a single entry, GUID_DEVCLASS_USB, and that is the one
        /// class a DS3 is never in (#265). GUID_DEVCLASS_USB holds host controllers,
        /// root hubs, hubs and composite parents. A USB function device bound to the
        /// inbox HidUsb sits in HIDCLASS, because HidUsb is installed by input.inf
        /// with Class=HIDClass. So the gate returned false for exactly the state it
        /// exists to detect, and the background auto-bind was unreachable from
        /// 4.0.1 through 4.1.0.</para>
        ///
        /// <para>Completeness here is safe because it does not decide anything. The
        /// SERVICE allowlist below is the gate, and it is unchanged: only an empty
        /// service or HidUsb is ever bound. Widening the search only lets the
        /// allowlist see a device it was always meant to judge.</para></summary>
        internal static readonly Guid[] Ds3HostClasses =
        {
            new Guid("745A17A0-74D3-11D0-B6FE-00A0C90F57DA"), // HIDCLASS: on inbox HidUsb
            new Guid("4D36E97E-E325-11CE-BFC1-08002BE10318"), // UNKNOWN: no driver bound
            new Guid("88BAE032-5A81-49F0-BC3D-A4FF138216D6"), // USBDEVICE: a WinUSB-class INF
            new Guid("36FC9E60-C465-11CF-8056-444553540000"), // USB: composite parents
        };

        /// <summary>Instance IDs of every present DS3 USB node, across every class it
        /// can be in. Duplicates are impossible (a node has exactly one class) so the
        /// union needs no de-duplication.</summary>
        private static IEnumerable<string> FindDs3UsbNodes(bool presentOnly = true)
        {
            foreach (var cls in Ds3HostClasses)
            {
                IEnumerable<string> ids = null;
                try
                {
                    if (!Devcon.FindInDeviceClassByHardwareId(
                            cls, @"USB\VID_054C&PID_0268", out ids, presentOnly, false))
                        continue;
                }
                catch { continue; }
                if (ids == null) continue;
                foreach (var id in ids) yield return id;
            }
        }

        /// <summary><para>True when this machine has EVER had a DualShock 3 docked.
        /// A device node survives unplugging: Windows keeps it as a non-present
        /// devnode, which is why Device Manager has a "show hidden devices" mode.
        /// So this is a durable "a DS3 lives here" marker that needs no new
        /// persisted state of our own.</para>
        ///
        /// <para>It exists because AnyDs3Paired cannot answer the question the
        /// PSM-patch policy actually asks. That probe finds pads PADFORGE paired,
        /// by the BTHPORT VID/PID record its ceremony writes. A DS3 paired any
        /// other way has no such record at all: BthPS3 identifies pads by remote
        /// NAME and the pairing itself lives inside the controller, which stores
        /// the host radio's MAC. Measured on a machine whose DS3 connects over
        /// BthPS3 daily and has no BTHPORT record of any kind (#265 audit).</para>
        ///
        /// <para>Getting this wrong disarms PSM patching on exactly the machines
        /// that need it, so the pad silently stops connecting over Bluetooth.</para>
        /// </summary>
        public static bool MachineHasDs3()
        {
            try { return FindDs3UsbNodes(presentOnly: false).Any(); }
            catch { return false; }
        }

        /// <summary>True only when a USB DualShock 3 (VID_054C&amp;PID_0268) is present AND
        /// still on the inbox HID driver (or no driver), meaning nothing is driving it and
        /// it needs our WinUSB bind. This is the ALLOWLIST that keeps the background bind
        /// safe: it fires only for the one state where binding is both needed and harmless.
        ///
        /// <para>Whether an existing WinUSB binding is OURS is not decided here. The caller
        /// (<see cref="Common.Input.Ds3DirectService"/>) first calls FindWinUsbDs3, which
        /// matches our own INF's interface GUID {B35924D6-...}; if that hits, the pad is
        /// opened directly with no rebind. Ownership is a PERSISTED devnode binding
        /// (DEVPKEY_Device_Service), so it survives our process dying or never running.
        /// An abrupt close therefore leaves the pad on WinUSB and the next run just reopens
        /// it. This method is reached only when our interface is ABSENT, and it returns
        /// false for every non-inbox state, so anything else driving the pad (DsHidMini,
        /// whose UMDF2 service reads WUDFRd; ScpToolkit; a stray WinUSB binding with a
        /// different GUID) is left strictly alone. The explicit pairing dialog is the only
        /// path that force-rebinds (via <see cref="EnsureWinUsbBound"/>).</para></summary>
        public static bool IsUsbDs3NeedingWinUsb() => IsUsbDs3NeedingWinUsb(null);

        /// <summary>As above, narrating what it found. The decision was
        /// entirely silent, so a machine where the bind never fires produced
        /// no evidence of why (discussion #283 arrived with a DIAG that could
        /// not distinguish "no DS3 node" from "a node we declined to touch").
        /// The log fires only when the verdict CHANGES, so a 500 ms poll loop
        /// cannot flood the ring.</summary>
        public static bool IsUsbDs3NeedingWinUsb(Action<string> log)
        {
            try
            {
                var seen = new List<string>();
                bool needs = false;
                foreach (var id in FindDs3UsbNodes())
                {
                    string svc;
                    try
                    {
                        var dev = PnPDevice.GetDeviceByInstanceId(id, DeviceLocationFlags.Normal);
                        svc = dev.GetProperty<string>(DevicePropertyKey.Device_Service) ?? string.Empty;
                    }
                    catch { continue; }
                    seen.Add(svc.Length == 0 ? "(no driver)" : svc);
                    // Allowlist: bind ONLY on the inbox HID driver or no driver. Every
                    // other service (WINUSB, WUDFRd/DsHidMini, usbccgp on a composite
                    // parent, ...) is left alone. This is the real gate; the class
                    // sweep above only decides what gets shown to it.
                    if (svc.Length == 0 || svc.Equals("HidUsb", StringComparison.OrdinalIgnoreCase))
                        needs = true;
                }
                if (log != null)
                {
                    string verdict = seen.Count == 0
                        ? "no DS3 USB node present"
                        : $"DS3 USB node(s) on service {string.Join(", ", seen)} -> "
                          + (needs ? "needs the WinUSB bind" : "left alone");
                    if (verdict != _lastUsbVerdict) { _lastUsbVerdict = verdict; log(verdict); }
                }
                return needs;
            }
            catch { return false; }
        }

        private static string _lastUsbVerdict;

        /// <summary><para>Reboot-free radio re-enumeration. CyclePort
        /// (IOCTL_USB_HUB_CYCLE_PORT) first, and when the hub refuses it, a
        /// devnode disable/enable of the radio itself. The fallback exists
        /// because the refusal is real hardware behavior: on a MediaTek
        /// MT7925 the port cycle failed, the filter never attached, the
        /// advertised service PDO never re-enumerated, and the install
        /// reported failure until the user toggled the adapter by hand in
        /// Device Manager, which is exactly the disable/enable this now does
        /// itself (observed on the 2026-08-06 arcade-PC rehearsal).</para></summary>
        public static void CycleBluetoothRadio(Action<string> log)
        {
            try
            {
                lock (_cycleLock)
                {
                    if (!Devcon.FindByInterfaceGuid(RadioInterface, out PnPDevice radio))
                    {
                        log("No USB Bluetooth radio to cycle.");
                        return;
                    }
                    try
                    {
                        radio.ToUsbPnPDevice().CyclePort();
                        log("Bluetooth radio re-enumerated.");
                        return;
                    }
                    catch (Exception ex)
                    {
                        log("Radio port cycle refused (" + ex.Message + "); toggling the adapter instead.");
                    }
                    radio.Disable();
                    Thread.Sleep(500);
                    radio.Enable();
                }
                log("Bluetooth radio toggled off and on.");
            }
            catch (Exception ex) { log("Radio cycle failed: " + ex.Message); }
            finally
            {
                // A cycle returns before the radio is BACK, and everything the
                // install does next needs it: advertising the profile service
                // needs a radio handle, and arming PSM patching needs the
                // filter re-attached to it. Returning early made the very next
                // step log "No radio to advertise the service on" and carry on
                // as though the install had worked, so BthPS3Service was never
                // advertised and the pad had nothing to connect to (observed
                // in the arcade-PC log, 0.7 s after this call returned).
                if (!WaitForBluetoothRadio(20000))
                    log("WARNING: the Bluetooth radio did not come back after the cycle.");
            }
        }

        /// <summary>Waits for a Bluetooth radio handle to be obtainable. This
        /// is the readiness signal for anything that talks to the radio, and
        /// it is NOT the same as the devnode existing.</summary>
        public static bool WaitForBluetoothRadio(int timeoutMs) =>
            WaitForCondition(() =>
            {
                var p = new BLUETOOTH_FIND_RADIO_PARAMS
                { dwSize = (uint)Marshal.SizeOf<BLUETOOTH_FIND_RADIO_PARAMS>() };
                IntPtr find = BluetoothFindFirstRadio(ref p, out IntPtr radio);
                if (find == IntPtr.Zero) return false;
                CloseHandle(radio);
                BluetoothFindRadioClose(find);
                return true;
            }, timeoutMs, 500);

        // NOTE: there is deliberately no "remove the BthPS3 PDO node" helper. Forcibly
        // removing the raw PDO with PnP (dev.Remove()) frees BthPS3's per-connection
        // context out from under BTHport, and the next HCI disconnect faults on it
        // (BSOD 0xD1, BthPS3.sys, 2026-07-09). The PDO is transient: it self-destroys
        // when the pad disconnects, which the radio cycle triggers through BthPS3's own
        // in-order path against a valid context.

        // ── BR/EDR link-key anchor (remembered-device persistence) ────────────────

        // Fixed non-zero 16-byte key. bthport only needs a link-key VALUE to exist for
        // the device MAC to flag it remembered+authenticated (BDIF_PAIRED); the DS3 does
        // no SSP so the value is never validated over the air. That remembered state is
        // what makes bthport serve the injected Name to BthPS3's IOCTL_BTH_GET_DEVICE_INFO
        // on every connect instead of overwriting it with the clone's blank over-air name,
        // and what keeps the Devices record from being pruned on radio re-enumeration.
        // Hardware-confirmed 2026-07-09 (rem/auth flags flipped, identified as SIXAXIS, no
        // encryption block). Constant is ScpToolkit's BdLink (GlobalConfiguration.cs).
        private static readonly byte[] Ds3LinkKey =
            { 0x56, 0xE8, 0x81, 0x38, 0x08, 0x06, 0x51, 0x41, 0xC0, 0x7F, 0x12, 0xAA, 0xD9, 0x66, 0x3C, 0xCE };

        private const string BthPortKeysKey =
            @"SYSTEM\CurrentControlSet\Services\BTHPORT\Parameters\Keys\";
        private const string BthPortDevicesKey =
            @"SYSTEM\CurrentControlSet\Services\BTHPORT\Parameters\Devices\";
        private const string DS3_REMOTE_NAME = "PLAYSTATION(R)3 Controller";

        /// <summary>
        /// Writes the full remembered-device record for the DS3 so BthPS3 identifies it
        /// on every connect. Three parts, all hardware-confirmed load-bearing 2026-07-09:
        ///   1. Name/VID/PID into Devices\&lt;mac&gt;.
        ///   2. The Devices record's OWNER set to SYSTEM. bthport prunes device records
        ///      it doesn't own on radio re-enumeration, so an admin-owned record is
        ///      dropped and the pad stops identifying; a SYSTEM-owned record is kept.
        ///   3. A synthetic link key under Keys\&lt;radiomac&gt; flags the pad
        ///      remembered+authenticated so the stored Name is served instead of the
        ///      clone's blank over-air name.
        /// All native, from the elevated (admin, not SYSTEM) app: SYSTEM-ACL'd keys are
        /// written through REG_OPTION_BACKUP_RESTORE and the owner is set with
        /// SeTakeOwnership/SeRestore, both held by the elevated token.
        /// </summary>
        public static bool WriteRememberedDeviceRecord(byte[] radioMacBigEndian, string deviceMacHex, Action<string> log)
        {
            if (!WriteDeviceNameRecord(deviceMacHex, log)) return false;
            if (!SetDeviceRecordOwnerToSystem(deviceMacHex, log)) return false;
            return WriteLinkKeyAnchor(radioMacBigEndian, deviceMacHex, log);
        }

        // Name/VID/PID via REG_OPTION_BACKUP_RESTORE, so a pre-existing SYSTEM-owned
        // record from an earlier pairing can still be overwritten by the elevated app.
        private static bool WriteDeviceNameRecord(string deviceMacHex, Action<string> log)
        {
            EnablePrivilege("SeBackupPrivilege");
            EnablePrivilege("SeRestorePrivilege");
            int rc = RegCreateKeyEx(HKLM, BthPortDevicesKey + deviceMacHex, 0, null,
                REG_OPTION_BACKUP_RESTORE, KEY_READ | KEY_WRITE, IntPtr.Zero, out IntPtr hk, out _);
            if (rc != 0) { log($"Opening the device record failed (rc={rc})."); return false; }
            try
            {
                byte[] ascii = System.Text.Encoding.ASCII.GetBytes(DS3_REMOTE_NAME);
                byte[] name = new byte[ascii.Length + 1];
                Array.Copy(ascii, name, ascii.Length);
                RegSetValueEx(hk, "Name", 0, REG_BINARY, name, name.Length);
                RegSetValueEx(hk, "VID", 0, REG_DWORD, BitConverter.GetBytes(0x054C), 4);
                RegSetValueEx(hk, "PID", 0, REG_DWORD, BitConverter.GetBytes(0x0268), 4);
                return true;
            }
            finally { RegCloseKey(hk); }
        }

        // Owner -> SYSTEM (S-1-5-18). Requires SeRestore to assign an owner other than
        // the caller; SeTakeOwnership to touch the record's security at all.
        private static bool SetDeviceRecordOwnerToSystem(string deviceMacHex, Action<string> log)
        {
            EnablePrivilege("SeTakeOwnershipPrivilege");
            EnablePrivilege("SeRestorePrivilege");
            if (!ConvertStringSidToSid("S-1-5-18", out IntPtr pSid)) { log("SID convert failed."); return false; }
            try
            {
                int rc = SetNamedSecurityInfo(@"MACHINE\" + BthPortDevicesKey + deviceMacHex,
                    SE_REGISTRY_KEY, OWNER_SECURITY_INFORMATION, pSid, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                if (rc != 0) { log($"Setting the record owner failed (rc={rc})."); return false; }
                return true;
            }
            finally { LocalFree(pSid); }
        }

        /// <summary>Deletes the DS3's remembered-device record + link key for a clean
        /// unpair. The Devices subkey is SYSTEM-owned, so ownership is taken back to
        /// Administrators first (SeTakeOwnership/SeRestore) before the delete.</summary>
        /// <summary>Grants BUILTIN\Administrators full control of an HKLM key
        /// whose ownership we have just taken. Ownership alone confers only
        /// WRITE_DAC, so without this step every subsequent operation on a
        /// SYSTEM-owned key still fails with ERROR_ACCESS_DENIED.</summary>
        private static void GrantAdministratorsFullControl(string subKey, Action<string> log)
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(subKey,
                    RegistryKeyPermissionCheck.ReadWriteSubTree,
                    System.Security.AccessControl.RegistryRights.ChangePermissions);
                if (key == null) return;
                var sec = key.GetAccessControl();
                sec.AddAccessRule(new System.Security.AccessControl.RegistryAccessRule(
                    new System.Security.Principal.SecurityIdentifier(
                        System.Security.Principal.WellKnownSidType.BuiltinAdministratorsSid, null),
                    System.Security.AccessControl.RegistryRights.FullControl,
                    System.Security.AccessControl.InheritanceFlags.ContainerInherit,
                    System.Security.AccessControl.PropagationFlags.None,
                    System.Security.AccessControl.AccessControlType.Allow));
                key.SetAccessControl(sec);
            }
            catch (Exception ex) { log?.Invoke("Granting access to the record failed: " + ex.Message); }
        }

        public static void DeleteRememberedDeviceRecord(byte[] radioMacBigEndian, string deviceMacHex, Action<string> log)
        {
            try
            {
                EnablePrivilege("SeTakeOwnershipPrivilege");
                EnablePrivilege("SeRestorePrivilege");
                if (ConvertStringSidToSid("S-1-5-32-544", out IntPtr admins)) // BUILTIN\Administrators
                {
                    try
                    {
                        SetNamedSecurityInfo(@"MACHINE\" + BthPortDevicesKey + deviceMacHex,
                            SE_REGISTRY_KEY, OWNER_SECURITY_INFORMATION, admins, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                    }
                    finally { LocalFree(admins); }
                }
                // Taking OWNERSHIP does not grant access. The record is written
                // SYSTEM-owned so bthport keeps it, and becoming its owner only
                // earns the RIGHT to rewrite its DACL: the delete still fails
                // with ERROR_ACCESS_DENIED until Administrators is actually
                // granted control. That is the rc=5 in the arcade-PC log, and
                // it is why an unpair left the record behind and the user had
                // to delete it by hand before a second pairing would take.
                GrantAdministratorsFullControl(BthPortDevicesKey + deviceMacHex, log);

                // Log a nonzero rc the way WriteLinkKeyAnchor does. Discarding
                // it meant an unpair that silently left the device record in
                // place reported success, and the next pair attempt then hit a
                // stale record with nothing in the log to explain it.
                int rcDel = RegDeleteKey(HKLM, BthPortDevicesKey + deviceMacHex);
                if (rcDel != 0) log($"Removing the device record failed (rc={rcDel}).");
            }
            catch (Exception ex) { log("Removing the device record failed: " + ex.Message); }
            DeleteLinkKeyAnchor(radioMacBigEndian, deviceMacHex, log);
        }

        /// <summary>Writes the link-key value that anchors the DS3's Name record in
        /// bthport's remembered-device set. Value name = device MAC (12 lowercase hex),
        /// under Keys\&lt;radiomac&gt;. The Keys subtree is SYSTEM-ACL'd, so it is opened
        /// with REG_OPTION_BACKUP_RESTORE after enabling the (held-but-disabled) backup
        /// and restore privileges of the elevated token.</summary>
        public static bool WriteLinkKeyAnchor(byte[] radioMacBigEndian, string deviceMacHex, Action<string> log)
        {
            IntPtr hk = OpenKeysBackupRestore(radioMacBigEndian, log);
            if (hk == IntPtr.Zero) return false;
            try
            {
                int rc = RegSetValueEx(hk, deviceMacHex, 0, REG_BINARY, Ds3LinkKey, Ds3LinkKey.Length);
                if (rc != 0) { log($"Writing the pairing key failed (rc={rc})."); return false; }
                return true;
            }
            finally { RegCloseKey(hk); }
        }

        /// <summary>Removes the link-key anchor for a clean unpair.</summary>
        public static void DeleteLinkKeyAnchor(byte[] radioMacBigEndian, string deviceMacHex, Action<string> log)
        {
            IntPtr hk = OpenKeysBackupRestore(radioMacBigEndian, log);
            if (hk == IntPtr.Zero) return;
            try { RegDeleteValue(hk, deviceMacHex); }
            finally { RegCloseKey(hk); }
        }

        private static IntPtr OpenKeysBackupRestore(byte[] radioMacBigEndian, Action<string> log)
        {
            EnablePrivilege("SeBackupPrivilege");
            EnablePrivilege("SeRestorePrivilege");
            var sb = new System.Text.StringBuilder(radioMacBigEndian.Length * 2);
            foreach (byte b in radioMacBigEndian) sb.Append(b.ToString("x2"));
            int rc = RegCreateKeyEx(HKLM, BthPortKeysKey + sb, 0, null, REG_OPTION_BACKUP_RESTORE,
                KEY_READ | KEY_WRITE, IntPtr.Zero, out IntPtr hk, out _);
            if (rc != 0) { log($"Opening the pairing-key store failed (rc={rc})."); return IntPtr.Zero; }
            return hk;
        }

        // ── install helpers ──────────────────────────────────────────────────────

        private static void InstallInf(string infPath, Action<string> log)
        {
            if (!File.Exists(infPath)) throw new FileNotFoundException("Bundled driver missing", infPath);
            Devcon.Install(infPath, out bool reboot);
            if (reboot) log("(a reboot was requested by " + Path.GetFileName(infPath) + ")");
        }

        /// <summary><para>Writes BthPS3's consumer parameters. Opens the key,
        /// never creates it: BthPS3 reads these at load, so writing them
        /// against a driver that is not installed cannot take effect, and the
        /// write itself would MANUFACTURE a Services\BthPS3 key with no
        /// ImagePath. That shell then reads as an installed service to any
        /// existence check and permanently blocks the install that would make
        /// the values meaningful. Its twin EnsurePadForgeOwnsPsmPatch has
        /// always opened rather than created, and said so; this one did not,
        /// and that divergence is what shipped the 2026-08-06 dead
        /// stack.</para>
        ///
        /// <para>The service check comes FIRST, then CreateSubKey: BthPS3.inf
        /// writes Parameters itself (RawPDO 0 / ExclusivePDO 1, the defaults
        /// these lines override), so on a healthy install the subkey is
        /// already there, and creating it under a REAL service is safe
        /// either way. What must never happen is creating the parent.</para></summary>
        /// <summary>Writes the consumer overrides, returning true when it
        /// actually CHANGED something. The caller needs that answer: BthPS3
        /// reads RawPDO when it builds the child PDO, so a fresh write has to
        /// be followed by a re-enumeration or the running driver keeps serving
        /// the INF's defaults (RawPDO=0), and a non-raw child is a child that
        /// needs a function driver no INF here can supply.</summary>
        private static bool EnsureConsumerParams()
        {
            if (!IsServiceInstalled("BthPS3")) return false;
            using var key = Registry.LocalMachine.CreateSubKey(BthPs3ParamsKey, writable: true);
            if (key == null) return false;
            bool changed = !(key.GetValue("RawPDO") is int raw && raw == 1)
                        || !(key.GetValue("ExclusivePDO") is int excl && excl == 0);
            key.SetValue("RawPDO", 1, RegistryValueKind.DWord);       // enumerate with no function driver
            key.SetValue("ExclusivePDO", 0, RegistryValueKind.DWord); // allow our shared open
            // AutoEnableFilter=0 hands PadForge sole ownership of PSM patching
            // (issue #199 crash mitigation). BthPS3's default (1) auto-arms
            // patching at radio power-up AND re-arms it ~10 s after it denies a
            // foreign device (BthPS3 L2CAP.Connect.c:242, the exact re-arm seen
            // in the 2026-07-10 crash log at 12:29:04). With it off, the filter
            // only patches when PadForge's SetPsmPatching enables it, so BthPS3
            // receives zero incoming connections whenever no DS3 is in play and
            // its use-after-free-on-disconnect path (upstream #48, unfixed at
            // v2.10.470.0) is unreachable. AutoDisableFilter stays default (1):
            // deny-then-off is a fail-safe we keep.
            //
            // NOT on a DsHidMini system (audit 2026-07-24, lens 1r): the
            // coexistence policy says PadForge never owns arming there,
            // because their DS3s connect only while patching is armed and
            // leave no BTHPORT record for AnyDs3Paired to find. Writing the
            // override here would re-take the ownership
            // ReconcilePsmPatchForCrashSafety just repaired, and it
            // outlives PadForge. The install/pair path is the one caller
            // that reached this line without consulting the policy.
            if (!IsDsHidMiniInstalled())
                key.SetValue("AutoEnableFilter", 0, RegistryValueKind.DWord);
            return changed;
        }

        /// <summary>Arms PSM patching, waiting up to 20 s for the filter
        /// to attach. Every arm site follows either an install or a radio
        /// cycle, and the control device is absent while the filter
        /// re-attaches, so an arm with no wait is an arm that may do
        /// nothing. Returns the number of radios patched; 0 is failure.</summary>
        private static int EnsurePsmPatch(Action<string> log) => SetPsmPatching(true, log, 20000);

        /// <summary>True when the BthPS3 profile driver service is installed
        /// (the stack that carries the DS3 over Bluetooth). Cheap registry-free
        /// SCM query; the crash-safety reconcile no-ops when this is false.</summary>
        public static bool IsBthPs3Installed() => IsServiceInstalled("BthPS3");

        /// <summary><para>True when Nefarius DsHidMini is installed. DsHidMini is
        /// a UMDF driver (its INF's AddService entries are the generic WUDFRd /
        /// mshidumdf reflector, dshidmini.inf), so there is no "dshidmini"
        /// service key to probe. Gates the PSM-patch crash policy: a DsHidMini
        /// system's DS3s connect through BthPS3 patching, so PadForge must never
        /// disarm it there (the 2026-07-24 coexistence audit: the startup disarm
        /// was breaking foreign DsHidMini setups whose pads leave no BTHPORT
        /// VID/PID record for AnyDs3Paired to find).</para>
        ///
        /// <para>What is NOT a marker, and used to be: the driver's config root
        /// at %ProgramData%\DsHidMini. That directory is created by the driver
        /// for its settings and SURVIVES uninstall, the way application config
        /// normally does, so on its own it is a leftover rather than evidence.
        /// Accepting it meant a machine that had removed DsHidMini still read as
        /// having it, which flipped PsmPatchPolicy to never-own / always-armed
        /// and silently disabled the #204 crash-safety mitigation. Confirmed on
        /// a machine with the folder present, no driver package, and no service
        /// (#265 audit).</para>
        ///
        /// <para>Both markers below are evidence the driver is actually THERE,
        /// not that it once was.</para></summary>
        public static bool IsDsHidMiniInstalled()
        {
            // 1. The driver package in the store. Authoritative both ways:
            //    DsHidMini installs by INF so the key exists while it does, and
            //    an uninstall (pnputil /delete-driver) removes it.
            try
            {
                using var pkgs = Registry.LocalMachine.OpenSubKey(
                    @"SYSTEM\DriverDatabase\DriverPackages");
                if (pkgs != null)
                {
                    foreach (string name in pkgs.GetSubKeyNames())
                        if (name.StartsWith("dshidmini.inf_", StringComparison.OrdinalIgnoreCase))
                            return true;
                }
            }
            catch { /* fall through to the live-device probe */ }

            // 2. A pad this machine is driving with it RIGHT NOW. DsHidMini's INF
            //    binds the UMDF reflector, so such a node's service reads WUDFRd.
            //    Belt and braces for a store read that failed above.
            try
            {
                foreach (var id in FindDs3UsbNodes())
                {
                    try
                    {
                        var dev = PnPDevice.GetDeviceByInstanceId(id, DeviceLocationFlags.Normal);
                        string svc = dev.GetProperty<string>(DevicePropertyKey.Device_Service) ?? string.Empty;
                        if (svc.Equals("WUDFRd", StringComparison.OrdinalIgnoreCase))
                            return true;
                    }
                    catch { }
                }
            }
            catch { }

            return false;
        }

        /// <summary>Restores BthPS3's own PSM-patch auto-arm by deleting the
        /// AutoEnableFilter override (the driver default is TRUE,
        /// BthPS3 Bluetooth.Context.c:279, read from the registry only when
        /// present). The repair half of the DsHidMini coexistence policy: a
        /// PadForge build before 2026-07-24 took sole ownership
        /// (AutoEnableFilter=0) on every BthPS3 system, which left foreign
        /// DsHidMini setups unable to re-arm on their own. Idempotent; no-op
        /// when the value is absent. Takes effect on the next BthPS3 load;
        /// SetPsmPatching drives the immediate state.</summary>
        public static void RestoreBthPs3AutoArm()
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(BthPs3ParamsKey, writable: true);
                if (key != null && key.GetValue("AutoEnableFilter") != null)
                    key.DeleteValue("AutoEnableFilter", throwOnMissingValue: false);
            }
            catch { /* best effort; SetPsmPatching still governs the live state */ }
        }

        /// <summary>Asserts AutoEnableFilter=0 on the BthPS3 Parameters key so
        /// BthPS3 stops auto-arming PSM patching on its own (issue #199): it
        /// otherwise arms patching at radio power-up and re-arms it ~10 s after
        /// denying a foreign device (BthPS3 L2CAP.Connect.c:242, the exact
        /// re-arm in the 2026-07-10 crash log). With it off, PadForge's
        /// SetPsmPatching is the sole enabler, so a disable actually sticks.
        /// Takes effect on the next BthPS3 load (the running driver cached the
        /// value at init); SetPsmPatching drives the immediate state. Idempotent,
        /// only writes when the value isn't already 0, never creates the key.</summary>
        public static void EnsurePadForgeOwnsPsmPatch()
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(BthPs3ParamsKey, writable: true);
                if (key != null && !(key.GetValue("AutoEnableFilter") is int v && v == 0))
                    key.SetValue("AutoEnableFilter", 0, RegistryValueKind.DWord);
            }
            catch { /* best effort; SetPsmPatching still governs the live state */ }
        }

        /// <summary>Enables or disables BthPS3 PSM patching on EVERY attached
        /// radio (issue #199 crash mitigation). Patching rewrites incoming HID
        /// L2CAP PSMs (0x11/0x13) to BthPS3's DS3 PSMs so the connection routes
        /// to BthPS3 (BthPS3PSM Filter.c:157-205). Disabled, the PSMs pass
        /// through untouched to the inbox Bluetooth HID stack, so BthPS3's
        /// profile driver sees no incoming connection and its racy
        /// connect/identify/disconnect/destroy path cannot run. The filter
        /// persists the state per radio devnode and restores it on attach, and
        /// with AutoEnableFilter=0 (EnsureConsumerParams) BthPS3 never flips it
        /// back, so a disable sticks across radio cycles and reboots until
        /// PadForge re-enables it.
        ///
        /// <para>Idempotent and safe when the filter is absent (logs and
        /// returns). Enumerates radios by DeviceIndex 0..N via GET until
        /// ERROR_NO_SUCH_DEVICE rather than assuming a single radio at index
        /// 0.</para></summary>
        /// <summary><para>Waits for the PSM filter's control device to be
        /// open-able. A radio cycle detaches and re-attaches the filter, and
        /// the device is absent for the seconds in between, so anything that
        /// arms patching straight after a cycle must wait or it silently
        /// no-ops.</para>
        ///
        /// <para>This is the readiness signal that matters. The BthPS3
        /// SERVICE key existing does not imply the filter is attached, and
        /// waiting on the key alone left the first pairing on a clean machine
        /// arming patching into a closed window: the DS3 was then refused and
        /// flashed forever, and only a SECOND run of the ceremony worked,
        /// because by then patching had been armed once and the filter
        /// restores its per-radio state across cycles (observed end to end on
        /// the 2026-08-06 arcade-PC rehearsal).</para></summary>
        public static bool WaitForPsmControlDevice(int timeoutMs) =>
            WaitForCondition(IsPsmFilterPresent, timeoutMs, 500);

        /// <summary>Enables or disables PSM patching, returning how many
        /// radios accepted the toggle. Zero means it did NOT take, which the
        /// caller must treat as failure rather than silence.</summary>
        public static int SetPsmPatching(bool enable, Action<string> log, int waitForFilterMs = 0)
        {
            if (waitForFilterMs > 0 && !IsPsmFilterPresent())
                WaitForPsmControlDevice(waitForFilterMs);

            IntPtr h = CreateFile(PsmControlPath, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_RW,
                IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_NO_BUFFERING | FILE_FLAG_WRITE_THROUGH, IntPtr.Zero);
            if (h == INVALID_HANDLE)
            {
                log?.Invoke($"PSM control device not present; cannot {(enable ? "enable" : "disable")} patching.");
                return 0;
            }
            try
            {
                uint toggleCode = enable
                    ? IOCTL_BTHPS3PSM_ENABLE_PSM_PATCHING
                    : IOCTL_BTHPS3PSM_DISABLE_PSM_PATCHING;

                int count = 0;
                // Drive the sweep off the toggle IOCTL itself: the filter
                // completes it with STATUS_NO_SUCH_DEVICE for an index past the
                // last radio (Sideband.c:317, WdfCollectionGetItem == NULL).
                // Index 0 is always attempted, exactly as the proven single-
                // radio path did, so nothing regresses on a one-radio host. The
                // 32 cap is a spin guard; no host has that many radios. The
                // NO_SUCH_DEVICE early-out is only an optimization: attempting a
                // bad index is a harmless no-op, so correctness never depends on
                // the exact Win32 error mapping.
                for (int index = 0; index < 32; index++)
                {
                    byte[] payload = new byte[4]; // { ULONG DeviceIndex }
                    BitConverter.GetBytes(index).CopyTo(payload, 0);
                    if (DeviceIoControl(h, toggleCode, payload, payload.Length, null, 0, out _, IntPtr.Zero))
                    {
                        count++;
                        continue;
                    }
                    if (Marshal.GetLastWin32Error() == ERROR_NO_SUCH_DEVICE) break;
                }
                log?.Invoke($"PSM patching {(enable ? "enabled" : "disabled")} on {count} radio(s).");
                return count;
            }
            finally { CloseHandle(h); }
        }

        // ── native BluetoothSetLocalServiceInfo (the one Bluetooth-specific call) ──

        /// <summary>Advertises the BthPS3 profile service, which is what makes
        /// BTHENUM spawn the PDO that loads BthPS3.sys. Returns false when the
        /// advertisement did not happen: without it there is no profile
        /// driver, so the pad's connection reaches nothing and it flashes
        /// forever.</summary>
        private static bool EnableBthPs3Service(Action<string> log)
        {
            // Wait for the radio rather than reading its absence as "no radio
            // exists". This ran 0.7 s after a radio cycle and took the early
            // exit, silently skipping the advertisement.
            if (!WaitForBluetoothRadio(20000))
            {
                log("No radio to advertise the service on.");
                return false;
            }
            var fp = new BLUETOOTH_FIND_RADIO_PARAMS { dwSize = (uint)Marshal.SizeOf<BLUETOOTH_FIND_RADIO_PARAMS>() };
            IntPtr hFind = BluetoothFindFirstRadio(ref fp, out IntPtr hRadio);
            if (hFind == IntPtr.Zero) { log("No radio to advertise the service on."); return false; }
            try
            {
                EnablePrivilege("SeLoadDriverPrivilege");
                var info = new BLUETOOTH_LOCAL_SERVICE_INFO { Enabled = 1, szName = BthPs3ServiceName };
                uint rc = BluetoothSetLocalServiceInfo(hRadio, ref BthPs3ServiceGuidLocal, 0, ref info);
                log(rc == 0 ? "Profile service advertised." : $"Advertise service rc={rc}.");
                return rc == 0;
            }
            finally { CloseHandle(hRadio); BluetoothFindRadioClose(hFind); }
        }

        // ref needs a static field (can't ref a readonly through a property).
        private static Guid BthPs3ServiceGuidLocal = new Guid("1cb831ea-79cd-4508-b0fc-85f7c85ae8e0");

        private static void EnablePrivilege(string name)
        {
            if (!OpenProcessToken(GetCurrentProcess(), TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, out IntPtr tok)) return;
            try
            {
                if (!LookupPrivilegeValue(null, name, out LUID luid)) return;
                var tp = new TOKEN_PRIVILEGES { PrivilegeCount = 1, Luid = luid, Attributes = SE_PRIVILEGE_ENABLED };
                AdjustTokenPrivileges(tok, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero);
            }
            finally { CloseHandle(tok); }
        }

        // ── embedded driver extraction ─────────────────────────────────────────────

        private static string _extractedDir;
        internal static string ExtractDrivers()
        {
            if (_extractedDir != null && Directory.Exists(_extractedDir)) return _extractedDir;
            string root = Path.Combine(Path.GetTempPath(), "PadForge", "BthPS3Drivers");
            var asm = Assembly.GetExecutingAssembly();
            foreach (string res in asm.GetManifestResourceNames().Where(n => n.StartsWith("BthPS3.", StringComparison.Ordinal)))
            {
                // LogicalName "BthPS3.BthPS3PSM_x64/BthPS3PSM.inf" -> path under root
                string rel = res.Substring("BthPS3.".Length).Replace('/', Path.DirectorySeparatorChar);
                string dest = Path.Combine(root, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(dest));
                using Stream s = asm.GetManifestResourceStream(res);
                using FileStream fs = File.Create(dest);
                s.CopyTo(fs);
            }
            _extractedDir = root;
            return root;
        }

        // ── service helpers ───────────────────────────────────────────────────────

        /// <summary>True once the driver's service key exists (i.e. the INF installed).
        /// A registry probe avoids a dependency on System.ServiceProcess and is the
        /// right "is it installed" question - running state is handled by the profile
        /// service advertisement + AutoEnableFilter.</summary>
        /// <summary><para>True when a kernel service is REALLY installed, which
        /// means its key carries an ImagePath. The key's mere existence is not
        /// the same question: any write under
        /// Services\&lt;name&gt;\&lt;anything&gt; creates the parent on the way
        /// down, so a settings write against a driver that is not installed
        /// yet leaves a key that looks exactly like an installed service to a
        /// null check.</para>
        ///
        /// <para>That is not hypothetical. On a fresh machine (2026-08-06)
        /// Services\BthPS3 held nothing but the Parameters subkey PadForge
        /// itself had written, with no ImagePath, no Start and no Type. The
        /// old check returned true, EnsureInstalled short-circuited its whole
        /// eight-step install forever, the profile service was never
        /// advertised, no PDO ever spawned, PSM patching could never arm, and
        /// the DualShock 3 sat flashing because the inbox HID stack refused
        /// its L2CAP connection. Get-Service reported NOT INSTALLED the entire
        /// time.</para></summary>
        private static bool IsServiceInstalled(string name)
        {
            using var services = Registry.LocalMachine.OpenSubKey(ServicesRoot);
            return IsServiceInstalled(services, name);
        }

        private const string ServicesRoot = @"SYSTEM\CurrentControlSet\Services";

        /// <summary>Polls a condition to completion or timeout. Exists because
        /// PnP driver installation is asynchronous and a one-shot probe races
        /// it; internal so the poll arithmetic is testable.</summary>
        internal static bool WaitForCondition(Func<bool> probe, int timeoutMs, int pollMs)
        {
            long deadline = Environment.TickCount64 + timeoutMs;
            while (true)
            {
                if (probe()) return true;
                if (Environment.TickCount64 >= deadline) return false;
                Thread.Sleep(pollMs);
            }
        }

        /// <summary>The predicate itself, against any services root, so the
        /// contract is testable without writing to HKLM.</summary>
        internal static bool IsServiceInstalled(RegistryKey servicesRoot, string name)
        {
            using var k = servicesRoot?.OpenSubKey(name);
            return k?.GetValue("ImagePath") != null;
        }

        /// <summary>True for the damaged shape above: a BthPS3 key with no
        /// ImagePath. Distinct from "absent", because absent is the normal
        /// first-run state and needs no repair, while this one blocks the
        /// install that would fix it.</summary>
        private static bool HasOrphanedBthPs3Key()
        {
            using var services = Registry.LocalMachine.OpenSubKey(ServicesRoot);
            return HasOrphanedServiceKey(services, "BthPS3");
        }

        /// <summary>Present but not a service. Deliberately NOT the negation of
        /// IsServiceInstalled: absent is the normal first-run state and needs
        /// no repair, while present-without-ImagePath blocks the install that
        /// would fix it.</summary>
        internal static bool HasOrphanedServiceKey(RegistryKey servicesRoot, string name)
        {
            using var k = servicesRoot?.OpenSubKey(name);
            return k != null && k.GetValue("ImagePath") == null;
        }

        // ── P/Invoke ──────────────────────────────────────────────────────────────

        private static readonly IntPtr INVALID_HANDLE = new IntPtr(-1);
        private const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000, FILE_SHARE_RW = 3, OPEN_EXISTING = 3;
        private const uint FILE_FLAG_NO_BUFFERING = 0x20000000, FILE_FLAG_WRITE_THROUGH = 0x80000000;
        private const uint TOKEN_ADJUST_PRIVILEGES = 0x20, TOKEN_QUERY = 0x8, SE_PRIVILEGE_ENABLED = 0x2;
        private static readonly UIntPtr HKLM = unchecked((UIntPtr)0x80000002u);
        private const int REG_OPTION_BACKUP_RESTORE = 0x04, REG_BINARY = 3, REG_DWORD = 4, KEY_READ = 0x20019, KEY_WRITE = 0x20006;
        private const int SE_REGISTRY_KEY = 4, OWNER_SECURITY_INFORMATION = 0x1;

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern int RegCreateKeyEx(UIntPtr hKey, string subKey, int reserved, string cls, int options, int sam, IntPtr sa, out IntPtr res, out int disp);
        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern int RegSetValueEx(IntPtr hKey, string name, int reserved, int type, byte[] data, int cb);
        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern int RegDeleteValue(IntPtr hKey, string name);
        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern int RegDeleteKey(UIntPtr hKey, string subKey);
        [DllImport("advapi32.dll", SetLastError = true)] private static extern int RegCloseKey(IntPtr hKey);
        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern int SetNamedSecurityInfo(string name, int objType, int secInfo, IntPtr owner, IntPtr group, IntPtr dacl, IntPtr sacl);
        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern bool ConvertStringSidToSid(string s, out IntPtr sid);
        [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr p);

        [StructLayout(LayoutKind.Sequential)] private struct BLUETOOTH_FIND_RADIO_PARAMS { public uint dwSize; }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct BLUETOOTH_LOCAL_SERVICE_INFO
        {
            public int Enabled;
            public ulong btAddr;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szDeviceClass;
        }

        [StructLayout(LayoutKind.Sequential)] private struct LUID { public uint LowPart; public int HighPart; }
        [StructLayout(LayoutKind.Sequential)]
        private struct TOKEN_PRIVILEGES { public uint PrivilegeCount; public LUID Luid; public uint Attributes; }

        [DllImport("bthprops.cpl", SetLastError = true)] private static extern IntPtr BluetoothFindFirstRadio(ref BLUETOOTH_FIND_RADIO_PARAMS p, out IntPtr phRadio);
        [DllImport("bthprops.cpl", SetLastError = true)] private static extern bool BluetoothFindRadioClose(IntPtr hFind);
        [DllImport("bthprops.cpl", SetLastError = true)] private static extern uint BluetoothSetLocalServiceInfo(IntPtr hRadio, ref Guid pClassGuid, uint ulInstance, ref BLUETOOTH_LOCAL_SERVICE_INFO info);

        [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(IntPtr h, uint access, out IntPtr tok);
        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern bool LookupPrivilegeValue(string sys, string name, out LUID luid);
        [DllImport("advapi32.dll", SetLastError = true)] private static extern bool AdjustTokenPrivileges(IntPtr tok, bool disableAll, ref TOKEN_PRIVILEGES newState, uint len, IntPtr prev, IntPtr prevLen);
        [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateFile(string n, uint a, uint s, IntPtr sa, uint d, uint f, IntPtr t);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr h);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool DeviceIoControl(IntPtr h, uint code, byte[] inb, int inl, byte[] outb, int outl, out int ret, IntPtr ov);
    }
}
