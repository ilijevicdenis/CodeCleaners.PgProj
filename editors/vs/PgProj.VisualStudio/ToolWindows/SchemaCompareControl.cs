// EP-VS #25 Route B (modern). The Remote UI control hosting the Schema Compare view.
using Microsoft.VisualStudio.Extensibility.UI;

namespace PgProj.VisualStudio.ToolWindows;

/// <summary>
/// Remote UI control for the Schema Compare tool window. Its markup is the embedded
/// <c>SchemaCompareControl.xaml</c> data template; the data context is the live
/// <see cref="SchemaCompareViewModel"/> session (pickers, checkable diff, Script/Apply).
/// </summary>
internal sealed class SchemaCompareControl : RemoteUserControl
{
    public SchemaCompareControl(SchemaCompareViewModel dataContext)
        : base(dataContext)
    {
    }
}
