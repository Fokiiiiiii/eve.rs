using Eve.Macho;

namespace Eve.Services.Dogma.Tests;

/// <summary>The modules of space as a test plays them: what the dogma service asks is kept, and what it is answered is what the test says.</summary>
internal sealed class FakeModules : ISpaceModules
{
    public List<string> Asked { get; } = [];

    /// <summary>What every order is answered with; null when the session has no ship in space.</summary>
    public ModuleResult? Answer { get; set; } = ModuleResult.Done;

    public Task<ModuleResult?> ActivateAsync(ClientSession session, long moduleId, string effectName, long targetId, int repeat, CancellationToken cancellationToken)
    {
        Asked.Add($"activate {moduleId} {effectName} {targetId} {repeat}");
        return Task.FromResult(Answer);
    }

    public Task<ModuleResult?> DeactivateAsync(ClientSession session, long moduleId, string effectName, CancellationToken cancellationToken)
    {
        Asked.Add($"deactivate {moduleId} {effectName}");
        return Task.FromResult(Answer);
    }
}
