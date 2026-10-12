using Eve.Marshal;
using Eve.Services;
using Npgsql;
using Ins = Eve.Insurgency;
using static Eve.Host.HostConfiguration;

namespace Eve.Host;

internal sealed partial class CharacterServicesComposition
{
    private Ins.InsurgencyStore insurgencyStore = default!;
    private Ins.InsurgencyCampaigns insurgencyCampaigns = default!;
    /// <summary>The stargate graph of the static data (set by the space composition; the incursion layout and the insurgency hubs use its jumps).</summary>
    private Eve.Universe.StargateGraph? stargateGraph;

    /// <summary>
    /// The pirate insurgency campaigns (C-M1, C-S1): the store and the deadline jobs of the state machine, the campaign snapshot the client asks for, and the
    /// notices of each change to the pilots it concerns (V24.01 insurgencyCampaignSvc.py:59-143). No client or operator path starts a campaign yet (Q-INS-4),
    /// so a server without one answers the empty list.
    /// </summary>
    private async Task Insurgency()
    {
        var schema = Env("EVE_INSURGENCY_SCHEMA", Ins.InsurgencyStore.DefaultSchema);
        await Ins.InsurgencyStore.MigrateAsync(source, schema);
        insurgencyStore = new Ins.InsurgencyStore(source, schema);
        insurgencyCampaigns = new Ins.InsurgencyCampaigns(insurgencyStore, foundations.Scheduler, foundations.Events, loyaltyPoints, id => solarSystems.Get((int)id)?.FactionId ?? 0);
        insurgencyCampaigns.Register();
        // the pirate sites, the start of the campaigns and their points need the space runtime (the dungeon waves and the stargates)
        if (dungeonWavesDirector is not null && stargateGraph is not null)
        {
            var sites = new Eve.Services.Space.Insurgency.InsurgencyDirector(insurgencyStore, insurgencyCampaigns, foundations.Scheduler, foundations.Events,
                foundations.Content, dungeonWavesDirector, solarSystems, stargateGraph, Eve.Dungeons.DungeonCatalog.LoadEmbedded(), structureTimers, structureStore);
            await sites.AttachAsync();
        }
        foundations.Events.Subscribe("insurgency.campaign-notices", [Ins.InsurgencyCampaigns.CampaignChangedTopic],
            (context, cancellation) =>
            {
                NotifyCampaignChanged(context.Event.Payload<Ins.CampaignChanged>()!);
                return Task.CompletedTask;
            });
        foundations.Events.Subscribe("insurgency.point-notices", [Ins.InsurgencyCampaigns.PointScoredTopic],
            (context, cancellation) =>
            {
                NotifyPointScored(context.Event.Payload<Ins.PointScored>()!);
                return Task.CompletedTask;
            });
        await using var connection = await source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var scheduled = await insurgencyCampaigns.EnsureScheduledAsync(connection, transaction);
        await transaction.CommitAsync();
        if (scheduled > 0)
            Console.Error.WriteLine($"eve-host: {scheduled} insurgency campaign(s) have their deadlines");
    }

    /// <summary>GetAllVisibleCampaigns (systemId null) and GetLocalCampaignClientSnapshot's lookup (the pilot's system): the campaigns the client may be shown.</summary>
    private async ValueTask<IReadOnlyList<Eve.Services.Core.InsurgencyCampaignSnapshot>> ReadInsurgencyCampaignsAsync(long? systemId, CancellationToken token)
    {
        await using var connection = await source.OpenConnectionAsync(token);
        var visible = await insurgencyStore.VisibleAsync(connection, null, token);
        return [.. visible.Where(campaign => systemId is null || CampaignCovers(campaign, systemId.Value)).Select(ToSnapshot)];
    }

    private static bool CampaignCovers(Ins.Campaign campaign, long systemId) =>
        campaign.OriginSolarSystemId == systemId || campaign.CoveredSolarSystemIds.Contains(systemId);

