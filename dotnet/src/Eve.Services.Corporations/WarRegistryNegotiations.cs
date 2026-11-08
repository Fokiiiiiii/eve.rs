using System.Globalization;
using System.Text.Json.Nodes;
using Eve.Corporations;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Wallet;
using Npgsql;

namespace Eve.Services.Corporations;

public sealed partial class WarRegistryService
{
    private const int AllyOfferType = 0;
    private const int SurrenderOfferType = 2;
    private const int NegotiationNew = 0;
    private const int NegotiationAccepted = 1;
    private const int NegotiationDeclined = 2;
    private const int NegotiationRetracted = 3;
    private const int WarNotificationGroup = 8;
    private const int ConcordCorporationId = 1_000_125;
    private const long TicksPerHour = 36_000_000_000;
    private const long TicksPerDay = 864_000_000_000;
    private const long FileTimeEpoch = 116_444_736_000_000_000;

    [Method("GetNegotiations")]
    public async ValueTask<PyValue> GetNegotiations(ServiceCall call)
    {
        var ownerId = EntityOf(call.Session);
        if (ownerId <= 0 || call.Session.CharacterId is not { } characterId ||
            canManageWarAsync is not { } authorize || !await authorize(characterId, ownerId, call.Cancellation))
            return new PyList([]);
        var rows = await negotiations.PendingForOwnerAsync(ownerId, call.Cancellation);
        return new PyList([.. rows.Select(NegotiationPayload)]);
    }

    [Method("GetWarNegotiation")]
    public async ValueTask<PyValue> GetWarNegotiation(ServiceCall call)
    {
        var ownerId = EntityOf(call.Session);
        await RequireWarDirectorAsync(call, ownerId);
        var id = Integral(call.Arg(0)) ?? 0;
        var negotiation = id > 0 ? await negotiations.GetAsync(id, call.Cancellation) : null;
        if (negotiation is not null && negotiation.OwnerId1 != ownerId && negotiation.OwnerId2 != ownerId)
            throw MachoErrors.ThrowUserError("CrpAccessDenied");
        return negotiation is null ? PyNone.Instance : NegotiationPayload(negotiation);
    }

    [Method("CreateWarAllyOffer")]
    public async ValueTask<PyValue> CreateWarAllyOffer(ServiceCall call)
    {
        var ownerId = EntityOf(call.Session);
        await RequireWarDirectorAsync(call, ownerId);
        var warId = Integral(call.Arg(0)) ?? 0;
        var war = (await WarsAsync(call.Cancellation)).FirstOrDefault(candidate => candidate.WarId == warId);
        if (war is null) return PyNone.Instance;

        var requestedDefender = Integral(call.Arg(2)) ?? 0;
        if (requestedDefender > 0 && requestedDefender != war.AgainstId)
            throw MachoErrors.ThrowUserError("CrpAccessDenied");
        var creator = call.Session.CharacterId ?? 0;
        var createdAt = Py.FileTimeFromUnixMs(nowMs());
        WarNegotiation negotiation;
        await using (var connection = await source.OpenConnectionAsync(call.Cancellation))
        await using (var transaction = await connection.BeginTransactionAsync(call.Cancellation))
        {
            var current = await relations.GetAsync(WarKind, war.DeclaredById, war.AgainstId,
                connection, transaction, call.Cancellation, forUpdate: true);
            if (current is null || current.Id != warId) return PyNone.Instance;
            negotiation = await negotiations.CreateAsync(connection, transaction, warId, AllyOfferType,
                ownerId, war.AgainstId, war.DeclaredById, war.AgainstId, MoneyAmount(call.Arg(1)),
                SessionAccountKey(call.Session), Eve.Macho.JsSemantics.StringValue(call.Arg(3)) ?? "",
                creator, createdAt, call.Cancellation);
            await transaction.CommitAsync(call.Cancellation);
        }

        var sender = creator > 0 ? creator : negotiation.OwnerId1;
        await NotifyOwnersAsync([negotiation.OwnerId2], sender, 102, new JsonObject
        {
            ["mercID"] = negotiation.OwnerId1,
            ["defenderID"] = negotiation.AgainstId,
            ["aggressorID"] = negotiation.DeclaredById,
            ["iskValue"] = negotiation.IskValue,
        }, $"war-negotiation:{negotiation.Id}:ally-offer-defender", call.Cancellation);
        await NotifyOwnersAsync([negotiation.OwnerId1], sender, 126, new JsonObject
        {
            ["defenderID"] = negotiation.AgainstId,
            ["enemyID"] = negotiation.DeclaredById,
            ["charID"] = creator,
            ["iskValue"] = negotiation.IskValue,
        }, $"war-negotiation:{negotiation.Id}:ally-offer-ally", call.Cancellation);
        return PyNone.Instance;
    }

