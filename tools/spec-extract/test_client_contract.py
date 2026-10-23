from __future__ import annotations

import pathlib
import tempfile
import unittest

import client_contract


class HostRouteParserTests(unittest.TestCase):
    def test_qualified_service_attributes_are_registered(self) -> None:
        source = '''
[Eve.Services.Service("warsInfoMgr")]
public sealed class WarsInfoMgrService
{
    [Eve.Services.Method("GetTop50")]
    public PyValue GetTop50(ServiceCall call) => PyNone.Instance;
}
'''
        with tempfile.TemporaryDirectory() as temporary:
            root = pathlib.Path(temporary)
            (root / "WarsInfoMgrService.cs").write_text(source)

            self.assertIn(("warsInfoMgr", "GetTop50"), client_contract.host_registered_methods(root))

    def test_manual_collection_routes_are_registered(self) -> None:
        source = '''
public sealed class LookupSvcService
{
    private static readonly HashSet<string> Methods = new(StringComparer.Ordinal)
    {
        "LookupCharacters",
        "LookupKnownLocationsByGroup",
    };
    public string Name => "lookupSvc";
    public bool HasMethod(string method) => Methods.Contains(method);
}
'''
        with tempfile.TemporaryDirectory() as temporary:
            root = pathlib.Path(temporary)
            (root / "LookupSvcService.cs").write_text(source)

            self.assertEqual(
                {
                    ("lookupSvc", "LookupCharacters"),
                    ("lookupSvc", "LookupKnownLocationsByGroup"),
                },
                client_contract.host_registered_methods(root),
            )

    def test_manual_service_name_switch_routes_are_registered(self) -> None:
        source = '''
public sealed class StructureTravelRpcService
{
    public bool HasMethod(string method) => Name switch
    {
        "structureCynoBeaconMgr" => method is "CmdBridgeToStructureBeacon" or "CmdJumpToStructureBeacon",
        "structureJumpBridgeMgr" => method is "CmdJumpThroughStructureStargate" or "GetLinkedStructure",
        _ => false,
    };
}
'''
        with tempfile.TemporaryDirectory() as temporary:
            root = pathlib.Path(temporary)
            (root / "StructureTravelRpcService.cs").write_text(source)

            self.assertEqual(
                {
                    ("structureCynoBeaconMgr", "CmdBridgeToStructureBeacon"),
                    ("structureCynoBeaconMgr", "CmdJumpToStructureBeacon"),
                    ("structureJumpBridgeMgr", "CmdJumpThroughStructureStargate"),
                    ("structureJumpBridgeMgr", "GetLinkedStructure"),
                },
                client_contract.host_registered_methods(root),
            )

    def test_client_call_arguments_and_notifications_render(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = pathlib.Path(temporary)
            client = root / "client"
            server = root / "dotnet" / "src"
            client.mkdir()
            server.mkdir(parents=True)
            (client / "example.py").write_text(
                "class Example:\n"
                "    __notifyevents__ = ('OnExample',)\n"
                "    def OnExample(self, value):\n"
                "        pass\n"
                "    def Call(self):\n"
                "        return RemoteSvc('sampleSvc').Run(1, nested={'x': [2, 3]})\n"
            )
            (server / "Sample.cs").write_text(
                '[Service("sampleSvc")]\n'
                "public sealed class SampleService {\n"
                '    [Method("Run")]\n'
                "    public PyValue Run(ServiceCall call) => PyNone.Instance;\n"
                '    public void Notify(ClientSession session) => session.SendNotification("OnExample", "clientID");\n'
                "}\n"
            )
            data = client_contract.collect(client, server)
            rendered = client_contract.markdown(data, None, root)

            self.assertEqual(1, len(data["service_method_pairs"]))
            self.assertIn("sampleSvc.Run(1, nested={'x': [2, 3]})", rendered)
            self.assertIn("`OnExample`", rendered)
            self.assertIn("example.py", rendered)
            self.assertEqual("Sample.cs", data["host_literal_notifications"][0]["file"])


if __name__ == "__main__":
    unittest.main()
