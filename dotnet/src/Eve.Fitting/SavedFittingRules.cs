namespace Eve.Fitting;

/// <summary>
/// names, so the service can answer the client's own error message.
/// </summary>
public sealed record FittingFault(string Code, long Value = 0);

public static class SavedFittingRules
{
    public const int CategoryShip = 6, CategoryStructure = 65;
    public const int MaxName = 50, MaxDescription = 500;

    public static readonly int[] NonSlotFlags = [5, 87, 158];

    public static (string Name, string Description) Normalize(string? name, string? description) =>
        ((name ?? "").Trim() is var trimmed && trimmed.Length > MaxName ? trimmed[..MaxName] : trimmed,
         (description ?? "") is var text && text.Length > MaxDescription ? text[..MaxDescription] : text);

    /// <summary>The fault in a draft, or null when it may be saved. <paramref name="categoryOf"/> gives the category of a type, or null when it does not exist.</summary>
    public static FittingFault? Check(FittingDraft draft, Func<int, int?> categoryOf)
    {
        if (draft.Name.Trim().Length == 0)
        {
            return new FittingFault("FITTING_NAME_REQUIRED");
        }
        if (draft.Name.Contains("@@", StringComparison.Ordinal) || draft.Description.Contains("@@", StringComparison.Ordinal))
        {
            return new FittingFault("FITTING_INVALID_TEXT");
        }
        if (draft.ShipTypeId <= 0 || categoryOf(draft.ShipTypeId) is not { } shipCategory)
        {
            return new FittingFault("FITTING_INVALID_TYPE_ID", draft.ShipTypeId);
        }
        if (shipCategory is not (CategoryShip or CategoryStructure))
        {
            return new FittingFault("FITTING_INVALID_SHIP_TYPE", draft.ShipTypeId);
        }
        if (draft.FitData.Count == 0)
        {
            return new FittingFault("FITTING_DATA_EMPTY");
        }
        if (draft.FitData.Count > 500)
        {
            return new FittingFault("FITTING_TOO_MANY_ITEMS", draft.FitData.Count);
        }
        foreach (var item in draft.FitData)
        {
            if (categoryOf(item.TypeId) is null)
            {
                return new FittingFault("FITTING_INVALID_TYPE_ID", item.TypeId);
            }
            if (item.Flag <= 0 || !(Slots.IsSlotFlag(item.Flag) || NonSlotFlags.Contains(item.Flag)))
            {
                return new FittingFault("FITTING_INVALID_FLAG", item.TypeId);
            }
            if (item.Quantity <= 0)
            {
                return new FittingFault("FITTING_INVALID_QUANTITY", item.TypeId);
            }
        }
        return null;
    }
}
