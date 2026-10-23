namespace Eve.Relations;

/// <summary>How many objects a subject can have in a relation of a kind.</summary>
public enum Cardinality
{

    Many,

    /// <summary>One: setting a new object ends the old one (a character is in one corporation).</summary>
    OnePerSubject,
}

/// <summary>The rules of a kind of relation. A kind is defined once, in code, and used by name.</summary>
public sealed record RelationKind(string Name)
{
    public double Min { get; init; } = double.NegativeInfinity;
    public double Max { get; init; } = double.PositiveInfinity;
    public Cardinality Cardinality { get; init; } = Cardinality.Many;

    /// <summary>Whether every change is written to the history.</summary>
    public bool KeepHistory { get; init; } = true;

    public double Clamp(double value) => Math.Clamp(value, Min, Max);
}

/// <summary>The kinds the game has. Add yours here (or define it with <see cref="RelationStore.Define"/>) rather than keeping a table of your own.</summary>
public static class RelationKinds
{

    public static readonly RelationKind Standing = new("standing") { Min = -10, Max = 10 };

    /// <summary>A character's security status; the object is <see cref="RelationStore.World"/>, −10…10.</summary>
    public static readonly RelationKind SecurityStatus = new("security_status") { Min = -10, Max = 10 };

    /// <summary>subject (the aggressor) is at war with object (the defender); attributes hold the rest (mutual, open for allies, who declared). <c>until</c> is the end of a retracted war.</summary>
    public static readonly RelationKind War = new("war");

    /// <summary>subject (the ally) fights in a war for a side; the object is the id of the <c>war</c> relation, the attributes say which side.</summary>
    public static readonly RelationKind WarAlly = new("war.ally");

    /// <summary>subject (a character) is a member of object (a corporation).</summary>
    public static readonly RelationKind CorporationMember = new("affiliation.corporation") { Cardinality = Cardinality.OnePerSubject };

    /// <summary>subject (a corporation) is a member of object (an alliance).</summary>
    public static readonly RelationKind AllianceMember = new("affiliation.alliance") { Cardinality = Cardinality.OnePerSubject };

    /// <summary>subject (a corporation, an alliance or an enlisted character) belongs to object (a faction).</summary>
    public static readonly RelationKind FactionMember = new("affiliation.faction") { Cardinality = Cardinality.OnePerSubject };

    public static IReadOnlyList<RelationKind> All { get; } = [Standing, SecurityStatus, War, WarAlly, CorporationMember, AllianceMember, FactionMember];
}
