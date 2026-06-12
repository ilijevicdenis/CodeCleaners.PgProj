// EP-VS "Import Database…" — Remote UI view model for the import dialog: connect to a live
// PostgreSQL database, list every importable object (the engine's extract file units), let the
// user check which to import, then PgProjImportCommand writes the checked units into the project.
// VS's built-in Connect-to-Database (DDEX/Server Explorer) is deliberately NOT used: it has no
// maintained PostgreSQL provider, so this dialog owns the connection field + a Test Connection
// probe through the in-process Npgsql engine instead.
using System.Runtime.Serialization;
using Microsoft.VisualStudio.Extensibility.UI;
using PgProj.VisualStudio.Engine;

namespace PgProj.VisualStudio.ImportDialog;

/// <summary>
/// Data context for <see cref="ImportDatabaseDialogControl"/>. Test Connection and Load Objects run
/// live from the dialog; the loaded units (relative path → SQL) stay on this instance so the command
/// can write the checked subset after OK.
/// </summary>
[DataContract]
internal sealed class ImportDatabaseDialogViewModel : NotifyPropertyChangedObject
{
    private string connectionString = string.Empty;
    private string status = "Enter a connection string, then Load Objects.";
    private bool overwriteExisting;
    private bool rememberConnection = true;
    private bool isBusy;

    public ImportDatabaseDialogViewModel()
    {
        this.TestConnectionCommand = new AsyncCommand((_, ct) => this.TestConnectionAsync(ct));
        this.LoadObjectsCommand = new AsyncCommand((_, ct) => this.LoadObjectsAsync(ct));
        this.IncludeAllCommand = new AsyncCommand((_, _) => this.SetAllIncludedAsync(true));
        this.ExcludeAllCommand = new AsyncCommand((_, _) => this.SetAllIncludedAsync(false));
    }

    /// <summary>The loaded extract units (relative path → SQL). Read by the command after OK.</summary>
    public IReadOnlyDictionary<string, string> LoadedUnits { get; private set; } =
        new Dictionary<string, string>();

    /// <summary>The Npgsql connection string of the database to import (never persisted).</summary>
    [DataMember]
    public string ConnectionString
    {
        get => this.connectionString;
        set => this.SetProperty(ref this.connectionString, value);
    }

    /// <summary>Progress/result line (connection test outcome, object counts, errors).</summary>
    [DataMember]
    public string Status
    {
        get => this.status;
        set => this.SetProperty(ref this.status, value);
    }

    /// <summary>When true, importing overwrites project files that already exist (default: skip them).</summary>
    [DataMember]
    public bool OverwriteExisting
    {
        get => this.overwriteExisting;
        set => this.SetProperty(ref this.overwriteExisting, value);
    }

    /// <summary>
    /// Remember the connection for this project after a successful import (DPAPI-encrypted in the
    /// per-user store — never in the .pgproj). Unchecking also forgets a stored one.
    /// </summary>
    [DataMember]
    public bool RememberConnection
    {
        get => this.rememberConnection;
        set => this.SetProperty(ref this.rememberConnection, value);
    }

    /// <summary>True while a connect/introspect runs (disables the buttons).</summary>
    [DataMember]
    public bool IsBusy
    {
        get => this.isBusy;
        set => this.SetProperty(ref this.isBusy, value);
    }

    [DataMember]
    public ObservableList<ImportObjectViewModel> Objects { get; } = new();

    [DataMember]
    public AsyncCommand TestConnectionCommand { get; }

    [DataMember]
    public AsyncCommand LoadObjectsCommand { get; }

    [DataMember]
    public AsyncCommand IncludeAllCommand { get; }

    [DataMember]
    public AsyncCommand ExcludeAllCommand { get; }

    private async Task TestConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = this.ConnectionString.Trim();
        if (connection.Length == 0)
        {
            this.Status = "Enter a connection string first.";
            return;
        }

        this.IsBusy = true;
        this.Status = "Connecting…";
        try
        {
            var version = await PgProjEngine.TestConnectionAsync(connection, cancellationToken);
            this.Status = $"Connected: {version}";
        }
        catch (Exception ex)
        {
            this.Status = $"Connection failed: {ex.Message}";
        }
        finally
        {
            this.IsBusy = false;
        }
    }

    private async Task LoadObjectsAsync(CancellationToken cancellationToken)
    {
        var connection = this.ConnectionString.Trim();
        if (connection.Length == 0)
        {
            this.Status = "Enter a connection string first.";
            return;
        }

        this.IsBusy = true;
        this.Status = "Reading the database…";
        try
        {
            var units = await PgProjEngine.ReadDatabaseFileUnitsAsync(connection, cancellationToken);
            this.LoadedUnits = units;

            this.Objects.Clear();
            // The unit path is "<KindFolder>/<schema>.<name>.sql" — group by folder, show the object name.
            foreach (var path in units.Keys.OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                var separator = path.IndexOf('/');
                this.Objects.Add(new ImportObjectViewModel
                {
                    RelativePath = path,
                    Kind = separator > 0 ? path[..separator] : "",
                    Name = Path.GetFileNameWithoutExtension(separator > 0 ? path[(separator + 1)..] : path),
                    IsIncluded = true,
                });
            }

            this.Status = $"{units.Count} object(s) found — uncheck what you don't want, then OK to import.";
        }
        catch (Exception ex)
        {
            this.LoadedUnits = new Dictionary<string, string>();
            this.Objects.Clear();
            this.Status = $"Load failed: {ex.Message}";
        }
        finally
        {
            this.IsBusy = false;
        }
    }

    private Task SetAllIncludedAsync(bool included)
    {
        foreach (var item in this.Objects)
            item.IsIncluded = included;
        return Task.CompletedTask;
    }
}

/// <summary>One importable object: its extract unit path, kind folder, and the include checkbox.</summary>
[DataContract]
internal sealed class ImportObjectViewModel : NotifyPropertyChangedObject
{
    private bool included;

    /// <summary>The extract-layout relative path the unit is written to (e.g. <c>Tables/app.customers.sql</c>).</summary>
    [DataMember]
    public string RelativePath { get; init; } = string.Empty;

    /// <summary>The kind folder (Tables, Views, Functions, Sequences, Schemas, Triggers, …).</summary>
    [DataMember]
    public string Kind { get; init; } = string.Empty;

    /// <summary>The object's display name (<c>schema.name</c>).</summary>
    [DataMember]
    public string Name { get; init; } = string.Empty;

    [DataMember]
    public bool IsIncluded
    {
        get => this.included;
        set => this.SetProperty(ref this.included, value);
    }
}
