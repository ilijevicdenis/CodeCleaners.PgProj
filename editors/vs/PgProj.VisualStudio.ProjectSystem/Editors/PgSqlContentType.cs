// EP-VS — the PostgreSQL editor content type. A .sql buffer carrying this content type gets the
// PgProj language client (PostgreSQL diagnostics/IntelliSense via the bundled LSP server) and is
// invisible to Visual Studio's built-in T-SQL components, which key on their own content type.
using System.ComponentModel.Composition;
using Microsoft.VisualStudio.Utilities;

namespace PgProj.VisualStudio.ProjectSystem.Editors
{
    /// <summary>
    /// MEF declaration of the <c>pgsql</c> content type. There is deliberately NO
    /// FileExtensionToContentTypeDefinition for <c>.sql</c>: the extension stays owned by the
    /// T-SQL tooling globally, and <see cref="PgSqlEditorFactory"/> assigns this content type
    /// per-buffer, only for files that belong to a PgProj project.
    /// </summary>
    internal static class PgSqlContentType
    {
        public const string Name = "pgsql";

        [Export]
        [Name(Name)]
        [BaseDefinition("code")]
        internal static ContentTypeDefinition PgSqlContentTypeDefinition;
    }
}