    [Method("RetractWarAllyOffer")]
    public async ValueTask<PyValue> RetractWarAllyOffer(ServiceCall call) =>
        await TransitionOfferAsync(call, AllyOfferType, ownerId2: false, NegotiationRetracted);

    [Method("CreateSurrenderNegotiation")]
    public async ValueTask<PyValue> CreateSurrenderNegotiation(ServiceCall call)
    {
        var ownerId = EntityOf(call.Session);
        await RequireWarDirectorAsync(call, ownerId);
        var warId = Integral(call.Arg(0)) ?? 0;
        var war = (await WarsAsync(call.Cancellation)).FirstOrDefault(candidate => candidate.WarId == warId);
        if (war is null || (war.DeclaredById != ownerId && war.AgainstId != ownerId)) return PyNone.Instance;

        var otherOwnerId = war.DeclaredById == ownerId ? war.AgainstId : war.DeclaredById;
        var createdAt = Py.FileTimeFromUnixMs(nowMs());
        await using var connection = await source.OpenConnectionAsync(call.Cancellation);
        await using var transaction = await connection.BeginTransactionAsync(call.Cancellation);
        var current = await relations.GetAsync(WarKind, war.DeclaredById, war.AgainstId,
            connection, transaction, call.Cancellation, forUpdate: true);
        if (current is null || current.Id != warId) return PyNone.Instance;
        var negotiation = await negotiations.CreateAsync(connection, transaction, warId, SurrenderOfferType,
            ownerId, otherOwnerId, war.DeclaredById, war.AgainstId, MoneyAmount(call.Arg(1)),
            SessionAccountKey(call.Session), Eve.Macho.JsSemantics.StringValue(call.Arg(2)) ?? "",
            call.Session.CharacterId ?? 0, createdAt, call.Cancellation);
        await transaction.CommitAsync(call.Cancellation);

        var sender = negotiation.CreatedByCharacterId > 0 ? negotiation.CreatedByCharacterId : negotiation.OwnerId1;
        await NotifyOwnersAsync([negotiation.OwnerId2], sender, 103, new JsonObject
        {
            ["ownerID1"] = negotiation.OwnerId1,
            ["ownerID2"] = negotiation.OwnerId2,
            ["iskValue"] = negotiation.IskValue,
        }, $"war-negotiation:{negotiation.Id}:surrender-offer", call.Cancellation);
        await NotifyOwnersAsync([negotiation.OwnerId2], sender, 122, new JsonObject
        {
            ["entityID"] = negotiation.OwnerId1,
            ["offeredID"] = negotiation.OwnerId2,
            ["charID"] = negotiation.CreatedByCharacterId,
            ["iskValue"] = negotiation.IskValue,
        }, $"war-negotiation:{negotiation.Id}:surrender-offered", call.Cancellation);
        return PyNone.Instance;
    }

    [Method("AcceptAllyNegotiation")]
    public async ValueTask<PyValue> AcceptAllyNegotiation(ServiceCall call) =>
        await AcceptOfferAsync(call, AllyOfferType);

