using Eve.Host;

namespace Eve.Host.Tests;

public class AutoReloadAccountsTests
{
    [Fact]
    public void The_accounts_named_for_reloads_are_read_and_an_empty_setting_names_none()
    {
        Assert.Equal(new HashSet<long> { 34, 35 }, HostConfiguration.AutoReloadUserIds(" 34, 35,,").ToHashSet());
        Assert.Empty(HostConfiguration.AutoReloadUserIds(""));
    }

    [Fact]
    public void An_entry_that_is_not_an_account_id_stops_the_host_rather_than_switching_a_reload_on_silently()
    {
        Assert.Throws<InvalidOperationException>(() => HostConfiguration.AutoReloadUserIds("34,bot"));
        Assert.Throws<InvalidOperationException>(() => HostConfiguration.AutoReloadUserIds("0"));
    }
}
