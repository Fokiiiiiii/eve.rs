using System.Text.Json;
using System.Text.Json.Nodes;
using static Eve.Host.HostConfiguration;

namespace Eve.Host;

internal sealed partial class CharacterServicesComposition
{
    private readonly object warTypeGate = new();
    private Task<int[]>? warEligibleTypes;

    private Task<int[]> WarEligibleTypesAsync()
    {
        lock (warTypeGate)
            return warEligibleTypes ??= LoadWarEligibleTypesAsync();
    }

    private async Task<int[]> LoadWarEligibleTypesAsync()
    {
        var groups = await sde.ReadAsync("groups");
        var eligibleGroups = groups.Where(row =>
                row.Key == 365 || row.Doc.TryGetProperty("categoryID", out var category) &&
                category.ValueKind == JsonValueKind.Number && category.GetInt32() == 65)
            .Select(row => row.Key).ToHashSet();
        var types = await sde.ReadAsync("types");
        return types.Where(row => row.Doc.TryGetProperty("groupID", out var group) &&
                group.ValueKind == JsonValueKind.Number && eligibleGroups.Contains(group.GetInt64()) &&
                row.Key > 0 && row.Key <= int.MaxValue)
            .Select(row => (int)row.Key).ToArray();
    }

    private async ValueTask<bool> ReadWarEligibilityAsync(long ownerId, CancellationToken token)
    {
        var eligibility = await ReadWarEligibilityBatchAsync([ownerId], token);
        return eligibility.GetValueOrDefault(ownerId);
    }

    private async ValueTask<IReadOnlyDictionary<long, bool>> ReadWarEligibilityBatchAsync(
        IReadOnlyList<long> ownerIds, CancellationToken token)
    {
        var requested = ownerIds.Where(id => id > 0).ToHashSet();
        var result = ownerIds.Distinct().ToDictionary(id => id, _ => false);
        if (requested.Count == 0) return result;
        var corporationsById = new Dictionary<long,Eve.Corporations.Corporation>();
        var alliances = new HashSet<long>();
        if (requested.Count > 32)
        {
            foreach (var corporation in await corporations.AllAsync(token))
                if (!corporation.Deleted && !corporation.IsNpc) corporationsById[corporation.Id] = corporation;
            foreach (var alliance in await corporations.AllAlliancesAsync(token))
                if (!alliance.Deleted) alliances.Add(alliance.Id);
        }
        else
        {
            foreach (var id in requested)
            {
                var corporation = await corporations.GetAsync(id, token);
                if (corporation is { Deleted: false, IsNpc: false })
                {
                    corporationsById[id] = corporation;
                    if (corporation.AllianceId is { } allianceId &&
                        await corporations.AllianceAsync(allianceId, token) is { Deleted: false })
                        alliances.Add(allianceId);
                }
                else if (await corporations.AllianceAsync(id, token) is { Deleted: false })
                    alliances.Add(id);
            }
            foreach (var alliance in alliances)
                foreach (var member in await corporations.AllianceMembersAsync(alliance, token))
                    if (!corporationsById.ContainsKey(member.CorporationId) &&
                        await corporations.GetAsync(member.CorporationId, token) is { Deleted: false, IsNpc: false } memberCorporation)
                        corporationsById[memberCorporation.Id] = memberCorporation;
        }
        var relevantAlliances = new HashSet<long>(requested.Where(alliances.Contains));
        foreach (var id in requested)
            if (corporationsById.TryGetValue(id, out var corporation) && corporation.AllianceId is { } alliance && alliances.Contains(alliance))
                relevantAlliances.Add(alliance);
        var ownerCorporations = corporationsById.Values.Where(c => requested.Contains(c.Id) ||
            c.AllianceId is { } alliance && relevantAlliances.Contains(alliance)).Select(c => c.Id).ToArray();
        if (ownerCorporations.Length == 0) return result;
        var typeIds = await WarEligibleTypesAsync().WaitAsync(token);
        await using var connection = await source.OpenConnectionAsync(token);
        var eligible = new HashSet<long>(await structureStore.EligibleStructureOwnersAsync(connection, null, ownerCorporations, typeIds, token));
        eligible.UnionWith(await new Eve.Ships.PgDeployedStore(source, spaceSchema)
            .AnchoredControlTowerOwnersAsync(connection, null, ownerCorporations, now(), token));
        eligible.UnionWith(await new Eve.Planets.PlanetStore(source, Env("EVE_PLANETS_SCHEMA", Eve.Planets.PlanetStore.DefaultSchema))
            .CustomsOfficeOwnersAsync(connection, null, inventorySchema, ownerCorporations, token));

        // Sovereignty is write-behind; use the live cache instead of the persisted read model.
        var cache = mailCache.Read("sovereignty");
        if (!cache.Success || cache.Data is not JsonObject sovereignty)
            throw new InvalidOperationException("Sovereignty cache is not loaded or is invalid.");
        var (sovCorporations, eligibleAlliances) = WarSovereigntyOwners(sovereignty);
        eligible.UnionWith(sovCorporations);
        foreach (var corporation in corporationsById.Values)
            if (eligible.Contains(corporation.Id) && corporation.AllianceId is { } alliance && alliances.Contains(alliance))
                eligibleAlliances.Add(alliance);
        foreach (var id in requested)
        {
            if (alliances.Contains(id))
                result[id] = eligibleAlliances.Contains(id);
            else if (corporationsById.TryGetValue(id, out var corporation))
                result[id] = corporation.AllianceId is { } alliance && alliances.Contains(alliance)
                    ? eligibleAlliances.Contains(alliance) : eligible.Contains(id);
        }
        return result;
    }

