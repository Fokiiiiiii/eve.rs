using System.Globalization;
using Eve.Wallet;
using Npgsql;

namespace Eve.Services.Slash;

/// <summary>
/// <c>/isk [me] &lt;amount&gt;</c>: the game master gives the pilot ISK (a negative amount takes it away). The change is one wallet journal entry
/// in its own transaction, with the ESI reference type of a game master's cash transfer (<see cref="GmCashTransferRefType"/>). Whole cents only.
/// The reference's other target forms are not served: the pilot is the target. The balance change is announced after the commit, as a
/// wallet change of the cash account (1000) (the client's balance display follows it).
/// </summary>
public sealed class IskCommands(WalletTransactions wallets, NpgsqlDataSource source, Func<long> nowMs, PgWalletStore balances)
{
    /// <summary>The V24.01 client's <c>appConst.refGMCashTransfer</c> (eve-logic appConst.py), the journal reference the client shows for this.</summary>
    public const int GmCashTransferRefType = 3;

    public const string Usage = "Usage: /isk [me] <amount>  (whole ISK or cents; a negative amount takes ISK away)";

    /// <summary>Reads the amount of <c>/isk</c>: an optional <c>me</c> word, then one number with at most two decimals and no zero.</summary>
    public static decimal? ParseAmount(string argument)
    {
        var words = argument.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 2 && words[0].Equals("me", StringComparison.OrdinalIgnoreCase)) words = [words[1]];
        if (words.Length != 1) return null;
        if (!decimal.TryParse(words[0], NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var amount)) return null;
        if (amount == 0 || decimal.Round(amount, 2) != amount) return null;
        return amount;
    }

    public const string SetUsage = "Usage: /setisk <amount>  (whole ISK or cents; the balance becomes the amount)";

    /// <summary>The ISK and PLEX of the pilot (EveJS <c>/wallet</c>, and <c>/isk</c> with no amount).</summary>
    public async Task<string> WalletAsync(ClientSession session, CancellationToken cancellationToken)
    {
        if (session.CharacterId is not { } character) return "Select a character before checking wallet balance.";
        var balance = await balances.GetAsync(character, cancellationToken);
        return $"ISK: {(balance?.Isk ?? 0m).ToString("N2", CultureInfo.InvariantCulture)}. PLEX: {balance?.Plex ?? 0}.";
    }

    /// <summary>
    /// <c>/setisk &lt;amount&gt;</c> (EveJS): the pilot's ISK becomes the amount, zero or more, as one journal entry of the difference. The balance is read
    /// before the write, in the command's own call: a wallet change between the read and the write would be overwritten (a GM tool is run alone).
    /// </summary>
    public async Task<string> SetAsync(ClientSession session, string argument, CancellationToken cancellationToken)
    {
        if (session.CharacterId is not { } character) return "Select a character before changing wallet balance.";
        var words = argument.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length != 1 || !decimal.TryParse(words[0], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var target)
            || target < 0 || decimal.Round(target, 2) != target)
            return SetUsage;
        var current = (await balances.GetAsync(character, cancellationToken))?.Isk ?? 0m;
        if (current == target) return $"ISK for character {character} is already {target.ToString("N2", CultureInfo.InvariantCulture)}.";
        var now = nowMs();
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var balance = await wallets.AdjustAsync(connection, transaction, character, target - current, 0, GmCashTransferRefType, "gm isk", now,
            $"gm-setisk:{character}:{now}:{Guid.NewGuid():N}", cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        wallets.AnnounceCommitted(new WalletBalanceChange(character, 1000, balance));
        return $"ISK for character {character} set to {target.ToString("N2", CultureInfo.InvariantCulture)}. Balance: {balance.ToString("N2", CultureInfo.InvariantCulture)} ISK.";
    }

    public async Task<string> RunAsync(ClientSession session, string argument, CancellationToken cancellationToken)
    {
        if (argument.Trim().Length == 0) return await WalletAsync(session, cancellationToken);
        if (ParseAmount(argument) is not { } amount) return Usage;
        if (session.CharacterId is not { } character) return "Select a character before using /isk.";
        var now = nowMs();
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var balance = await wallets.AdjustAsync(connection, transaction, character, amount, 0, GmCashTransferRefType, "gm isk", now,
            $"gm-isk:{character}:{now}:{Guid.NewGuid():N}", cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        wallets.AnnounceCommitted(new WalletBalanceChange(character, 1000, balance));
        return $"{amount.ToString("N2", CultureInfo.InvariantCulture)} ISK for character {character}. Balance: {balance.ToString("N2", CultureInfo.InvariantCulture)} ISK.";
    }
}
