using Eve.Data;
using Npgsql;

namespace Eve.Fitting.Tests;

/// <summary>The saved fittings: the rules of a draft (no database), and the store in PostgreSQL (a fresh schema per test, migrations applied).</summary>
public class SavedFittingStoreTests : IAsyncLifetime
{
    private readonly List<NpgsqlDataSource> _sources = [];
    private readonly List<string> _schemas = [];

    private const long Character = 140_200_001, OtherCharacter = 140_200_002, Corporation = 98_200_001;
    private const int Tengu = 29984, Ibis = 601, Afterburner = 21857, DamageControl = 2046;
    private const long Now = 133_000_000_000_000_000L;

    private static readonly FittingDraft Draft = new(Ibis, "Ibis PvE", "dps", [new FittingItem(Afterburner, 15, 1), new FittingItem(DamageControl, 11, 1)]);

    private static int? Categories(int typeId) => typeId switch
    {
        Tengu or Ibis => 6,
        Afterburner or DamageControl => 7,
        _ => null,
    };

    [Fact]
    public void A_draft_with_a_valid_ship_and_slot_items_is_accepted()
    {
        Assert.Null(SavedFittingRules.Check(Draft, Categories));
    }

    [Theory]
    [InlineData("FITTING_NAME_REQUIRED", 0)]
    [InlineData("FITTING_INVALID_TEXT", 0)]
    [InlineData("FITTING_INVALID_TYPE_ID", 999999)]
    [InlineData("FITTING_DATA_EMPTY", 0)]
    [InlineData("FITTING_INVALID_FLAG", 2046)]
    [InlineData("FITTING_INVALID_QUANTITY", 2046)]
    public void Each_invalid_draft_is_named_by_the_reference_error_code(string code, long value)
    {
        var draft = code switch
        {
            "FITTING_NAME_REQUIRED" => Draft with { Name = "  " },
            "FITTING_INVALID_TEXT" => Draft with { Description = "a@@b" },
            "FITTING_INVALID_TYPE_ID" => Draft with { FitData = [new FittingItem(999999, 15, 1)] },
            "FITTING_DATA_EMPTY" => Draft with { FitData = [] },
            "FITTING_INVALID_FLAG" => Draft with { FitData = [new FittingItem(DamageControl, 2, 1)] },
            _ => Draft with { FitData = [new FittingItem(DamageControl, 11, 0)] },
        };
        var fault = SavedFittingRules.Check(draft, Categories);
        Assert.Equal(code, fault?.Code);
        Assert.Equal(value, fault?.Value ?? 0);
    }

    [Fact]
    public void A_negative_fitting_quantity_is_refused()
    {
        var draft = Draft with { FitData = [new FittingItem(DamageControl, 11, -1)] };

        var fault = SavedFittingRules.Check(draft, Categories);

        Assert.Equal("FITTING_INVALID_QUANTITY", fault?.Code);
        Assert.Equal(DamageControl, fault?.Value);
    }

    [Fact]
    public void A_fitting_with_more_than_500_items_is_refused_before_category_lookups()
    {
        var draft = Draft with { FitData = Enumerable.Repeat(new FittingItem(DamageControl, 11, 1), 501).ToArray() };
        var lookups = 0;

        var fault = SavedFittingRules.Check(draft, id =>
        {
            lookups++;
            return Categories(id);
        });

        Assert.Equal("FITTING_TOO_MANY_ITEMS", fault?.Code);
        Assert.Equal(501, fault?.Value);
        Assert.Equal(1, lookups);
    }

    [Fact]
    public void A_station_or_a_non_ship_is_refused_as_the_ship_type()
    {
        Assert.Equal("FITTING_INVALID_SHIP_TYPE", SavedFittingRules.Check(Draft with { ShipTypeId = Afterburner }, Categories)?.Code);
    }

    [Fact]
    public void The_cargo_drone_and_fighter_bays_are_allowed_besides_the_slots()
    {
        Assert.Null(SavedFittingRules.Check(Draft with { FitData = [new FittingItem(DamageControl, 5, 2), new FittingItem(DamageControl, 87, 3), new FittingItem(DamageControl, 158, 1)] }, Categories));
    }

    [Fact]
    public void Names_and_descriptions_are_trimmed_and_cut_as_the_client_cuts_them()
    {
        var (name, description) = SavedFittingRules.Normalize("  " + new string('n', 60) + "  ", new string('d', 600));
        Assert.Equal(50, name.Length);
        Assert.Equal(500, description.Length);
    }

