using System;
using System.Linq;
using System.Security.AccessControl;
using PadForge.Engine.Common;
using Xunit;
using Level = PadForge.Engine.Common.ChangeRule.Level;
using Verdict = PadForge.Engine.Common.ChangeRule.Verdict;

namespace PadForge.Tests
{
    /// <summary>
    /// The one rule for whether another account may change a file or folder
    /// (#597), which PadForge's protected folders, their parent, the exe's
    /// folder and the vendor libraries are all judged by. It reads the raw
    /// descriptor, so an entry of a kind it does not read fails the object
    /// at every level, and it runs in the protected start.
    /// </summary>
    public class ChangeRuleTests
    {
        private const string TI = "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464";

        internal static byte[] Binary(string sddl)
        {
            var sd = new RawSecurityDescriptor(sddl);
            byte[] bytes = new byte[sd.BinaryLength];
            sd.GetBinaryForm(bytes, 0);
            return bytes;
        }

        /// <summary>The descriptor with its list's first entry made of
        /// <paramref name="kind"/>.</summary>
        private static byte[] WithFirstEntryOfKind(byte[] descriptor, byte kind)
        {
            int dacl = BitConverter.ToInt32(descriptor, 16);
            var copy = (byte[])descriptor.Clone();
            copy[dacl + 8] = kind;
            return copy;
        }

        private static readonly Level[] Vendor = { Level.VendorFile, Level.VendorFolder, Level.VendorAbove };

