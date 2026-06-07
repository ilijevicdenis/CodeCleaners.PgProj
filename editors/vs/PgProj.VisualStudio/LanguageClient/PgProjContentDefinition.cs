// EP-VS #25 Route B — content type that binds .sql files to the PgProj LSP client. SCAFFOLD.
using System.ComponentModel.Composition;
using Microsoft.VisualStudio.Utilities;

namespace PgProj.VisualStudio.LanguageClient
{
    /// <summary>
    /// Declares a content type for PgProj .sql files and maps the ".sql" file extension to it, so the
    /// MEF-exported <see cref="PgProjLanguageClient"/> activates for SQL editors in a PgProj solution.
    /// (Mirrors the VS Code DocumentSelector { language: "sql" } from docs/LSP_LANGUAGE_SERVER.md.)
    /// </summary>
    internal static class PgProjContentDefinition
    {
        public const string ContentTypeName = "PgProjSql";

        [Export]
        [Name(ContentTypeName)]
        [BaseDefinition(CodeRemoteContentDefinition.CodeRemoteContentTypeName)]
        internal static ContentTypeDefinition PgProjSqlContentType;

        [Export]
        [FileExtension(".sql")]
        [ContentType(ContentTypeName)]
        internal static FileExtensionToContentTypeDefinition PgProjSqlFileExtension;
    }
}
