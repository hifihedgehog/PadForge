using System;
using System.Collections.Generic;
using System.Linq;
using PadForge.Services;
using PadForge.SteamWorkshop.Translation;

namespace PadForge.Common.ConfigImport
{
    /// <summary>The reason keys the import preview shows, each a
    /// <c>Import_Tr_*</c> string resource. Separate from Steam's reason
    /// vocabulary, whose list is locked to what its translator emits.</summary>
    internal static class ImportReasons
    {
        // Whole-file refusals.
        public const string FileUnreadable = "Import_Tr_FileUnreadable";
        public const string FileTooLarge = "Import_Tr_FileTooLarge";
        public const string FileNotConfig = "Import_Tr_FileNotConfig";
        public const string FileHasDtd = "Import_Tr_FileHasDtd";
        public const string RewasdOtherVersion = "Import_Tr_RewasdOtherVersion";

        // Skipped lines.
        public const string UnknownInput = "Import_Tr_UnknownInput";
        public const string UnknownOutput = "Import_Tr_UnknownOutput";
        public const string UnknownCommand = "Import_Tr_UnknownCommand";
        public const string NoSuchPadButton = "Import_Tr_NoSuchPadButton";
        public const string MacroCannotPress = "Import_Tr_MacroCannotPress";
        public const string Crosshair = "Import_Tr_Crosshair";
        public const string JitterShaking = "Import_Tr_JitterShaking";
        public const string AnalogActuation = "Import_Tr_AnalogActuation";
        public const string RemoteTarget = "Import_Tr_RemoteTarget";
        public const string ThirdStick = "Import_Tr_ThirdStick";
        public const string SteeringWheel = "Import_Tr_SteeringWheel";
        public const string ControllerSetting = "Import_Tr_ControllerSetting";
        public const string NoSuchSetting = "Import_Tr_NoSuchSetting";
        public const string ActionNotFound = "Import_Tr_ActionNotFound";

        // Partial lines.
        public const string ConsumeKeys = "Import_Tr_ConsumeKeys";
        public const string MappedKeyHeldBack = "Import_Tr_MappedKeyHeldBack";
        public const string HideController = "Import_Tr_HideController";
        public const string MouseMovement = "Import_Tr_MouseMovement";
        public const string MotionOutput = "Import_Tr_MotionOutput";
        public const string UnloadTrigger = "Import_Tr_UnloadTrigger";
        public const string RadialFlattened = "Import_Tr_RadialFlattened";
        public const string RadialMenu = "Import_Tr_RadialMenu";
        public const string SharedControllers = "Import_Tr_SharedControllers";
        public const string MoveSpeed = "Import_Tr_MoveSpeed";
        public const string TouchpadShape = "Import_Tr_TouchpadShape";
        public const string LayerNoInput = "Import_Tr_LayerNoInput";
        public const string GyroAxes = "Import_Tr_GyroAxes";
        public const string TiltLean = "Import_Tr_TiltLean";
        public const string TouchpadDiagonal = "Import_Tr_TouchpadDiagonal";
        public const string GyroZone = "Import_Tr_GyroZone";
        public const string AbsoluteMouse = "Import_Tr_AbsoluteMouse";
        public const string StickBeforeRotation = "Import_Tr_StickBeforeRotation";
        public const string StickBeforeCurve = "Import_Tr_StickBeforeCurve";
        public const string StickByAngle = "Import_Tr_StickByAngle";
        public const string GyroByAngle = "Import_Tr_GyroByAngle";
        public const string CurveOnRows = "Import_Tr_CurveOnRows";

        // DSX. Each names the fact behind its line: what DSX publishes, and
        // where PadForge keeps a setting a profile does not hold.
        public const string DsxNumber = "Import_Tr_DsxNumber";
        public const string DsxUnit = "Import_Tr_DsxUnit";
        public const string DsxShaping = "Import_Tr_DsxShaping";
        public const string DsxDpad = "Import_Tr_DsxDpad";
        public const string DsxEdited = "Import_Tr_DsxEdited";
        public const string DsxPreset = "Import_Tr_DsxPreset";
        public const string DsxModeName = "Import_Tr_DsxModeName";
        public const string DsxPassthrough = "Import_Tr_DsxPassthrough";
        public const string DsxNoData = "Import_Tr_DsxNoData";
        public const string DsxTurbo = "Import_Tr_DsxTurbo";
        public const string DsxKeyName = "Import_Tr_DsxKeyName";
        public const string DsxMotionAxes = "Import_Tr_DsxMotionAxes";
        public const string DsxHaptics = "Import_Tr_DsxHaptics";
        public const string AppSetting = "Import_Tr_AppSetting";
    }