        /// <summary>
        /// Each case as the two rules this one replaced judged it, read from
        /// them at 7f5b7d74b before they were removed: ProtectedFolder.Judge
        /// (PadForge's own folder, with and without TrustedInstaller as an
        /// owner), ProtectedFolder.JudgeParent, and TrustedLibrary's rule for
        /// a vendor's file, its folder and a folder above (true where no
        /// other account may change it). The one rule gives the same answer
        /// at each level. The cases where it was meant to differ are the
        /// tests below.
        /// </summary>
        [Theory]
        [InlineData("own-sddl", "O:BAD:P(A;OICI;FA;;;BA)(A;OICI;FA;;;SY)(A;OICI;0x1200a9;;;BU)", 0, "Sound", "Sound", "Sound", true, true, true)]
        [InlineData("sy-owned", "O:SYD:P(A;OICI;FA;;;BA)(A;OICI;FA;;;SY)", 0, "Sound", "Sound", "Sound", true, true, true)]
        [InlineData("users-everyone-read", "O:BAD:P(A;OICI;FA;;;BA)(A;OICI;0x1200a9;;;BU)(A;OICI;GRGX;;;WD)", 0, "Sound", "Sound", "Sound", true, true, true)]
        [InlineData("creator-owner-io", "O:BAD:P(A;OICI;FA;;;BA)(A;OICIIO;FA;;;CO)", 0, "Sound", "Sound", "Sound", true, true, true)]
        [InlineData("deny", "O:BAD:P(D;OICI;FA;;;BU)(A;OICI;FA;;;BA)", 0, "Sound", "Sound", "Sound", true, true, true)]
        [InlineData("user-owner", "O:S-1-5-21-1-2-3-1001D:P(A;OICI;FA;;;BA)", 0, "OtherOwner", "OtherOwner", "OtherOwner", false, false, false)]
        [InlineData("bu-owner", "O:BUD:P(A;OICI;FA;;;BA)", 0, "OtherOwner", "OtherOwner", "OtherOwner", false, false, false)]
        [InlineData("no-list", "O:BA", 0, "NoList", "NoList", "NoList", false, false, false)]
        [InlineData("c-root-child", "O:BAD:(A;OICI;FA;;;BA)(A;;0x1301bf;;;AU)(A;OICIIO;SDGXGWGR;;;AU)", 0, "OthersMayChange", "OthersMayChange", "Sound", false, false, false)]
        [InlineData("io-gw", "O:BAD:(A;OICI;FA;;;BA)(A;OICIIO;GW;;;AU)", 0, "OthersMayChange", "OthersMayChange", "Sound", true, true, true)]
        [InlineData("bu-2", "O:BAD:(A;OICI;FA;;;BA)(A;;0x2;;;BU)", 0, "OthersMayChange", "OthersMayChange", "Sound", false, false, true)]
        [InlineData("bu-4", "O:BAD:(A;OICI;FA;;;BA)(A;;0x4;;;BU)", 0, "OthersMayChange", "OthersMayChange", "Sound", false, false, true)]
        [InlineData("bu-sd", "O:BAD:(A;OICI;FA;;;BA)(A;;SD;;;BU)", 0, "OthersMayChange", "OthersMayChange", "Sound", false, false, false)]
        [InlineData("bu-wd", "O:BAD:(A;OICI;FA;;;BA)(A;;WD;;;BU)", 0, "OthersMayChange", "OthersMayChange", "OthersMayChange", false, false, false)]
        [InlineData("bu-wo", "O:BAD:(A;OICI;FA;;;BA)(A;;WO;;;BU)", 0, "OthersMayChange", "OthersMayChange", "OthersMayChange", false, false, false)]
        [InlineData("bu-40", "O:BAD:(A;OICI;FA;;;BA)(A;;0x40;;;BU)", 0, "OthersMayChange", "OthersMayChange", "OthersMayChange", true, false, false)]
        [InlineData("wd-ga", "O:BAD:(A;OICI;FA;;;BA)(A;;GA;;;WD)", 0, "OthersMayChange", "OthersMayChange", "OthersMayChange", false, false, false)]
        [InlineData("programdata", "O:SYG:SYD:PAI(A;OICIIO;GA;;;CO)(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;0x1200a9;;;BU)(A;CI;DCLCRPCR;;;BU)", 0, "OthersMayChange", "OthersMayChange", "Sound", false, false, true)]
        [InlineData("parent-bu-40", "O:SYD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;;0x40;;;BU)", 0, "OthersMayChange", "OthersMayChange", "OthersMayChange", true, false, false)]
        [InlineData("parent-au-wd", "O:SYD:P(A;OICI;FA;;;SY)(A;;WD;;;AU)", 0, "OthersMayChange", "OthersMayChange", "OthersMayChange", false, false, false)]
        [InlineData("parent-wd-ga", "O:SYD:P(A;OICI;FA;;;SY)(A;;GA;;;WD)", 0, "OthersMayChange", "OthersMayChange", "OthersMayChange", false, false, false)]
        [InlineData("parent-bu-owner", "O:BUD:P(A;OICI;FA;;;SY)", 0, "OtherOwner", "OtherOwner", "OtherOwner", false, false, false)]
        [InlineData("parent-io-ga", "O:SYD:P(A;OICI;FA;;;SY)(A;OICIIO;GA;;;AU)", 0, "OthersMayChange", "OthersMayChange", "Sound", true, true, true)]
        [InlineData("c-root", "O:"+TI+"D:PAI(A;;LC;;;AU)(A;OICIIO;SDGXGWGR;;;AU)(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;0x1200a9;;;BU)", 0, "OtherOwner", "OthersMayChange", "Sound", false, false, true)]
        [InlineData("exe-folder", "O:S-1-5-21-3155206576-1441097098-1295047565-1001G:S-1-5-21-3155206576-1441097098-1295047565-513D:AI(A;OICIID;FA;;;BA)(A;OICIID;FA;;;SY)(A;OICIID;0x1200a9;;;BU)(A;ID;0x1301bf;;;AU)(A;OICIIOID;SDGXGWGR;;;AU)", 0, "OtherOwner", "OtherOwner", "OtherOwner", false, false, false)]
        [InlineData("ti-owned", "O:"+TI+"D:P(A;OICI;FA;;;BA)(A;OICI;FA;;;SY)(A;OICI;FA;;;"+TI+")", 0, "OtherOwner", "Sound", "Sound", true, true, true)]
        [InlineData("pf-ti", "O:"+TI+"D:(A;;FA;;;"+TI+")(A;;FA;;;SY)(A;;FA;;;BA)(A;;0x1200a9;;;BU)(A;OICIIO;FA;;;CO)", 0, "OtherOwner", "Sound", "Sound", true, true, true)]
        [InlineData("pf-ba", "O:BAD:(A;;FA;;;"+TI+")(A;;FA;;;SY)(A;;FA;;;BA)(A;;0x1200a9;;;BU)(A;OICIIO;FA;;;CO)", 0, "Sound", "Sound", "Sound", true, true, true)]
        [InlineData("pf-sy", "O:SYD:(A;;FA;;;"+TI+")(A;;FA;;;SY)(A;;FA;;;BA)(A;;0x1200a9;;;BU)(A;OICIIO;FA;;;CO)", 0, "Sound", "Sound", "Sound", true, true, true)]
        [InlineData("steamvr", "O:BAD:(A;;FA;;;BA)(A;;0x1301bf;;;AU)", 0, "OthersMayChange", "OthersMayChange", "Sound", false, false, false)]
        [InlineData("readonly-user-owner", "O:S-1-5-21-1-2-3-1001D:(A;;0x1200a9;;;BU)", 0, "OtherOwner", "OtherOwner", "OtherOwner", false, false, false)]
        [InlineData("bu-append", "O:BAD:(A;;0x4;;;BU)", 0, "OthersMayChange", "OthersMayChange", "Sound", false, false, true)]
        [InlineData("bu-changeperm", "O:BAD:(A;;WD;;;BU)", 0, "OthersMayChange", "OthersMayChange", "OthersMayChange", false, false, false)]
        [InlineData("bu-delete", "O:BAD:(A;;SD;;;BU)", 0, "OthersMayChange", "OthersMayChange", "Sound", false, false, false)]
        [InlineData("bu-genwrite", "O:BAD:(A;;GW;;;BU)", 0, "OthersMayChange", "OthersMayChange", "Sound", false, false, true)]
        [InlineData("above-root", "O:SYD:(A;;FA;;;BA)(A;;0x4;;;AU)(A;OICIIO;0x1301bf;;;AU)(A;;0x1200a9;;;BU)", 0, "OthersMayChange", "OthersMayChange", "Sound", false, false, true)]
        [InlineData("above-au-40", "O:SYD:(A;;0x40;;;AU)", 0, "OthersMayChange", "OthersMayChange", "OthersMayChange", true, false, false)]
        [InlineData("callback-bu", "O:BAD:P(A;;FA;;;BA)(XA;;FA;;;BU;(Member_of {SID(BA)}))", 0, "OthersMayChange", "OthersMayChange", "OthersMayChange", false, false, false)]
        [InlineData("unknown-11-io", "O:BAD:P(A;OICIIO;FA;;;BU)(A;;FA;;;BA)", 0x11, "OthersMayChange", "OthersMayChange", "Sound", true, true, true)]
        [InlineData("null-dacl", "O:BAD:NO_ACCESS_CONTROL", 0, "NoList", "NoList", "NoList", false, false, false)]
        [InlineData("empty-dacl", "O:BAD:", 0, "Sound", "Sound", "Sound", true, true, true)]
        [InlineData("no-owner", "D:(A;;FA;;;BA)", 0, "OtherOwner", "OtherOwner", "OtherOwner", false, false, false)]
        [InlineData("maxallowed", "O:BAD:(A;;0x2000000;;;BU)", 0, "OthersMayChange", "OthersMayChange", "OthersMayChange", true, true, true)]
        public void EachLevel_AnswersAsTheRuleItReplaced(string name, string sddl, int kind, string own, string ownTrustedInstaller,
            string parent, bool file, bool folder, bool above)
        {
            byte[] d = Binary(sddl);
            if (kind != 0) d = WithFirstEntryOfKind(d, (byte)kind);
            Assert.True(Enum.Parse<Verdict>(own) == ChangeRule.Judge(d, Level.Own, false), name + " own");
            Assert.True(Enum.Parse<Verdict>(ownTrustedInstaller) == ChangeRule.Judge(d, Level.Own, true), name + " own, TrustedInstaller trusted");
            Assert.True(Enum.Parse<Verdict>(parent) == ChangeRule.Judge(d, Level.Parent), name + " parent");
            Assert.True(file == (ChangeRule.Judge(d, Level.VendorFile) == Verdict.Sound), name + " file");
            Assert.True(folder == (ChangeRule.Judge(d, Level.VendorFolder) == Verdict.Sound), name + " folder");
            Assert.True(above == (ChangeRule.Judge(d, Level.VendorAbove) == Verdict.Sound), name + " above");
        }

