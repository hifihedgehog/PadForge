// Single source of truth for AssemblyVersion + AssemblyFileVersion across
// the PadForge solution. Both PadForge.App and PadForge.Engine link this
// file as a shared compile item via <Compile Include="..\SharedVersion.cs">,
// so the two assemblies always ship at the same version number — no
// human discipline needed at release time, repo-enforced.
//
// Bump checklist for every release: edit the two numbers below. That's it.
// Both .dll artifacts pick up the new version on the next build.
//
// See memory: feedback_engine_version_matches_app.md for the rule's history.

using System.Reflection;

[assembly: AssemblyVersion("4.2.0.0")]
[assembly: AssemblyFileVersion("4.2.0.0")]