    /// <summary>What an import came to: a profile and its report, or the
    /// reason the file was refused.</summary>
    public sealed class ForeignImportResult
    {
        public ForeignConfigKind Kind { get; init; }
        public ProfileData Profile { get; init; }
        public TranslationReport Report { get; init; }
        public bool ReadsKeyboard { get; init; }
        public bool ReadsMouse { get; init; }
        public bool ReadsController { get; init; }

        /// <summary>What the profile's buttons start or power off.</summary>
        public IReadOnlyList<string> Runs { get; init; } = Array.Empty<string>();

        /// <summary>What the profile starts by itself when it loads.</summary>
        public IReadOnlyList<string> Starts { get; init; } = Array.Empty<string>();

        /// <summary>A <c>Import_Tr_*</c> key when the file was refused, else
        /// null.</summary>
        public string RefusedKey { get; init; }

        public bool Refused => RefusedKey != null;

        /// <summary>The tool's name as its own project writes it.</summary>
        public string ToolName => Kind switch
        {
            ForeignConfigKind.Rewasd => "reWASD",
            ForeignConfigKind.Ds4Windows => "DS4Windows",
            ForeignConfigKind.Dsx => "DSX",
            _ => string.Empty,
        };

        /// <summary>True when the profile claims at least one slot.</summary>
        public bool HasSlots => Profile?.SlotCreated != null && Profile.SlotCreated.Any(c => c);

        internal static ForeignImportResult Refuse(ForeignConfigKind kind, string key)
            => new() { Kind = kind, RefusedKey = key };
    }

    /// <summary>Reads another tool's config file and translates it. Nothing
    /// here changes PadForge's state: the caller decides whether the result
    /// joins the profile list.</summary>
    public static class ForeignConfigImporter
    {
        /// <summary>The file picker's places, when they exist on this PC:
        /// reWASD's shared profile folder, DS4Windows' per-user one and DSX's
        /// in each Steam library. The per-user folder and Steam's own path
        /// are the desktop user's (<see cref="PadForge.Common.ShellUser"/>),
        /// never the account PadForge runs as when an administrator's
        /// credentials were typed at its rights prompt.</summary>
        public static IEnumerable<string> ProfileFolders()
            => ProfileFolders(PadForge.Common.ShellUser.Folder);

        internal static IEnumerable<string> ProfileFolders(Func<PadForge.Services.Install.UserFolder, string> userFolder)
        {
            string rewasd = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonDocuments),
                "reWASD", "Profiles");
            if (System.IO.Directory.Exists(rewasd)) yield return rewasd;

            string roaming = userFolder(PadForge.Services.Install.UserFolder.RoamingData);
            if (!string.IsNullOrEmpty(roaming))
            {
                string ds4 = System.IO.Path.Combine(roaming, "DS4Windows", "Profiles");
                if (System.IO.Directory.Exists(ds4)) yield return ds4;
            }