        /// <summary>An entry of a kind the rule does not read could allow
        /// anything to anyone, so it fails a vendor's folder, file and the
        /// folders above, as it already failed PadForge's own. The managed
        /// access rules TrustedLibrary read before left such an entry out and
        /// passed the folder. An entry of that kind that only what is made
        /// inside inherits does not apply to the folder itself.</summary>
        [Theory]
        [InlineData(0x11)]
        [InlineData(0x13)]
        [InlineData(0x15)]
        [InlineData(0x04)]
        public void AnEntryTheRuleCannotRead_FailsAVendorFolder(int kind)
        {
            byte[] d = WithFirstEntryOfKind(Binary("O:BAD:P(A;;FA;;;BU)(A;;FA;;;BA)"), (byte)kind);
            foreach (var level in Vendor.Append(Level.Own).Append(Level.Parent))
                Assert.Equal(Verdict.OthersMayChange, ChangeRule.Judge(d, level));
            byte[] inherited = WithFirstEntryOfKind(Binary("O:BAD:P(A;OICIIO;FA;;;BU)(A;;FA;;;BA)"), (byte)kind);
            Assert.Equal(Verdict.Sound, ChangeRule.Judge(inherited, Level.VendorFolder));
            Assert.Equal(Verdict.OthersMayChange, ChangeRule.Judge(inherited, Level.Own));
        }

