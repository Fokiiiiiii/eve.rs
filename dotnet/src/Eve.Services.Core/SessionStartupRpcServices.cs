using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Ships;
using System.Security.Cryptography;

namespace Eve.Services.Core;

[Service("chatAuthenticationService")]
public sealed partial class ChatAuthenticationService(
    Func<long, CancellationToken, ValueTask<string?>>? tokenByCharacter = null,
    Func<ClientSession, long, CancellationToken, ValueTask<string?>>? tokenProvider = null)
{
    [Method("GetAuthenticationToken")]
    public async ValueTask<PyValue> GetAuthenticationToken(ServiceCall call)
    {
        call.Cancellation.ThrowIfCancellationRequested();
        if (call.Args.Count != 0 || call.Kwargs is not PyNone)
            throw MachoErrors.ThrowUserError("CHAT_AUTHENTICATION_ARGUMENTS_INVALID");
        var characterId = call.Session.CharacterId ?? 0;
        var token = tokenByCharacter is { } readToken
            ? await readToken(characterId, call.Cancellation)
            : null;
        if (string.IsNullOrEmpty(token)) token = StoredToken(call.Session, characterId);
        if (string.IsNullOrEmpty(token) && tokenProvider is { } provideToken)
            token = await provideToken(call.Session, characterId, call.Cancellation);
        if (string.IsNullOrEmpty(token) && characterId > 0)
        {
            token = Text(call.Session.Get("chatAuthenticationToken"));
            if (string.IsNullOrEmpty(token))
            {
                token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
                call.Session.Set("chatAuthenticationToken", new PyStr(token));
            }
        }
        return string.IsNullOrEmpty(token) ? PyNone.Instance : new PyStr(token);
    }

    /// <summary>The token the session already holds for the character (issued earlier or configured on the session), or null. The XMPP login checks against it.</summary>
    public static string? StoredToken(ClientSession session, long characterId)
    {
        var token = Text(session.Get("chatAuthenticationToken"));
        if (string.IsNullOrEmpty(token)) token = Text(session.Get("xmppAuthenticationToken"));
        if (string.IsNullOrEmpty(token)) token = MapToken(session.Get("chatAuthenticationTokensByCharacterID"), characterId);
        if (string.IsNullOrEmpty(token)) token = MapToken(session.Get("xmppAuthenticationTokensByCharacterID"), characterId);
        return token;
    }

    private static string? Text(PyValue? value) =>
        value is null or PyNone ? null : JsSemantics.StringValue(value);

    private static string? MapToken(PyValue? value, long characterId)
    {
        if (characterId <= 0 || value is not PyDict dictionary)
            return null;
        var key = characterId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        foreach (var (mapKey, mapValue) in dictionary.Entries)
            if (JsSemantics.StringValue(mapKey) == key)
                return Text(mapValue);
        return null;
    }
}

public sealed record JumpTimerState(long? JumpActivation, long? JumpFatigue, long? LastUpdated);

public delegate ValueTask<JumpTimerState?> ReadJumpTimersCallback(long characterId, CancellationToken cancellationToken);

[Service("jumpTimers")]
public sealed partial class JumpTimersService(ReadJumpTimersCallback? readTimers = null)
{
    [Method("GetTimers")]
    public async ValueTask<PyValue> GetTimers(ServiceCall call)
    {
        call.Cancellation.ThrowIfCancellationRequested();
        if (call.Args.Count != 1 || call.Kwargs is not PyNone)
            throw MachoErrors.ThrowUserError("JUMP_TIMER_ARGUMENTS_INVALID");
        var requested = Positive(call.Arg(0));
        var characterId = call.Session.CharacterId ?? 0;
        if (requested <= 0 || characterId <= 0 || requested != characterId)
            throw MachoErrors.ThrowUserError("JUMP_TIMER_CHARACTER_INVALID");
        var state = characterId > 0 && readTimers is { } read
            ? await read(characterId, call.Cancellation)
            : null;
        return Py.Tuple(Long(state?.JumpActivation), Long(state?.JumpFatigue), Long(state?.LastUpdated));
    }

    private static long Positive(PyValue value) => value switch
    {
        PyInt number when number.Value > 0 => number.Value,
        PyLong number when number.Value > 0 => number.Value,
        _ => 0,
    };

    private static PyValue Long(long? value) => value is { } ticks ? new PyLong(ticks) : PyNone.Instance;
}

/// <summary>
/// A character's jump timers from the PostgreSQL fatigue store that the jumps write, the same values SpaceBridges sends in OnJumpTimersUpdated, so login and the
/// notice agree. A character with no row there falls back to the legacy reader.
/// </summary>
public static class JumpTimerReads
{
    public static ReadJumpTimersCallback Create(PgJumpFatigueStore fatigue, Func<long> nowUnixMs, ReadJumpTimersCallback? legacy = null) =>
        async (characterId, cancellationToken) =>
        {
            var state = await fatigue.GetAsync(characterId, cancellationToken);
            if (state != JumpFatigueState.None)
                return FromFatigue(state, nowUnixMs());
            return legacy is null ? null : await legacy(characterId, cancellationToken);
        };

