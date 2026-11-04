using Eve.Marshal;
using Eve.Services.Slash;
using Eve.Skills;

namespace Eve.Services.Dogma.Tests;

public class SkillAdminCommandTests
{
    private static ClientSession Gm(ClientSession session)
    {
        session.Set("accountRole", new PyLong(GameMasterRoles.Gml));
        return session;
    }

    private static CharacterSkill Skill(Rig rig, int typeId, int level)
    {
        var type = rig.Catalog.Get(typeId)!;
        return new CharacterSkill(typeId, level, SkillPoints.At(type.Rank, level));
    }

    private static async Task GrantAsync(Rig rig, long characterId, params CharacterSkill[] rows)
    {
        await using var connection = await rig.Source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await rig.Skills.GrantAsync(connection, transaction, characterId, rows);
        await transaction.CommitAsync();
    }

    private static async Task<int> AccountOfAsync(Rig rig, long characterId) =>
        (await rig.Characters.GetAsync(characterId))?.AccountId ?? throw new InvalidOperationException($"Character {characterId} is missing.");

    private static async Task SaveQueueAsync(Rig rig, long characterId, int accountId, params QueueEntry[] entries)
    {
        Assert.IsType<QueueSaved>(await rig.Training.SaveQueueAsync(
            characterId, accountId, entries, activate: true, [characterId]));
    }

    [PgFact]
    public async Task Give_and_remove_resolve_target_id_and_exact_name_and_notify_only_the_online_target()
    {
        await using var rig = await Rig.CreateAsync();
        var caller = await rig.NewCharacterAsync("Skill Caller");
        var target = await rig.NewCharacterAsync("Skill Target");
        var targetAccount = await AccountOfAsync(rig, target.CharacterId);
        var callerSession = Gm(Rig.Session(caller.CharacterId));
        var targetSession = Rig.Session(target.CharacterId, id: 2);
        rig.SetOnline(targetSession);

        await GrantAsync(rig, caller.CharacterId, Skill(rig, 3300, 4));
        await GrantAsync(rig, target.CharacterId, Skill(rig, 3300, 1), Skill(rig, 3301, 0));

        Assert.StartsWith($"Set Gunnery(3300) for character({target.CharacterId})",
            await rig.SlashAsync(callerSession, $"/giveskill {target.CharacterId} Gunnery 3"));
        Assert.Equal(4, Assert.Single(await rig.Skills.ForCharacterAsync(caller.CharacterId), skill => skill.TypeId == 3300).Level);
        Assert.Equal(3, Assert.Single(await rig.Skills.ForCharacterAsync(target.CharacterId), skill => skill.TypeId == 3300).Level);
        Assert.Empty(Events.Of(callerSession));
        Assert.Equal(
            ["OnServerSkillsChanged", "OnSkillLevelsTrained", "OnSkillsChanged", "OnNewSkillQueueSaved", "OnSkillQueuePausedServer"],
            Events.Of(targetSession).Select(notice => notice.Name));

        Assert.StartsWith($"Set Small Hybrid Turret(3301) for character({target.CharacterId})",
            await rig.SlashAsync(callerSession, "/giveskill \"Skill Target\" Small Hybrid Turret 2"));
        Assert.Equal(2, Assert.Single(await rig.Skills.ForCharacterAsync(target.CharacterId), skill => skill.TypeId == 3301).Level);
        Assert.Empty(Events.Of(callerSession));
        Assert.Contains("OnNewSkillQueueSaved", Events.Of(targetSession).Select(notice => notice.Name));

        Assert.Equal($"Removed Gunnery(3300) from character({target.CharacterId}).",
            await rig.SlashAsync(callerSession, "/removeskill \"Skill Target\" Gunnery"));
        Assert.DoesNotContain(await rig.Skills.ForCharacterAsync(target.CharacterId), skill => skill.TypeId == 3300);
        var removed = Events.Of(targetSession);
        Assert.Equal("OnServerSkillsRemoved", removed[0].Name);
        Assert.Equal("clientID", removed[0].IdType);
        Assert.Contains("OnSkillsChanged", removed.Select(notice => notice.Name));

        rig.SetOffline(target.CharacterId);
        Assert.StartsWith($"Set Gunnery(3300) for character({target.CharacterId})",
            await rig.SlashAsync(callerSession, $"/giveskill {target.CharacterId} Gunnery 5"));
        Assert.Empty(Events.Of(callerSession));
        var offlineView = await rig.Training.ViewAsync(target.CharacterId, targetAccount);
        Assert.Equal(5, Assert.Single(offlineView.Skills, skill => skill.TypeId == 3300).Level);
    }

