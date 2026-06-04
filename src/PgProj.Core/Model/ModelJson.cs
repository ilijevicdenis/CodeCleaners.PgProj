using System.Text.Json;
using System.Text.Json.Serialization;

namespace PgProj.Core.Model;

/// <summary>
/// Serialises a <see cref="DatabaseModel"/> to JSON — the build artifact (the moral equivalent of
/// a .dacpac, just human-readable). It lets <c>compare</c>/<c>publish</c> consume a pre-built model
/// without re-parsing every .sql file.
/// </summary>
public static class ModelJson
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // The model exposes its collections as get-only `List<T>` properties (no setters). Tell
        // System.Text.Json to populate the already-constructed lists instead of skipping them — otherwise
        // a round-tripped model deserializes empty. Serialization is unaffected, so `model.json` output
        // stays byte-identical to the prior behaviour.
        PreferredObjectCreationHandling = JsonObjectCreationHandling.Populate,
    };

    public static string Serialize(DatabaseModel model) => JsonSerializer.Serialize(model, Options);

    public static DatabaseModel Deserialize(string json) =>
        JsonSerializer.Deserialize<DatabaseModel>(json, Options) ?? new DatabaseModel();
}
