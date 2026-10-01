using Eve.Corporations;
using Eve.Marshal;

namespace Eve.Services.Corporations;

public static class CorporationWire
{
    private static PyValue None => PyNone.Instance;
    public static PyValue Id(long? id) => id is > 0 ? new PyInt(id.Value) : None;
    public static PyValue Time(long ms) => new PyLong(Py.FileTimeFromUnixMs(ms));
    private static PyValue Setting(Corporation c,string key,PyValue? fallback = null) =>
        c.Settings.TryGetProperty(key,out var v) ? v.ValueKind switch
        {
            System.Text.Json.JsonValueKind.String => new PyStr(v.GetString()!),
            System.Text.Json.JsonValueKind.Number => Py.Number(v.GetDouble()),
            System.Text.Json.JsonValueKind.True => new PyInt(1),
            System.Text.Json.JsonValueKind.False => new PyInt(0),
            _ => None,
        } : fallback ?? None;

    public static (string Key,PyValue Value)[] Fields(Corporation c,int members) =>
    [
        ("corporationID",new PyInt(c.Id)),("corporationName",new PyStr(c.Name)),
        ("ticker",new PyStr(c.Ticker)),("tickerName",new PyStr(c.Ticker)),
        ("ceoID",Id(c.CeoId)),("creatorID",Id(c.CreatorId)),("allianceID",Id(c.AllianceId)),
        ("factionID",Setting(c,"factionID")),("warFactionID",Setting(c,"warFactionID",Setting(c,"factionID"))),
        ("membership",new PyInt(1)),("description",new PyStr(c.Description)),("url",new PyStr(c.Url)),
        ("stationID",Id(c.StationId)),("deleted",new PyInt(c.Deleted ? 1 : 0)),
        ("taxRate",Py.Number((double)c.TaxRate)),("loyaltyPointTaxRate",Py.Number((double)c.LoyaltyPointTaxRate)),
        ("friendlyFire",new PyInt(c.FriendlyFire ? 1 : 0)),("memberCount",new PyInt(members)),("memberLimit",new PyInt(c.MemberLimit)),
        ("shares",Setting(c,"shares",new PyInt(c.IsNpc ? 0 : 1000))),
        // Until structure/war eligibility is ported no corporation is eligible to declare war.
        ("allowWar",PyBool.False),("allowedMemberRaceIDs",Setting(c,"allowedMemberRaceIDs",new PyInt(0))),
        ("corporationType",Setting(c,"corporationType",new PyInt(0))),("minimumJoinStanding",Setting(c,"minimumJoinStanding",new PyInt(0))),
        ("sendCharTerminationMessage",PyBool.True),("createDate",Time(c.CreatedMs)),
        ("aggressionEnableAfter",None),("aggressionDisableAfter",None),
        ("applicationsEnabled",Setting(c,"applicationsEnabled",new PyInt(0))),
        .. Enumerable.Range(1,7).Select(i => ($"division{i}",Division(c,i))),
        .. Enumerable.Range(1,7).Select(i => ($"walletDivision{i}",Division(c,i+7))),
        .. new[]{"shape1","shape2","shape3","color1","color2","color3","typeface"}.Select(key => (key,Setting(c,key))),
        ("isRecruiting",new PyInt(c.Settings.TryGetProperty("applicationsEnabled",out var app) &&
            (app.ValueKind == System.Text.Json.JsonValueKind.False || app.ValueKind == System.Text.Json.JsonValueKind.Number && app.GetInt32() == 0) ? 0 : 1)),
    ];

    private static PyValue Division(Corporation c,int index) =>
        c.Settings.TryGetProperty("divisionNames",out var names) && names.TryGetProperty(index.ToString(),out var name)
            ? new PyStr(name.GetString() ?? "") : new PyStr(index <= 7 ? $"Division {index}" : $"Wallet Division {index-7}");

