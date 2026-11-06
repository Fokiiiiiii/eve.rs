using Eve.Macho;
using Eve.Marshal;
using Eve.Space;

namespace Eve.Services.Space;

public readonly record struct WarpCoordinates(int SolarSystemId, double X, double Y, double Z);
