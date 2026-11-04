import importlib.util
import pathlib
import sys
import unittest

ROOT = pathlib.Path(__file__).resolve().parents[2]
SCRIPT = ROOT / "tools/spec-extract/run_service_audit_current.py"
SPEC = importlib.util.spec_from_file_location("current_service_audit_local_calls", SCRIPT)
current_service_audit = importlib.util.module_from_spec(SPEC)
sys.modules[SPEC.name] = current_service_audit
SPEC.loader.exec_module(current_service_audit)


class LocalAccessorCallTests(unittest.TestCase):
    def test_dict_and_instance_methods_after_accessors_are_not_rpc_calls(self):
        previous_accessors = current_service_audit.GLOBAL_ACCESSORS
        previous_pattern = current_service_audit.GLOBAL_ACCESSOR_PATTERN
        self.addCleanup(setattr, current_service_audit, "GLOBAL_ACCESSORS", previous_accessors)
        self.addCleanup(setattr, current_service_audit, "GLOBAL_ACCESSOR_PATTERN", previous_pattern)
        contract = current_service_audit.audit.load_contract()
        config_source = """
class MachoNetService:
    def GetGlobalConfig(self):
        return self.session.ConnectToSolServerService('machoNet').GetGlobalConfig()
"""
        self.assertNotIn(
            ("MachoNetService", "GetGlobalConfig"),
            current_service_audit.accessor_definitions(contract, config_source),
        )
        current_service_audit.GLOBAL_ACCESSORS = {
            "GetGlobalConfig": {"machoNet"},
            "guests": {"station"},
        }
        current_service_audit.GLOBAL_ACCESSOR_PATTERN = current_service_audit.audit.re.compile(
            r"\b(?:GetGlobalConfig|guests)\s*(?:\(\s*\)|\.)"
        )
        source = """
class StationSvc:
    def __init__(self):
        self.guests = {}

    def GetGlobalConfig(self):
        return {}

    def GetStationService(self):
        return sm.RemoteSvc("station")

    def GetGuests(self):
        self.GetGlobalConfig().get("key")
        self.guests.clear()
        guests = sm.RemoteSvc("station").GetGuests()
        self.guests.update(guests)
        return self.GetStationService().GetGuests()
"""
        routes, type_by_service, _ = current_service_audit.complete_host_routes(
            contract, ROOT
        )
        self.assertTrue(
            ("search", "QuickQuery") in routes,
            f"search service type: {type_by_service.get('search')!r}",
        )
        self.assertTrue(
            ("invbroker", "GetItemDescriptor") in routes,
            f"invbroker service type: {type_by_service.get('invbroker')!r}",
        )
        wired_pairs = {
            ("config", "GetOwnersEx"),
            ("config", "GetPositionEx"),
            ("config", "GetPositionsEx"),
            ("invbroker", "GetItemDescriptor"),
            ("search", "QuickQuery"),
        }
        self.assertTrue(wired_pairs.issubset(routes))
        host_tests = current_service_audit.host_harness_test_evidence(
            ROOT, wired_pairs, type_by_service
        )
        self.assertEqual(wired_pairs, set(host_tests), f"Host wire evidence: {host_tests!r}")
        self.assertEqual(
            ["dotnet/tests/Eve.Host.Tests/SearchQuickQueryHostWireTests.cs:Search_quick_query_returns_client_id_list_through_host_with_transport_kwargs"],
            host_tests[("search", "QuickQuery")],
        )

        calls = current_service_audit.accessor_calls(
            contract,
            pathlib.Path("station/base.py"),
            source,
        )
        self.assertEqual(
            [("station", "GetGuests")],
            sorted(set((call["service"], call["method"]) for call in calls)),
        )


if __name__ == "__main__":
    unittest.main()
