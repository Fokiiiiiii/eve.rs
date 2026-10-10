namespace Eve.Services.Space.Sites;

/// <summary>
/// What a site takes from a seed: a number in [0, 1) and its code, three letters and three digits (the form of the codes the client lists: ABC-123). A seed is text, so the
/// same seed gives the same place and the same code on every run; a respawn changes the seed.
/// </summary>
public static class SiteSeed
{
    private const int Letters = 26;
    private const int CodeSpace = Letters * Letters * Letters * 1000;

    /// <summary>FNV-1a over the UTF-16 units of the text.</summary>
    public static uint Hash(string seed)
    {
        var hash = 2166136261u;
        foreach (var unit in seed)
        {
            hash = (hash ^ unit) * 16777619u;
        }
        return hash;
    }

    public static double Fraction(string seed) => Hash(seed) / 4294967296.0;

    /// <summary>The code of a seed: <c>ABC-123</c>. Two seeds can give one code; the director asks for another seed when a system already has it.</summary>
    public static string Code(string seed)
    {
        var value = (int)(Hash(seed) % CodeSpace);
        var letters = value / 1000;
        var number = value % 1000;
        return string.Create(7, (letters, number), static (span, state) =>
        {
            span[0] = (char)('A' + state.letters / (Letters * Letters));
            span[1] = (char)('A' + state.letters / Letters % Letters);
            span[2] = (char)('A' + state.letters % Letters);
            span[3] = '-';
            span[4] = (char)('0' + state.number / 100);
            span[5] = (char)('0' + state.number / 10 % 10);
            span[6] = (char)('0' + state.number % 10);
        });
    }
}
