import importlib.util
import pathlib
import tempfile
import unittest

ROOT = pathlib.Path(__file__).resolve().parents[2]
SCRIPT = ROOT / "tools/spec-extract/run_service_audit.py"
SPEC = importlib.util.spec_from_file_location("service_audit_runner", SCRIPT)
service_audit = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(service_audit)

CURRENT_SCRIPT = ROOT / "tools/spec-extract/run_service_audit_current.py"
CURRENT_SPEC = importlib.util.spec_from_file_location("current_service_audit_runner", CURRENT_SCRIPT)
current_service_audit = importlib.util.module_from_spec(CURRENT_SPEC)
CURRENT_SPEC.loader.exec_module(current_service_audit)


class ServiceAuditPatternTests(unittest.TestCase):
    def test_remote_patterns_getter_properties_and_stargate_status(self):
        contract = service_audit.audit.load_contract()
        source = """
class Gateway:
    @property
    def XmppChatMgr(self):
        return sm.ProxySvc("XmppChatMgr")
    def GetContractProxySvc(self):
        self.contractProxySvc = sm.ProxySvc("contractProxy")
        return self.contractProxySvc
    def issue_calls(self):
        hostname = self.XmppChatMgr.Hostname()
        info = self.GetContractProxySvc().GetLoginInfo()
        return hostname, info

def issue_calls(session, sm):
    inv = session.ConnectToRemoteService("inventory")
    items = inv.GetInventoryFromId(42)
    ping = sm.ProxySvc("pingService").Ping()
    return sm.GetService("michelle").GetRemotePark().CmdStargateJump(77)
owners = Recordset(EveOwners, "ownerID", "GetOwnersEx", "GetMultiOwnersEx")
"""
        calls = current_service_audit.audit.extended_calls(contract, pathlib.Path("fixture.py"), source)
        found = {(call["service"], call["method"]) for call in calls}
        self.assertTrue({
            ("inventory", "GetInventoryFromId"),
            ("pingService", "Ping"),
            ("beyonce", "CmdStargateJump"),
            ("config", "GetOwnersEx"),
            ("config", "GetMultiOwnersEx"),
            ("XmppChatMgr", "Hostname"),
            ("contractProxy", "GetLoginInfo"),
        }.issubset(found))
        login = next(call for call in calls if call["service"] == "contractProxy" and call["method"] == "GetLoginInfo")
        self.assertIn("reply assigned to info", login["consumer"])

        path = pathlib.Path("eve/client/script/parklife/autopilot.py")
        jump_call = {
            "service": "beyonce",
            "method": "CmdStargateJump",
            "line": 358,
            "arguments": ["destID", "theJump.toCelestialID", "session.shipid"],
            "kind": "bound-object-method-reference",
            "consumer": "reply consumed inline",
        }
        original = current_service_audit._original_extended_calls
        current_service_audit._original_extended_calls = lambda *_args: [jump_call]
        try:
            client_calls = current_service_audit.complete_extended_calls(
                contract, path, "def Update(self): pass\n"
            )
        finally:
            current_service_audit._original_extended_calls = original
        jump = next(call for call in client_calls if call["method"] == "CmdStargateJump")
        self.assertEqual(["destID", "theJump.toCelestialID", "session.shipid"], jump["arguments"])
        self.assertIn("callback to SessionMgr.PerformSessionChange", jump["consumer"])

        pair = ("beyonce", "CmdStargateJump")
        status, evidence = current_service_audit.audit.status_for(
            pair,
            {pair},
            {pair: ["dotnet/tests/Eve.Services.Space.Tests/StargateJumpWireTests.cs:Bound_client_warp_then_stargate_jump_uses_the_wire_and_clears_warp_state"]},
            contract,
        )
        self.assertEqual(
            "IMPLEMENTED-TESTED (dotnet/tests/Eve.Services.Space.Tests/StargateJumpWireTests.cs:Bound_client_warp_then_stargate_jump_uses_the_wire_and_clears_warp_state)",
            status,
        )
        self.assertIn("supersedes the owner log", evidence)

    def test_host_route_extraction_resolves_registered_string_constants(self):
        contract = service_audit.audit.load_contract()
        with tempfile.TemporaryDirectory() as temporary:
            repo_root = pathlib.Path(temporary)
            service_file = repo_root / "dotnet/src/Eve.Services.Core/SearchQueryService.cs"
            registration_file = repo_root / "dotnet/src/Eve.Host/Hosting/HostComposition.cs"
            service_file.parent.mkdir(parents=True)
            registration_file.parent.mkdir(parents=True)
            service_file.write_text(
                'public sealed class SearchQueryService {\n'
                '    private const string QueryMethod = "Query";\n'
                '    public string Name => "search";\n'
                '    public bool HasMethod(string method) => method == QueryMethod;\n'
                '}\n'
            )
            registration_file.write_text("registry.Register(new SearchQueryService(emptySearchSources));\n")
            routes, type_by_service, _ = current_service_audit.complete_host_routes(contract, repo_root)
        self.assertIn(("search", "Query"), routes, repr(type_by_service))


if __name__ == "__main__":
    unittest.main()
