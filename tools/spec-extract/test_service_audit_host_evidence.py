import importlib.util
import pathlib
import sys
import tempfile
import unittest

ROOT = pathlib.Path(__file__).resolve().parents[2]
SCRIPT = ROOT / "tools/spec-extract/run_service_audit_current.py"
SPEC = importlib.util.spec_from_file_location("current_service_audit_evidence", SCRIPT)
current_service_audit = importlib.util.module_from_spec(SPEC)
sys.modules[SPEC.name] = current_service_audit
SPEC.loader.exec_module(current_service_audit)


class HostWireTestEvidenceTests(unittest.TestCase):
    def test_registered_start_connect_send_test_counts_as_wire_evidence(self):
        with tempfile.TemporaryDirectory() as temporary:
            repo_root = pathlib.Path(temporary)
            test_file = repo_root / "dotnet/tests/Eve.Host.Tests/CharMgrPublicInfoHostTests.cs"
            test_file.parent.mkdir(parents=True)
            test_file.write_text(
                """
public class HostServerTests
{
    [Fact]
    public async Task Char_mgr_get_public_info_returns_client_keyval_through_host_with_macho_version()
    {
        var service = new CharMgrService();
        Start(configure: registry => registry.Register(service));
        using var front = await Handshaken(await ConnectAsync());
        await front.SendAsync(MakeCall("GetPublicInfo"));
        await front.ReceiveAsync();
    }
}
"""
            )
            found = current_service_audit.filtered_test_evidence(
                repo_root,
                {("charMgr", "GetPublicInfo")},
                {"charMgr": "CharMgrService"},
            )
        self.assertEqual(
            {
                ("charMgr", "GetPublicInfo"): [
                    "dotnet/tests/Eve.Host.Tests/CharMgrPublicInfoHostTests.cs:"
                    "Char_mgr_get_public_info_returns_client_keyval_through_host_with_macho_version"
                ]
            },
            found,
        )


if __name__ == "__main__":
    unittest.main()
