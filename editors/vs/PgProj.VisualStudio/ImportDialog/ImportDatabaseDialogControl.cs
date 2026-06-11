// EP-VS "Import Database…". The Remote UI control hosting the import dialog.
using Microsoft.VisualStudio.Extensibility.UI;

namespace PgProj.VisualStudio.ImportDialog;

/// <summary>
/// Remote UI control for the Import Database dialog. Its markup is the embedded
/// <c>ImportDatabaseDialogControl.xaml</c> data template; the data context is an
/// <see cref="ImportDatabaseDialogViewModel"/> the command reads back after OK.
/// </summary>
internal sealed class ImportDatabaseDialogControl : RemoteUserControl
{
    public ImportDatabaseDialogControl(ImportDatabaseDialogViewModel dataContext)
        : base(dataContext)
    {
    }
}
