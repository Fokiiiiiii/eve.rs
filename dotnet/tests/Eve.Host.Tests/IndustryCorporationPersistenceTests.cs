using Eve.Services.Core;
using Xunit;

namespace Eve.Host.Tests;

/// <summary>
/// A corporation's industry job over PostgreSQL: the client's rules for a corporation owner (industry/job.py) are a Factory Manager,
/// the manufacturing role for a manufacturing job or the science role for any other, and the wallet division's take role.
/// </summary>
public sealed class IndustryCorporationPersistenceTests
{
    private const int Blueprint = 681;
    private const int Material = 38;
    private const long FactoryManager = 1024;
    private const long FactoryManufacturing = 1L << 50;
    private const long WalletDivisionOne = 1L << 27;

    [Fact]
    public async Task A_director_installs_a_manufacturing_job_for_the_corporation_from_its_wallet()
    {
        await using var rig = await IndustryRig.CreateAsync();
        var director = await rig.Owner(10_000_000m);
        var corporation = await rig.Corporation(director);
        await rig.Fund(corporation, 1_000_000m);
        var blueprint = await rig.Give(corporation, Blueprint, 1, blueprint: true);
        await rig.Give(corporation, Material, 86);
        var balanceBefore = await rig.Balance(corporation);

        var jobId = await rig.InstallForCorporation(director, corporation, blueprint, IndustryRules.Manufacturing);

        var (status, cost, _) = await rig.JobRow(jobId);
        Assert.Equal(1, status);
        Assert.Equal(balanceBefore - cost, await rig.Balance(corporation));
        Assert.Equal(0, await rig.Quantity(corporation, Material));
        Assert.Equal(corporation, await rig.JobOwner(jobId));
    }

    [Fact]
    public async Task A_member_with_factory_manager_but_no_manufacturing_role_cannot_install_a_manufacturing_job()
    {
        await using var rig = await IndustryRig.CreateAsync();
        var director = await rig.Owner(10_000_000m);
        var corporation = await rig.Corporation(director);
        await rig.Fund(corporation, 1_000_000m);
        var blueprint = await rig.Give(corporation, Blueprint, 1, blueprint: true);
        await rig.Give(corporation, Material, 86);
        var member = await rig.Member(corporation, FactoryManager | WalletDivisionOne);

        var error = await Assert.ThrowsAsync<IndustryJobUserErrorException>(() =>
            rig.InstallForCorporation(member, corporation, blueprint, IndustryRules.Manufacturing));

        Assert.Equal("IndustryInvalidOwner", error.MessageKey);
        Assert.Equal(0, await rig.JobCount());
        Assert.Equal(86, await rig.Quantity(corporation, Material));
    }

    [Fact]
    public async Task A_member_with_the_manufacturing_role_but_no_science_role_cannot_install_a_research_job()
    {
        await using var rig = await IndustryRig.CreateAsync();
        var director = await rig.Owner(10_000_000m);
        var corporation = await rig.Corporation(director);
        await rig.Fund(corporation, 1_000_000m);
        var blueprint = await rig.Give(corporation, Blueprint, 1, blueprint: true);
        var member = await rig.Member(corporation, FactoryManager | FactoryManufacturing | WalletDivisionOne);

        var error = await Assert.ThrowsAsync<IndustryJobUserErrorException>(() =>
            rig.InstallForCorporation(member, corporation, blueprint, IndustryRules.ResearchMaterial));

        Assert.Equal("IndustryInvalidOwner", error.MessageKey);
        Assert.Equal(0, await rig.JobCount());
        Assert.False((await rig.Blueprint(blueprint)).BlueprintLocked);
    }

    [Fact]
    public async Task A_member_without_the_wallet_division_take_role_cannot_pay_from_the_corporation_wallet()
    {
        await using var rig = await IndustryRig.CreateAsync();
        var director = await rig.Owner(10_000_000m);
        var corporation = await rig.Corporation(director);
        await rig.Fund(corporation, 1_000_000m);
        var blueprint = await rig.Give(corporation, Blueprint, 1, blueprint: true);
        await rig.Give(corporation, Material, 86);
        var member = await rig.Member(corporation, FactoryManager | FactoryManufacturing);

        var error = await Assert.ThrowsAsync<IndustryJobUserErrorException>(() =>
            rig.InstallForCorporation(member, corporation, blueprint, IndustryRules.Manufacturing));

        Assert.Equal("IndustryInvalidOwner", error.MessageKey);
        Assert.Equal(0, await rig.JobCount());
    }
}
