import importlib.util
import pathlib
import sys
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


if __name__ == "__main__":
    unittest.main()
