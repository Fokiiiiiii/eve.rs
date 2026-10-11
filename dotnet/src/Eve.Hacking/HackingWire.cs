namespace Eve.Hacking;

// Wire values of the V24.01 client: eve-logic packages/hackingcommon/hackingConstants.py (types, subtypes, events) and
// eve/client/script/ui/hacking/hackingUIConst.py (the grid). The client's names for the two defensive subsystems that the
// wiki calls Virus Suppressor and Restoration Node are SUBTYPE_DS_HONEYPOT_STRENGTH and SUBTYPE_DS_HONEYPOT_HEALING.
public static class HackingWire
{
    public const int GameTypeHacking = 0;
    public const int GameTypeArchaeology = 1;

    public const int TypeNone = -1;
    public const int TypeSegment = 0;
    public const int TypeVirus = 1;
    public const int TypeCore = 2;
    public const int TypeDefenseSoftware = 3;
    public const int TypeUtilityElementTile = 5;
    public const int TypeDataCache = 6;

    public const int SubtypeNone = -1;
    public const int SubtypeCoreLow = 11;
    public const int SubtypeUeSelfRepair = 0;
    public const int SubtypeUeKernelRot = 1;
    public const int SubtypeUeSecondaryVector = 2;
    public const int SubtypeUePolymorphicShield = 3;
    public const int SubtypeDsAntiVirus = 100;
    public const int SubtypeDsFirewall = 101;
    public const int SubtypeDsVirusSuppressor = 102;
    public const int SubtypeDsRestorationNode = 103;

    public const int EventGameLost = 0;
    public const int EventGameWon = 1;
    public const int EventGameStart = 2;
    public const int EventVirusCreated = 3;
    public const int EventObjectKilled = 4;
    public const int EventGameStyle = 5;
    public const int EventAck = 6;
    public const int EventTileFlipped = 100;
    public const int EventTileCreated = 101;
    public const int EventUePickedUp = 200;
    public const int EventUeRemoved = 201;
    public const int EventUeInUse = 202;
    public const int EventDataCacheOpen = 300;
    public const int EventAttack = 301;
    public const int EventKernelRot = 302;
    public const int EventSelfRepair = 303;
    public const int EventSecondaryVector = 304;
    public const int EventPolymorphicShieldHit = 305;
    public const int EventVirusSuppressor = 306;
    public const int EventRestorationHeal = 307;
    public const int EventDistanceIndicator = 308;

    public const int GridColumns = 10;
    public const int GridRows = 9;
}
