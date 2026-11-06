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

        // Nothing named in a file runs.
        public const string ProgramNotRun = "Import_Tr_ProgramNotRun";
        public const string SystemCommand = "Import_Tr_SystemCommand";

        // Skipped lines.
        public const string UnknownInput = "Import_Tr_UnknownInput";
        public const string UnknownOutput = "Import_Tr_UnknownOutput";
        public const string UnknownCommand = "Import_Tr_UnknownCommand";
        public const string NoSuchPadButton = "Import_Tr_NoSuchPadButton";
        public const string Crosshair = "Import_Tr_Crosshair";
        public const string JitterShaking = "Import_Tr_JitterShaking";
        public const string AnalogActuation = "Import_Tr_AnalogActuation";
        public const string RemoteTarget = "Import_Tr_RemoteTarget";
        public const string ThirdStick = "Import_Tr_ThirdStick";
        public const string SteeringWheel = "Import_Tr_SteeringWheel";
        public const string ProfileSwitch = "Import_Tr_ProfileSwitch";
        public const string ControllerSetting = "Import_Tr_ControllerSetting";
        public const string ActionNotFound = "Import_Tr_ActionNotFound";

        // Partial lines.
        public const string ConsumeKeys = "Import_Tr_ConsumeKeys";
        public const string HeldAsTap = "Import_Tr_HeldAsTap";
        public const string CurveFitted = "Import_Tr_CurveFitted";
        public const string CurveApproximated = "Import_Tr_CurveApproximated";
        public const string ZoneOutward = "Import_Tr_ZoneOutward";
        public const string UnlockLayer = "Import_Tr_UnlockLayer";
        public const string RadialFlattened = "Import_Tr_RadialFlattened";
        public const string RadialMenu = "Import_Tr_RadialMenu";
        public const string RegionCentered = "Import_Tr_RegionCentered";
        public const string SharedControllers = "Import_Tr_SharedControllers";
        public const string ToggleSameGesture = "Import_Tr_ToggleSameGesture";
        public const string LedShownAs = "Import_Tr_LedShownAs";
        public const string LoweredToKeys = "Import_Tr_LoweredToKeys";
        public const string StickAdded = "Import_Tr_StickAdded";
        public const string MoveSpeed = "Import_Tr_MoveSpeed";
        public const string HipFire = "Import_Tr_HipFire";
        public const string MaxOutput = "Import_Tr_MaxOutput";
        public const string TouchpadShape = "Import_Tr_TouchpadShape";
        public const string LayerNoInput = "Import_Tr_LayerNoInput";
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

        /// <summary>A <c>Import_Tr_*</c> key when the file was refused, else
        /// null.</summary>
        public string RefusedKey { get; init; }

        public bool Refused => RefusedKey != null;

        /// <summary>The tool's name as its own project writes it.</summary>
        public string ToolName => Kind switch
        {
            ForeignConfigKind.Rewasd => "reWASD",
            ForeignConfigKind.Ds4Windows => "DS4Windows",
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
        /// reWASD's shared profile folder and DS4Windows' per-user one.</summary>
        public static IEnumerable<string> ProfileFolders()
        {
            string rewasd = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonDocuments),
                "reWASD", "Profiles");
            if (System.IO.Directory.Exists(rewasd)) yield return rewasd;

            string ds4 = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "DS4Windows", "Profiles");
            if (System.IO.Directory.Exists(ds4)) yield return ds4;
        }

        /// <summary>True when the file's extension is one of the two this
        /// importer takes. The content still decides.</summary>
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
