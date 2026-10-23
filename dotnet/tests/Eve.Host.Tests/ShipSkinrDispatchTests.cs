using Eve.Host.Persistence;
using Eve.Host.PublicGateway;
using Eve.Services;
using Eve.Skills;

namespace Eve.Host.Tests;

public sealed class ShipSkinrDispatchTests
{
    private const string Draft = "eve_public.cosmetic.ship.skin.thirdparty.draft.api.";
    private const string Sequencing = "eve_public.cosmetic.ship.skin.thirdparty.sequencing.api.";
    private const string Job = "eve_public.cosmetic.ship.skin.thirdparty.sequencing.job.api.";

    [Fact]
    public async Task V24_skinr_routes_expose_the_called_methods_and_tier_rows()
    {
        var draftHandlers = ShipSkinrGateway.DraftHandlers(
            null!, null!, null!, null!, new Dictionary<string, int>());
        Assert.Equal(new[]
        {
            Draft + "DeleteRequest",
            Draft + "GetAllSavedRequest",
            Draft + "GetRequest",
            Draft + "GetSaveCapacityRequest",
            Draft + "GetSharedRequest",
            Draft + "SaveRequest",
            Draft + "UpdateRequest",
        }, draftHandlers.Keys.Order(StringComparer.Ordinal));

        var gateway = new ShipSkinrSequencingGateway(
            null!, null!, null!, null!, new Dictionary<string, int>(), null!, "inventory",
            null!, null!, new NoticeHub(), new CharacterSessions(), null);
        var handlers = gateway.Handlers();
        Assert.Equal(new[]
        {
            "eve_public.cosmetic.ship.skin.thirdparty.api.GetRequest",
            "eve_public.cosmetic.ship.skin.thirdparty.component.license.api.GetOwnedRequest",
            "eve_public.cosmetic.ship.skin.thirdparty.component.license.finite.api.GetRequest",
            "eve_public.cosmetic.ship.skin.thirdparty.component.license.infinite.api.GetRequest",
            "eve_public.cosmetic.ship.skin.thirdparty.component.license.item.api.ConsumeRequest",
            "eve_public.cosmetic.ship.skin.thirdparty.license.api.ActivateRequest",
            "eve_public.cosmetic.ship.skin.thirdparty.license.api.ApplyRequest",
            "eve_public.cosmetic.ship.skin.thirdparty.license.api.GetOwnedRequest",
            "eve_public.cosmetic.ship.skin.thirdparty.license.api.GetRequest",
            "eve_public.cosmetic.ship.skin.thirdparty.license.api.UnapplyRequest",
            Job + "GetAllActiveRequest",
            Job + "GetRequest",
            Job + "InitiateRequest",
            Sequencing + "GetTierDurationsRequest",
            Sequencing + "GetTierPricingRequest",
        }.Order(StringComparer.Ordinal).ToArray(), handlers.Keys.Order(StringComparer.Ordinal).ToArray());

        var request = Request([]);
        var pricing = await handlers[Sequencing + "GetTierPricingRequest"](request, CancellationToken.None);
        Assert.Equal(200u, pricing.StatusCode);
        Assert.Equal(Sequencing + "GetTierPricingResponse", pricing.ResponseTypeName);
        var prices = Fields(pricing.Payload, 1);
        Assert.Equal(18, prices.Count);
        Assert.Equal(1UL, Scalar(Message(prices[0].Bytes, 1), 1));
        Assert.Equal(2500UL, Scalar(Message(prices[0].Bytes, 2), 1));
        Assert.Equal(18UL, Scalar(Message(prices[^1].Bytes, 1), 1));
        Assert.Equal(72000UL, Scalar(Message(prices[^1].Bytes, 2), 1));

        Assert.Contains(Sequencing + "GetTierDurationsRequest", handlers.Keys);
        var durations = ShipSkinrSequencingGateway.TierDurations(discountPercentageApplied: 15);
        var tiers = Fields(durations, 1);
        Assert.Equal(18, tiers.Count);
        Assert.Equal(15UL, Scalar(durations, 2));
        Assert.Equal(10800UL, Scalar(Message(tiers[0].Bytes, 2), 1));
        Assert.Equal(1_915_200UL, Scalar(Message(tiers[^1].Bytes, 2), 1));
    }

