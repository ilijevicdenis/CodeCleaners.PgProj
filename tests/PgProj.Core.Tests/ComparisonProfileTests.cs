using System.IO;
using PgProj.Core.Comparison;
using PgProj.Core.Deployment;
using Xunit;

namespace PgProj.Core.Tests;

/// <summary>
/// Phase 18 — ComparisonProfile (#58): a serializable, committable profile separate from PublishProfile
/// that round-trips to disk and projects onto live ComparerOptions deterministically.
/// </summary>
public class ComparisonProfileTests
{
    [Fact]
    public void Empty_profile_reproduces_behaviour_preserving_defaults()
    {
        var opts = new ComparisonProfile().ToComparerOptions();
        Assert.False(opts.DropObjectsNotInSource);
        Assert.False(opts.IgnoreColumnOrder);
        Assert.True(opts.IgnoreStorageParameters);   // default = ignore (today's behaviour)
        Assert.False(opts.CaseSensitiveIdentifiers);
    }

    [Fact]
    public void Profile_round_trips_through_json()
    {
        var profile = new ComparisonProfile
        {
            DropObjectsNotInSource = true,
            IgnoreColumnOrder = true,
            IgnoreStorageParameters = false,
            CaseSensitiveIdentifiers = true,
        };

        var parsed = ComparisonProfile.Parse(profile.ToJson());
        Assert.Equal(profile, parsed);
    }

    [Fact]
    public void Profile_round_trips_to_disk()
    {
        var profile = new ComparisonProfile { IgnoreColumnOrder = true, IgnoreStorageParameters = false };
        var path = Path.Combine(Path.GetTempPath(), "pgproj-test-" + System.Guid.NewGuid().ToString("N") + ComparisonProfile.Extension);
        try
        {
            profile.Save(path);
            Assert.True(File.Exists(path));
            var loaded = ComparisonProfile.Load(path);
            Assert.Equal(profile, loaded);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Serialization_is_deterministic()
    {
        var profile = new ComparisonProfile { CaseSensitiveIdentifiers = true };
        Assert.Equal(profile.ToJson(), profile.ToJson());
    }

    [Fact]
    public void From_and_to_comparer_options_are_inverses()
    {
        var opts = new ComparerOptions
        {
            DropObjectsNotInSource = true,
            IgnoreColumnOrder = true,
            IgnoreStorageParameters = false,
            CaseSensitiveIdentifiers = true,
        };
        var back = ComparisonProfile.FromComparerOptions(opts).ToComparerOptions();
        Assert.Equal(opts.DropObjectsNotInSource, back.DropObjectsNotInSource);
        Assert.Equal(opts.IgnoreColumnOrder, back.IgnoreColumnOrder);
        Assert.Equal(opts.IgnoreStorageParameters, back.IgnoreStorageParameters);
        Assert.Equal(opts.CaseSensitiveIdentifiers, back.CaseSensitiveIdentifiers);
    }

    [Fact]
    public void Malformed_json_throws()
    {
        Assert.Throws<ComparisonProfileException>(() => ComparisonProfile.Parse("{ not json"));
    }

    [Fact]
    public void Blank_json_is_a_defaults_profile()
    {
        Assert.Equal(new ComparisonProfile(), ComparisonProfile.Parse("   "));
    }

    [Fact]
    public void Is_profile_path_recognizes_the_extension()
    {
        Assert.True(ComparisonProfile.IsProfilePath("team.pgcompare.json"));
        Assert.False(ComparisonProfile.IsProfilePath("team.pgpublish.json"));
    }

    [Fact]
    public void Profile_is_distinct_type_from_publish_profile()
    {
        // The two profile families have different extensions and are not interchangeable.
        Assert.NotEqual(ComparisonProfile.Extension, PublishProfile.Extension);
    }
}