    public static PyValue Row(Corporation c,int members)
    {
        var fields = Fields(c,members);
        return new PyObject(new PyStr("util.Row"),Py.Dict(
            ("header",Py.Tuple([.. fields.Select(f => (PyValue)new PyStr(f.Key))])),
            ("line",Py.Tuple([.. fields.Select(f => f.Value)]))));
    }
    public static PyValue PublicInfo(Corporation c,int members)
    {
        string[] keys = ["corporationID","corporationName","ticker","tickerName","ceoID","creatorID","allianceID","warFactionID",
            "description","stationID","shares","deleted","url","taxRate","loyaltyPointTaxRate","friendlyFire","allowWar","memberCount",
            "applicationsEnabled","isRecruiting","shape1","shape2","shape3","color1","color2","color3","typeface"];
        var fields = Fields(c,members).ToDictionary(f => f.Key,f => f.Value);
        return Py.KeyVal([.. keys.Select(key => (key,fields[key]))]);
    }

    private static Descriptor DescriptorOf(params (string Name,int Type)[] columns) => Descriptor.FromHeader(new PyObjectEx(ObjectExKind.Ex1,
        Py.Tuple(new PyToken("blue.DBRowDescriptor"),Py.Tuple(Py.Tuple([.. columns.Select(c => (PyValue)Py.Tuple(new PyStr(c.Name),new PyInt(c.Type)))]))),[],[]));
    private static readonly Descriptor MemberDescriptor = DescriptorOf(
        ("characterID",3),("corporationID",3),("divisionID",3),("squadronID",3),("title",130),("roles",64),("grantableRoles",64),
        ("startDateTime",64),("baseID",3),("rolesAtHQ",64),("grantableRolesAtHQ",64),("rolesAtBase",64),("grantableRolesAtBase",64),
        ("rolesAtOther",64),("grantableRolesAtOther",64),("titleMask",3),("accountKey",3),("rowDate",64),("blockRoles",11),("ownerName",130));
    private static readonly Descriptor TitleDescriptor = DescriptorOf(("titleID",3),("titleName",130),
        ("roles",20),("grantableRoles",20),("rolesAtHQ",20),("grantableRolesAtHQ",20),
        ("rolesAtBase",20),("grantableRolesAtBase",20),("rolesAtOther",20),("grantableRolesAtOther",20));
    private static PyValue[] Masks(RoleMasks m) => [.. new[]{m.Roles,m.GrantableRoles,m.AtHq,m.GrantableHq,m.AtBase,m.GrantableBase,m.AtOther,m.GrantableOther}.Select(v => (PyValue)new PyLong(v))];
    public static PyValue Member(Member m,string name) => new PyPackedRow(MemberDescriptor,[
        new PyInt(m.CharacterId),new PyInt(m.CorporationId),new PyInt(m.DivisionId),new PyInt(m.SquadronId),new PyWString(m.Title),
        new PyLong(m.Masks.Roles),new PyLong(m.Masks.GrantableRoles),Time(m.StartedMs),Id(m.BaseId),
        new PyLong(m.Masks.AtHq),new PyLong(m.Masks.GrantableHq),new PyLong(m.Masks.AtBase),new PyLong(m.Masks.GrantableBase),
        new PyLong(m.Masks.AtOther),new PyLong(m.Masks.GrantableOther),new PyInt(m.TitleMask),new PyInt(m.AccountKey),Time(m.StartedMs),
        m.BlockRoles ? PyBool.True : PyBool.False,new PyWString(name)]);
    public static PyValue Titles(IReadOnlyList<CorporationTitle> titles) => new PyObjectEx(ObjectExKind.Ex2,
        Py.Tuple(Py.Tuple(new PyToken("carbon.common.script.sys.crowset.CIndexedRowset")),
            Py.Dict(("header",TitleDescriptor.Header),("idName",new PyStr("titleID")),("columnName",new PyStr("titleID")))),[],
        [.. titles.Select(t => new KeyValuePair<PyValue,PyValue>(new PyInt(t.Id),
            new PyPackedRow(TitleDescriptor,[new PyInt(t.Id),new PyWString(t.Name),.. Masks(t.Masks)])))]);
}
