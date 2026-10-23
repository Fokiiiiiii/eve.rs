using Eve.Macho;
using Eve.Marshal;
using System.Runtime.CompilerServices;

namespace Eve.Services;

/// <summary>
/// Routes decoded MachoNet packets after the handshake: calls to services, pings, and notifications
/// about bound objects. Behaves like the reference server's PacketDispatcher, whose recorded packets
/// the tests replay byte for byte.
/// </summary>
public sealed class PacketDispatcher
{
    private const string ReleaseMethod = "ClientHasReleasedTheseObjects";
    private const string ServiceNotFoundClass = "eveexceptions.ServiceNotFound";
    private const string AttributeErrorClass = "exceptions.AttributeError";
    private const string ReleaseKey = "OID-";

    private readonly ServiceRegistry _registry;
    private readonly Func<long> _nowUnixMs;
    private readonly ConditionalWeakTable<ClientSession, ExpensiveRpcBudget> _expensiveBudgets = new();

    public PacketDispatcher(ServiceRegistry registry, Func<long>? nowUnixMs = null)
    {
        _registry = registry;
        _nowUnixMs = nowUnixMs ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    /// <summary>Called with a problem worth logging (a failed handler, an unroutable call).</summary>
    public Action<string, Exception?>? OnProblem { get; set; }

    /// <summary>
    /// An unknown service, an unknown method and a call with no service are answered with the Macho error the V24.01
    /// server sends for them (a wrapped <c>ServiceNotFound</c> or <c>AttributeError</c>), which the client raises at
    /// its call site; a None result would be read as the call's answer (the client dies on <c>None.level</c>).
    /// A handler that throws an exception other than a user error is answered with the V24.01 server's wrapped
    /// <c>RuntimeError</c> carrying an error ID; the host logs the exception under that ID. Strict mode (EVE_STRICT_RPC=1)
    /// also names the failure in the error text and logs the call's arguments and the stack trace.
    /// </summary>
    public bool StrictRpc { get; init; }

    /// <summary>Weighted work units available to one session during a rolling minute.</summary>
    public int MaxExpensiveRpcUnitsPerMinute { get; init; } = 120;

    /// <summary>
    /// Handles one decoded packet from <paramref name="session"/>. Returns whether it was handled;
    /// responses go out through the session's sink.
    /// </summary>
    public async Task<bool> DispatchAsync(PyValue raw, ClientSession session, CancellationToken ct = default)
    {
        var packet = MachoPacket.Decode(raw);
        if (packet is null)
        {
            return false;
        }
        switch (packet.Type)
        {
            case MachoMessage.CallReq:
                return await HandleCallRequestAsync(packet, session, ct);
            case MachoMessage.CallRsp:
                // The client's answer to a call the server made (SendClientCall); any other answer has no waiter.
                session.CompleteClientCall(JsSemantics.IsNumber(packet.CallId) ? (long)JsSemantics.NumberValue(packet.CallId) : 0, ClientAnswer(packet));
                return true;
            case MachoMessage.PingRsp:
            case MachoMessage.SessionChangeNotification:
            case MachoMessage.ErrorResponse:
                return true;
            case MachoMessage.Notification:
                HandleNotification(packet, session);
                return true;
            case MachoMessage.PingReq:
                session.Send(ResponseBuilder.PingResponse(packet, _nowUnixMs()));
                return true;
            default:
                return false;
        }
    }

    // ── calls ───────────────────────────────────────────────────────────────

    private async Task<bool> HandleCallRequestAsync(MachoPacket packet, ClientSession session, CancellationToken ct)
    {
        var serviceName = packet.Service;
        var call = CallRequest.Decode(packet.Payload);
        if (call is null)
        {
            return false;
        }

        // A bound-object call names its object in the call body, not in the address.
        if (string.IsNullOrEmpty(serviceName) && JsSemantics.Truthy(call.RemoteObject))
        {
            serviceName = BoundObjects.ExtractName(call.RemoteObject) ?? serviceName;
        }

        // EVE_TRACE_RPC=1 traces unhandled calls; =full adds the argument and result trees to every call.
        var traceMode = Environment.GetEnvironmentVariable("EVE_TRACE_RPC");
        var traceRpc = traceMode is "1" or "full";
        var traceFull = traceMode == "full";

        if (string.IsNullOrEmpty(serviceName))
        {
            if (traceRpc)
                Console.Error.WriteLine($"[rpc-trace] unhandled session={session.SessionId} service=<none> method={call.Method} reason=missing-service-address");
            if (StrictRpc)
            {
                FailStrictly(packet, session, null, call, "unknown service", "the call names no service");
                return true;
            }
            SendError(packet, MachoErrors.UserError("UnMachoDestination"), session, null);
            return true;
        }

        var service = _registry.Lookup(serviceName);
        var traceLogin = string.Equals(
            Environment.GetEnvironmentVariable("EVE_TRACE_LOGIN"), "1", StringComparison.Ordinal);
        var trace = traceRpc || traceLogin;
        var traceLabel = traceRpc ? "rpc-trace" : "login-trace";
        if (trace)
        {
            Console.Error.WriteLine(
                $"[{traceLabel}] request session={session.SessionId} service={service?.Name ?? serviceName} " +
                $"method={call.Method} args={call.Args.Count} bound={call.RemoteObject is not null}" +
                (traceFull ? $" values={RpcDiagnostics.Args(call.Args)}" : ""));
        }
        if (service is null)
        {
            if (serviceName.StartsWith(BoundObjects.OidPrefix, StringComparison.Ordinal))
            {
                if (traceRpc)
                    Console.Error.WriteLine($"[rpc-trace] unhandled session={session.SessionId} service={serviceName} method={call.Method} reason=released-bound-object");
                if (StrictRpc)
                    Console.Error.WriteLine($"[strict-rpc] unregistered bound object {serviceName}.{call.Method} session={session.SessionId}: answered UnMachoDestination");
                // A released bound object. Answering None would be read as the call's result (a colony
                // that dies on None.level); UnMachoDestination makes the client unresolve, rebind and
                // retry once.
                SendError(packet, MachoErrors.UserError("UnMachoDestination"), session, serviceName);
                return true;
            }
            if (StrictRpc)
            {
                FailStrictly(packet, session, serviceName, call, "unknown service", serviceName);
                return true;
            }
            if (traceRpc)
                Console.Error.WriteLine($"[rpc-trace] unhandled session={session.SessionId} service={serviceName} method={call.Method} reason=service-not-registered");
            // The V24.01 server's answer to an unknown service: ServiceNotFound, wrapped (machoNetTransport.SessionCall).
            SendError(packet, MachoErrors.Wrapped(ServiceNotFoundClass, [new PyStr(serviceName)]), session, serviceName);
            return true;
        }

        if (!service.HasMethod(call.Method))
        {
            if (traceRpc)
                Console.Error.WriteLine($"[rpc-trace] unhandled session={session.SessionId} service={service.Name} method={call.Method} reason=method-not-registered");
            if (StrictRpc)
            {
                FailStrictly(packet, session, service.Name, call, "unknown method", $"{service.Name}.{call.Method}");
                return true;
            }
            // The V24.01 server's answer to a method it does not export: the AttributeError of getattr, wrapped
            // (ObjectCallGPCS.ObjectCall, then ExceptionWrapper.CallUp).
            SendError(packet, MachoErrors.Wrapped(AttributeErrorClass,
                [new PyStr($"'{service.Name}' object has no attribute '{call.Method}'")]), session, service.Name);
            return true;
        }

        var cost = ExpensiveRpcCost(service.Name, call.Method);
        if (call.Method == "MachoBindObject" && call.Args.Count > 1 &&
            NestedBoundMethod(call.Args[1]) is { } nestedMethod)
            cost = Math.Max(cost, ExpensiveRpcCost(service.Name, nestedMethod));
        if (cost > 0 && !_expensiveBudgets.GetValue(session, _ => new ExpensiveRpcBudget())
                .TryConsume(_nowUnixMs(), cost, MaxExpensiveRpcUnitsPerMinute))
        {
            SendError(packet, MachoErrors.UserError("SessionRpcBudgetExceeded"), session, serviceName);
            return true;
        }

        var previousBoundObjectId = session.CurrentBoundObjectId;
        try
        {
            session.CurrentBoundObjectId = serviceName.StartsWith(BoundObjects.OidPrefix, StringComparison.Ordinal) ? serviceName : null;
            var result = await service.CallAsync(new ServiceCall
            {
                Method = call.Method,
                Args = call.Args,
                Kwargs = call.Kwargs,
                Session = session,
                CallId = packet.CallId,
                BoundObjectId = session.CurrentBoundObjectId,
                BoundObjectParameters = session.CurrentBoundObjectId is { } oid
                    ? _registry.GetBoundObjectParams(oid)
                    : null,
                Cancellation = ct,
            });
            if (trace)
            {
                Console.Error.WriteLine(
                    $"[{traceLabel}] response session={session.SessionId} service={service.Name} " +
                    $"method={call.Method} result={result.GetType().Name}" +
                    (traceFull ? $" tree={RpcDiagnostics.Describe(result)}" : ""));
            }

            if (result is PyDeferred deferred)
            {
                await deferred.Start(new DeferredContext(
                    packet, session, service.Name, reply =>
                    {
                        if (traceRpc)
                            Console.Error.WriteLine($"[rpc-trace] deferred-response session={session.SessionId} service={service.Name} method={call.Method} result={reply.GetType().Name}");
                        SendCallResponse(packet, reply, session, service.Name);
                    }, () => SendProvisional(packet, session, service.Name)));
                return true;
            }

            RegisterBoundObjects(
                result,
                service,
                session,
                call.Method == "MachoBindObject" && call.Args.Count > 0 ? call.Args[0] : null);
            SendCallResponse(packet, result, session, service.Name);
            session.RunAfterResponse();
            return true;
        }
        catch (MachoWrappedException wrapped)
        {
            session.DropAfterResponse();
            if (traceRpc)
                Console.Error.WriteLine($"[rpc-trace] error session={session.SessionId} service={service.Name} method={call.Method} class={wrapped.Error.ClassName}");
            if (!wrapped.Error.IsUserError)
            {
                OnProblem?.Invoke($"{service.Name}.{call.Method}: wrapped exception {wrapped.Error.ClassName}", wrapped);
            }
            SendError(packet, wrapped.Error, session, service.Name);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (trace)
            {
                Console.Error.WriteLine(
                    $"[{traceLabel}] exception session={session.SessionId} service={service.Name} " +
                    $"method={call.Method} error={ex.GetType().Name}");
            }
            session.DropAfterResponse();
            if (StrictRpc)
            {
                OnProblem?.Invoke($"{service.Name}.{call.Method}: {ex.Message}", ex);
                FailStrictly(packet, session, service.Name, call, "unhandled exception", $"{ex.GetType().Name}: {ex.Message}", ex);
                return true;
            }
            // Answered as the V24.01 server answers an unexpected exception: a wrapped error under an ID. The host logs
            // the exception under the same ID; the client gets the ID and neither the message nor the stack.
            var errorId = RpcDiagnostics.CorrelationId();
            OnProblem?.Invoke($"{service.Name}.{call.Method} [{errorId}]: {ex.GetType().Name}: {ex.Message}", ex);
            SendError(packet, MachoErrors.UnexpectedFailure(errorId, $"{service.Name}.{call.Method}"), session, service.Name);
            return true;
        }
        finally
        {
            session.CurrentBoundObjectId = previousBoundObjectId;
        }
    }

    /// <summary>
    /// Strict mode: answers the call with a Macho error that names the failure and a correlation id, and logs the
    /// failure with the call's arguments (and the exception's stack trace, when there is one) to stderr.
    /// </summary>
    private static int ExpensiveRpcCost(string service, string method)
    {
        if (method == "GetAllInfo")
            return 10;
        if (service == "marketProxy" && method is
            "GetOrders" or "GetStationAsks" or "GetSystemAsks" or "GetRegionBest" or
            "GetMarketOrderHistory" or "GetOldPriceHistory" or "GetNewPriceHistory" or
            "GetCharOrders" or "GetCorporationOrders" or "GetCharEscrow" or
            "GetPlexOrders" or "GetPlexBest" or "GetPlexHistory" or
            "GetPlexOldPriceHistory" or "GetPlexNewPriceHistory" or "CharGetTransactions" or
            "CorpGetTransactions" or "BuyMultipleItems" or "PlaceMultiSellOrder" or
            "GetHistoryForManyTypeIDs")
            return 10;
        if (service == "corporationSvc" && method is "GetEmploymentRecord" or "GetEmployementRecordAndCharacterTransfers" or "GetMyCorporationInfo" or "GetMedalsReceived" or "GetAllCorpMedals" or "GetRecipientsOfMedal" or "SetMedalStatus" or "GiveMedalToCharacters")
            return 10;
        if (service == "ownerGroupManager")
            return 10;
        if (service == "mailMgr" && method is "SyncMail" or "GetMailHeaders" or "GetBody" or "MarkAsRead" or "SendMail" or "PokePlayerAboutChatMsgGm")
            return 10;
        if (service == "mailingListsMgr")
            return 10;
        if (service == "contractProxy")
            return 10;
        if (service == "insuranceSvc" && method == "GetContracts")
            return 10;
        if (service == "planetOrbitalRegistryBroker" && method == "GetTaxRate")
            return 2;
        if (service == "planetOrbitalRegistryBroker" && method == "UpdateSettings")
            return 10;
        if (service == "corpStationMgr" && method == "MachoBindObject")
            return 10;
        if (service == "billMgr")
            return 10;
        if (service == "industryManager" && method is ("GetJobCounts" or "GetJob" or "GetJobsByOwner" or "InstallJob" or "CompleteJob" or "CompleteManyJobs" or "CancelJob"))
            return 10;
        if (service == "shipCosmeticsMgr" && method is ("GetLicencedSkins" or "GiveSkin" or "RemoveSkin" or "GMExpireSkinLicense" or "ApplySkinToShip" or "ActivateSkinLicense"))
            return 10;
        if ((service is "charFittingMgr" or "corpFittingMgr" or "allianceFittingMgr") && (method is "GetFittings" or "SaveFitting" or "UpdateFitting" or "SaveManyFittings" or "DeleteFitting" or "DeleteManyFittings" or "UpdateNameAndDescription" or "GetCommunityFittings"))
            return 10;
        if (service == "invbroker" && method is "MultiMerge" or "MultiAdd" or "StackAll")
            return 10;
        if (service == "repackagingSvc" && method == "RepackageItems")
            return 10;
        if (service == "invbroker" && method is "GetDamageForCrystals" or "ValidateItemListCanBeOpened" or "ListIncludingContainers" or "ImportExportWithPlanet")
            return 10;
        if (service == "invbroker" && method is "GetInventory" or "GetInventoryFromId" or "GetContainerContents" or "GetItems" or "List" or "ListByFlags" or "ListStations" or "ListStationItems" or "GetAssetWorth" or "GetCapacity")
            return 2;
        if (service == "corpMgr" && (method is "GetAssetInventory" or "GetAssetInventoryForLocation" or "SearchAssets" or "GetAggressionSettingsForCorps" or "AuditMember"))
            return 10;
        if (service == "itemLocking" && (method is "GetItemsByLocation" or "GetLockedItemLocations"))
            return 10;
        if ((service == "crimewatch" && method == "GetSecurityStatusTransactions") ||
            (service == "killRightMgr" && method == "GetKillRights"))
            return 10;
        if (service == "account" && method is "GetTransactions" or "GiveCash" or "GiveCashFromCorpAccount")
            return 10;
        if (service == "config" && method is "GetAverageMarketPrices" or "GetMultiOwnersEx" or "GetMultiCorpTickerNamesEx" or "GetMultiAllianceShortNamesEx" or "GetMultiLocationsEx")
            return 10;
        if (service == "LPStoreMgr" && method is "GetAvailableOffersFromCorp" or "TakeOfferForCharacter" or "TakeOfferForCorporation")
            return 10;
        if (service == "corpRegistry" && method is "GetCorporation" or "GetEveOwners" or "GetMemberIDsByQuery" or "GetMemberIDsWithMoreThanAvgShares" or "GetNumberOfPotentialCEOs" or "GetMembersByIds" or "GetMembersPaged" or "GetMemberTrackingInfo" or "GetMemberTrackingInfoSimple" or "GetShareholders" or "GetSharesByShareholder" or "PayoutDividend" or "ExecuteActions" or "UpdateMembers" or "KickOutMembers" or "UpdateTitles" or "UpdateCorporation" or "UpdateLogo" or "UpdateDivisionNames" or "UpdateCorporationAbilities" or "RegisterNewAcceptStructureSettings" or "RegisterNewCorpMailRestrictionSettings" or "SetStructureReinforceDefault" or "RegisterNewAggressionSettings" or "RemoveCorporateContacts" or "EditContactsRelationshipID" or "AssignLabels" or "RemoveLabels" or "UpdateBulletinOrder" or "GetRecentKills" or "GetRecentLosses" or "GetCorporateContacts" or "GetContactList" or "GetBulletins" or "GetBulletinEntries" or "GetApplications" or "GetOldApplications" or "GetMyApplications" or "GetMyOldApplications" or "GetMyOpenInvitations" or "UpdateApplicationOffer" or "GetRecruitmentAd" or "GetRecruitmentAdsByCorpID" or "GetRecruitmentAdsForCorporation")
            return 10;
        if (service == "allianceRegistry" && method is "GetAlliance" or "GetAlliancePublicInfo" or "GetAllianceSovereigntyStructuresInfo" or "GetRelationships" or "SetRelationship" or "GetCapitalSystemInfo" or "SetCapitalSystem" or "SetPrimeHour" or "CancelCapitalSystemTransition" or "UpdateAlliance" or "DeclareWarAgainst" or "PayBill" or "GetBillBalance" or "GetBills" or "GetBillsReceivable" or "GetApplications" or "GetEveOwners" or "GetRankedAlliances" or "GetAllianceMembers" or "GetMembers" or "GetEmploymentRecord" or "GetAllianceMembersOlderThan" or "GetDaysInAlliance" or "GetAllianceApplications" or "GetAllianceContacts" or "GetContactList" or "GetBulletins" or "GetBulletinEntries" or "DeclareExecutorSupport" or "AddAllianceContact" or "EditAllianceContact" or "RemoveAllianceContacts" or "EditContactsRelationshipID" or "CreateLabel" or "DeleteLabel" or "EditLabel" or "AssignLabels" or "RemoveLabels" or "UpdateBulletin" or "DeleteBulletin" or "UpdateBulletinOrder")
            return 10;
        if (service == "charMgr" && method is "GetPublicInfo" or "GetPublicInfo3" or "GetCharacterDescription" or "GetCharacterCreationDate" or "GetRecentShipKillsAndLosses" or "GetPrivateInfo" or "GetRecentShipKillsAndLosses" or "GetOrganizationInfoForCharacters" or "SetCharacterDescription" or "GetContactList" or "GetCharacterSettings" or "SaveCharacterSetting" or "DeleteCharacterSetting" or "GetLabels" or "GetOwnerNoteLabels" or "GetOwnerNote" or "GetNote" or "LogSettings" or "AddOwnerNote" or "EditOwnerNote" or "RemoveOwnerNote" or "SetNote" or "AddContact" or "EditContact" or "DeleteContacts" or "BlockOwners" or "UnblockOwners" or "EditContactsRelationshipID" or "AssignLabels" or "RemoveLabels" or "CreateLabel" or "DeleteLabel" or "EditLabel" or "List" or "ListStations" or "ListStationItems" or "ListIncludingContainers" or "GetAssetWorth")
            return 10;
        if (service == "fleetObject" && method is "SendBroadcast" or "Invite" or "MassInvite" or "Init" or "GetInitState" or "GetFleetComposition" or "GetJoinRequests")
            return 10;
        if (service == "corpRecProxy" && method is "SearchCorpAds")
            return 10;
        if (service == "structureDirectory" && method is "CheckMyDockingAccessToStructures" or "GetMyDockableStructures" or "GetMyCharacterStructures" or "GetMyCorporationStructures")
            return 10;
        if (service == "structureJumpBridgeMgr" && method is "GetJbStructureDestination" or "GetLinkedStructure")
            return 10;
        if (service == "officeManager" && method is "HasCorpImpoundedItems" or "GetImpoundReleasePrice" or "RentOffice" or "UnrentOffice" or "GetItemsFromImpound" or "TrashImpoundedOffice")
            return 10;
        if (service == "stationSvc" && method is ("GetCorporationsWithOffices" or "GetEmptyOfficeCount" or "HasCorpImpoundedItems" or "GetImpoundReleasePrice" or "TrashImpoundedOffice" or "PrimeOfficeItem" or "UnrentOffice"))
            return 10;
        if (service == "stationSvc" && method == "GetGuests")
            return 10;
        if (service == "skillHandler" && method is "InjectSkillIntoBrain" or "InjectSkillpoints" or "ExtractSkills" or "GetDiminishedSpFromInjectors" or "SplitSkillInjector" or "CombineSkillInjector")
            return 10;
        if (service == "accessGroupBookmarkMgr" && method is "GetMyActiveBookmarks" or "SearchFoldersWithAdminAccess" or "DeleteBookmarks" or "MoveBookmarksToFolderAndSubfolder" or "CopyBookmarksToFolderAndSubfolder")
            return 10;
        return 0;
    }

    private static string? NestedBoundMethod(PyValue value)
    {
        var items = value switch
        {
            PyTuple tuple => tuple.Items,
            PyList list => list.Items,
            _ => Array.Empty<PyValue>(),
        };
        return items.Count > 0 ? JsSemantics.StringValue(items[0]) : null;
    }

    private sealed class ExpensiveRpcBudget
    {
        private const long WindowMilliseconds = 60_000;
        private readonly object _gate = new();
        private long _windowStartedAt;
        private int _used;

        public bool TryConsume(long now, int cost, int limit)
        {
            lock (_gate)
            {
                if (_windowStartedAt == 0 || now < _windowStartedAt || now - _windowStartedAt >= WindowMilliseconds)
                {
                    _windowStartedAt = now;
                    _used = 0;
                }
                if (limit <= 0 || cost > limit - _used)
                    return false;
                _used += cost;
                return true;
            }
        }
    }

    private void FailStrictly(MachoPacket packet, ClientSession session, string? serviceName, CallRequest call, string kind, string detail, Exception? error = null)
    {
        var correlation = RpcDiagnostics.CorrelationId();
        Console.Error.WriteLine(
            $"[strict-rpc] {correlation} session={session.SessionId} {kind}: {detail} at {serviceName ?? "<none>"}.{call.Method} args={RpcDiagnostics.Args(call.Args)}");
        if (error is not null)
            Console.Error.WriteLine(error.ToString());
        SendError(packet, MachoErrors.UserError("CustomNotify", ("notify", new PyStr($"Server error {correlation}: {kind}: {detail}"))), session, serviceName);
    }

    private static void SendCallResponse(MachoPacket packet, PyValue result, ClientSession session, string? serviceName) =>
        session.Send(ResponseBuilder.CallResponse(packet, result, session.Get("userid"), serviceName));

    private static void SendProvisional(MachoPacket packet, ClientSession session, string? serviceName) =>
        session.Send(ResponseBuilder.CallResponse(packet, new PyTuple([]), session.Get("userid"), serviceName, new CallResponseOptions
        {
            WrapResultInSubstream = false,
            NamedPayloadEntries = [new(new PyStr("provisional"), Py.Tuple(new PyInt(86400), new PyStr("OnAgentProvisionalResponse"), new PyTuple([])))],
        }));

    private static PyValue? ClientAnswer(MachoPacket packet)
    {
        var body = packet.Payload is PyTuple { Items.Count: 1 } tuple ? tuple.Items[0] : packet.Payload;
        var value = body is PySubStream stream ? stream.Decoded : body;
        return value is PyNone ? null : value;
    }

    private static void SendError(MachoPacket packet, MachoError error, ClientSession session, string? serviceName) =>
        session.Send(ResponseBuilder.ErrorResponse(packet, error.ErrorCode, error.Payload, session.Get("userid"), serviceName, error.Detail));

    /// <summary>
    /// A result that hands out a bound object (<c>[substruct(substream((oid, refID))), callResult]</c>, or the
    /// substruct itself) registers the object so later calls to its OID reach this service.
    /// </summary>
    private void RegisterBoundObjects(PyValue result, IService service, ClientSession session, PyValue? bindParameters)
    {
        if (!JsSemantics.Truthy(result))
        {
            return;
        }
        try
        {
            if (result is PyTuple tuple)
            {
                foreach (var element in tuple.Items)
                {
                    TryRegister(element, service, session, bindParameters);
                }
            }
            TryRegister(result, service, session, bindParameters);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            OnProblem?.Invoke($"registering a bound object from a call result failed: {ex.Message}", ex);
        }
    }

    private void TryRegister(PyValue value, IService service, ClientSession session, PyValue? bindParameters)
    {
        if (value is PySubStruct substruct && BoundObjects.TryRegistration(substruct, out var registration))
        {
            _registry.RegisterBoundObject(registration.Oid, service, registration.RefId);
            if (bindParameters is not null)
            {
                _registry.RegisterBoundObjectParams(registration.Oid, bindParameters);
            }
            session.RememberBoundObject(registration.Oid);
        }
    }

    // ── notifications ───────────────────────────────────────────────────────

    private void HandleNotification(MachoPacket packet, ClientSession session)
    {
        var carriers = new[] { packet.NamedPayload, packet.Oob };
        var hasReleaseMetadata = carriers.Any(carrier =>
            JsSemantics.DictEntries(carrier).Any(entry => JsSemantics.ToJsString(entry.Key) == ReleaseKey));
        var notification = hasReleaseMetadata ? CallRequest.Decode(packet.Payload) : null;
        if (notification is null || notification.Method != ReleaseMethod)
        {
            return;
        }

        var released = new HashSet<string>(StringComparer.Ordinal);
        var ordered = new List<string>();
        void Collect(PyValue? value) => BoundObjects.CollectIds(value, oid =>
        {
            if (released.Add(oid))
            {
                ordered.Add(oid);
            }
        });

        Collect(notification.RemoteObject);
        foreach (var carrier in carriers)
        {
            foreach (var (key, value) in JsSemantics.DictEntries(carrier))
            {
                if (JsSemantics.ToJsString(key) == ReleaseKey)
                {
                    Collect(value);
                }
            }
        }

        var refIds = ReleasedRefIds(carriers);
        foreach (var oid in ordered)
        {
            _registry.UnregisterBoundObject(oid, session, refIds.GetValueOrDefault(oid));
        }
    }

    /// <summary>
    /// <c>OID-</c> maps each released object to the refID of the newest bind the client still knew about.
    /// Objects named only by the notification's remote object carry no refID.
    /// </summary>
    private static Dictionary<string, long?> ReleasedRefIds(IEnumerable<PyValue> carriers)
    {
        var refIds = new Dictionary<string, long?>(StringComparer.Ordinal);
        foreach (var carrier in carriers)
        {
            foreach (var (key, value) in JsSemantics.DictEntries(carrier))
            {
                if (JsSemantics.ToJsString(key) != ReleaseKey)
                {
                    continue;
                }
                foreach (var (objectId, refId) in JsSemantics.DictEntries(value))
                {
                    if (JsSemantics.PlainText(objectId) is { } oid && oid.StartsWith(BoundObjects.OidPrefix, StringComparison.Ordinal))
                    {
                        refIds[oid] = NormalizeRefId(refId);
                    }
                }
            }
        }
        return refIds;
    }

    private static long? NormalizeRefId(PyValue value) => value switch
    {
        PyLong number => number.Value,
        PyInt number => number.Value,
        PyReal number when double.IsFinite(number.Value) => (long)Math.Truncate(number.Value),
        _ => null,
    };
}