    internal static void SubscribeWarEligibilityChanges(
        Eve.Events.EventBus events, Eve.Structures.StructureStore structureStore,
        Func<long, long, CancellationToken, Task<Action>> prepare)
    {
        events.Subscribe("war.eligibility.structure", [Eve.Structures.StructureTimers.StateChangedTopic],
            async (context, token) =>
            {
                var change = context.Event.Payload<Eve.Structures.StructureStateChanged>()
                    ?? throw new InvalidOperationException("Structure state event has no payload.");
                var owner = (await structureStore.GetAsync(context.Connection, context.Transaction,
                    change.StructureId, false, token))?.OwnerCorporationId ?? 0;
                if (owner > 0)
                {
                    var send = await prepare(owner, 0, token);
                    context.AfterCommit(_ => { send(); return Task.CompletedTask; });
                }
            });
        events.Subscribe("war.eligibility.tower", [Eve.Services.Space.SpaceContainers.ControlTowerEligibilityChangedTopic],
            async (context, token) =>
            {
                var change = context.Event.Payload<Eve.Services.Space.SpaceContainers.ControlTowerEligibilityChanged>()
                    ?? throw new InvalidOperationException("Tower eligibility event has no payload.");
                if (change.CorporationId <= 0) return;
                var send = await prepare(change.CorporationId, 0, token);
                context.AfterCommit(_ => { send(); return Task.CompletedTask; });
            });
        events.Subscribe("war.eligibility.customs", [Eve.Inventory.PgItemStore.ItemOwnershipChangedTopic],
            async (context, token) =>
            {
                var change = context.Event.Payload<Eve.Inventory.PgItemStore.ItemOwnershipChanged>()
                    ?? throw new InvalidOperationException("Item ownership event has no payload.");
                if (change.TypeId != Eve.Planets.Customs.OfficeTypeId) return;
                var sends = new List<Action>();
                if (change.PreviousOwnerId > 0)
                    sends.Add(await prepare(change.PreviousOwnerId, 0, token));
                if (change.OwnerId > 0 && change.OwnerId != change.PreviousOwnerId)
                    sends.Add(await prepare(change.OwnerId, 0, token));
                context.AfterCommit(_ =>
                {
                    foreach (var send in sends) send();
                    return Task.CompletedTask;
                });
            });
        events.Subscribe("war.eligibility.sovereignty", [WarSovereigntyEvents.Topic],
            async (context, token) =>
            {
                var change = context.Event.Payload<WarSovereigntyEvents.OwnersChanged>()
                    ?? throw new InvalidOperationException("Sovereignty ownership event has no payload.");
                var sends = new List<Action>();
                foreach (var corporation in change.Corporations.Distinct())
                    sends.Add(await prepare(corporation, 0, token));
                foreach (var alliance in change.Alliances.Distinct())
                    sends.Add(await prepare(0, alliance, token));
                context.AfterCommit(_ =>
                {
                    foreach (var send in sends) send();
                    return Task.CompletedTask;
                });
            });
        events.Subscribe("war.eligibility.alliance",
            ["relation.affiliation.alliance.changed", "relation.affiliation.alliance.ended"],
            async (context, token) =>
            {
                var change = context.Event.Payload<Eve.Relations.RelationChange>()
                    ?? throw new InvalidOperationException("Alliance affiliation event has no payload.");
                var send = await prepare(change.Subject, change.Object, token);
                context.AfterCommit(_ => { send(); return Task.CompletedTask; });
            });
    }

