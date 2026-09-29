using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace Eve.Identity;

/// <summary>What the front knows about a login attempt.</summary>
public sealed record LoginAttempt(string UserName, ReadOnlyMemory<byte> PasswordHash, string Language, string Address);

public abstract record LoginResult;

/// <summary>The login was accepted, as this account.</summary>
public sealed record LoggedIn(uint AccountId, ulong Role, string CountryCode) : LoginResult;

/// <summary>The login was refused; <see cref="Reason"/> is the code the client is shown.</summary>
public sealed record LoginRefused(string Reason) : LoginResult
{
    public const string BadCredentials = "LoginAuthFailed";
    public const string Banned = "ACCOUNTBANNED";
    public const string Unavailable = "Unavailable";
}

/// <summary>Who may log in, and as which account.</summary>
public interface IAccountDirectory
{
    ValueTask<LoginResult> LoginAsync(LoginAttempt attempt, CancellationToken cancellationToken);
}

public sealed record AccountOptions
{
    /// <summary>An unknown user name creates an account with the password hash it presented.</summary>
    public bool AutoCreate { get; init; } = true;

    /// <summary>Accept any password hash. For development only: it makes every account open to anyone.</summary>
    public bool SkipPasswordCheck { get; init; }

    public string CountryCode { get; init; } = "GB";

    /// <summary>The role mask a new account gets (0 is an ordinary player).</summary>
    public ulong NewAccountRole { get; init; }
}

/// <summary>Which user names may exist.</summary>
public static class AccountNames
{
    public const int MaxLength = 128;

    /// <summary>A name with something in it, of sensible length, without a NUL (PostgreSQL text cannot hold one).</summary>
    public static bool IsAcceptable(string name) =>
        !string.IsNullOrWhiteSpace(name) && name.Length <= MaxLength && !name.Contains('\0', StringComparison.Ordinal);
}

/// <summary>
/// Accounts kept in memory: for tests and for running without a database. Nothing survives a restart.
/// </summary>
public sealed class InMemoryAccountDirectory : IAccountDirectory
{
    private sealed class Account(uint id, byte[] passwordHash, ulong role)
    {
        public uint Id { get; } = id;

        public byte[] PasswordHash { get; } = passwordHash;

        public ulong Role { get; } = role;

        public bool Banned { get; set; }
    }

    /// <summary>The accounts themselves, shared by the views <see cref="WithOptions"/> makes.</summary>
    private sealed class Store
    {
        public ConcurrentDictionary<string, Account> Accounts { get; } = new(StringComparer.Ordinal);

        public Lock Create { get; } = new();

        public uint LastId { get; set; }
    }

    private readonly AccountOptions _options;
    private readonly Store _store;

    public InMemoryAccountDirectory(AccountOptions options)
        : this(options, new Store())
    {
    }

    private InMemoryAccountDirectory(AccountOptions options, Store store)
    {
        _options = options;
        _store = store;
    }

    /// <summary>The same accounts, seen with other options (two servers over one database, in a test).</summary>
    public InMemoryAccountDirectory WithOptions(AccountOptions options) => new(options, _store);

    public void Ban(string userName)
    {
        if (_store.Accounts.TryGetValue(userName, out var account))
        {
            account.Banned = true;
        }
    }

    public ValueTask<LoginResult> LoginAsync(LoginAttempt attempt, CancellationToken cancellationToken)
    {
        if (!AccountNames.IsAcceptable(attempt.UserName))
        {
            return ValueTask.FromResult<LoginResult>(new LoginRefused(LoginRefused.BadCredentials));
        }
        if (!_store.Accounts.TryGetValue(attempt.UserName, out var account))
        {
            if (!_options.AutoCreate)
            {
                return ValueTask.FromResult<LoginResult>(new LoginRefused(LoginRefused.BadCredentials));
            }
            lock (_store.Create)
            {
                if (!_store.Accounts.TryGetValue(attempt.UserName, out account))
                {
                    account = new Account(++_store.LastId, attempt.PasswordHash.ToArray(), _options.NewAccountRole);
                    _store.Accounts[attempt.UserName] = account;
                }
            }
        }
        return ValueTask.FromResult(Decide(account.Banned, account.PasswordHash, account.Id, account.Role, attempt, _options));
    }

    /// <summary>The refusal, or the login, for an account that exists. A ban outranks a wrong password.</summary>
    internal static LoginResult Decide(bool banned, ReadOnlySpan<byte> storedHash, uint id, ulong role, LoginAttempt attempt, AccountOptions options)
    {
        var passwordOk = options.SkipPasswordCheck || CryptographicOperations.FixedTimeEquals(storedHash, attempt.PasswordHash.Span);
        if (banned)
        {
            return new LoginRefused(LoginRefused.Banned);
        }
        return passwordOk ? new LoggedIn(id, role, options.CountryCode) : new LoginRefused(LoginRefused.BadCredentials);
    }
}
