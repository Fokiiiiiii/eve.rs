namespace Eve.Services;

/// <summary>
/// The account role bits of the game masters (carbon/common/script/sys/serviceConst.py in the V24.01 client). The bits come from the
/// account row at login (<c>accountRole</c> on the session); the client's own <c>role</c> is not a grant, so a player cannot give
/// one to himself through a session attribute. The accounts that hold them are set through the database (tests seed them, a
/// GM is granted with <c>UPDATE accounts SET role = ...</c>).
/// </summary>
public static class GameMasterRoles
{
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

    /// <summary>True when the session's account holds any bit of <paramref name="mask"/>.</summary>
    public static bool Has(ClientSession session, long mask) => ((session.GetInt("accountRole") ?? 0) & mask) != 0;
}