            foreach (string dsx in DsxProfileFolders(SteamLibraries())) yield return dsx;
        }

        /// <summary>DSX 3.1 keeps its profiles in
        /// <c>DSX_Savefile\Configuration Files\Controller Profiles</c> under its
        /// folder, which is Steam's <c>steamapps\common\DSX</c> or a folder one
        /// level inside it (a beta build's <c>Main_v3_Beta</c>).</summary>
        internal static IEnumerable<string> DsxProfileFolders(IEnumerable<string> libraries)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string library in libraries)
            {
                string dsx = System.IO.Path.Combine(library, "steamapps", "common", "DSX");
                var roots = new List<string> { dsx };
                try
                {
                    if (System.IO.Directory.Exists(dsx))
                        roots.AddRange(System.IO.Directory.EnumerateDirectories(dsx));
                }
                catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
                {
                }
                foreach (string root in roots)
                {
                    string profiles = System.IO.Path.Combine(root, "DSX_Savefile", "Configuration Files", "Controller Profiles");
                    if (System.IO.Directory.Exists(profiles) && seen.Add(profiles)) yield return profiles;
                }
            }
        }

        /// <summary>Steam's library folders: its own install, from the
        /// registry or its default place, and every path its
        /// <c>steamapps\libraryfolders.vdf</c> lists.</summary>
        internal static IEnumerable<string> SteamLibraries()
        {
            var steam = new List<string>();
            try
            {
                using var key = PadForge.Common.ShellUser.OpenKey(@"Software\Valve\Steam");
                if (key?.GetValue("SteamPath") is string path && path.Length > 0)
                    steam.Add(path.Replace('/', '\\'));
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or System.IO.IOException)
            {
            }
            steam.Add(System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam"));

            var libraries = new List<string>();
            foreach (string root in steam.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!System.IO.Directory.Exists(root)) continue;
                libraries.Add(root);
                byte[] vdf = ForeignConfigReader.ReadCapped(
                    System.IO.Path.Combine(root, "steamapps", "libraryfolders.vdf"), out _);
                if (vdf == null) continue;
                libraries.AddRange(LibraryPaths(System.Text.Encoding.UTF8.GetString(vdf)));
            }
            return libraries.Distinct(StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>The <c>"path"</c> values of a <c>libraryfolders.vdf</c>,
        /// whose backslashes the file doubles.</summary>
        internal static IEnumerable<string> LibraryPaths(string vdf)
        {
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(
                         vdf ?? "", "\"path\"\\s+\"([^\"]+)\""))
                yield return m.Groups[1].Value.Replace(@"\\", @"\");
        }

        /// <summary>True when the file's extension is one this importer
        /// takes. The content still decides.</summary>
        public static bool HandlesFile(string path) => ForeignConfigReader.HasForeignExtension(path);

        public static ForeignImportResult Translate(string path)
        {
            byte[] bytes = ForeignConfigReader.ReadCapped(path, out string errorKey);
            if (bytes == null) return ForeignImportResult.Refuse(ForeignConfigKind.Unknown, errorKey);
            return Translate(bytes, path);
        }

        /// <summary>Translates a file's bytes. The path names the profile and
        /// finds the files beside it. It is never opened as anything else. A
        /// file from anywhere must not be able to bring the app down, so a
        /// shape the translators did not expect refuses the file.</summary>
        public static ForeignImportResult Translate(byte[] bytes, string path)
        {
            try
            {
                return TranslateCore(bytes, path);
            }
            catch (Exception ex) when (ex is InvalidOperationException or FormatException or ArgumentException
                or KeyNotFoundException or IndexOutOfRangeException or NullReferenceException or OverflowException)
            {
                return ForeignImportResult.Refuse(ForeignConfigKind.Unknown, ImportReasons.FileNotConfig);
            }
        }

        internal static ForeignImportResult TranslateCore(byte[] bytes, string path)
        {
            var kind = ForeignConfigReader.Detect(bytes);
            switch (kind)
            {
                case ForeignConfigKind.Rewasd:
                    using (var doc = ForeignConfigReader.TryParseJson(bytes))
                    {
                        if (doc == null) return ForeignImportResult.Refuse(kind, ImportReasons.FileNotConfig);
                        return RewasdTranslator.Translate(doc.RootElement, path);
                    }
                case ForeignConfigKind.Ds4Windows:
                    var xml = ForeignConfigReader.TryParseXml(bytes, out _);
                    if (xml == null) return ForeignImportResult.Refuse(kind, ImportReasons.FileNotConfig);
                    return Ds4WindowsTranslator.Translate(xml, path);
                case ForeignConfigKind.Dsx:
                    using (var dsx = ForeignConfigReader.TryParseJson(bytes))
                    {
                        if (dsx == null) return ForeignImportResult.Refuse(kind, ImportReasons.FileNotConfig);
                        return DsxTranslator.Translate(dsx.RootElement, path);
                    }
                default:
                    // Say why when the reason is the safety rule, so a
                    // hostile file is not reported as merely unrecognized.
                    ForeignConfigReader.TryParseXml(bytes, out bool refusedDtd);
                    return ForeignImportResult.Refuse(kind,
                        refusedDtd ? ImportReasons.FileHasDtd : ImportReasons.FileNotConfig);
            }
        }
    }
}
