using System.Collections.Concurrent;
using Eve.Marshal;
using Eve.Services;

namespace Eve.Services.CharacterSelect;

/// <summary>
/// The multi-login check of the SSO login: <c>multiLoginBlocker.Login(computerHash)</c>, which the client calls before it opens
/// a character (characterSelection.py _CanLogin; only when the login carries a computer hash). A session that asks is recorded
/// with its account, its computer hash and its clone grade. The answer is False when another account is logged in from the
/// same computer and either account is Alpha (the reference's rule, EveJS multiLoginBlockerService.js). Two Omega accounts may
/// share a computer. A session that closes is forgotten.
/// </summary>
[Service("multiLoginBlocker")]
public sealed partial class MultiLoginBlockerService(Func<ClientSession, long>? cloneGradeOf = null)
{
    /// <summary>The clone grade of an Alpha account.</summary>
    public const long CloneGradeAlpha = 0;

    private readonly ConcurrentDictionary<long, LoggedIn> _logins = new();

    private sealed record LoggedIn(ClientSession Session, long AccountId, string ComputerHash, long CloneGrade);

    /// <summary>The logins the service knows of, by session id (the sessions that asked and have not closed).</summary>
    public int Count => _logins.Count;

    [Method("Login")]
    public PyValue Login(ServiceCall call)
    {
        var hash = ComputerHash(call.Arg(0));
        if (hash.Length == 0)
            return PyBool.True;

        var session = call.Session;
        var accountId = session.UserId ?? 0;
        var grade = cloneGradeOf?.Invoke(session) ?? 1;
        var conflict = _logins.Values.Any(other =>
            other.Session != session
            && other.AccountId != accountId
            && other.ComputerHash == hash
            && (grade == CloneGradeAlpha || other.CloneGrade == CloneGradeAlpha));
        if (conflict)
            return PyBool.False;

        if (!_logins.ContainsKey(session.SessionId))
            session.Closed += () => _logins.TryRemove(session.SessionId, out _);
        _logins[session.SessionId] = new LoggedIn(session, accountId, hash, grade);
        return PyBool.True;
    }

    /// <summary>The computer hash as the reference compares it: trimmed and lower case; empty when none was sent.</summary>
    private static string ComputerHash(PyValue? value) => value switch
    {
        var text when Eve.Macho.JsSemantics.StringValue(text) is not null => Eve.Macho.JsSemantics.StringValue(text)!.Trim().ToLowerInvariant(),
        PyBuffer bytes => System.Text.Encoding.UTF8.GetString(bytes.Value.Span).Trim().ToLowerInvariant(),
        _ => string.Empty,
    };
}
