namespace Eve.Services;

/// <summary>
/// The account role bits of the game masters (carbon/common/script/sys/serviceConst.py in the V24.01 client). The bits come from the
/// account row at login (<c>accountRole</c> on the session); the client's own <c>role</c> is not a grant, so a player cannot give
/// one to himself through a session attribute. The accounts that hold them are set through the database (tests seed them, a
/// GM is granted with <c>UPDATE accounts SET role = ...</c>).
/// </summary>
public static class GameMasterRoles
{
    public const long PinkChat = 1L << 6;      // ROLE_PINKCHAT
    public const long Centurion = 1L << 11;    // ROLE_CENTURION
    public const long ChtInvisible = 1L << 20; // ROLE_CHTINVISIBLE
    public const long Legioneer = 1L << 18;    // ROLE_LEGIONEER
    public const long Gms = 1L << 38;          // ROLE_GMS
    public const long ExtGm1 = 1L << 46;      // ROLE_EXT_GM1
    public const long ExtGm2 = 1L << 47;      // ROLE_EXT_GM2
    public const long ExtGm3 = 1L << 48;      // ROLE_EXT_GM3
    public const long ExtGm4 = 1L << 49;      // ROLE_EXT_GM4
    public const long Programmer = 1L << 51;  // ROLE_PROGRAMMER
    public const long Qa = 1L << 52;          // ROLE_QA
    public const long Gmh = 1L << 53;         // ROLE_GMH
    public const long Gml = 1L << 54;         // ROLE_GML
    public const long Content = 1L << 55;     // ROLE_CONTENT
    public const long Admin = 1L << 56;       // ROLE_ADMIN

    /// <summary>Every staff role that may use the in-game GM tools (the slash commands, the QA helpers).</summary>
    public const long Staff = Admin | Content | Gml | Gmh | Qa | ExtGm1 | ExtGm2 | ExtGm3 | ExtGm4;

    private static readonly Dictionary<string, long> RoleNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["GML"] = Gml, ["GMH"] = Gmh, ["QA"] = Qa, ["PROGRAMMER"] = Programmer, ["CONTENT"] = Content, ["ADMIN"] = Admin,
        ["EXT_GM1"] = ExtGm1, ["EXT_GM2"] = ExtGm2, ["EXT_GM3"] = ExtGm3, ["EXT_GM4"] = ExtGm4,
    };

    /// <summary>
    /// Parses the grants of EVE_GM_ACCOUNTS: <c>user=ROLE+ROLE,other=ROLE</c>, with the names of <see cref="RoleNames"/>. An unknown
    /// name or an empty grant is a format error (the host does not start with a grant it cannot read).
    /// </summary>
    public static IReadOnlyList<(string UserName, long Mask)> ParseGrants(string spec)
    {
        var grants = new List<(string, long)>();
        foreach (var entry in spec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = entry.Split('=', 2);
            if (parts.Length != 2 || parts[0].Trim().Length == 0)
                throw new FormatException($"EVE_GM_ACCOUNTS entry \"{entry}\": expected user=ROLE+ROLE");
            long mask = 0;
            foreach (var role in parts[1].Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                mask |= RoleNames.TryGetValue(role, out var bit) ? bit : throw new FormatException($"EVE_GM_ACCOUNTS: unknown role \"{role}\"");
            if (mask == 0) throw new FormatException($"EVE_GM_ACCOUNTS entry \"{entry}\": no role");
            grants.Add((parts[0].Trim(), mask));
        }
        return grants;
    }

    /// <summary>True when the session's account holds any bit of <paramref name="mask"/>.</summary>
    public static bool Has(ClientSession session, long mask) => ((session.GetInt("accountRole") ?? 0) & mask) != 0;
}
