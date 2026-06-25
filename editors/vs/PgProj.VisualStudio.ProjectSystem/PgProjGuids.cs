// EP-VS #25 Route B — shared GUIDs/IDs for the PgProj VS project-system extension.
using System;

namespace PgProj.VisualStudio
{
    /// <summary>
    /// Stable identifiers for the package, the .pgproj project type, the command set, and the
    /// "a PgProj project is present" UIContext. These must stay stable once shipped (VS persists
    /// them in .suo / registry, and the sibling OOP extension mirrors the command-set GUID + group
    /// id to place its database commands into our project context-menu group).
    /// </summary>
    internal static class PgProjGuids
    {
        /// <summary>The VS package GUID (matches <c>[Guid]</c> on <see cref="PgProjPackage"/>).</summary>
        public const string PackageGuidString = "b0000000-0000-0000-0000-000000000025";

        /// <summary>The .pgproj project-type GUID — what VS keys the CPS project type on.</summary>
        public const string ProjectTypeGuidString = "b0000000-0000-0000-0000-0000000000a1";

        /// <summary>
        /// The command-set GUID for PgProjCommands.vsct. The .pgproj project context-menu group
        /// (id 0x1020) lives in this set; the OOP extension (PgProj.VisualStudio, file
        /// Commands/PgProjProjectSystemMenus.cs) places Publish / Schema Compare into it.
        /// </summary>
        public const string CommandSetGuidString = "b0000000-0000-0000-0000-0000000000a2";

        /// <summary>The .pgproj project-node context-menu group (mirrored in PgProjCommands.vsct).</summary>
        public const int ProjectContextGroupId = 0x1020;

        /// <summary>The in-proc Import Database command (mirrored in PgProjCommands.vsct).</summary>
        public const int ImportDatabaseCommandId = 0x0103;

        /// <summary>The in-proc "Generate Tests" command on the .pgproj project node (mirrored in PgProjCommands.vsct).</summary>
        public const int GenerateTestsCommandId = 0x0105;

        /// <summary>The .sql item-node context-menu group (mirrored in PgProjCommands.vsct).</summary>
        public const int ItemContextGroupId = 0x1021;

        /// <summary>The in-proc "Sync with Database" file command (mirrored in PgProjCommands.vsct).</summary>
        public const int SyncFileCommandId = 0x0104;

        /// <summary>
        /// UIContext that is active iff the open solution contains a PgProj project
        /// (SolutionHasProjectCapability:PgProj). Database controls key their visibility on it.
        /// </summary>
        public const string PgProjLoadedUIContextGuidString = "b0000000-0000-0000-0000-0000000000a4";
    }
}
