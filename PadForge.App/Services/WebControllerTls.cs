using System;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace PadForge.Services
{
    /// <summary>
    /// The HTTPS lane for the web controller (#296 phase 0). Motion sensors
    /// (DeviceMotionEvent, and on iOS DeviceMotionEvent.requestPermission)
    /// exist only in a secure context, and http://&lt;LAN-IP&gt; is never one.
    /// So the server needs to serve https://, which needs a certificate.
    ///
    /// PadForge hosts nothing and asks nobody for a cert. It generates a
    /// self-signed one at first enable (the app already runs elevated),
    /// installs it to LocalMachine\My, and binds it to the port with
    /// http.sys via netsh. The phone shows a one-time "not private" warning;
    /// after the user proceeds the page is a secure context and the sensor
    /// APIs appear. The certificate is a stable, reused identity: generated
    /// once, kept in the store, rebound if the port changes.
    ///
    /// This is the standard self-signed-LAN pattern, not a workaround. The
    /// alternative (a public CA) cannot issue for a private IP, and a trusted
    /// local CA install is far more friction on a phone than one warning tap.
    /// </summary>
    internal static class WebControllerTls
    {
        // A fixed application GUID for the http.sys sslcert appid field. Any
        // stable GUID works; this one is PadForge's web lane.
        private const string AppId = "{b9a1f2c4-7d3e-4a6b-9c8f-1e2d3a4b5c6d}";
        private const string CertSubject = "CN=PadForge Web Controller";
        private const string FriendlyName = "PadForge Web Controller";

        /// <summary>Ensures a certificate exists and is bound to the port.
        /// Returns the cert thumbprint on success, or null if any step failed
        /// (the caller then falls back to plain HTTP). Best-effort and
        /// self-contained: every failure is swallowed and reported as null.</summary>
        public static string EnsureHttpsBinding(int port)
        {
            try
            {
                var cert = FindOrCreateCert();
                if (cert == null) return null;

                // Rebind idempotently: delete any prior binding on this port
                // (a stale cert or a different port config), then add ours.
                RunNetsh($"http delete sslcert ipport=0.0.0.0:{port}");
                var add = RunNetsh(
                    $"http add sslcert ipport=0.0.0.0:{port} " +
                    $"certhash={cert.Thumbprint} appid={AppId} certstorename=MY");

                // netsh prints a localized success line; the reliable signal is
                // that a re-query now shows the binding.
                var show = RunNetsh($"http show sslcert ipport=0.0.0.0:{port}");
                if (show.IndexOf(cert.Thumbprint, StringComparison.OrdinalIgnoreCase) >= 0)
                    return cert.Thumbprint;

                PadForge.Engine.SdlDiagLog.WriteLine("WEBTLS bind not confirmed: " + Truncate(add));
                return null;
            }
            catch (Exception ex)
            {
                PadForge.Engine.SdlDiagLog.WriteLine("WEBTLS ensure failed: " + ex.Message);
                return null;
            }
        }

        /// <summary>Removes the port binding (uninstall / disable). The cert is
        /// left in the store, reused on the next enable.</summary>
        public static void RemoveBinding(int port)
        {
            try { RunNetsh($"http delete sslcert ipport=0.0.0.0:{port}"); }
            catch { }
        }

        private static X509Certificate2 FindOrCreateCert()
        {
            using var store = new X509Store(StoreName.My, StoreLocation.LocalMachine);
            store.Open(OpenFlags.ReadWrite);

            foreach (var existing in store.Certificates)
            {
                if (existing.Subject == CertSubject && existing.NotAfter > DateTime.Now.AddDays(30))
                    return existing;
            }

            // Generate a fresh self-signed RSA cert. SANs cover the wildcard
            // and loopback; the phone reaches us by raw LAN IP, and browsers
            // accept a warning-bypassed cert for any host, so an exhaustive
            // SAN list is unnecessary. localhost keeps desktop testing clean.
            using var rsa = RSA.Create(2048);
            var req = new CertificateRequest(CertSubject, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
            req.CertificateExtensions.Add(new X509KeyUsageExtension(
                X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, false));
            req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
                new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") /* serverAuth */ }, false));
            var san = new SubjectAlternativeNameBuilder();
            san.AddDnsName("localhost");
            san.AddIpAddress(System.Net.IPAddress.Loopback);
            req.CertificateExtensions.Add(san.Build());

            var cert = req.CreateSelfSigned(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddYears(10));
            cert.FriendlyName = FriendlyName;

            // http.sys binds by the store copy, and the private key must be
            // persisted with it. Round-trip through PFX so the stored cert
            // carries a machine-keyset private key.
            var pwd = Guid.NewGuid().ToString("N");
            var pfx = cert.Export(X509ContentType.Pfx, pwd);
            var storable = X509CertificateLoader.LoadPkcs12(pfx, pwd,
                X509KeyStorageFlags.MachineKeySet | X509KeyStorageFlags.PersistKeySet | X509KeyStorageFlags.Exportable);
            store.Add(storable);
            PadForge.Engine.SdlDiagLog.WriteLine("WEBTLS generated cert " + storable.Thumbprint);
            return storable;
        }

        private static string RunNetsh(string arguments)
        {
            var psi = new ProcessStartInfo
            {
                FileName = "netsh.exe",
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc == null) return string.Empty;
            var read = proc.StandardOutput.ReadToEndAsync();
            if (!proc.WaitForExit(5_000)) { try { proc.Kill(); } catch { } }
            try { return read.Wait(2_000) ? read.Result : string.Empty; }
            catch { return string.Empty; }
        }

        private static string Truncate(string s)
            => string.IsNullOrEmpty(s) ? "" : (s.Length > 120 ? s.Substring(0, 120) : s);
    }
}
