using Eve.Macho;

namespace Eve.Services.Dogma.Tests;

/// <summary>The modules of space as a test plays them: what the dogma service asks is kept, and what it is answered is what the test says.</summary>
internal sealed class FakeModules : ISpaceModules
{
    public List<string> Asked { get; } = [];

    /// <summary>What every order is answered with; null when the session has no ship in space.</summary>
    public ModuleResult? Answer { get; set; } = ModuleResult.Done;

    public Task<ModuleResult?> ActivateAsync(ClientSession session, long moduleId, string effectName, long targetId, int repeat, CancellationToken cancellationToken, IReadOnlyList<double>? aim = null, IReadOnlyList<double>? aimEnd = null)
    {
        Asked.Add($"activate {moduleId} {effectName} {targetId} {repeat}");
        return Task.FromResult(Answer);
    }

    /// <summary>What every repair start is answered with (null: no ship in space); a stop is answered with <see cref="Repair"/>.</summary>
    public ModuleRepairStart? RepairStart { get; set; }

    public ModuleRepairRecord? Repair { get; set; }

    public Task<ModuleRepairStart?> StartRepairAsync(ClientSession session, IReadOnlyList<long> moduleIds, CancellationToken cancellationToken)
    {
        Asked.Add($"repair {string.Join(",", moduleIds)}");
        return Task.FromResult(RepairStart);
    }

    public Task<ModuleRepairRecord?> EndRepairAsync(ClientSession session, long moduleId, CancellationToken cancellationToken)
    {
        Asked.Add($"stop repair {moduleId}");
        var record = Repair;
        Repair = null;
        return Task.FromResult(record);
    }

    public Task<ModuleResult?> DeactivateAsync(ClientSession session, long moduleId, string effectName, CancellationToken cancellationToken)
    {
        Asked.Add($"deactivate {moduleId} {effectName}");
        return Task.FromResult(Answer);
    }

    public Task<ModuleResult?> OverloadAsync(ClientSession session, long moduleId, int effectId, bool active, CancellationToken cancellationToken)
    {
        Asked.Add($"{(active ? "overload" : "stop overload")} {moduleId} {effectId}");
        return Task.FromResult(Answer);
    }

    public Task<ModuleResult?> OfflineAsync(ClientSession session, long moduleId, CancellationToken cancellationToken)
    {
        Asked.Add($"offline {moduleId}");
        return Task.FromResult(Answer);
    }

    public Task<ModuleResult?> OverloadRackAsync(ClientSession session, long moduleId, bool active, CancellationToken cancellationToken)
    {
        Asked.Add($"{(active ? "overload rack" : "stop overload rack")} {moduleId}");
        return Task.FromResult(Answer);
    }

    public Task RefreshShipAsync(ClientSession session, CancellationToken cancellationToken)
    {
        Asked.Add("refresh ship");
        return Task.CompletedTask;
    }
}
