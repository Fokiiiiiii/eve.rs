using Eve.Dogma;

namespace Eve.Ships;

/// <summary>
/// What a mining cycle can take: an asteroid (its ore or ice) or a harvestable gas cloud (its compressed gas, CCP's Harvestable Cloud group). One rule for the
/// drone and the module cycle, the target checks and the spawn of the belt.
/// </summary>
public static class MiningTargets
{
    public const string GasCloudGroup = "Harvestable Cloud";

    public static bool IsGasCloud(DogmaStatic statics, TypeDef type) =>
        statics.GroupName(type.GroupId).Equals(GasCloudGroup, StringComparison.OrdinalIgnoreCase);

    public static bool IsMineable(DogmaStatic statics, TypeDef type) =>
        IsGasCloud(statics, type) || statics.CategoryName(type.CategoryId) == "Asteroid";
}
