namespace Eve.Wallet;

public sealed record WalletBalanceChange(long OwnerId, int AccountKey, decimal Balance);