    private async Task<Action> PrepareWarEligibilityNotificationAsync(long corporationId, long affectedAllianceId, CancellationToken token)
    {
        var recipients = await AffectedWarCorporationsAsync(corporations, corporationId, affectedAllianceId, token);
        if (recipients.Count == 0) return static () => { };
        var eligibility = await ReadWarEligibilityBatchAsync(recipients.ToArray(), token);
        return PrepareWarEligibilitySends(characterSessions, recipients, eligibility);
    }

    internal static async Task<HashSet<long>> AffectedWarCorporationsAsync(Eve.Corporations.CorporationStore store,
        long corporationId, long affectedAllianceId, CancellationToken token)
    {
        var recipients = new HashSet<long>();
        if (corporationId > 0) recipients.Add(corporationId);
        var corporation = corporationId > 0 ? await store.GetAsync(corporationId, token) : null;
        var alliances = new HashSet<long>();
        if (affectedAllianceId > 0) alliances.Add(affectedAllianceId);
        if (corporation?.AllianceId is { } currentAllianceId) alliances.Add(currentAllianceId);
        foreach (var alliance in alliances)
            foreach (var member in await store.AllianceMembersAsync(alliance, token))
                recipients.Add(member.CorporationId);
        return recipients;
    }

    internal static Action PrepareWarEligibilitySends(Eve.Services.CharacterSessions sessions,
        IEnumerable<long> recipients, IReadOnlyDictionary<long, bool> eligibility)
    {
        var notices = recipients.Distinct().SelectMany(recipient => sessions.OfCorporation(recipient)
            .Select(session => (Session: session, Corporation: recipient, Allowed: eligibility.GetValueOrDefault(recipient))))
            .ToArray();
        return () =>
        {
            foreach (var notice in notices)
                notice.Session.SendNotification("OnAllowWarUpdated", "clientID", new Eve.Marshal.PyLong(notice.Corporation),
                    notice.Allowed ? Eve.Marshal.PyBool.True : Eve.Marshal.PyBool.False);
        };
    }

    internal static (HashSet<long> Corporations, HashSet<long> Alliances) WarSovereigntyOwners(JsonObject root)
    {
        var eligible = new HashSet<long>();
        var eligibleAlliances = new HashSet<long>();
        if (root["systems"] is JsonObject systems)
            foreach (var system in systems.Select(entry => entry.Value).OfType<JsonObject>())
            {
                if (WarOwnerId(system["claimStructureID"]) <= 0 && WarOwnerId(system["infrastructureHubID"]) <= 0) continue;
                var corporation = WarOwnerId(system["corporationID"]);
                var alliance = WarOwnerId(system["allianceID"]);
                if (corporation > 0) eligible.Add(corporation);
                if (alliance > 0) eligibleAlliances.Add(alliance);
            }
        if (root["skyhooks"] is JsonObject skyhooks)
            foreach (var (key, node) in skyhooks)
            {
                if (node is not JsonObject skyhook) continue;
                var id = WarOwnerId(skyhook["skyhookID"] ?? skyhook["itemID"]);
                if (id <= 0) long.TryParse(key, out id);
                var owner = WarOwnerId(skyhook["corporationID"] ?? skyhook["ownerCorpID"]);
                if (id > 0 && WarOwnerId(skyhook["typeID"]) == 81080 &&
                    WarOwnerId(skyhook["solarSystemID"]) > 0 && WarOwnerId(skyhook["planetID"]) > 0 &&
                    owner > 0 && !WarFlag(skyhook["destroyed"]))
                    eligible.Add(owner);
            }
        return (eligible, eligibleAlliances);
    }

    private static bool WarFlag(JsonNode? value) =>
        bool.TryParse(value?.ToString(), out var flag) ? flag : WarOwnerId(value) != 0;

    private static long WarOwnerId(JsonNode? value)
    {
        if (value is not JsonValue item) return 0;
        if (item.TryGetValue<long>(out var number)) return number;
        return item.TryGetValue<string>(out var text) && long.TryParse(text, out number) ? number : 0;
    }
}