    [PgFact]
    public async Task A_fitting_is_saved_listed_changed_renamed_and_deleted()
    {
        var (source, schema) = await FreshAsync();
        var store = new PgSavedFittingStore(source, schema);

        var ids = await store.SaveManyAsync(FittingOwnerScope.Character, Character, [Draft], 500, Now);
        var id = Assert.Single(ids!);
        var listed = Assert.Single(await store.ListAsync(FittingOwnerScope.Character, Character));
        Assert.Equal(id, listed.FittingId);
        AssertSameDraft(Draft, listed.Draft);
        Assert.Equal(Now, listed.SavedAtFileTime);

        var changed = Draft with { FitData = [new FittingItem(Afterburner, 15, 2)] };
        Assert.True(await store.UpdateAsync(FittingOwnerScope.Character, Character, id, changed, Now + 1));
        AssertSameDraft(changed, Assert.Single(await store.ListAsync(FittingOwnerScope.Character, Character)).Draft);

        Assert.True(await store.RenameAsync(FittingOwnerScope.Character, Character, id, "Renamed", "text", Now + 2));
        var renamed = Assert.Single(await store.ListAsync(FittingOwnerScope.Character, Character));
        Assert.Equal(("Renamed", "text"), (renamed.Draft.Name, renamed.Draft.Description));

        Assert.Equal([id], await store.DeleteAsync(FittingOwnerScope.Character, Character, [id]));
        Assert.Empty(await store.ListAsync(FittingOwnerScope.Character, Character));
    }

    [PgFact]
    public async Task A_change_to_a_fitting_the_owner_does_not_have_changes_nothing()
    {
        var (source, schema) = await FreshAsync();
        var store = new PgSavedFittingStore(source, schema);
        var id = (await store.SaveManyAsync(FittingOwnerScope.Character, Character, [Draft], 500, Now))![0];

        Assert.False(await store.UpdateAsync(FittingOwnerScope.Character, OtherCharacter, id, Draft, Now));
        Assert.False(await store.RenameAsync(FittingOwnerScope.Corporation, Character, id, "x", "", Now));
        Assert.Empty(await store.DeleteAsync(FittingOwnerScope.Character, OtherCharacter, [id]));
        Assert.Single(await store.ListAsync(FittingOwnerScope.Character, Character));
        Assert.Empty(await store.ListAsync(FittingOwnerScope.Character, OtherCharacter));
        Assert.Empty(await store.ListAsync(FittingOwnerScope.Corporation, Character));
    }

    [PgFact]
    public async Task An_owner_goes_over_its_limit_and_nothing_of_that_batch_is_kept()
    {
        var (source, schema) = await FreshAsync();
        var store = new PgSavedFittingStore(source, schema);
        Assert.NotNull(await store.SaveManyAsync(FittingOwnerScope.Corporation, Corporation, [Draft, Draft], 3, Now));

        Assert.Null(await store.SaveManyAsync(FittingOwnerScope.Corporation, Corporation, [Draft, Draft], 3, Now));
        Assert.Equal(2, (await store.ListAsync(FittingOwnerScope.Corporation, Corporation)).Count);
    }

    [PgFact]
    public async Task The_fittings_survive_a_new_store_on_the_same_schema()
    {
        var (source, schema) = await FreshAsync();
        var first = new PgSavedFittingStore(source, schema);
        var id = (await first.SaveManyAsync(FittingOwnerScope.Character, Character, [Draft], 500, Now))![0];

        var restarted = new PgSavedFittingStore(source, schema);
        var listed = Assert.Single(await restarted.ListAsync(FittingOwnerScope.Character, Character));
        Assert.Equal(id, listed.FittingId);
        AssertSameDraft(Draft, listed.Draft);
        Assert.Equal((FittingOwnerScope.Character, Character), await restarted.OwnerOfAsync(id));
    }

    /// <summary>A draft compared field by field: its item list is an <see cref="IReadOnlyList{T}"/>, which record equality does not compare by value.</summary>
    private static void AssertSameDraft(FittingDraft expected, FittingDraft actual)
    {
        Assert.Equal(expected.ShipTypeId, actual.ShipTypeId);
        Assert.Equal(expected.Name, actual.Name);
        Assert.Equal(expected.Description, actual.Description);
        Assert.Equal(expected.FitData, actual.FitData);
    }

    private async Task<(NpgsqlDataSource Source, string Schema)> FreshAsync()
    {
        var source = NpgsqlDataSource.Create(PgConnection.ForTest(PgFactAttribute.Url!));
        _sources.Add(source);
        var schema = "sfit_" + Guid.NewGuid().ToString("N")[..12];
        _schemas.Add(schema);
        await PgModuleStateStore.MigrateAsync(source, schema);
        return (source, schema);
    }

    /// <summary>Drops this test's scratch schemas (a failed drop does not fail the test), then closes its sources.</summary>
    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var schema in _schemas)
        {
            try
            {
                await using var drop = _sources[0].CreateCommand($"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE");
                await drop.ExecuteNonQueryAsync();
            }
            catch (NpgsqlException)
            {
                // Leaving a scratch schema behind is better than failing the test.
            }
        }
        foreach (var source in _sources)
        {
            await source.DisposeAsync();
        }
    }
}
