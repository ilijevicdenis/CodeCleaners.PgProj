// EP-VS #25 Route B — shared GUIDs/IDs for the PgProj VS extension. SCAFFOLD.
using System;

namespace PgProj.VisualStudio
{
    /// <summary>
    /// Stable identifiers for the package, the .pgproj project type, the command set, and the
    /// tool windows. These must stay stable once shipped (VS persists them in .suo / registry).
    /// </summary>
    internal static class PgProjGuids
    {
        /// <summary>The VS package GUID (matches <c>[Guid]</c> on <see cref="PgProjPackage"/>).</summary>
        public const string PackageGuidString = "b0000000-0000-0000-0000-000000000025";

        /// <summary>The .pgproj project-type GUID — what VS keys the project factory/flavor on.</summary>
        public const string ProjectTypeGuidString = "b0000000-0000-0000-0000-0000000000a1";

        /// <summary>The command-set GUID shared by the .vsct and the command classes.</summary>
        public const string CommandSetGuidString = "b0000000-0000-0000-0000-0000000000a2";
        public static readonly Guid CommandSet = new Guid(CommandSetGuidString);

        // Command IDs (mirrored in PgProjCommands.vsct).
        public const int PublishCommandId = 0x0100;
        public const int SchemaCompareCommandId = 0x0101;

        /// <summary>Tool-window GUID for the Schema Compare window.</summary>
        public const string SchemaCompareWindowGuidString = "b0000000-0000-0000-0000-0000000000a3";
    }
}