    [PgFact]
    public async Task Queue_is_persistently_pruned_in_order_then_cleared_on_lowering_and_removal()
    {
        await using var rig = await Rig.CreateAsync();
        var caller = await rig.NewCharacterAsync("Queue Admin");
        var target = await rig.NewCharacterAsync("Queue Target");
        var targetAccount = await AccountOfAsync(rig, target.CharacterId);
        var callerSession = Gm(Rig.Session(caller.CharacterId));
        var targetSession = Rig.Session(target.CharacterId, id: 2);
        rig.SetOnline(targetSession);
        await GrantAsync(rig, target.CharacterId, Skill(rig, 3300, 1), Skill(rig, 3301, 0));
        await SaveQueueAsync(rig, target.CharacterId, targetAccount,
            new QueueEntry(3300, 2), new QueueEntry(3300, 3), new QueueEntry(3301, 1));

        await rig.SlashAsync(callerSession, $"/giveskill {target.CharacterId} Gunnery 2");
        var afterRaise = await rig.Training.ViewAsync(target.CharacterId, targetAccount);
        Assert.True(afterRaise.State.Running);
        Assert.Equal([new QueueEntry(3300, 3), new QueueEntry(3301, 1)], afterRaise.State.Entries);
        Assert.Contains("OnNewSkillQueueSaved", Events.Of(targetSession).Select(notice => notice.Name));

        await rig.SlashAsync(callerSession, $"/giveskill {target.CharacterId} Gunnery 0");
        var afterLower = await rig.Training.ViewAsync(target.CharacterId, targetAccount);
        Assert.Empty(afterLower.State.Entries);
        Assert.False(afterLower.State.Running);
        Assert.Equal(
            ["OnServerSkillsChanged", "OnSkillsChanged", "OnNewSkillQueueSaved", "OnSkillQueuePausedServer"],
            Events.Of(targetSession).Select(notice => notice.Name));

        await GrantAsync(rig, target.CharacterId, Skill(rig, 3300, 1), Skill(rig, 3301, 0));
        await SaveQueueAsync(rig, target.CharacterId, targetAccount, new QueueEntry(3300, 2), new QueueEntry(3301, 1));
        await rig.SlashAsync(callerSession, $"/removeskill {target.CharacterId} Gunnery");
        var afterRemove = await rig.Training.ViewAsync(target.CharacterId, targetAccount);
        Assert.Empty(afterRemove.State.Entries);
        Assert.False(afterRemove.State.Running);
        Assert.Equal(
            ["OnServerSkillsRemoved", "OnSkillsChanged", "OnNewSkillQueueSaved", "OnSkillQueuePausedServer"],
            Events.Of(targetSession).Select(notice => notice.Name));
    }

    [PgFact]
    public async Task Starter_and_randomize_skill_changes_clear_queues_after_lowering_skills()
    {
        await using (var rig = await Rig.CreateAsync())
        {
            var caller = await rig.NewCharacterAsync("Starter Admin");
            var target = await rig.NewCharacterAsync("Starter Target");
            var targetAccount = await AccountOfAsync(rig, target.CharacterId);
            var session = Gm(Rig.Session(caller.CharacterId));
            var starter = rig.Catalog.StartingSet(1)!;
            var lowered = Assert.Single(starter.Where(skill => skill.Level < SkillPoints.MaxLevel).Take(1));
            var starterIds = starter.Select(skill => skill.TypeId).ToHashSet();
            var queueType = rig.Catalog.Skills.First(skill =>
                skill.Published && skill.TypeId != lowered.TypeId && skill.Needs.Count == 0 && !starterIds.Contains(skill.TypeId));
            await GrantAsync(rig, target.CharacterId,
                Skill(rig, lowered.TypeId, SkillPoints.MaxLevel), new CharacterSkill(queueType.TypeId, 0, 0));
            await SaveQueueAsync(rig, target.CharacterId, targetAccount, new QueueEntry(queueType.TypeId, 1));

            await rig.SlashAsync(session, $"/givestarterskills {target.CharacterId}");
            var view = await rig.Training.ViewAsync(target.CharacterId, targetAccount);
            Assert.Empty(view.State.Entries);
            Assert.False(view.State.Running);
        }

        await using (var rig = await Rig.CreateAsync(skillRandom: new ZeroRandom()))
        {
            var target = await rig.NewCharacterAsync("Random Target");
            var targetAccount = await AccountOfAsync(rig, target.CharacterId);
            var session = Gm(Rig.Session(target.CharacterId));
            var published = rig.Catalog.Skills.Where(skill => skill.Published)
                .Select(skill => new CharacterSkill(skill.TypeId, SkillPoints.MaxLevel, SkillPoints.At(skill.Rank, SkillPoints.MaxLevel)))
                .ToList();
            await GrantAsync(rig, target.CharacterId, [.. published]);
            await GrantAsync(rig, target.CharacterId, Skill(rig, 3300, 1));
            await SaveQueueAsync(rig, target.CharacterId, targetAccount, new QueueEntry(3300, 2));

            await rig.SlashAsync(session, "/randomizeskills");
            var view = await rig.Training.ViewAsync(target.CharacterId, targetAccount);
            Assert.Empty(view.State.Entries);
            Assert.False(view.State.Running);
            Assert.Equal(0, Assert.Single(view.Skills, skill => skill.TypeId == 3300).Level);
        }
    }

    private sealed class ZeroRandom : Random
    {
        public override int Next(int minValue, int maxValue) => minValue;
    }
}
