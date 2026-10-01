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
    PgSkillStore skills,
    PgWalletStore wallets,
    CharacterCreation creation,
    CreationCatalog catalog,
    OnlineCharacters online,
    IReadOnlyList<ICharacterEntryHook> entryHooks,
    Random random)
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

    [Method("GetNumCharacters")]
    public async ValueTask<PyValue> GetNumCharacters(ServiceCall call) =>
        new PyInt(AccountOf(call) is { } account ? await characters.CountForAccountAsync(account, Token(call)) : 0);

    [Method("GetCharactersToSelect")]
    public async ValueTask<PyValue> GetCharactersToSelect(ServiceCall call)
    {
        var list = AccountOf(call) is { } account ? await characters.ForAccountAsync(account, Token(call)) : [];
        return new PyList([.. list.Select(SelectRow)]);
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
        var points = await skills.TotalPointsAsync(list.Select(c => c.Id), Token(call));
        var balances = await wallets.ForOwnersAsync(list.Select(c => c.Id), Token(call));
        var rows = new List<PyValue>();
        foreach (var character in list)
        {
            rows.Add(await SelectionRow(character, points[character.Id], balances.GetValueOrDefault(character.Id), Token(call)));
        }
        return Py.Tuple(
            new PyList(info is null ? [] : [AccountRow(info)]),
            Py.Tuple(None, None),
            new PyList(rows),
            new PyList([]));
    }

    [Method("GetCohortsForUser")]
    public PyValue GetCohortsForUser(ServiceCall call) => new PyList([]);

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
        if (AccountOf(call) is not { } account || call.Arg(0) is not (PyInt or PyLong))
        {
            return None;
        }
        var characterId = call.Arg(0) is PyLong wide ? wide.Value : ((PyInt)call.Arg(0)).Value;
        var character = await characters.GetAsync(characterId, Token(call));
        if (character is null)
        {
            return None;
        }
        if (character.AccountId != account)
        {
            throw Info("That character is not available on this account.");
        }
        if (character.IsQueuedForDeletion)
        {
            throw Info("That character is queued for deletion and cannot enter the game.");
        }
        if (!online.TryEnter(character.Id, call.Session.SessionId))
        {
            throw Info($"{character.Name} is already online.");
        }

        var accountRole = call.Session.Get("accountRole") is PyLong r ? r.Value : 0;
        (string Name,PyValue Value)[] selected = [
            ("charid", new PyInt(character.Id)),
            ("corpid", new PyInt(character.CorporationId)),
            ("genderID", new PyInt(character.Gender)),
            ("bloodlineID", new PyInt(character.BloodlineId)),
            ("raceID", new PyInt(character.RaceId)),
            ("schoolID", new PyInt(character.SchoolId)),
            ("factionid", character.FactionId is { } faction ? new PyInt(faction) : None),
            ("stationid", new PyInt(character.StationId)),
            ("stationid2", new PyInt(character.StationId)),
            ("solarsystemid2", new PyInt(character.SolarSystemId)),
            ("constellationid", new PyInt(character.ConstellationId)),
            ("regionid", new PyInt(character.RegionId)),
            ("shipid", character.ShipId is { } ship ? new PyLong(ship) : None),
            ("locationid", new PyInt(character.StationId)),
            ("corpAccountKey", new PyInt(DefaultCorpAccountKey)),
            ("hqID", None),
            ("role", new PyLong(accountRole | SessionBaseRole)),
            ("corprole", new PyLong(0)),
            ("rolesAtAll", new PyLong(0)),
            ("rolesAtBase", new PyLong(0)),
            ("rolesAtHQ", new PyLong(0)),
            ("rolesAtOther", new PyLong(0))];
        var attributes = selected.ToDictionary(a => a.Name,a => a.Value,StringComparer.Ordinal);
        foreach (var hook in entryHooks)
            foreach (var (name,value) in await hook.GetSessionAttributesAsync(call.Session,character.Id,Token(call)))
                attributes[name] = value;
        var announced = call.Session.Change([.. attributes.Select(a => (a.Key,a.Value))]);
        // Whatever else the client needs to learn about the character: the skills module tells it to load its skills.
        // Choosing the character the session already has is not entering it again, as on the reference.
        if (announced.Any(change => change.Name == "charid"))
        {
            foreach (var hook in entryHooks)
            {
                await hook.OnCharacterEnteredAsync(call.Session, character.Id, Token(call));
            }
        }
        return None;
    }

    private static MachoWrappedException Info(string text) => MachoErrors.ThrowUserError("CustomInfo", ("info", new PyStr(text)));

    private static int Int(PyValue value) => value switch
    {
        PyInt number => (int)Math.Clamp(number.Value, int.MinValue, int.MaxValue),
        PyLong number => (int)Math.Clamp(number.Value, int.MinValue, int.MaxValue),
        PyReal number => (int)Math.Clamp(number.Value, int.MinValue, int.MaxValue),
        _ => 0,
    };

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

    private async Task<PyValue> SelectionRow(Character c, long skillPoints, Balance? balance, CancellationToken ct)
    {
        var ship = c.ShipId is { } shipId ? await items.GetAsync(shipId, ct) : null;
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
            ("skillPoints", new PyInt(skillPoints)),
            ("shipTypeID", ship is null ? None : new PyInt(ship.TypeId)),
            ("shipName", new PyStr(ship is null ? "" : catalog.ShipName(ship.TypeId))),
            ("securityRating", new PyInt(0)),
            ("securityStatus", new PyInt(0)),
            ("title", new PyStr("")),
            // Gap: mail belongs to a module that does not exist yet; this is the reference's value for a new character.
            ("unreadMailCount", new PyInt(1)),
            ("paperdollState", new PyInt(0)),
            ("lockTypeID", None),
            ("logoffDate", new PyLong(FileTime(logoff))),
            ("skillTypeID", None),
            ("toLevel", None),
            ("trainingStartTime", None),
            ("trainingEndTime", None),
            ("queueEndTime", None),
            ("finishSP", None),
            ("trainedSP", None),
            ("finishedSkills", new PyInt(0)));
    }
}
