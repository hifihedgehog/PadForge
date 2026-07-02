using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

// The wire codecs' framing internals (device-list encode/decode) are
// pinned by tests so a layout change can never ship silently.
[assembly: InternalsVisibleTo("PadForge.Tests")]

[assembly: AssemblyTitle("PadForge.Engine")]
[assembly: AssemblyDescription("PadForge input engine — SDL3, Raw Input, and HID device enumeration.")]
[assembly: AssemblyConfiguration("")]
[assembly: AssemblyCompany("PadForge")]
[assembly: AssemblyProduct("PadForge")]
[assembly: AssemblyCopyright("Copyright © PadForge Contributors")]
[assembly: AssemblyTrademark("")]
[assembly: AssemblyCulture("")]

[assembly: ComVisible(false)]

[assembly: Guid("A1B2C3D4-E5F6-7890-ABCD-EF1234567890")]

// AssemblyVersion + AssemblyFileVersion live in ..\SharedVersion.cs,
// linked into both PadForge.App and PadForge.Engine so the two
// assemblies can never drift apart.