    private static Eve.Services.Core.InsurgencyCampaignSnapshot ToSnapshot(Ins.Campaign campaign) => new(
        AntipiratePointsScored: campaign.AntipiratePointsScored,
        StateExpiryTimestamp: campaign.StateExpiresMs == 0 ? 0 : Py.FileTimeFromUnixMs(campaign.StateExpiresMs),
        PirateFactionId: campaign.PirateFactionId,
        WarzoneId: campaign.WarzoneId,
        CoveredSolarSystemIds: campaign.CoveredSolarSystemIds,
        CampaignId: campaign.CampaignId,
        PiratePointsRequired: campaign.PiratePointsRequired,
        PiratePointsScored: campaign.PiratePointsScored,
        StructureId: campaign.StructureId,
        FsmState: campaign.State,
        OriginSolarSystemId: campaign.OriginSolarSystemId,
        AntipiratePointsRequired: campaign.AntipiratePointsRequired);

    private static PyValue SnapshotPayload(Ins.Campaign campaign) =>
        Eve.Services.Core.InsurgencySolarsystemService.CampaignPayload(ToSnapshot(campaign));

    /// <summary>
    /// The notices of a state change, after the commit. The location notices go to the pilots in the covered systems (the client drops the rest); the faction
    /// notices to every pilot; the ending goes to each covered system, as the client compares the system id.
    /// </summary>
    private void NotifyCampaignChanged(Ins.CampaignChanged change)
    {
        var campaign = change.Campaign;
        switch (change.To)
        {
            case Ins.CampaignStates.Forecasting:
                SendAtSystems(campaign.CoveredSolarSystemIds, "OnInsurgencyCampaignStartedForLocation", SnapshotPayload(campaign));
                SendToEverySession("OnInsurgencyStateChangedForFaction", new PyInt(change.To), new PyInt(campaign.PirateFactionId), new PyInt(campaign.OriginSolarSystemId));
                break;
            case Ins.CampaignStates.Active or Ins.CampaignStates.PirateWin or Ins.CampaignStates.AntipirateWin or Ins.CampaignStates.NoWinner:
                SendAtSystems(campaign.CoveredSolarSystemIds, "OnInsurgencyCampaignUpdatedForLocation", SnapshotPayload(campaign));
                SendToEverySession("OnInsurgencyStateChangedForFaction", new PyInt(change.To), new PyInt(campaign.PirateFactionId), new PyInt(campaign.OriginSolarSystemId));
                break;
            case Ins.CampaignStates.Terminal:
                foreach (var system in campaign.CoveredSolarSystemIds)
                    SendAtSystems([system], "OnInsurgencyCampaignEndingForLocation", new PyInt(system));
                break;
        }
    }

    /// <summary>A point: the system counts as fully corrupted or suppressed for the faction, and the covered pilots see the campaign's new count.</summary>
    private void NotifyPointScored(Ins.PointScored point)
    {
        var campaign = point.Campaign;
        if (point.Side == Ins.Side.Pirate)
            SendToEverySession("OnSystemFullyCorruptedForFaction", new PyInt(point.SolarSystemId), new PyInt(campaign.PirateFactionId),
                new PyInt(campaign.PiratePointsScored), new PyInt(campaign.PiratePointsRequired));
        else
            SendToEverySession("OnSystemFullySuppressedForFaction", new PyInt(point.SolarSystemId), new PyInt(campaign.AntipiratePointsScored),
                new PyInt(campaign.AntipiratePointsRequired));
        SendAtSystems(campaign.CoveredSolarSystemIds, "OnInsurgencyCampaignUpdatedForLocation", SnapshotPayload(campaign));
    }

    private void SendAtSystems(IEnumerable<long> systems, string name, params PyValue[] args)
    {
        var wanted = systems.ToHashSet();
        foreach (var session in characterSessions.All())
            if (session.CharacterId is not null && (session.GetInt("solarsystemid2") ?? session.GetInt("solarsystemid")) is { } system && wanted.Contains(system))
                session.SendNotification(name, "clientID", args);
    }

    private void SendToEverySession(string name, params PyValue[] args)
    {
        foreach (var session in characterSessions.All())
            if (session.CharacterId is not null)
                session.SendNotification(name, "clientID", args);
    }
}
