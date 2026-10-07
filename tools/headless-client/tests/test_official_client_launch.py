from pathlib import Path
from types import SimpleNamespace
from unittest import TestCase
from unittest.mock import patch

from evecli import __main__ as cli


class OfficialClientLaunchTests(TestCase):
    def test_cli_passes_login_inputs_to_official_launcher(self):
        with patch.object(cli, "_launch_official_client", return_value=SimpleNamespace(pid=7)) as launch:
            self.assertEqual(
                0,
                cli.main([
                    "--official-client",
                    "--launcher-root", "C:/Launcher",
                    "--host", "127.0.0.1",
                    "--port", "26000",
                    "--user", "pilot",
                    "--password", "secret",
                    "--character-id", "140000052",
                ]),
            )

        launch.assert_called_once_with(
            "C:/Launcher",
            host="127.0.0.1",
            port=26000,
            user="pilot",
            password="secret",
            character_id=140000052,
        )

    def test_official_client_receives_connection_and_character_arguments(self):
        executable = Path("C:/Launcher/tq/bin64/exefile.exe")
        with (
            patch.object(cli, "_find_official_exefile", return_value=executable),
            patch.object(cli.subprocess, "Popen", return_value=SimpleNamespace(pid=7)) as popen,
        ):
            cli._launch_official_client(
                "C:/Launcher",
                host="127.0.0.1",
                port=26000,
                user="pilot",
                password="secret",
                character_id=140000052,
            )

        self.assertEqual(
            [
                str(executable),
                "/server:127.0.0.1",
                "/port:26000",
                "/login:pilot:secret",
                "/autoSelectCharacter:140000052",
            ],
            popen.call_args.args[0],
        )
