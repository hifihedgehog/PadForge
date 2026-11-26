namespace PadForge.Engine.Common
{
    /// <summary>
    /// The one rule for whether an account other than Administrators, SYSTEM
    /// and TrustedInstaller may change a file or folder (#600), read from its
    /// security descriptor in the self-relative form Windows gives
    /// (GetSecurityInfo). PadForge's protected folders and the parent that
    /// holds them (PadForge.Common.ProtectedFolder), the folder PadForge.exe
    /// runs from (PadForge.Services.ExeFolder) and a vendor library with the
    /// folders above it (<see cref="TrustedLibrary"/>) are all judged
    /// here.
    ///
    /// <para>The owner must be Administrators or SYSTEM, or TrustedInstaller
    /// where the caller trusts it as an owner, since an owner can always
    /// change the list. There must be a list, since none lets everyone do
    /// everything. Then each entry that allows a right the level counts goes
    /// to Administrators, SYSTEM or TrustedInstaller, or to CREATOR OWNER or
    /// OWNER RIGHTS, which give their rights to whoever owns what they
    /// cover, an owner the rule already holds to those three (an entry for
    /// CREATOR OWNER that is not inherited matches no one). Deny and audit
    /// entries allow nothing. An entry of a kind the rule does not read could
    /// allow anything to anyone, so it fails the object.</para>
    ///
    /// <para>The protected start judges PadForge's folders before the process
    /// may trust the folder its native libraries were unpacked into, so this
    /// class reads the bytes itself: it calls into no library, reads no
    /// culture and loads nothing native.</para>
    /// </summary>
    public static class ChangeRule
    {
        /// <summary>What is judged, and so which rights count.</summary>
        public enum Level
        {
            /// <summary>A folder of PadForge's own, or a file in one: every
            /// right past reading counts, and so do the entries only what is
            /// made inside inherits, since what PadForge puts there carries
            /// them.</summary>
            Own,

            /// <summary>The folder that holds one of PadForge's own: deleting
            /// or replacing what it holds, changing who may, or owning
            /// it.</summary>
            Parent,

            /// <summary>A vendor library's file: writing, appending or
            /// deleting it, changing who may, or owning it.</summary>
            VendorFile,

            /// <summary>The folder that holds a vendor library: adding a file
            /// or folder, deleting one, deleting the folder, changing who may,
            /// or owning it.</summary>
            VendorFolder,

            /// <summary>A folder above that one: deleting or renaming what it
            /// holds or the folder itself, changing who may, or owning
            /// it.</summary>
            VendorAbove,
        }

        public enum Verdict
        {
            /// <summary>No other account may change it.</summary>
            Sound,

            /// <summary>The descriptor is not one this rule can read.</summary>
            Unreadable,

            /// <summary>Owned by another account, which can always change its
            /// list.</summary>
            OtherOwner,

            /// <summary>It has no access list, which lets everyone do
            /// everything.</summary>
            NoList,

            /// <summary>Its list lets another account change it, or holds an
            /// entry the rule does not read.</summary>
            OthersMayChange,
        }

        // Access rights (Microsoft, File Security and Access Rights; Access
        // Mask Format).
        private const int WriteData = 0x2, AppendData = 0x4, DeleteChild = 0x40, Delete = 0x10000, WriteDac = 0x40000,
            WriteOwner = 0x80000, MaximumAllowed = 0x02000000, GenericAll = 0x10000000, GenericWrite = 0x40000000;

        /// <summary>The rights that only read: read data and attributes and
        /// extended attributes, execute or traverse, read the access list,
        /// wait on the object, and the generic read and execute. At
        /// <see cref="Level.Own"/> every other bit counts.</summary>
        public const int ReadOnlyRights = unchecked((int)0xA01200A9);

        /// <summary>The rights over a parent that let an account take away or
        /// replace a folder inside it: delete a child, change the list or the
        /// owner, all rights, and the most it may be granted.</summary>
        public const int ReplaceChildRights = DeleteChild | WriteDac | WriteOwner | GenericAll | MaximumAllowed;

        public const int VendorFileRights = WriteDac | WriteOwner | GenericAll | Delete | WriteData | AppendData | GenericWrite;

        public const int VendorFolderRights = VendorFileRights | DeleteChild;

        public const int VendorAboveRights = WriteDac | WriteOwner | GenericAll | Delete | DeleteChild;

        /// <summary>The rights that count at <paramref name="level"/>.</summary>
        public static int RightsThatCount(Level level) => level switch
        {
            Level.Own => ~ReadOnlyRights,
            Level.Parent => ReplaceChildRights,
            Level.VendorFile => VendorFileRights,
            Level.VendorFolder => VendorFolderRights,
            _ => VendorAboveRights,
        };

        // The descriptor's control bits and the entries' kinds and flags
        // (Microsoft, SECURITY_DESCRIPTOR_CONTROL, ACE_HEADER).
        private const int DaclPresent = 0x0004, SelfRelative = 0x8000;
        private const int InheritOnlyAce = 0x08;
        private const int ObjectTypePresent = 0x1, InheritedObjectTypePresent = 0x2;

        /// <summary>
        /// Judges <paramref name="descriptor"/>, a self-relative security
        /// descriptor, at <paramref name="level"/>. TrustedInstaller may own
        /// it when <paramref name="trustedInstallerMayOwn"/>, as it owns what
        /// Windows and its installers set up. Never throws.
        /// </summary>
        public static Verdict Judge(byte[] descriptor, Level level, bool trustedInstallerMayOwn = true)
        {
            byte[] d = descriptor;
            if (d == null || d.Length < 20 || d[0] != 1) return Verdict.Unreadable;
            int control = U16(d, 2);
            if ((control & SelfRelative) == 0) return Verdict.Unreadable;
            int ownerAt = I32(d, 4), daclAt = I32(d, 16);
            bool hasList = (control & DaclPresent) != 0 && daclAt != 0;

            // Every part is read before anything is judged, so a descriptor
            // that cannot be read is that, whatever its owner.
            if (ownerAt != 0 && !IsSid(d, ownerAt, d.Length)) return Verdict.Unreadable;
            int count = 0, first = 0, end = 0;
            if (hasList)
            {
                if (daclAt < 20 || daclAt > d.Length - 8) return Verdict.Unreadable;
                if (d[daclAt] != 2 && d[daclAt] != 4) return Verdict.Unreadable;
                int size = U16(d, daclAt + 2);
                count = U16(d, daclAt + 4);
                if (size < 8 || daclAt + size > d.Length) return Verdict.Unreadable;
                first = daclAt + 8;
                end = daclAt + size;
                int at = first;
                for (int i = 0; i < count; i++)
                {
                    if (at > end - 4) return Verdict.Unreadable;
                    int length = U16(d, at + 2);
                    if (length < 4 || at + length > end) return Verdict.Unreadable;
                    if (Read(d, at, length, out _, out int sidAt) != Kind.Unknown && !IsSid(d, sidAt, at + length))
                        return Verdict.Unreadable;
                    at += length;
                }
            }

            if (ownerAt == 0 || !IsTrustedOwner(d, ownerAt, trustedInstallerMayOwn)) return Verdict.OtherOwner;
            if (!hasList) return Verdict.NoList;
            int counted = RightsThatCount(level);
            bool inheritOnlyCounts = level == Level.Own;
            for (int i = 0, at = first; i < count; i++)
            {
                int length = U16(d, at + 2);
                bool inheritOnly = (d[at + 1] & InheritOnlyAce) != 0;
                int here = at;
                at += length;
                if (inheritOnly && !inheritOnlyCounts) continue;
                Kind kind = Read(d, here, length, out int mask, out int sidAt);
                if (kind == Kind.Unknown) return Verdict.OthersMayChange;
                if (kind == Kind.AllowsNothing || (mask & counted) == 0) continue;
                if (IsTrustedAccount(d, sidAt) || IsOwnerStandIn(d, sidAt)) continue;
                return Verdict.OthersMayChange;
            }
            return Verdict.Sound;
        }

        private enum Kind { Allows, AllowsNothing, Unknown }

        /// <summary>
        /// Reads the entry at <paramref name="at"/>: its kind, its rights and
        /// where its SID starts (-1 when the entry is too short for one). The
        /// allow entries, plain, with a condition or for an object type, allow
        /// their rights. The deny, audit and alarm entries of the same forms
        /// allow nothing. Every other kind is <see cref="Kind.Unknown"/>
        /// (Microsoft, ACE_HEADER: ACCESS_ALLOWED_ACE_TYPE 0x0 to
        /// SYSTEM_ALARM_CALLBACK_OBJECT_ACE_TYPE 0x10, with the compound kind
        /// 0x4 left out).
        /// </summary>
        private static Kind Read(byte[] d, int at, int length, out int mask, out int sidAt)
        {
            mask = 0;
            sidAt = -1;
            int type = d[at];
            // The mask, then the SID. The callback forms 0x09, 0x0A, 0x0D and
            // 0x0E are laid out as the plain ones are (winnt.h,
            // SYSTEM_ALARM_CALLBACK_ACE: Header, Mask, SidStart).
            bool plain = type is 0x00 or 0x01 or 0x02 or 0x03 or 0x09 or 0x0A or 0x0D or 0x0E;
            // The mask, the flags, the GUIDs the flags name, then the SID.
            bool forObject = type is 0x05 or 0x06 or 0x07 or 0x08 or 0x0B or 0x0C or 0x0F or 0x10;
            if (!plain && !forObject) return Kind.Unknown;
            if (length >= 8) mask = I32(d, at + 4);
            if (plain) sidAt = at + 8;
            else if (length >= 12)
            {
                int flags = I32(d, at + 8);
                sidAt = at + 12 + ((flags & ObjectTypePresent) != 0 ? 16 : 0) + ((flags & InheritedObjectTypePresent) != 0 ? 16 : 0);
            }
            return type is 0x00 or 0x05 or 0x09 or 0x0B ? Kind.Allows : Kind.AllowsNothing;
        }

        /// <summary>Whether a whole SID starts at <paramref name="at"/> and
        /// ends by <paramref name="limit"/>: revision 1, at most 15
        /// sub-authorities.</summary>
        private static bool IsSid(byte[] d, int at, int limit)
        {
            if (at < 0 || at > limit - 8 || d[at] != 1) return false;
            int subs = d[at + 1];
            return subs <= 15 && at + 8 + 4 * subs <= limit;
        }

        private static bool IsTrustedOwner(byte[] d, int at, bool trustedInstallerMayOwn)
            => IsAdministrators(d, at) || IsSystem(d, at) || (trustedInstallerMayOwn && IsTrustedInstaller(d, at));

        /// <summary>Administrators, SYSTEM and TrustedInstaller.</summary>
        private static bool IsTrustedAccount(byte[] d, int at)
            => IsAdministrators(d, at) || IsSystem(d, at) || IsTrustedInstaller(d, at);

        /// <summary>CREATOR OWNER (S-1-3-0) and OWNER RIGHTS (S-1-3-4).</summary>
        private static bool IsOwnerStandIn(byte[] d, int at)
            => Is(d, at, 3, 0) || Is(d, at, 3, 4);

        /// <summary>S-1-5-32-544.</summary>
        private static bool IsAdministrators(byte[] d, int at) => Is(d, at, 5, 32, 544);

        /// <summary>S-1-5-18.</summary>
        private static bool IsSystem(byte[] d, int at) => Is(d, at, 5, 18);

        /// <summary>S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464.</summary>
        private static bool IsTrustedInstaller(byte[] d, int at)
            => Is(d, at, 5, 80, 956008885, 3418522649, 1831038044, 1853292631, 2271478464);

        /// <summary>Whether the SID at <paramref name="at"/> is the one with
        /// that identifier authority and those sub-authorities. The authority
        /// is six bytes, most significant first, and each sub-authority four,
        /// least significant first (Microsoft, SID).</summary>
        private static bool Is(byte[] d, int at, long authority, params uint[] subs)
        {
            if (d[at + 1] != subs.Length) return false;
            long read = 0;
            for (int i = 0; i < 6; i++) read = (read << 8) | d[at + 2 + i];
            if (read != authority) return false;
            for (int i = 0; i < subs.Length; i++)
                if (unchecked((uint)I32(d, at + 8 + 4 * i)) != subs[i]) return false;
            return true;
        }

        private static int U16(byte[] d, int at) => d[at] | (d[at + 1] << 8);

        private static int I32(byte[] d, int at) => d[at] | (d[at + 1] << 8) | (d[at + 2] << 16) | (d[at + 3] << 24);
    }
}