        /// <summary>OWNER RIGHTS gives its rights to whoever owns the object,
        /// and the rule holds the owner to Administrators, SYSTEM or
        /// TrustedInstaller, so an OWNER RIGHTS entry with write rights
        /// passes at every level. TrustedLibrary's rule read it as another
        /// account before. Under an owner the rule does not trust, the owner
        /// fails it anyway.</summary>
        [Theory]
        [InlineData("O:BAD:P(A;OICI;FA;;;BA)(A;OICI;FA;;;S-1-3-4)")]
        [InlineData("O:SYD:P(A;OICI;FA;;;SY)(A;;0x1301bf;;;S-1-3-4)")]
        [InlineData("O:" + TI + "D:P(A;OICI;FA;;;SY)(A;OICI;FA;;;S-1-3-4)")]
        public void AnOwnerRightsEntry_WithWriteRights_UnderATrustedOwner_PassesEveryLevel(string sddl)
        {
            byte[] d = Binary(sddl);
            foreach (var level in Vendor.Append(Level.Parent))
                Assert.Equal(Verdict.Sound, ChangeRule.Judge(d, level));
            Assert.Equal(Verdict.Sound, ChangeRule.Judge(d, Level.Own, true));
            Assert.Equal(Verdict.OtherOwner, ChangeRule.Judge(Binary("O:BUD:P(A;OICI;FA;;;S-1-3-4)"), Level.VendorFolder));
        }

        /// <summary>An entry for CREATOR OWNER that is not inherited matches
        /// no account, so it changes nothing at any level, as PadForge's own
        /// rule already read it. The parent's and TrustedLibrary's rules
        /// failed it before.</summary>
        [Fact]
        public void ACreatorOwnerEntryThatIsNotInherited_MatchesNoOne()
        {
            byte[] d = Binary("O:BAD:P(A;;FA;;;BA)(A;;FA;;;CO)");
            foreach (var level in Vendor.Append(Level.Own).Append(Level.Parent))
                Assert.Equal(Verdict.Sound, ChangeRule.Judge(d, level));
        }

        /// <summary>A conditional allow entry and an allow entry for an
        /// object type are read as allow entries for their SID.</summary>
        [Fact]
        public void ConditionalAndObjectEntries_AreReadForTheirAccount()
        {
            byte[] conditional = Binary("O:BAD:P(A;;FA;;;BA)(XA;;FA;;;BU;(Member_of {SID(BA)}))");
            byte[] trustedConditional = Binary("O:BAD:P(A;;FA;;;BA)(XA;;FA;;;SY;(Member_of {SID(BA)}))");
            byte[] forObject = Binary("O:BAD:P(A;;FA;;;BA)(OA;;FA;bf967aba-0de6-11d0-a285-00aa003049e2;;BU)");
            foreach (var level in Vendor.Append(Level.Own).Append(Level.Parent))
            {
                Assert.Equal(Verdict.OthersMayChange, ChangeRule.Judge(conditional, level));
                Assert.Equal(Verdict.Sound, ChangeRule.Judge(trustedConditional, level));
                Assert.Equal(Verdict.OthersMayChange, ChangeRule.Judge(forObject, level));
            }
        }