    [Method("DeclineAllyOffer")]
    public async ValueTask<PyValue> DeclineAllyOffer(ServiceCall call) =>
        await TransitionOfferAsync(call, AllyOfferType, ownerId2: true, NegotiationDeclined);

    [Method("AcceptSurrender")]
    public async ValueTask<PyValue> AcceptSurrender(ServiceCall call) =>
        await AcceptOfferAsync(call, SurrenderOfferType);

    [Method("DeclineSurrender")]
    public async ValueTask<PyValue> DeclineSurrender(ServiceCall call) =>
        await TransitionOfferAsync(call, SurrenderOfferType, ownerId2: true, NegotiationDeclined);

    private async Task<PyValue> TransitionOfferAsync(ServiceCall call, int typeId, bool ownerId2, int nextState)
    {
        var ownerId = EntityOf(call.Session);
        await RequireWarDirectorAsync(call, ownerId);
        var id = Integral(call.Arg(0)) ?? 0;
        if (id <= 0) return PyNone.Instance;
        WarNegotiation? changed;
        await using (var connection = await source.OpenConnectionAsync(call.Cancellation))
        await using (var transaction = await connection.BeginTransactionAsync(call.Cancellation))
        {
            var current = await negotiations.GetAsync(connection, transaction, id, forUpdate: true, call.Cancellation);
            if (current is null || current.State != NegotiationNew || current.TypeId != typeId ||
                (ownerId2 ? current.OwnerId2 : current.OwnerId1) != ownerId)
                return PyNone.Instance;
            changed = await negotiations.TransitionPendingAsync(connection, transaction, id, nextState,
                Py.FileTimeFromUnixMs(nowMs()), call.Cancellation);
            if (changed is null) return PyNone.Instance;
            await transaction.CommitAsync(call.Cancellation);
        }

        var characterId = call.Session.CharacterId ?? 0;
        if (typeId == AllyOfferType && nextState == NegotiationRetracted)
        {
            await NotifyOwnersAsync([changed.OwnerId2], characterId, 241, new JsonObject
            {
                ["mercID"] = changed.OwnerId1,
                ["defenderID"] = changed.AgainstId,
                ["aggressorID"] = changed.DeclaredById,
            }, $"war-negotiation:{changed.Id}:ally-retracted-defender", call.Cancellation);
            await NotifyOwnersAsync([changed.OwnerId1], characterId, 242, new JsonObject
            {
                ["allyID"] = changed.OwnerId1,
                ["defenderID"] = changed.AgainstId,
                ["enemyID"] = changed.DeclaredById,
                ["charID"] = characterId,
            }, $"war-negotiation:{changed.Id}:ally-retracted-ally", call.Cancellation);
        }
        else if (typeId == AllyOfferType)
        {
            await NotifyOwnersAsync([changed.OwnerId1], characterId, 110, new JsonObject
            {
                ["defenderID"] = changed.AgainstId,
                ["aggressorID"] = changed.DeclaredById,
                ["allyID"] = changed.OwnerId1,
                ["charID"] = characterId,
            }, $"war-negotiation:{changed.Id}:ally-declined", call.Cancellation);
        }
        else
        {
            await NotifyOwnersAsync([changed.OwnerId1], characterId, 104, new JsonObject
            {
                ["ownerID"] = changed.OwnerId2,
                ["iskValue"] = changed.IskValue,
            }, $"war-negotiation:{changed.Id}:surrender-declined", call.Cancellation);
        }
        return PyNone.Instance;
    }

