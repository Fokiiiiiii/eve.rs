using Eve.Macho;
using Eve.Marshal;
using Eve.Space;

namespace Eve.Services.Space;

internal readonly record struct FleetFormationSettings(
    int Type,
    double SpacingMeters,
    double SizeMeters,
    int SpacingSkillLevel = 0,
    int SizeSkillLevel = 0);
