namespace Eve.Skills.Tests;

/// <summary>The expert system rules (EveJS <c>expertSystemRuntime.js</c>, <c>expertSystemProjection.js</c>) and the catalogue they read.</summary>
public class ExpertSystemTests
{
    private const long Now = 1_700_000_000_000;
    private const long Day = ExpertSystemRules.Day;
    private const int Afterburner2 = 57190, Archaeology2 = 57191, ThreeDays = 57192, Hidden = 57193, Plain = 57194;
    private const int Afterburner = 3450, Archaeology = 13278, SmallHybridTurret = 3301;

    private static readonly ExpertSystemCatalog Catalog = new(3, 30,
    [
        new ExpertSystemType(Afterburner2, "Afterburner", 1, false, false, [new SkillGrant(Afterburner, 2)]),
        new ExpertSystemType(Archaeology2, "Archaeology", 7, false, false, [new SkillGrant(Afterburner, 4), new SkillGrant(Archaeology, 2)]),
        new ExpertSystemType(ThreeDays, "Turret", 30, false, false, [new SkillGrant(SmallHybridTurret, 3)]),
        new ExpertSystemType(Hidden, "Hidden", 30, true, false, []),
        new ExpertSystemType(Plain, "Plain", 30, false, false, []),
    ]);

    private static ExpertSystemEntry Installed(int typeId, long expiresAtMs) => new(1001, typeId, Now, expiresAtMs, null, "item", Now);

    [Fact]
    public void The_catalogue_embedded_in_the_server_has_the_reference_rows_and_limits()
    {
        var embedded = ExpertSystemCatalog.LoadEmbedded();
        Assert.Equal(51, embedded.Types.Count);
        Assert.Equal(3, embedded.MaxInstallations);
        Assert.Equal(30, embedded.TopUpWindowDays);
        var row = embedded.Get(57190)!;
        Assert.Equal("QA ES below starting skills Afterburner II", row.Name);
        Assert.Equal(1, row.DurationDays);
        Assert.Equal([new SkillGrant(3450, 2)], row.SkillsGranted);
    }

    [Fact]
    public void Systems_granting_one_skill_give_its_highest_level_and_a_lapsed_system_gives_nothing()
    {
        var grants = ExpertSystemRules.GrantsOf(Catalog,
        [
            Installed(Afterburner2, Now + Day),
            Installed(Archaeology2, Now + 7 * Day),
            Installed(ThreeDays, Now - Day),
        ], Now);
        Assert.Equal(2, grants.Count);
        Assert.Equal(4, grants[Afterburner]);
        Assert.Equal(2, grants[Archaeology]);
        Assert.False(grants.ContainsKey(SmallHybridTurret));
    }

    [Fact]
    public void A_granted_level_shows_above_the_trained_one_with_its_points_and_an_untrained_skill_has_none()
    {
        var trained = new[] { new CharacterSkill(SmallHybridTurret, 1, 250), new CharacterSkill(Afterburner, 5, 300_000) };
        var effective = ExpertSystemRules.Effective(trained, new Dictionary<int, int> { [SmallHybridTurret] = 3, [Afterburner] = 2, [Archaeology] = 2 });
        Assert.Equal(new CharacterSkill(SmallHybridTurret, 3, 250), effective.Single(skill => skill.TypeId == SmallHybridTurret));
        Assert.Equal(new CharacterSkill(Afterburner, 5, 300_000), effective.Single(skill => skill.TypeId == Afterburner));
        Assert.Equal(new CharacterSkill(Archaeology, 2, 0), effective.Single(skill => skill.TypeId == Archaeology));
    }

    [Fact]
    public void A_new_system_needs_a_free_installation_and_a_hidden_type_is_not_activated()
    {
        var full = new[] { Installed(Afterburner2, Now + Day), Installed(Archaeology2, Now + Day), Installed(ThreeDays, Now + Day) };
        Assert.Equal("You already have 3/3 Expert Systems installed.", ExpertSystemRules.RefusalOf(Catalog, Catalog.Get(Plain), full, Now));
        Assert.Equal("That Expert System is hidden or retired and cannot be activated normally.", ExpertSystemRules.RefusalOf(Catalog, Catalog.Get(Hidden), [], Now));
        Assert.Equal("Expert System type not found.", ExpertSystemRules.RefusalOf(Catalog, null, [], Now));
        Assert.Null(ExpertSystemRules.RefusalOf(Catalog, Catalog.Get(Plain), full[..2], Now));
    }

    [Fact]
    public void A_top_up_is_allowed_only_with_thirty_days_or_less_left()
    {
        Assert.Equal(
            "That Expert System cannot be extended until it has 30 days or less remaining.",
            ExpertSystemRules.RefusalOf(Catalog, Catalog.Get(ThreeDays), [Installed(ThreeDays, Now + 31 * Day)], Now));
        Assert.Null(ExpertSystemRules.RefusalOf(Catalog, Catalog.Get(ThreeDays), [Installed(ThreeDays, Now + 30 * Day)], Now));
    }

    [Fact]
    public void A_top_up_adds_the_duration_to_the_time_left_and_a_new_installation_lasts_its_duration()
    {
        var existing = Installed(ThreeDays, Now + 10 * Day) with { InstalledAtMs = Now - 5 * Day };
        var topped = ExpertSystemRules.InstalledEntry(Catalog.Get(ThreeDays)!, [existing], 1001, Now, 77);
        Assert.Equal(Now + 10 * Day + 30 * Day, topped.ExpiresAtMs);
        Assert.Equal(existing.InstalledAtMs, topped.InstalledAtMs);
        Assert.Equal(77, topped.SourceItemId);

        var fresh = ExpertSystemRules.InstalledEntry(Catalog.Get(Afterburner2)!, [], 1001, Now, 78);
        Assert.Equal(new ExpertSystemEntry(1001, Afterburner2, Now, Now + Day, 78, "item", Now), fresh);
    }
}

public class ExpertSystemStoreTests
{
    private const long Now = 1_700_000_000_000;

    [PgFact]
    public async Task An_installed_system_is_kept_and_lapses_only_when_its_time_has_run_out()
    {
        await using var schema = await PgSchema.CreateAsync();
        var store = new PgExpertSystemStore(schema.Source, schema.Name);
        var entry = new ExpertSystemEntry(1001, 57190, Now, Now + ExpertSystemRules.Day, 555, "item", Now);
        await using (var connection = await schema.Source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await store.UpsertAsync(connection, transaction, entry);
            await transaction.CommitAsync();
        }
        Assert.Equal([entry], await store.ForCharacterAsync(1001));
        Assert.Empty(await store.ExpiredAsync(Now + ExpertSystemRules.Day - 1, 10));
        Assert.Equal([entry], await store.ExpiredAsync(Now + ExpertSystemRules.Day, 10));

        await using var removing = await schema.Source.OpenConnectionAsync();
        await using var removeTransaction = await removing.BeginTransactionAsync();
        Assert.False(await store.RemoveAsync(removing, removeTransaction, 1001, 57190, Now));
        Assert.True(await store.RemoveAsync(removing, removeTransaction, 1001, 57190, Now + ExpertSystemRules.Day));
        await removeTransaction.CommitAsync();
        Assert.Empty(await store.ForCharacterAsync(1001));
    }
}
