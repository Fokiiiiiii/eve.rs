using Eve.Characters;

namespace Eve.Host;

internal sealed partial class CharacterServicesComposition
{
    /// <summary>The New Eden store introduction states (the characters schema's <c>nes_intro_states</c>).</summary>
    private NesIntroStore nesIntro = default!;

    private void NesIntroStoreSetup() => nesIntro = new NesIntroStore(source, characterSchema, now);
}
