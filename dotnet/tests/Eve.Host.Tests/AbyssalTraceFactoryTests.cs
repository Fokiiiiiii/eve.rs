using Eve.Space;
using Xunit;

namespace Eve.Host.Tests;

public sealed class AbyssalTraceFactoryTests
{
    [Fact]
    public void T0_trace_uses_the_pinned_EveJS_captured_offset_and_space_contract()
    {
        var trace = AbyssalTraceFactory.Create(
            solarSystemId: 30000142,
            traceItemId: 700001,
            gameModeId: 2,
            filamentTypeId: 56132,
            difficultyTier: 0,
            expiresAtMs: 1_800_000,
            radius: 500,
            sourceShipPosition: [10, 20, 30],
            sourceShipDirection: [0, 0, 1]);

        Assert.Equal(30000142, trace.SolarSystemId);
        Assert.Equal(700001, trace.ItemId);
        Assert.Equal(48964, trace.TypeId);
        Assert.Equal(1991, trace.GroupId);
        Assert.Equal(2, trace.CategoryId);
        Assert.Equal(1, trace.OwnerId);
        Assert.Equal(500, trace.Radius);
        Assert.Equal(2, trace.GameModeId);
        Assert.Equal(56132, trace.FilamentTypeId);
        Assert.Equal(0, trace.DifficultyTier);
        Assert.Equal(1_800_000, trace.ExpiresAtMs);
        Assert.Equal(
            new[] { -86.8582763671875, -146.0657196044922, 171.95849609375 },
            trace.Position);
        Assert.Equal(trace.Position, trace.TargetPoint);
        Assert.Equal(new[] { 0d, 0d, 0d }, trace.Velocity);
        Assert.Equal(new[] { 0d, 0d, 1d }, trace.Direction);
        Assert.Equal(0, trace.SpeedFraction);
    }

    [Fact]
    public void Other_filaments_use_the_pinned_EveJS_2500_meter_directional_placement()
    {
        var trace = AbyssalTraceFactory.Create(
            solarSystemId: 30000142,
            traceItemId: 700002,
            gameModeId: 3,
            filamentTypeId: 56200,
            difficultyTier: 6,
            expiresAtMs: 1_800_000,
            radius: 500,
            sourceShipPosition: [10, 20, 30],
            sourceShipDirection: [0, 0, 2]);

        Assert.Equal(new[] { 10d, 20d, 2530d }, trace.Position);
        Assert.Equal(new[] { 0d, 0d, 1d }, trace.Direction);
        Assert.Equal(3, trace.GameModeId);
        Assert.Equal(6, trace.DifficultyTier);
    }


    [Fact]
    public void Restore_reuses_the_saved_trace_pose_without_recomputing_geometry()
    {
        var original = AbyssalTraceFactory.Create(
            solarSystemId: 30000142,
            traceItemId: 700005,
            gameModeId: 1,
            filamentTypeId: 56200,
            difficultyTier: 4,
            expiresAtMs: 1_800_000,
            radius: 500,
            sourceShipPosition: [1, 2, 3],
            sourceShipDirection: [1, 0, 0]);
        var snapshot = new AbyssalTracePoseSnapshot(1, original.Position, original.Direction);

        var restored = AbyssalTraceFactory.Restore(
            original.SolarSystemId,
            original.ItemId,
            original.GameModeId,
            original.FilamentTypeId,
            original.DifficultyTier,
            original.ExpiresAtMs,
            original.Radius,
            snapshot);

        Assert.Equal(original.Position, restored.Position);
        Assert.Equal(original.Direction, restored.Direction);
        Assert.Equal(original.TargetPoint, restored.TargetPoint);
        Assert.Equal(original.Velocity, restored.Velocity);
    }

    [Fact]
    public void Invalid_authoritative_pose_is_rejected_instead_of_replaced_with_a_guess()
    {
        Assert.Throws<ArgumentException>(() => AbyssalTraceFactory.Create(
            solarSystemId: 30000142,
            traceItemId: 700003,
            gameModeId: 1,
            filamentTypeId: 56131,
            difficultyTier: 0,
            expiresAtMs: 1_800_000,
            radius: 500,
            sourceShipPosition: [1, 2],
            sourceShipDirection: [1, 0, 0]));

        Assert.Throws<ArgumentException>(() => AbyssalTraceFactory.Create(
            solarSystemId: 30000142,
            traceItemId: 700004,
            gameModeId: 1,
            filamentTypeId: 56131,
            difficultyTier: 0,
            expiresAtMs: 1_800_000,
            radius: 500,
            sourceShipPosition: [1, 2, 3],
            sourceShipDirection: [0, 0, 0]));
    }
}