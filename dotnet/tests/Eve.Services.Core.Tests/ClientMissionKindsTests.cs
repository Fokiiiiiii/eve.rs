namespace Eve.Services.Core.Tests;

/// <summary>The trade and mining kinds are read from the client's content templates, which come from the embedded client data when no directory is given.</summary>
public class ClientMissionKindsTests
{
    [Fact]
    public void Trade_and_mining_missions_are_classified_from_the_embedded_client_data_without_a_directory()
    {
        // a null directory is the unset EVE_CLIENT_SDE_DIR: the templates come from the embedded copy (ClientStaticData.Open)
        var templates = ClientMissionTemplates.Parse(ClientMissionMessages.Open(null));

        Assert.Equal(ClientMissionTemplates.TradeTemplate, templates[1407]);
        Assert.Equal(ClientMissionTemplates.MiningTemplate, templates[4800]);
    }
}
