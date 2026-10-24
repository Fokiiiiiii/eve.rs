import importlib.util
import pathlib
import sys
import tempfile
import unittest

ROOT = pathlib.Path(__file__).resolve().parents[2]
SCRIPT = ROOT / "tools/spec-extract/run_service_audit_current.py"
SPEC = importlib.util.spec_from_file_location("current_service_audit_routes", SCRIPT)
current_service_audit = importlib.util.module_from_spec(SPEC)
sys.modules[SPEC.name] = current_service_audit
SPEC.loader.exec_module(current_service_audit)


class CompositionRouteEvidenceTests(unittest.TestCase):
    def test_partial_character_composition_registers_agent_service_and_its_wire_test(self):
        contract = current_service_audit.audit.load_contract()
        self.assertEqual(
            "[1,\n    2,\n    3]",
            contract.split_arguments("[1,\n    2, \n    3]")[0],
        )
        with tempfile.TemporaryDirectory() as temporary:
            repo_root = pathlib.Path(temporary)
            source_root = repo_root / "dotnet/src"
            host_root = repo_root / "dotnet/src/Eve.Host"
            test_root = repo_root / "dotnet/tests/Eve.Host.Tests"
            core = source_root / "Eve.Services.Core/AgentMissionService.cs"
            core.parent.mkdir(parents=True)
            core.write_text(
                'public sealed class AgentMissionService {\n'
                '    public string Name => "agentMgr";\n'
                '    public bool HasMethod(string method) => method == "GetAgents";\n'
                '}\n'
            )
            composition = host_root / "Composition"
            composition.mkdir(parents=True)
            (composition / "MissionComposition.cs").write_text(
                "agentMissionService = new AgentMissionService();\n"
            )
            (composition / "AgentRpc.cs").write_text(
                "IService[] AgentRpcServices() => [agentMissionService];\n"
            )
            hosting = host_root / "Hosting"
            hosting.mkdir()
            (hosting / "HostComposition.cs").write_text(
                "foreach (var service in characterServices) registry.Register(service);\n"
            )
            routes, type_by_service, _ = current_service_audit.complete_host_routes(
                contract, repo_root
            )
            self.assertEqual("AgentMissionService", type_by_service["agentMgr"])
            self.assertIn(("agentMgr", "GetAgents"), routes)

            test_root.mkdir(parents=True)
            test_file = test_root / "AgentMissionReadWireTests.cs"
            test_file.write_text(
                """
public class AgentMissionReadWireTests
{
    [Fact]
    public async Task Agent_mgr_read_calls_dispatch_through_the_service_registry_with_client_wire_shapes()
    {
        var service = new AgentMissionService();
        var registry = new ServiceRegistry();
        registry.Register(service);
        var dispatcher = new PacketDispatcher(registry);
        await dispatcher.DispatchAsync(Request("GetAgents"));
    }
}
"""
            )
            evidence = current_service_audit.filtered_test_evidence(
                repo_root,
                {("agentMgr", "GetAgents")},
                type_by_service,
            )
            self.assertEqual(
                ["dotnet/tests/Eve.Host.Tests/AgentMissionReadWireTests.cs:"
                 "Agent_mgr_read_calls_dispatch_through_the_service_registry_with_client_wire_shapes"],
                evidence[("agentMgr", "GetAgents")],
            )


if __name__ == "__main__":
    unittest.main()
