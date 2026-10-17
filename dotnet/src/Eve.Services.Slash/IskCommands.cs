using System.Globalization;
using Eve.Wallet;
using Npgsql;

namespace Eve.Services.Slash;

/// <summary>
/// <c>/isk [me] &lt;amount&gt;</c>: the game master gives the pilot ISK (a negative amount takes it away). The change is one wallet journal entry
/// in its own transaction, with the ESI reference type of a game master's cash transfer (<see cref="GmCashTransferRefType"/>). Whole cents only.
/// The reference's other target forms are not served: the pilot is the target.
/// </summary>
public sealed class IskCommands(WalletTransactions wallets, NpgsqlDataSource source, Func<long> nowMs)
{
    /// <summary>ESI wallet journal reference type 10: <c>gm_cash_transfer</c>.</summary>
    public const int GmCashTransferRefType = 10;

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

    public async Task<string> RunAsync(ClientSession session, string argument, CancellationToken cancellationToken)
    {
        if (ParseAmount(argument) is not { } amount) return Usage;
        if (session.CharacterId is not { } character) return "Select a character before using /isk.";
        var now = nowMs();
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var balance = await wallets.AdjustAsync(connection, transaction, character, amount, 0, GmCashTransferRefType, "gm isk", now,
            $"gm-isk:{character}:{now}:{Guid.NewGuid():N}", cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return $"{amount.ToString("N2", CultureInfo.InvariantCulture)} ISK for character {character}. Balance: {balance.ToString("N2", CultureInfo.InvariantCulture)} ISK.";
    }
}