    /// <summary>Each timer as the FILETIME it ends at, none once it has run out; null when neither runs. The last update is the read itself, as the store keeps no time of the jump.</summary>
    public static JumpTimerState? FromFatigue(JumpFatigueState state, long nowUnixMs)
    {
        var activation = state.CooldownUntilMs > nowUnixMs ? Py.FileTimeFromUnixMs(state.CooldownUntilMs) : (long?)null;
        var fatigue = state.FatigueUntilMs > nowUnixMs ? Py.FileTimeFromUnixMs(state.FatigueUntilMs) : (long?)null;
        return activation is null && fatigue is null
            ? null
            : new JumpTimerState(activation, fatigue, Py.FileTimeFromUnixMs(nowUnixMs));
    }
}

public sealed record DynamicBountyState(double Output, bool DbsiIsActive);

public delegate ValueTask<DynamicBountyState?> ReadDynamicBountyCallback(long? solarSystemId, CancellationToken cancellationToken);

[Service("dynamicBountyMgr")]
public sealed partial class DynamicBountyMgrService(ReadDynamicBountyCallback? readOutput = null)
{
    [Method("GetOutputForClientSolarSystem")]
    public async ValueTask<PyValue> GetOutputForClientSolarSystem(ServiceCall call)
    {
        call.Cancellation.ThrowIfCancellationRequested();
        if (call.Args.Count != 0 || call.Kwargs is not PyNone)
            throw MachoErrors.ThrowUserError("DYNAMIC_BOUNTY_ARGUMENTS_INVALID");
        var systemId = call.Session.GetInt("solarsystemid2") ?? call.Session.GetInt("solarsystemid");
        var result = readOutput is { } read
            ? await read(systemId, call.Cancellation)
            : null;
        return Py.Tuple(new PyReal(result?.Output ?? 1), result?.DbsiIsActive == true ? PyBool.True : PyBool.False);
    }
}

public sealed record SeasonClientEvent(PyValue EventType, PyValue ExtraData, PyValue Amount);

public delegate ValueTask ProcessSeasonClientEventCallback(
    SeasonClientEvent clientEvent,
    ClientSession session,
    CancellationToken cancellationToken);

[Service("seasonManager")]
public sealed partial class SeasonManagerService(ProcessSeasonClientEventCallback? processEvent = null)
{

    [Method("get_season_data_for_character")]
    public PyValue GetSeasonDataForCharacter(ServiceCall _) => PyNone.Instance;


    [Method("select_season")]
    public PyValue SelectSeason(ServiceCall _) => PyNone.Instance;

    [Method("get_season_end_time")]
    public PyValue GetSeasonEndTime(ServiceCall _) => new PyLong(DateTime.UtcNow.ToFileTimeUtc());

    /// <summary>

    /// challenge expiry, no rewards to claim. Progress and claims need the season definitions, which no source here carries.
    /// </summary>
    [Method("get_max_active_challenges")]
    public PyValue GetMaxActiveChallenges(ServiceCall _) => new PyInt(0);

    [Method("get_challenge")]
    public PyValue GetChallenge(ServiceCall _) => PyNone.Instance;

    [Method("challenge_is_dormant")]
    public PyValue ChallengeIsDormant(ServiceCall _) => PyBool.True;

    [Method("get_challenge_expiration_date")]
    public PyValue GetChallengeExpirationDate(ServiceCall _) => PyNone.Instance;

    [Method("fill_up_and_return_challenges_for_character")]
    public PyValue FillUpAndReturnChallengesForCharacter(ServiceCall _) => new PyDict([]);

    [Method("get_loot_table_by_typeID")]
    public PyValue GetLootTableByTypeId(ServiceCall _) => new PyList([]);

    [Method("claim_challenge_rewards")]
    public PyValue ClaimChallengeRewards(ServiceCall _) => PyBool.False;

    [Method("claim_goal_reward")]
    public PyValue ClaimGoalReward(ServiceCall _) => PyBool.False;


    [Method("get_character_progress_by_character_id")]
    public PyValue GetCharacterProgressByCharacterId(ServiceCall call) => Py.KeyVal(
        ("character_id", call.Session.CharacterId is { } characterId ? new PyLong(characterId) : PyNone.Instance),
        ("season_id", PyNone.Instance),
        ("seasonal_points", new PyInt(0)),
        ("max_seasonal_points", new PyInt(1)),
        ("seasonal_goals", new PyDict([])),
        ("next_seasonal_goal", PyNone.Instance),
        ("last_seasonal_goal", PyNone.Instance),
        ("challenges", new PyDict([])));

    [Method("process_client_event")]
    public async ValueTask<PyValue> ProcessClientEvent(ServiceCall call)
    {
        call.Cancellation.ThrowIfCancellationRequested();
        if (processEvent is not { } callback)
            throw MachoErrors.ThrowUserError(
                "CustomInfo", ("info", new PyStr("Season progression is unavailable.")));

        await callback(new SeasonClientEvent(
                call.Arg(0),
                call.Kwarg("extra_data") ?? PyNone.Instance,
                call.Kwarg("amount") ?? new PyInt(0)),
            call.Session,
            call.Cancellation);
        return PyNone.Instance;
    }
}