    private async Task<PyValue> AcceptOfferAsync(ServiceCall call, int typeId)
    {
        var acceptingOwnerId = EntityOf(call.Session);
        await RequireWarDirectorAsync(call, acceptingOwnerId);
        var negotiationId = Integral(call.Arg(0)) ?? 0;
        if (negotiationId <= 0) return PyNone.Instance;
        var acceptedAt = Py.FileTimeFromUnixMs(nowMs());
        WarNegotiation accepted;
        long allyStartAt = 0;
        WarsInfoMgrService.WarRecord? updatedWar = null;
        await using (var connection = await source.OpenConnectionAsync(call.Cancellation))
        await using (var transaction = await connection.BeginTransactionAsync(call.Cancellation))
        {
            var currentNegotiation = await negotiations.GetAsync(connection, transaction, negotiationId,
                forUpdate: true, call.Cancellation);
            if (currentNegotiation is null || currentNegotiation.State != NegotiationNew ||
                currentNegotiation.TypeId != typeId || currentNegotiation.OwnerId2 != acceptingOwnerId)
                return PyNone.Instance;

            var currentWar = await relations.GetAsync(WarKind, currentNegotiation.DeclaredById, currentNegotiation.AgainstId,
                connection, transaction, call.Cancellation, forUpdate: true);
            if (currentWar is null || currentWar.Id != currentNegotiation.WarId) return PyNone.Instance;
            var war = (await WarsAsync(call.Cancellation)).FirstOrDefault(candidate => candidate.WarId == currentNegotiation.WarId);
            if (war is null) return PyNone.Instance;

            var payerOwnerId = typeId == AllyOfferType ? currentNegotiation.OwnerId2 : currentNegotiation.OwnerId1;
            var receiverOwnerId = typeId == AllyOfferType ? currentNegotiation.OwnerId1 : currentNegotiation.OwnerId2;
            var payerWalletOwner = resolveWalletOwnerAsync is null
                ? null
                : await resolveWalletOwnerAsync(payerOwnerId, call.Cancellation);
            var receiverWalletOwner = resolveWalletOwnerAsync is null
                ? null
                : await resolveWalletOwnerAsync(receiverOwnerId, call.Cancellation);
            var amount = currentNegotiation.IskValue;
            if (typeId == AllyOfferType && (payerWalletOwner is null || receiverWalletOwner is null))
                return PyNone.Instance;
            if (typeId == SurrenderOfferType && amount > 0 && (payerWalletOwner is null || receiverWalletOwner is null))
                return PyNone.Instance;

            var accountKey = typeId == AllyOfferType
                ? SessionAccountKey(call.Session)
                : currentNegotiation.OwnerId1AccountKey;
            var payerAccount = new WalletAccount(payerWalletOwner ?? 0, accountKey);
            var receiverAccount = new WalletAccount(receiverWalletOwner ?? 0);
            var concordFee = 0m;
            if (typeId == AllyOfferType)
            {
                var existingAllyCount = war.Allies.Count(ally => ally.TimeFinished is null || ally.TimeFinished > acceptedAt);
                if (existingAllyCount > 0)
                {
                    var baseCost = readCharacterAllyBaseCostAsync is null
                        ? 0L
                        : await readCharacterAllyBaseCostAsync(call.Session.CharacterId ?? 0, call.Cancellation);
                    var exponent = Math.Min(existingAllyCount - 1, 20);
                    concordFee = Math.Max(0L, baseCost) * (1L << exponent);
                }
            }

            try
            {
                if (amount > 0)
                {
                    await wallets.TransferAsync(connection, transaction, payerAccount, receiverAccount, amount,
                        refTypeId: 10,
                        reason: typeId == AllyOfferType ? $"War ally fee for war {currentNegotiation.WarId}" : $"War surrender fee for war {currentNegotiation.WarId}",
                        timeMs: nowMs(), operationKey: $"war-negotiation:{negotiationId}:transfer",
                        reference: "war negotiation transfer", token: call.Cancellation);
                }
                if (concordFee > 0)
                {
                    await wallets.AdjustAsync(connection, transaction, payerAccount, -concordFee, ConcordCorporationId,
                        refTypeId: 10, reason: $"CONCORD ally registration fee for war {currentNegotiation.WarId}",
                        timeMs: nowMs(), operationKey: $"war-negotiation:{negotiationId}:concord-fee",
                        reference: "war negotiation concord fee", token: call.Cancellation);
                }
            }
            catch (Exception error) when (error is InsufficientFundsException or WalletNotFoundException or MoneyLimitException)
            {
                return PyNone.Instance;
            }

            if (typeId == AllyOfferType)
            {
                allyStartAt = checked(acceptedAt + 4 * TicksPerHour);
                var naturalEnd = checked(acceptedAt + 2 * 7 * TicksPerDay);
                var allyEnd = war.TimeFinished is { } warEnd && warEnd < naturalEnd ? warEnd : naturalEnd;
                var allyEndUnixMs = UnixMsFromFileTime(allyEnd);
                await relations.SetAsync(connection, transaction, "war.ally", currentNegotiation.OwnerId1,
                    currentNegotiation.WarId, 1, "ally offer accepted", new
                    {
                        timeStarted = allyStartAt.ToString(CultureInfo.InvariantCulture),
                        timeFinished = allyEnd.ToString(CultureInfo.InvariantCulture),
                    }, allyEndUnixMs, call.Cancellation);
            }
            else
            {
                var attrs = (JsonNode.Parse(string.IsNullOrWhiteSpace(currentWar.AttrsJson) ? "{}" : currentWar.AttrsJson) as JsonObject)
                    ?? new JsonObject();
                attrs["timeFinished"] = acceptedAt.ToString(CultureInfo.InvariantCulture);
                attrs["reasonEnded"] = 4;
                await relations.SetAsync(connection, transaction, WarKind, currentWar.Subject, currentWar.Object,
                    currentWar.Value, "war surrender accepted", attrs, currentWar.UntilMs, call.Cancellation);
                await negotiations.CreatePeaceTreatiesAsync(connection, transaction, currentNegotiation.WarId,
                    currentNegotiation.DeclaredById, currentNegotiation.AgainstId, peaceReason: 4,
                    createdDateTime: acceptedAt, expiryDateTime: checked(acceptedAt + 14 * TicksPerDay), token: call.Cancellation);
            }

            accepted = await negotiations.TransitionPendingAsync(connection, transaction, negotiationId,
                NegotiationAccepted, acceptedAt, call.Cancellation) ?? currentNegotiation;
            if (accepted.State != NegotiationAccepted) return PyNone.Instance;
            await transaction.CommitAsync(call.Cancellation);
        }

        var wars = await WarsAsync(call.Cancellation);
        updatedWar = wars.FirstOrDefault(candidate => candidate.WarId == accepted.WarId);
        if (updatedWar is not null)
            CorporationNotificationAdapter.WarChanged(sessions, call, updatedWar.DeclaredById, updatedWar.AgainstId, Payload(updatedWar));

        var characterId = call.Session.CharacterId ?? 0;
        if (typeId == AllyOfferType)
        {
            var noticeData = new JsonObject
            {
                ["allyID"] = accepted.OwnerId1,
                ["enemyID"] = accepted.DeclaredById,
                ["charID"] = characterId,
                ["iskValue"] = accepted.IskValue,
                ["time"] = FileTimeData(acceptedAt),
            };
            await NotifyOwnersAsync([accepted.OwnerId1, accepted.OwnerId2, accepted.DeclaredById], characterId, 127,
                noticeData, $"war-negotiation:{accepted.Id}:ally-accepted", call.Cancellation);
            var joinedData = new JsonObject
            {
                ["allyID"] = accepted.OwnerId1,
                ["defenderID"] = accepted.AgainstId,
                ["aggressorID"] = accepted.DeclaredById,
                ["startTime"] = FileTimeData(allyStartAt),
            };
            await NotifyOwnersAsync([accepted.AgainstId], accepted.OwnerId1, 99, joinedData,
                $"war-negotiation:{accepted.Id}:ally-joined-defender", call.Cancellation);
            await NotifyOwnersAsync([accepted.DeclaredById], accepted.OwnerId1, 100, joinedData,
                $"war-negotiation:{accepted.Id}:ally-joined-aggressor", call.Cancellation);
            await NotifyOwnersAsync([accepted.OwnerId1], accepted.OwnerId1, 101, joinedData,
                $"war-negotiation:{accepted.Id}:ally-joined-ally", call.Cancellation);
        }
        else
        {
            var acceptedData = new JsonObject
            {
                ["entityID"] = accepted.OwnerId2,
                ["offeringID"] = accepted.OwnerId1,
                ["charID"] = characterId,
                ["iskValue"] = accepted.IskValue,
            };
            await NotifyOwnersAsync([accepted.OwnerId1, accepted.OwnerId2], characterId, 123, acceptedData,
                $"war-negotiation:{accepted.Id}:surrender-accepted", call.Cancellation);
            var isAlliance = isAllianceAsync is not null && await isAllianceAsync(accepted.AgainstId, call.Cancellation);
            await NotifyOwnersAsync([accepted.AgainstId], characterId, isAlliance ? 6 : 29, new JsonObject(),
                $"war-negotiation:{accepted.Id}:war-surrender", call.Cancellation);
        }
        return PyNone.Instance;
    }

