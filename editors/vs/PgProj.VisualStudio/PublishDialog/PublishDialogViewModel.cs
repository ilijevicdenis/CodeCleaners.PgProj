// EP-VS #115. Remote UI view model for the modal Publish dialog — connection, profile, SQLCMD
// variable overrides, and options, mirroring the SSDT publish dialog surface. The dialog only
// COLLECTS choices; PublishCommand turns them into PublishPlanOptions and runs the shared
// PublishService, so the VS publish stays byte-identical to the CLI's.
using System.Runtime.Serialization;
using Microsoft.VisualStudio.Extensibility.UI;

namespace PgProj.VisualStudio.PublishDialog;

/// <summary>Data context for <see cref="PublishDialogControl"/>. All values are user-editable.</summary>
[DataContract]
internal sealed class PublishDialogViewModel : NotifyPropertyChangedObject
{
    private string projectName = string.Empty;
    private string connectionString = string.Empty;
    private string profilePath = string.Empty;
    private string variables = string.Empty;
    private bool allowDrops;
    private bool noTransaction;
    private bool generateScriptOnly;
    private bool rememberConnection = true;

    /// <summary>The project file name shown in the dialog header.</summary>
    [DataMember]
    public string ProjectName
    {
        get => this.projectName;
        set => this.SetProperty(ref this.projectName, value);
    }

    /// <summary>The Npgsql connection string of the publish target (prefilled from PGPROJ_CONNECTION; never persisted).</summary>
    [DataMember]
    public string ConnectionString
    {
        get => this.connectionString;
        set => this.SetProperty(ref this.connectionString, value);
    }

    /// <summary>Optional .pgpublish.json profile path (prefilled when one sits next to the project).</summary>
    [DataMember]
    public string ProfilePath
    {
        get => this.profilePath;
        set => this.SetProperty(ref this.profilePath, value);
    }

    /// <summary>SQLCMD variable overrides, <c>Name=Value;Name2=Value2</c> — they beat the profile, which beats the project defaults.</summary>
    [DataMember]
    public string Variables
    {
        get => this.variables;
        set => this.SetProperty(ref this.variables, value);
    }

    /// <summary>Allow destructive changes (drop objects missing from the project). OR-ed with the profile's allow-drops.</summary>
    [DataMember]
    public bool AllowDrops
    {
        get => this.allowDrops;
        set => this.SetProperty(ref this.allowDrops, value);
    }

    /// <summary>Do not wrap the deploy script in BEGIN/COMMIT.</summary>
    [DataMember]
    public bool NoTransaction
    {
        get => this.noTransaction;
        set => this.SetProperty(ref this.noTransaction, value);
    }

    /// <summary>Generate the deploy script and open it instead of executing it (the dry-run shape).</summary>
    [DataMember]
    public bool GenerateScriptOnly
    {
        get => this.generateScriptOnly;
        set => this.SetProperty(ref this.generateScriptOnly, value);
    }

    /// <summary>
    /// Remember the connection for this project after it is used successfully (DPAPI-encrypted in the
    /// per-user store — never in the .pgproj/profile). Unchecking also forgets a stored one.
    /// </summary>
    [DataMember]
    public bool RememberConnection
    {
        get => this.rememberConnection;
        set => this.SetProperty(ref this.rememberConnection, value);
    }
}