        /// <summary>Bytes the rule cannot read are unreadable whatever the
        /// level, and it never throws on them.</summary>
        [Fact]
        public void AGarbledDescriptor_IsUnreadable_AndNeverThrows()
        {
            byte[] sound = Binary("O:BAD:P(A;OICI;FA;;;BA)(A;OICI;0x1200a9;;;BU)");
            Assert.Equal(Verdict.Unreadable, ChangeRule.Judge(null, Level.Own));
            Assert.Equal(Verdict.Unreadable, ChangeRule.Judge(new byte[] { 1, 0, 4 }, Level.Own));
            // Not self-relative.
            var absolute = (byte[])sound.Clone();
            absolute[3] &= 0x7F;
            Assert.Equal(Verdict.Unreadable, ChangeRule.Judge(absolute, Level.Parent));
            // Every truncation and many single-byte changes, at every level.
            var random = new Random(597);
            for (int length = 0; length < sound.Length; length++)
                foreach (Level level in Enum.GetValues<Level>())
                    ChangeRule.Judge(sound.Take(length).ToArray(), level);
            for (int i = 0; i < 4000; i++)
            {
                var changed = (byte[])sound.Clone();
                changed[random.Next(changed.Length)] = (byte)random.Next(256);
                foreach (Level level in Enum.GetValues<Level>())
                    ChangeRule.Judge(changed, level);
            }
            // A list that says it runs past the descriptor.
            var overlong = (byte[])sound.Clone();
            int dacl = BitConverter.ToInt32(overlong, 16);
            overlong[dacl + 2] = 0xFF;
            Assert.Equal(Verdict.Unreadable, ChangeRule.Judge(overlong, Level.VendorFile));
        }

        /// <summary>The rule runs in the protected start: no DllImport, no
        /// culture, no System.Environment, and nothing that throws.</summary>
        [Fact]
        public void TheRule_CallsNothingTheProtectedStartMayNotCall()
        {
            string file = OffLimitsSource.Read("PadForge.Engine/Common/ChangeRule.cs");
            string code = string.Join("\n", file.Split('\n').Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)));
            foreach (string banned in new[] { "DllImport(", "LibraryImport(", "Environment.", " Path.", "(Path.", "ToString(", "throw ",
                         "Guid.", "StringComparer", "StringComparison", "System.Security", "NativeLibrary", "Marshal." })
                Assert.DoesNotContain(banned, code, StringComparison.Ordinal);
        }

        /// <summary>Both callers use the one rule, and the code it replaced is
        /// gone.</summary>
        [Fact]
        public void BothCallers_UseTheOneRule()
        {
            string folder = OffLimitsSource.Read("PadForge.App/Common/ProtectedFolder.cs");
            Assert.Contains("ChangeRule.Judge(descriptor, ChangeRule.Level.Parent)", folder, StringComparison.Ordinal);
            Assert.Contains("ChangeRule.Judge(descriptor, ChangeRule.Level.Own, trustTrustedInstaller)", folder, StringComparison.Ordinal);
            Assert.DoesNotContain("RawSecurityDescriptor", folder, StringComparison.Ordinal);
            Assert.DoesNotContain("record struct Entry", folder, StringComparison.Ordinal);
            string library = OffLimitsSource.Read("PadForge.Engine/Common/TrustedLibrary.cs");
            Assert.Contains("ChangeRule.Judge(own, ChangeRule.Level.VendorFile)", library, StringComparison.Ordinal);
            Assert.Contains("ChangeRule.Judge(descriptor, level)", library, StringComparison.Ordinal);
            Assert.Contains("string why = WhoMayChange(full, hold.SafeFileHandle);", library, StringComparison.Ordinal);
            Assert.DoesNotContain("GetAccessControl", library, StringComparison.Ordinal);
            Assert.DoesNotContain("GetAccessRules", library, StringComparison.Ordinal);
            Assert.DoesNotContain("OthersMayChange(", library, StringComparison.Ordinal);
        }
    }
}
