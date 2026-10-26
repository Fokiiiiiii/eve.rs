import importlib.util
import pathlib
import sys
import tempfile
import unittest

ROOT = pathlib.Path(__file__).resolve().parents[2]
SCRIPT = ROOT / "tools/spec-extract/run_service_audit_current.py"
SPEC = importlib.util.spec_from_file_location("false_positive_service_audit", SCRIPT)
audit = importlib.util.module_from_spec(SPEC)
sys.modules[SPEC.name] = audit
SPEC.loader.exec_module(audit)


class ReceiverTracingTests(unittest.TestCase):
    def test_methods_on_remote_call_results_are_local(self):
        contract = audit.audit.load_contract()
        source = """
class Example:
    def Calls(self):
        agents = sm.RemoteSvc('agentMgr').GetAgents().Clone()
        agents.AddField('name', None)
        agents.Index('agentID')
        agents.Filter('stationID')
        agents.get('id')
        petitioner = sm.RemoteSvc('petitionSvc').GetPetitions()
        petitioner.append(1)
        petitioner.has_key(1)
        dungeon = sm.RemoteSvc('map').GetDeadspaceComplexMap()
        dungeon.insert(0, 1)
        dungeon.sort()
        fighterMgr = sm.RemoteSvc('fighterMgr').GetFighters()
        fighterMgr.iteritems()
        planetMgr = sm.RemoteSvc('planetMgr').GetPlanets()
        planetMgr.append(1)
        map = sm.RemoteSvc('map').GetMap()
        map.Index('id')
        map.get('id')
        map.iteritems()
        map.keys()
"""
        calls = audit.audit.extended_calls(contract, pathlib.Path("snippet.py"), source)
        local_methods = {
            ("agentMgr", "AddField"), ("agentMgr", "Index"), ("agentMgr", "Filter"),
            ("agentMgr", "get"), ("petitionSvc", "append"), ("petitionSvc", "has_key"),
            ("map", "insert"), ("map", "sort"), ("fighterMgr", "iteritems"),
            ("planetMgr", "append"), ("map", "Index"), ("map", "get"),
            ("map", "iteritems"), ("map", "keys"),
        }
        self.assertTrue(local_methods.isdisjoint({(call["service"], call["method"]) for call in calls}))

    def test_remote_connections_moniker_and_bound_proxy_remain_calls(self):
        contract = audit.audit.load_contract()
        source = """
class Example:
    def Calls(self):
        sm.ConnectToRemoteService('x').Method()
        remote = sm.RemoteSvc('y')
        remote.NoBlock.Method()
        Moniker('invbroker', 1).GetItem(2)
        proxy = Moniker('beyonce', 1)
        proxy.CmdStargateJump(2)
        session.ConnectToSolarSystemService('beyonce').GetSolarSystem()
"""
        calls = audit.audit.extended_calls(contract, pathlib.Path("snippet.py"), source)
        actual = {(call["service"], call["method"]) for call in calls}
        bound_calls = contract.source_calls(pathlib.Path("snippet.py"), source, {})
        actual.update((call["service"], call["method"]) for call in bound_calls)
        self.assertTrue({("x", "Method"), ("y", "Method"), ("invbroker", "GetItem"),
                         ("beyonce", "CmdStargateJump"),
                         ("beyonce", "GetSolarSystem")}.issubset(actual))



    def test_inventory_add_routes_and_unreferenced_fleet_calls_are_classified(self):
        generator_spec = importlib.util.spec_from_file_location(
            "p0_service_audit_generator", ROOT / "tools/spec-extract/generate_service_audit.py"
        )
        generator = importlib.util.module_from_spec(generator_spec)
        sys.modules[generator_spec.name] = generator
        generator_spec.loader.exec_module(generator)
        contract = generator.load_contract()
        source = """
class FittingSlotController:
    def UnfitModule(self):
        invCache.GetInventory(const.containerHangar).Add(self.GetModuleID(), parentID, flag=const.flagHangar)

class Nodegraph:
    def start(self):
        sm.GetService('invCache').GetInventory(containerHangar).Add(self.item_id, session.shipid, flag=flagHangar)
"""
        for line in (4, 8):
            self.assertTrue(
                generator.rpc_context(contract, source, line, method="Add").endswith(
                    "; reply ignored by caller statement"
                )
            )
        wrapped_call = """
class Example:
    def call(self):
        logger.info(sm.RemoteSvc('x').Method())
"""
        wrapped_line = next(
            index for index, text in enumerate(wrapped_call.splitlines(), start=1)
            if "Method()" in text
        )
        self.assertTrue(
            generator.rpc_context(contract, wrapped_call, wrapped_line, method="Method").endswith(
                "; reply consumed inline by caller expression"
            )
        )

        calls = contract.source_calls(
            pathlib.Path("snippet.py"), source, {"GetInventory": {"charMgr"}}
        )
        self.assertEqual(
            {
                ("invbroker", "Add", ("self.GetModuleID()", "parentID", "flag=const.flagHangar")),
                ("invbroker", "Add", ("self.item_id", "session.shipid", "flag=flagHangar")),
            },
            {
                (call["service"], call["method"], tuple(call["arguments"]))
                for call in calls
            },
        )

        with tempfile.TemporaryDirectory() as temporary:
            repo_root = pathlib.Path(temporary)
            service_file = repo_root / "dotnet/src/Eve.Services.Inventory/InvBrokerService.cs"
            service_file.parent.mkdir(parents=True)
            service_file.write_text(
                '[Service("invbroker")]\n'
                'public class InvBrokerService {\n'
                '    [Method("Add")] public PyValue Add() => PyNone.Instance;\n'
                '}\n'
            )
            rpc_file = repo_root / "dotnet/src/Eve.Host/Rpc/InventoryRpc.cs"
            rpc_file.parent.mkdir(parents=True)
            rpc_file.write_text(
                "var inventory = HostInventoryServiceFactory.CreateInvBroker();\n"
                "registry.Register(inventory);\n"
            )
            test_file = repo_root / "dotnet/tests/Eve.Host.Tests/ServiceAuditP0HostWireTests.cs"
            test_file.parent.mkdir(parents=True)
            test_file.write_text(
                """
public class ServiceAuditP0HostWireTests
{
    [Fact]
    public async Task Invbroker_add_accepts_client_kwargs_through_registered_host_route()
    {
        var broker = new InvBrokerService();
        await Packets.CallWithKwargs("invbroker", "Add");
    }
}
"""
            )
            routes, service_types, _ = generator.host_routes(contract, repo_root)
            pair = ("invbroker", "Add")
            self.assertIn(pair, routes)
            wire_tests = generator.test_evidence(repo_root, {pair}, service_types)
            self.assertIn(
                "dotnet/tests/Eve.Host.Tests/ServiceAuditP0HostWireTests.cs:"
                "Invbroker_add_accepts_client_kwargs_through_registered_host_route",
                wire_tests[pair],
            )
            status, _ = generator.status_for(pair, routes, wire_tests, contract)
            self.assertTrue(status.startswith("IMPLEMENTED-TESTED"))

        for method in ("GetFleetLocationAndInfo", "GetFleetState", "GetRelativity"):
            status, _ = generator.status_for(("beyonce", method), routes, {}, contract)
            self.assertEqual("CLIENT-DEAD", status)

        with tempfile.TemporaryDirectory() as queue_dir:
            self.assertEqual(
                [],
                generator.queue_jobs(
                    [{
                        "row_type": "rpc",
                        "core_play_priority": "P0",
                        "status": "CLIENT-DEAD",
                        "service_family": "space",
                        "service": "beyonce",
                        "method": "GetFleetState",
                        "row_id": "RPC-00001",
                    }],
                    pathlib.Path(queue_dir),
                ),
            )

    def test_host_harness_accepts_shared_socket_helpers(self):
        with tempfile.TemporaryDirectory() as temporary:
            repo_root = pathlib.Path(temporary)
            test_file = repo_root / "dotnet/tests/Eve.Host.Tests/SharedHostWireTests.cs"
            test_file.parent.mkdir(parents=True)
            test_file.write_text(
                """
public partial class HostServerTests
{
    [PgFact]
    public async Task Client_read_uses_host_wire()
    {
        var service = new ClientService();
        Start(
            configure: registry =>
            {
                registry.Register(service);
                registry.RegisterAlias("example", "exampleAlias");
            });

        var (front, open) = await OpenFrontAsync();
        await HostRpcAsync(front, open, 1, "example", "Read");
    }
}
"""
            )
            matches = audit.host_harness_test_evidence(
                repo_root, {("example", "Read")}, {"example": "ClientService"}
            )
            self.assertIn(
                "dotnet/tests/Eve.Host.Tests/SharedHostWireTests.cs:Client_read_uses_host_wire",
                matches[("example", "Read")],
            )

    def test_host_test_with_multiple_pg_facts_is_detected(self):
        matches = audit.host_harness_test_evidence(
            ROOT, {("charMgr", "BlockOwners")}, {}
        )
        self.assertIn(
            "dotnet/tests/Eve.Host.Tests/CharacterManagerMutationHostWireTests.cs:"
            "Character_manager_mutations_persist_through_registered_host_wire",
            matches[("charMgr", "BlockOwners")],
        )

    def test_default_host_composition_and_shared_socket_test_is_detected(self):
        pairs = {("keeper", "GetLevelEditor"), ("localizationServer", "GetAllTextChanges")}
        matches = audit.host_harness_test_evidence(ROOT, pairs, {})
        test_name = (
            "dotnet/tests/Eve.Host.Tests/FocusedRpc01742HostWireTests.cs:"
            "Keeper_and_localization_RPCs_return_the_EveJS_shapes_through_host_registration"
        )
        self.assertIn(test_name, matches[("keeper", "GetLevelEditor")])
        self.assertIn(test_name, matches[("localizationServer", "GetAllTextChanges")])

if __name__ == "__main__":
    unittest.main()
