// EP-VS #115. The Remote UI control hosting the Publish dialog.
using Microsoft.VisualStudio.Extensibility.UI;

namespace PgProj.VisualStudio.PublishDialog;

/// <summary>
/// Remote UI control for the modal Publish dialog. Its markup is the embedded
/// <c>PublishDialogControl.xaml</c> data template; the data context is a
/// <see cref="PublishDialogViewModel"/> the command reads back after OK.
/// </summary>
internal sealed class PublishDialogControl : RemoteUserControl
{
    public PublishDialogControl(PublishDialogViewModel dataContext)
        : base(dataContext)
    {
    }
}