    private async Task NotifyOwnersAsync(IReadOnlyList<long> ownerIds, long senderId, int typeId,
        JsonObject data, string idempotencyKey, CancellationToken token)
    {
        if (notifyWarOwnersAsync is not { } notify || ownerIds.Count == 0) return;
        await notify(ownerIds.Distinct().ToArray(), senderId, typeId, data, idempotencyKey, token);
    }

    private static PyValue NegotiationPayload(WarNegotiation negotiation) => Py.KeyVal(
        ("warNegotiationID", InventoryWire.Id(negotiation.Id)),
        ("warID", InventoryWire.Id(negotiation.WarId)),
        ("warNegotiationTypeID", new PyInt(negotiation.TypeId)),
        ("ownerID1", InventoryWire.Id(negotiation.OwnerId1)),
        ("ownerID2", InventoryWire.Id(negotiation.OwnerId2)),
        ("declaredByID", InventoryWire.Id(negotiation.DeclaredById)),
        ("againstID", InventoryWire.Id(negotiation.AgainstId)),
        ("iskValue", new PyReal((double)negotiation.IskValue)),
        ("description", new PyWString(negotiation.Description)),
        ("negotiationState", new PyInt(negotiation.State)),
        ("createdDateTime", new PyLong(negotiation.CreatedDateTime)),
        ("timeAccepted", OptionalTime(negotiation.TimeAccepted)),
        ("timeDeclined", OptionalTime(negotiation.TimeDeclined)),
        ("timeRetracted", OptionalTime(negotiation.TimeRetracted)));

    private static decimal MoneyAmount(PyValue value)
    {
        decimal amount;
        try
        {
            amount = value switch
            {
                PyInt integer => integer.Value,
                PyLong integer => integer.Value,
                PyReal real when double.IsFinite(real.Value) => (decimal)real.Value,
                _ => 0m,
            };
        }
        catch (OverflowException)
        {
            amount = 0m;
        }
        return Math.Max(0m, decimal.Round(amount, 2, MidpointRounding.AwayFromZero));
    }

    private static int SessionAccountKey(ClientSession session)
    {
        var key = session.GetInt("corpaccountkey") ?? session.GetInt("corpAccountKey") ?? 1000;
        return key is >= 1000 and <= 1006 ? (int)key : 1000;
    }

    private static JsonObject FileTimeData(long filetime) => new()
    {
        ["type"] = "long",
        ["value"] = filetime.ToString(CultureInfo.InvariantCulture),
    };

    private static long UnixMsFromFileTime(long filetime) => (filetime - FileTimeEpoch) / 10_000;
}
