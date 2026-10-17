using System.Globalization;
using Eve.Marshal;
using Eve.Services.Slash;
using Eve.Wallet;

namespace Eve.Services.Dogma.Tests;

public class IskCommandTests
{
    [Theory]
    [InlineData("1000000", 1000000)]
    [InlineData("me 500", 500)]
    [InlineData("ME -250.50", -250.50)]
    [InlineData("0.01", 0.01)]
    public void The_amount_is_a_whole_number_of_cents(string argument, double expected)
    {
        Assert.Equal((decimal)expected, IskCommands.ParseAmount(argument));
    }

    [Theory]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("abc")]
    [InlineData("1.001")]
    [InlineData("1 2")]
    [InlineData("other 5")]
    public void Anything_else_is_the_usage(string argument)
    {
        Assert.Null(IskCommands.ParseAmount(argument));
    }

    [PgFact]
    public async Task The_game_master_gives_and_takes_isk_as_journal_entries_of_the_client_reference_and_the_pilot_is_told()
    {
        await using var rig = await Rig.CreateAsync();
        var pilot = await rig.NewCharacterAsync("Isk Pilot");
        var start = (await rig.Wallets.GetAsync(pilot.CharacterId))!.Isk;
        var announced = new List<WalletBalanceChange>();
        rig.Transactions.BalanceCommitted += announced.Add;

        var gave = await rig.SlashAsync(Rig.Session(pilot.CharacterId), "/isk me 500");
        Assert.Equal($"500.00 ISK for character {pilot.CharacterId}. Balance: {(start + 500m).ToString("N2", CultureInfo.InvariantCulture)} ISK.", gave);
        Assert.Equal(start + 500m, (await rig.Wallets.GetAsync(pilot.CharacterId))!.Isk);
        Assert.Equal(new WalletBalanceChange(pilot.CharacterId, 1000, start + 500m), Assert.Single(announced));

        Assert.Contains("-250.50 ISK", await rig.SlashAsync(Rig.Session(pilot.CharacterId), "/isk -250.50"));
        Assert.Equal(start + 249.50m, (await rig.Wallets.GetAsync(pilot.CharacterId))!.Isk);

        var journal = (await rig.Transactions.JournalAsync(pilot.CharacterId)).Where(e => e.Reason == "gm isk").OrderBy(e => e.Id).ToList();
        Assert.Equal([500m, -250.50m], journal.Select(e => e.Amount));
        Assert.All(journal, e => Assert.Equal(IskCommands.GmCashTransferRefType, e.RefTypeId));
        Assert.Equal(3, IskCommands.GmCashTransferRefType);
        Assert.Equal([start + 500m, start + 249.50m], journal.Select(e => e.Balance));
    }

    [PgFact]
    public async Task A_session_without_a_staff_role_or_a_character_is_refused_and_the_wallet_does_not_move()
    {
        await using var rig = await Rig.CreateAsync();
        var pilot = await rig.NewCharacterAsync("Isk Plain");
        var start = (await rig.Wallets.GetAsync(pilot.CharacterId))!.Isk;

        var plain = Rig.Session(pilot.CharacterId);
        plain.Set("accountRole", new PyLong(0));
        Assert.Equal(SlashService.NotGameMaster, await rig.SlashAsync(plain, "/isk 100"));
        Assert.Equal("Select a character before using /isk.", await rig.SlashAsync(Rig.Session(null), "/isk 100"));
        Assert.Equal(IskCommands.Usage, await rig.SlashAsync(Rig.Session(pilot.CharacterId), "/isk 0"));

        Assert.Equal(start, (await rig.Wallets.GetAsync(pilot.CharacterId))!.Isk);
        Assert.DoesNotContain((await rig.Transactions.JournalAsync(pilot.CharacterId)), e => e.Reason == "gm isk");
    }
}
