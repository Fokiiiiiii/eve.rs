using System.Globalization;
using System.Text;

namespace Eve.Characters;

/// <summary>What <c>ValidateNameEx</c> answers: 1 for a usable name, a negative code for why not.</summary>
public enum NameCheck
{
    Valid = 1,
    TooShort = -1,
    TooLong = -2,
    IllegalCharacter = -5,
    TooManySpaces = -6,
    ConsecutiveSpaces = -7,
    Unavailable = -101,
    Reserved = -102,
}

/// <summary>The rules for a character's name, as the client's creation screen expects to be told them.</summary>
public static class CharacterNames
{
    public const int MinLength = 3;
    public const int MaxLength = 37;
    public const int MaxSpaces = 2;

    /// <summary>Names of staff: the client shows them specially, so nobody else may take them.</summary>
    private static readonly string[] ProtectedPrefixes = ["ccp ", "gm ", "isd "];

    /// <summary>The white space JavaScript's <c>trim</c> and <c>\s</c> know, which is not quite .NET's.</summary>
    public static bool IsSpace(char c) => c is '\t' or '\n' or '\v' or '\f' or '\r' or ' '
        or (char)0xA0 or (char)0x1680 or (>= (char)0x2000 and <= (char)0x200A) or (char)0x2028 or (char)0x2029
        or (char)0x202F or (char)0x205F or (char)0x3000 or (char)0xFEFF;

    /// <summary>
    /// Fails, with the fix, when this process cannot normalise Unicode (.NET does not without ICU, or with invariant
    /// globalization): names would then not fold to one key, and two spellings of a name could both be taken.
    /// </summary>
    public static void EnsureUnicodeSupport()
    {
        // U+FF21 (a full-width "A") has the compatibility form "A"; U+0065 U+0301 composes to U+00E9.
        if ("\uFF21".Normalize(NormalizationForm.FormKC) != "A" || "e\u0301".Normalize(NormalizationForm.FormKC) != "\u00E9")
        {
            throw new PlatformNotSupportedException(
                "Unicode normalisation is not available: install ICU (libicu) and do not set DOTNET_SYSTEM_GLOBALIZATION_INVARIANT.");
        }
    }

    /// <summary>The name as it is compared and stored: compatibility-normalised and trimmed.</summary>
    public static string Clean(string? raw)
    {
        var text = (raw ?? "").Normalize(NormalizationForm.FormKC);
        var start = 0;
        var end = text.Length;
        while (start < end && IsSpace(text[start]))
        {
            start++;
        }
        while (end > start && IsSpace(text[end - 1]))
        {
            end--;
        }
        return text[start..end];
    }

    /// <summary>The name with each run of white space made one space.</summary>
    public static string Collapse(string cleaned)
    {
        var builder = new StringBuilder(cleaned.Length);
        var inSpace = false;
        foreach (var c in cleaned)
        {
            if (IsSpace(c))
            {
                if (!inSpace)
                {
                    builder.Append(' ');
                }
                inSpace = true;
            }
            else
            {
                builder.Append(c);
                inSpace = false;
            }
        }
        return builder.ToString();
    }

    /// <summary>The key two spellings of one name share: "Ann  Lee" and "ann lee" are the same name.</summary>
    public static string Key(string? raw) => Collapse(Clean(raw)).ToLowerInvariant();

    private static bool IsAllowed(Rune rune)
    {
        if (rune.Value is '\'' or ' ' or '-')
        {
            return true;
        }
        return Rune.GetUnicodeCategory(rune) is
            UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter or UnicodeCategory.TitlecaseLetter
            or UnicodeCategory.ModifierLetter or UnicodeCategory.OtherLetter
            or UnicodeCategory.DecimalDigitNumber or UnicodeCategory.LetterNumber or UnicodeCategory.OtherNumber;
    }

    /// <summary>
    /// Whether <paramref name="raw"/> may be a name at all, in the order the client's screen reports problems.
    /// Whether it is already taken is the store's question (<see cref="NameCheck.Unavailable"/>).
    /// </summary>
    public static NameCheck Validate(string? raw)
    {
        var name = Clean(raw);
        if (name.Length < MinLength)
        {
            return NameCheck.TooShort;
        }
        if (name.Length > MaxLength)
        {
            return NameCheck.TooLong;
        }
        for (var i = 1; i < name.Length; i++)
        {
            if (IsSpace(name[i]) && IsSpace(name[i - 1]))
            {
                return NameCheck.ConsecutiveSpaces;
            }
        }
        if (name.Count(c => c == ' ') > MaxSpaces)
        {
            return NameCheck.TooManySpaces;
        }
        foreach (var rune in name.EnumerateRunes())
        {
            if (!IsAllowed(rune))
            {
                return NameCheck.IllegalCharacter;
            }
        }
        var key = Key(name);
        return ProtectedPrefixes.Any(prefix => key.StartsWith(prefix, StringComparison.Ordinal)) ? NameCheck.Reserved : NameCheck.Valid;
    }
}
