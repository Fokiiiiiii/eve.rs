using Eve.Characters;
using Eve.Identity;
using Eve.Inventory;
using Eve.Macho;
using Eve.Marshal;
using Eve.Skills;
using Eve.Wallet;

namespace Eve.Services.CharacterSelect;

/// <summary>
/// The character selection screen: what the account has, what a new character may be called, making one, and
/// choosing which to play (<c>charUnboundMgr</c>). Choosing puts the character in the session, and the client learns
/// of it through one session change notification.
/// </summary>
[Service("charUnboundMgr")]
public sealed partial class CharUnboundMgrService(
    IAccountLookup accounts,
    PgCharacterStore characters,
    PgItemStore items,
    CharacterTraining training,
    PgWalletStore wallets,
    CharacterCreation creation,
    CreationCatalog catalog,
    OnlineCharacters online,
    IReadOnlyList<ICharacterEntryHook> entryHooks,
    Random random,
    bool newCharacterExperienceEnabled = false,
    Func<long, CancellationToken, Task>? sendWelcomeMailAsync = null,
    Func<long, int>? unreadMailCount = null,
    Func<long, CancellationToken, Task<double>>? securityStatusOf = null)
{
    /// <summary>What every session's role gets on top of the account's (the client shows staff roles and the base bits alike).</summary>
    public const long SessionBaseRole = 0x6000000080000000;

    /// <summary>The corporation account key every character starts with.</summary>
    private const int DefaultCorpAccountKey = 1000;

    /// <summary>Until the subscription module exists everyone is subscribed far into the future, as on the reference (year 2400).</summary>
    private const long SubscriptionEndFileTime = 157_469_184_000_000_000;

    /// <summary>Where account creation dates begin on the reference (2019), used when an account is unknown.</summary>
    private const long FallbackCreationFileTime = 132_000_000_000_000_000;

    private static readonly PyValue None = PyNone.Instance;

    private static CancellationToken Token(ServiceCall call) => call.Cancellation;

    private static int? AccountOf(ServiceCall call) => call.Session.UserId is { } id ? checked((int)id) : null;

    private static long FileTime(DateTimeOffset time) => Py.FileTimeFromUnixMs(time.ToUnixTimeMilliseconds());

    internal static int FinishedSkillsCount(IReadOnlyList<HistoryEntry> history)
    {
        var finished = history.Where(entry => entry.EventType is SkillEvents.TrainingComplete or SkillEvents.QueueTrainingCompleted).ToArray();
        var latest = finished.Select(entry => (long?)entry.LogDate).Max();
        return latest is { } at ? finished.Count(entry => entry.LogDate == at) : 0;
    }

    [Method("GetNumCharacters")]
    public async ValueTask<PyValue> GetNumCharacters(ServiceCall call) =>
        new PyInt(AccountOf(call) is { } account ? await characters.CountForAccountAsync(account, Token(call)) : 0);

    [Method("GetCharactersToSelect")]
    public async ValueTask<PyValue> GetCharactersToSelect(ServiceCall call)
    {
        var list = AccountOf(call) is { } account ? await characters.ForAccountAsync(account, Token(call)) : [];
        return new PyList([.. list.Select(SelectRow)]);
    }

    [Method("GetCharacterInfo")]
    public async ValueTask<PyValue> GetCharacterInfo(ServiceCall call)
    {
        var characterId = RequestCharacterId(call);
        var account = AccountOf(call);
        if (characterId <= 0)
        {
            var list = account is { } accountId ? await characters.ForAccountAsync(accountId, Token(call)) : [];
            return new PyList([.. list.Select(SelectRow)]);
        }
        if (account is not { } owner)
        {
            return None;
        }

        var character = await characters.GetAsync(characterId, Token(call));
        if (character is null || character.AccountId != owner)
        {
            return None;
        }
        var balances = await wallets.ForOwnersAsync([character.Id], Token(call));
        return await SelectionRow(character, balances.GetValueOrDefault(character.Id), Token(call));
    }

    /// <summary>
    /// <c>(account rows, (None, None), character rows, [])</c>. The rows carry values that belong to modules that do not
    /// exist yet (mail): they are the reference's for a new character, and are marked below.
    /// </summary>
    [Method("GetCharacterSelectionData")]
    public async ValueTask<PyValue> GetCharacterSelectionData(ServiceCall call)
    {
        var account = AccountOf(call);
        var list = account is { } id ? await characters.ForAccountAsync(id, Token(call)) : [];
        var info = account is { } known ? await accounts.FindAsync(checked((uint)known), Token(call)) : null;
        var balances = await wallets.ForOwnersAsync(list.Select(c => c.Id), Token(call));
        var rows = new List<PyValue>();
        foreach (var character in list)
        {
            rows.Add(await SelectionRow(character, balances.GetValueOrDefault(character.Id), Token(call)));
        }
        return Py.Tuple(
            new PyList(info is null ? [] : [AccountRow(info)]),
            Py.Tuple(None, None),
            new PyList(rows),
            new PyList([]));
    }

    [Method("GetCohortsForUser")]
    public PyValue GetCohortsForUser(ServiceCall call) => new PyList([]);

    [Method("GetQAStarterSystemIDs")]
    public PyValue GetQAStarterSystemIDs(ServiceCall call) => new PyList([
        .. CreationCatalog.Schools
            .Select(school => catalog.StarterFor(school.CorporationId)?.SolarSystemId)
            .Where(systemId => systemId is > 0)
            .Select(systemId => systemId!.Value)
            .Distinct()
            .OrderBy(systemId => systemId)
            .Select(systemId => (PyValue)new PyInt(systemId)),
    ]);

    [Method("GetCharacterLockType")]
    public PyValue GetCharacterLockType(ServiceCall call) => None;

    /// <summary>The client asks to turn name validation on; only staff may turn it off, so it is always on.</summary>
    [Method("ToggleValidation")]
    public PyValue ToggleValidation(ServiceCall call) => PyBool.False;

    [Method("GetValidRandomName")]
    public async ValueTask<PyValue> GetValidRandomName(ServiceCall call)
    {
        var race = Int(call.Arg(0));
        string name;
        var attempts = 0;
        do
        {
            name = RandomNames.Generate(race, random);
        }
        while (++attempts < 20 && await characters.NameTakenAsync(name, Token(call)));
        return new PyStr(name);
    }

    [Method("ValidateNameEx")]
    public async ValueTask<PyValue> ValidateNameEx(ServiceCall call)
    {
        var name = JsSemantics.StringValue(call.Arg(0));
        var check = CharacterNames.Validate(name);
        if (check == NameCheck.Valid && await characters.NameTakenAsync(name!, Token(call)))
        {
            check = NameCheck.Unavailable;
        }
        return new PyInt((int)check);
    }

    [Method("UpdateCharacterGender")]
    public async ValueTask<PyValue> UpdateCharacterGender(ServiceCall call)
    {
        var characterId = Long(call.Arg(0));
        var character = await characters.GetAsync(characterId, Token(call));
        if (character is null)
        {
            return None;
        }
        if (AccountOf(call) != character.AccountId)
        {
            throw Info("That character is not available on this account.");
        }

        var requestedGender = IntOrDefault(call.Arg(1), 1);
        var gender = requestedGender is >= 0 and <= 2 ? requestedGender : 1;
        await characters.SetGenderAsync(characterId, gender, Token(call));
        if (IsCurrentCharacter(call, characterId))
        {
            call.Session.Set("genderID", new PyInt(gender));
            call.Session.Set("genderid", new PyInt(gender));
        }
        return None;
    }

    [Method("UpdateCharacterBloodline")]
    public async ValueTask<PyValue> UpdateCharacterBloodline(ServiceCall call)
    {
        var characterId = Long(call.Arg(0));
        var character = await characters.GetAsync(characterId, Token(call));
        if (character is null)
        {
            return None;
        }
        if (AccountOf(call) != character.AccountId)
        {
            throw Info("That character is not available on this account.");
        }

        var requestedBloodlineId = IntOrDefault(call.Arg(1), 1);
        var bloodline = catalog.Bloodline(requestedBloodlineId);
        var bloodlineId = requestedBloodlineId != 0 ? requestedBloodlineId : character.BloodlineId;
        var raceId = bloodline?.RaceId ?? character.RaceId;
        var typeId = CreationCatalog.CharacterTypeId(requestedBloodlineId, character.TypeId);
        await characters.SetBloodlineAsync(characterId, bloodlineId, raceId, typeId, Token(call));
        if (IsCurrentCharacter(call, characterId))
        {
            call.Session.Set("bloodlineID", new PyInt(bloodlineId));
            call.Session.Set("bloodlineid", new PyInt(bloodlineId));
            call.Session.Set("raceID", new PyInt(raceId));
            call.Session.Set("raceid", new PyInt(raceId));
            call.Session.Set("characterTypeID", new PyInt(typeId));
        }
        return None;
    }

    /// <summary><c>(name, raceID, bloodlineID, genderID, ancestryID, charInfo, portraitInfo, schoolID, None, qaStarter)</c>: the new character's id.</summary>
    [Method("CreateCharacterWithDoll")]
    public async ValueTask<PyValue> CreateCharacterWithDoll(ServiceCall call)
    {
        if (AccountOf(call) is not { } account)
        {
            throw MachoErrors.ThrowUserError("CustomInfo", ("info", new PyStr("Log in before creating a character.")));
        }
        var request = new CreationRequest(
            account,
            JsSemantics.StringValue(call.Arg(0)) ?? "",
            Int(call.Arg(1)),
            Int(call.Arg(2)),
            Int(call.Arg(3)),
            Int(call.Arg(4)),
            Int(call.Arg(7)),
            Appearance(call.Arg(5)),
            Appearance(call.Arg(6)));
        var outcome = await creation.CreateAsync(request, Token(call));
        if (outcome is Created newCharacter && sendWelcomeMailAsync is not null)
            await sendWelcomeMailAsync(newCharacter.CharacterId, Token(call));
        return outcome switch
        {
            Created created => new PyInt(created.CharacterId),
            CreationRefused { Reason: CreationRefusal.InvalidName or CreationRefusal.NameTaken } => throw MachoErrors.ThrowUserError("CharNameInvalid"),
            CreationRefused { Reason: CreationRefusal.SlotsFull } => throw Info("You have no free character slot."),
            _ => throw Info("That character cannot be created."),
        };
    }

    [Method("SelectCharacterID")]
    public async ValueTask<PyValue> SelectCharacterID(ServiceCall call)
    {
        var skipTutorial = call.Args.Count > 2
            ? BooleanLike(call.Arg(2))
            : BooleanLike(call.Kwarg("skipTutorial") ?? None);
        var characterId = RequestCharacterId(call);
        if (AccountOf(call) is not { } account || characterId <= 0)
            return None;

        var character = await characters.GetAsync(characterId, Token(call));
        if (character is null)
            return None;
        if (character.AccountId != account)
            throw Info("That character is not available on this account.");
        if (character.IsQueuedForDeletion)
            throw Info("That character is queued for deletion and cannot enter the game.");

        var previousCharacterId = call.Session.CharacterId;
        if (!online.TryBeginEnter(character.Id, call.Session.SessionId))
            throw Info($"{character.Name} is already online.");

        var changedPreviousHooks = new List<ICharacterEntryHook>();
        var enteredCharacterHooks = new List<ICharacterEntryHook>();
        var previousAttributes = new Dictionary<string, PyValue>(StringComparer.Ordinal);
        var sessionChangeStarted = false;
        try
        {
            var accountRole = call.Session.Get("accountRole") is PyLong role ? role.Value : 0;
            (string Name, PyValue Value)[] selected =
            [
                ("charid", new PyInt(character.Id)),
                ("corpid", new PyInt(character.CorporationId)),
                ("allianceid", None),
                ("genderID", new PyInt(character.Gender)),
                ("bloodlineID", new PyInt(character.BloodlineId)),
                ("raceID", new PyInt(character.RaceId)),
                ("schoolID", new PyInt(character.SchoolId)),
                ("factionid", character.FactionId is { } faction ? new PyInt(faction) : None),
                ("warfactionid", None),
                ("stationid", new PyInt(character.StationId)),
                ("stationid2", new PyInt(character.StationId)),
                ("structureid", None),
                ("solarsystemid", None),
                ("solarsystemid2", new PyInt(character.SolarSystemId)),
                ("constellationid", new PyInt(character.ConstellationId)),
                ("regionid", new PyInt(character.RegionId)),
                ("shipid", character.ShipId is { } ship ? new PyLong(ship) : None),
                ("locationid", new PyInt(character.StationId)),
                ("worldspaceid", None),
                ("homeStationID", new PyInt(character.HomeStationId)),
                ("cloneStationID", new PyInt(character.HomeStationId)),
                ("fleetid", None),
                ("fleetrole", None),
                ("wingid", None),
                ("squadid", None),
                ("corpAccountKey", new PyInt(DefaultCorpAccountKey)),
                ("hqID", None),
                ("baseID", None),
                ("role", new PyLong(accountRole | SessionBaseRole)),
                ("corprole", new PyLong(0)),
                ("rolesAtAll", new PyLong(0)),
                ("rolesAtBase", new PyLong(0)),
                ("rolesAtHQ", new PyLong(0)),
                ("rolesAtOther", new PyLong(0)),
            ];
            var attributes = selected.ToDictionary(attribute => attribute.Name, attribute => attribute.Value, StringComparer.Ordinal);
            foreach (var hook in entryHooks)
            {
                foreach (var (name, value) in await hook.GetSessionAttributesAsync(call.Session, character.Id, Token(call)))
                    attributes[name] = value;
            }
            if (attributes.GetValueOrDefault("deferredDockedShipId") is PyLong deferredShipId && deferredShipId.Value > 0)
                attributes["shipid"] = None;
            foreach (var name in attributes.Keys)
                previousAttributes[name] = call.Session.Get(name) ?? None;

            int? airNpeStateChanged = null;
            if (skipTutorial)
                airNpeStateChanged = await characters.MarkTutorialSkippedAsync(character.Id, newCharacterExperienceEnabled, Token(call));
            else
                await characters.ConsumeTutorialRevealOnFirstLoginAsync(character.Id, newCharacterExperienceEnabled, Token(call));

            var changingCharacter = previousCharacterId != character.Id;
            if (changingCharacter && previousCharacterId is { } oldCharacterId)
            {
                foreach (var hook in entryHooks)
                {
                    changedPreviousHooks.Add(hook);
                    await hook.OnCharacterLeavingAsync(call.Session, oldCharacterId, Token(call));
                }
            }
            if (changingCharacter)
                call.Session.SendNotification("OnCharacterSelected", "clientID");

            sessionChangeStarted = true;
            var announced = call.Session.Change([.. attributes.Select(attribute => (attribute.Key, attribute.Value))]);
            if (announced.Any(change => change.Name == "charid"))
            {
                foreach (var hook in entryHooks)
                {
                    enteredCharacterHooks.Add(hook);
                    await hook.OnCharacterEnteredAsync(call.Session, character.Id, Token(call));
                }
            }

            AirNpeNotifications.NotifyStateChanged(call.Session, character.Id, airNpeStateChanged);
            online.CompleteEnter(call.Session.SessionId);
            return None;
        }
        catch
        {
            // Preserve the entry exception while each affected runtime gets a cleanup attempt.
            for (var index = enteredCharacterHooks.Count - 1; index >= 0; index--)
            {
                try
                {
                    await enteredCharacterHooks[index].OnCharacterLeavingAsync(call.Session, character.Id, CancellationToken.None);
                }
                catch
                {
                }
            }

            if (sessionChangeStarted)
            {
                try
                {
                    call.Session.Change([.. previousAttributes.Select(attribute => (attribute.Key, attribute.Value))]);
                }
                catch
                {
                }
            }

            online.CancelEnter(call.Session.SessionId);
            if (previousCharacterId is { } restoreCharacterId)
            {
                for (var index = changedPreviousHooks.Count - 1; index >= 0; index--)
                {
                    try
                    {
                        await changedPreviousHooks[index].OnCharacterEnteredAsync(call.Session, restoreCharacterId, CancellationToken.None);
                    }
                    catch
                    {
                    }
                }
            }
            throw;
        }
    }

    [Method("CancelCharacterDeletePrepare")]
    public async ValueTask<PyValue> CancelCharacterDeletePrepare(ServiceCall call)
    {
        var characterId = RequestCharacterId(call);
        var character = await characters.GetAsync(characterId, Token(call));
        if (character is null)
        {
            throw Info("That character no longer exists.");
        }
        if (AccountOf(call) is not { } account)
        {
            throw Info("Character deletion requires a valid authenticated account.");
        }
        if (character.AccountId != account)
        {
            throw Info("That character is not available on this account.");
        }
        if (online.IsOnline(characterId))
        {
            throw Info("That character is currently online and cannot be deleted.");
        }
        if (character.IsQueuedForDeletion && !await characters.ClearDeletePrepareAsync(characterId, account, Token(call)))
        {
            throw Info("That character no longer exists.");
        }
        return None;
    }

    private static MachoWrappedException Info(string text) => MachoErrors.ThrowUserError("CustomInfo", ("info", new PyStr(text)));

    private static long RequestCharacterId(ServiceCall call)
    {
        var positional = Long(call.Arg(0));
        if (positional > 0)
        {
            return positional;
        }
        return Long(call.Kwarg("charID") ?? call.Kwarg("characterID") ?? None);
    }

    private static bool IsCurrentCharacter(ServiceCall call, long characterId) =>
        call.Session.CharacterId == characterId ||
        Long(call.Session.Get("charid")) == characterId ||
        Long(call.Session.Get("characterID")) == characterId;

    private static long Long(PyValue? value) => value switch
    {
        PyInt number => number.Value,
        PyLong number => number.Value,
        _ => 0,
    };

    private static bool BooleanLike(PyValue? value)
    {
        if (value is PyBool boolean)
        {
            return boolean.Value;
        }
        if (value is PyInt integer)
        {
            return integer.Value != 0;
        }
        if (value is PyLong integer64)
        {
            return integer64.Value != 0;
        }
        if (value is PyReal real)
        {
            return real.Value != 0;
        }
        return JsSemantics.StringValue(value)?.Trim().ToLowerInvariant() switch
        {
            "true" or "1" or "yes" => true,
            _ => false,
        };
    }

    private static int Int(PyValue value) => value switch
    {
        PyInt number => (int)Math.Clamp(number.Value, int.MinValue, int.MaxValue),
        PyLong number => (int)Math.Clamp(number.Value, int.MinValue, int.MaxValue),
        PyReal number => (int)Math.Clamp(number.Value, int.MinValue, int.MaxValue),
        _ => 0,
    };

    private static int IntOrDefault(PyValue value, int fallback) => value is PyInt or PyLong or PyReal ? Int(value) : fallback;

    /// <summary>The paper doll or portrait the client sent, kept as it was sent.</summary>
    private static byte[]? Appearance(PyValue value) => value is PyNone ? null : MarshalCodec.Encode(value);

    private static PyValue SelectRow(Character c) => Py.KeyVal(
        ("characterID", new PyInt(c.Id)),
        ("characterName", new PyStr(c.Name)),
        ("deletePrepareDateTime", c.DeletePrepareAt is { } at ? new PyLong(FileTime(at)) : None),
        ("gender", new PyInt(c.Gender)),
        ("typeID", new PyInt(c.TypeId)));

    private PyValue AccountRow(AccountInfo info) => Py.KeyVal(
        ("characterSlots", new PyInt(CharacterCreation.SlotsPerAccount)),
        ("userName", new PyStr(info.UserName)),
        ("creationDate", new PyLong(info.CreatedAt == default ? FallbackCreationFileTime : FileTime(info.CreatedAt))),
        ("subscriptionEndTime", new PyLong(SubscriptionEndFileTime)),
        ("maxCharacterSlots", new PyInt(CharacterCreation.SlotsPerAccount)));

    private async Task<PyValue> SelectionRow(Character c, Balance? balance, CancellationToken ct)
    {
        var ship = c.ShipId is { } shipId ? await items.GetAsync(shipId, ct) : null;
        var trainingView = await training.ViewAsync(c.Id, c.AccountId, ct);
        var currentTraining = trainingView.Current;
        var trainingQueueEnd = currentTraining is null ? null : trainingView.Queue.LastOrDefault()?.End;
        var trainedSkillPoints = currentTraining is null
            ? null
            : trainingView.Skills.FirstOrDefault(skill => skill.TypeId == currentTraining.TypeId)?.SkillPoints;
        var finishedSkills = FinishedSkillsCount(await training.HistoryAsync(c.Id, ct));
        var securityStatus = securityStatusOf is null ? 0d : await securityStatusOf(c.Id, ct);
        var logoff = c.LastLogoffAt ?? c.CreatedAt;
        return Py.KeyVal(
            ("characterID", new PyInt(c.Id)),
            ("characterName", new PyStr(c.Name)),
            ("deletePrepareDateTime", c.DeletePrepareAt is { } at ? new PyLong(FileTime(at)) : None),
            ("gender", new PyInt(c.Gender)),
            ("typeID", new PyInt(c.TypeId)),
            ("raceID", new PyInt(c.RaceId)),
            ("bloodlineID", new PyInt(c.BloodlineId)),
            ("ancestryID", new PyInt(c.AncestryId)),
            ("schoolID", new PyInt(c.SchoolId)),
            ("corporationID", new PyInt(c.CorporationId)),
            ("allianceID", c.AllianceId is { } alliance ? new PyInt(alliance) : None),
            // The reference shows no faction here although the session has one.
            ("factionID", None),
            ("stationID", new PyInt(c.StationId)),
            ("solarSystemID", new PyInt(c.SolarSystemId)),
            ("constellationID", new PyInt(c.ConstellationId)),
            ("regionID", new PyInt(c.RegionId)),
            ("balance", new PyReal((double)(balance?.Isk ?? 0m))),
            ("plexBalance", new PyInt(balance?.Plex ?? 0)),
            // Gap: the wallet has no journal yet, so nothing has changed the balance.
            ("balanceChange", new PyReal(0.0)),
            ("skillPoints", new PyInt(trainingView.TotalPoints)),
            ("shipTypeID", ship is null ? None : new PyInt(ship.TypeId)),
            ("shipName", new PyStr(ship is null ? "" : ship.CustomName ?? catalog.ShipName(ship.TypeId))),
            ("securityRating", Py.Number(securityStatus)),
            ("securityStatus", Py.Number(securityStatus)),
            ("title", new PyStr("")),
            ("unreadMailCount", new PyInt(Math.Max(0, unreadMailCount?.Invoke(c.Id) ?? 0))),
            ("paperdollState", new PyInt(0)),
            ("lockTypeID", None),
            ("logoffDate", new PyLong(FileTime(logoff))),
            ("skillTypeID", currentTraining is null ? None : new PyInt(currentTraining.TypeId)),
            ("toLevel", currentTraining is null ? None : new PyInt(currentTraining.ToLevel)),
            ("trainingStartTime", currentTraining?.Start is { } trainingStart ? new PyLong(trainingStart) : None),
            ("trainingEndTime", currentTraining?.End is { } trainingEnd ? new PyLong(trainingEnd) : None),
            ("queueEndTime", trainingQueueEnd is { } queueEnd ? new PyLong(queueEnd) : None),
            ("finishSP", currentTraining is null ? None : new PyInt(currentTraining.DestinationPoints)),
            ("trainedSP", trainedSkillPoints is { } trained ? new PyInt(trained) : None),
            // ponytail: history has no settle-batch id, so separate completion batches with the same FILETIME merge.
            ("finishedSkills", new PyInt(finishedSkills)));
    }
}
