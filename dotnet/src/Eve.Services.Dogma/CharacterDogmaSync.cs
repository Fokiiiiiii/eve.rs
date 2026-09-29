using System.Collections.Concurrent;
using Eve.Dogma;
using Eve.Marshal;
using Eve.Services.Ships;
using Eve.Services.Skills;
using Eve.Ships;
using Eve.Skills;

namespace Eve.Services.Dogma;

/// <summary>
/// What a client is told about its character's dogma when it changes: the character's attributes and its brain (which <c>GetAllInfo</c> sends
/// too), and, when skills changed, the attributes of the ship that changed with them. The brain carries a version that counts every telling.
/// </summary>
public sealed class CharacterDogmaSync(DogmaStatic statics, CharacterShips ships, Func<long> nowUnixMs) : ICharacterDogmaSync
{
    /// <summary>How many times each character's brain has been told to the client.</summary>
    private readonly ConcurrentDictionary<long, int> _brainVersions = new();

    /// <summary>The service's own way of putting dogma in the shapes the client reads.</summary>
    public DogmaInfo Info { get; } = new(statics);

    /// <summary>The next version of a character's brain.</summary>
    public int NextBrainVersion(long characterId) => _brainVersions.AddOrUpdate(characterId, 1, (_, version) => version + 1);

    /// <summary>The character's attributes as two batches of changes, then the brain: what the client is sent after it asks for the character.</summary>
    public void SendCharacter(ClientSession session, ShipSnapshot snapshot, long fileTime, int version)
    {
        foreach (var events in Info.CharacterAttributeEvents(snapshot, fileTime))
        {
            session.SendNotification("OnModuleAttributeChanges", "clientID", events);
        }
        session.SendNotification("OnServerBrainUpdated", "clientID", Info.BrainUpdate(snapshot, version));
    }

    public async Task SkillsChangedAsync(ClientSession session, long characterId, IReadOnlyList<CharacterSkill> before, CancellationToken cancellationToken = default)
    {
        if (await ships.LoadAsync(characterId, cancellationToken) is not { } snapshot)
        {
            return;
        }
        var fileTime = Py.FileTimeFromUnixMs(nowUnixMs());
        var changes = await ships.SkillChangesAsync(characterId, before, cancellationToken);
        if (changes.Count > 0)
        {
            session.SendNotification(
                "OnModuleAttributeChanges", "clientID",
                new PyList([.. changes.Select(c => ShipNotifications.AttributeChange(characterId, c.ItemId, c.AttributeId, fileTime, c.Old, c.New))]));
        }
        SendCharacter(session, snapshot, fileTime, NextBrainVersion(characterId));
    }
}
