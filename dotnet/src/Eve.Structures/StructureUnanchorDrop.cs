using System;

namespace Eve.Structures;

public sealed record StructureUnanchorDrop(long HullItemId, long FittingsContainerItemId)
{
    // SDE 3396210 type 10167 is the Abandoned Container used for fitted structure assets.
    public const int FittingsContainerTypeId = 10167;

    private const string MarkerPrefix = "evejs:structure-unanchor";

    public static string HullMarker(long structureId) => $"{MarkerPrefix}:hull:{structureId}";

    public static string FittingsMarker(long structureId) => $"{MarkerPrefix}:fittings:{structureId}";

    public static bool IsArtifact(string? customInfo) =>
        IsHullArtifact(customInfo) || IsFittingsArtifact(customInfo);


    public static bool IsHullArtifact(string? customInfo) => HasMarker(customInfo, "hull");

    public static bool IsFittingsArtifact(string? customInfo) => HasMarker(customInfo, "fittings");

    private static bool HasMarker(string? customInfo, string kind)
    {
        var prefix = $"{MarkerPrefix}:{kind}:";
        return customInfo is not null &&
            customInfo.StartsWith(prefix, StringComparison.Ordinal) &&
            long.TryParse(customInfo.AsSpan(prefix.Length), System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var structureId) && structureId > 0;
    }

    public static bool IsFittingsContainer(int typeId, string? customInfo) =>
        typeId == FittingsContainerTypeId && IsFittingsArtifact(customInfo);
}
