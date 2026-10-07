using Eve.Macho;
using Eve.Marshal;
using Eve.Services;

namespace Eve.Services.CharacterSelect;

[Service("multiLoginBlocker")]
public sealed partial class MultiLoginBlockerService
{
    [Method("Login")]
    public PyValue Login(ServiceCall _) => PyBool.True;
}