    [Fact]
    public void Draft_save_and_read_responses_keep_the_v24_row_shape()
    {
        var id = Guid.NewGuid();
        var hex = new string('a', 64);
        var editorConfig = new ProtoWriter();
        editorConfig.Message(1, new ProtoWriter().ToArray());
        var editorConfigurations = new ProtoWriter();
        editorConfigurations.Message(1, editorConfig.ToArray());
        var draft = new ShipSkinrDraft(20, id, "Blue Nova", "Nova Line", 587, [], editorConfigurations.ToArray(),
            hex, 1_800_000_000_000, 1_800_000_000_100);

        var saved = ShipSkinrGateway.EncodeSavedDraft(draft);
        var savedFields = ProtoReader.Fields(saved).ToArray();
        Assert.Equal(new[] { 1, 2 }, savedFields.Select(field => field.Number));
        var identifier = Message(saved, 1);
        Assert.Equal(id.ToByteArray(bigEndian: true), Message(identifier, 1).ToArray());
        var attributes = Message(saved, 2);
        Assert.Equal(hex, System.Text.Encoding.UTF8.GetString(Message(attributes, 1).Span));

        var read = ShipSkinrGateway.EncodeAttributesResponse(draft, 1);
        Assert.Equal(new[] { 1 }, ProtoReader.Fields(read).Select(field => field.Number));
        Assert.Equal(hex, System.Text.Encoding.UTF8.GetString(Message(Message(read, 1), 1).Span));
        Assert.Single(Fields(Message(read, 1).ToArray(), 3));
    }

    [Fact]
    public void Batch_quantity_multiplies_plex_cost_but_keeps_tier_time()
    {
        var one = ShipSkinrSequencingGateway.CalculateQuote(tier: 8, quantity: 1, durationBonusPercent: 15);
        var five = ShipSkinrSequencingGateway.CalculateQuote(tier: 8, quantity: 5, durationBonusPercent: 15);

        Assert.Equal(90, one.PlexCost);
        Assert.Equal(450, five.PlexCost);
        Assert.Equal(229_500_000L, one.DurationMs);
        Assert.Equal(one.DurationMs, five.DurationMs);
    }

    [Fact]
    public async Task Invalid_v24_requests_keep_their_response_types_without_touching_state()
    {
        var gateway = new ShipSkinrSequencingGateway(
            null!, null!, null!, null!, new Dictionary<string, int>(), null!, "inventory",
            null!, null!, new NoticeHub(), new CharacterSessions(), null);
        var handlers = gateway.Handlers();

        var invalidSequence = await handlers[Job + "InitiateRequest"](Request([]), CancellationToken.None);
        Assert.Equal(400u, invalidSequence.StatusCode);
        Assert.Equal(Job + "InitiateResponse", invalidSequence.ResponseTypeName);

        var invalidComponent = await handlers["eve_public.cosmetic.ship.skin.thirdparty.component.license.item.api.ConsumeRequest"](
            Request([]), CancellationToken.None);
        Assert.Equal(400u, invalidComponent.StatusCode);
        Assert.Equal("eve_public.cosmetic.ship.skin.thirdparty.component.license.item.api.ConsumeResponse",
            invalidComponent.ResponseTypeName);

        var draftHandlers = ShipSkinrGateway.DraftHandlers(
            null!, null!, null!, null!, new Dictionary<string, int>());
        var invalidSave = await draftHandlers[Draft + "SaveRequest"](Request([]), CancellationToken.None);
        Assert.Equal(400u, invalidSave.StatusCode);
        Assert.Equal(Draft + "SaveResponse", invalidSave.ResponseTypeName);

        var invalidRead = await draftHandlers[Draft + "GetRequest"](Request([]), CancellationToken.None);
        Assert.Equal(400u, invalidRead.StatusCode);
        Assert.Equal(Draft + "GetResponse", invalidRead.ResponseTypeName);
    }

    private static GatewayRequest Request(byte[] payload) =>
        new(Guid.NewGuid().ToByteArray(bigEndian: true), "type.evetech.net/test", payload, 20);

    private static IReadOnlyList<ProtoField> Fields(byte[] payload, int number) =>
        ProtoReader.Fields(payload).Where(field => field.Number == number && field.WireType == 2).ToArray();

    private static ReadOnlyMemory<byte> Message(ReadOnlyMemory<byte> payload, int number) =>
        ProtoReader.Fields(payload).First(field => field.Number == number && field.WireType == 2).Bytes;

    private static ulong Scalar(ReadOnlyMemory<byte> payload, int number) =>
        ProtoReader.Fields(payload).First(field => field.Number == number && field.WireType == 0).Scalar;
}
