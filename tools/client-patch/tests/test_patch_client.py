"""Unit tests for patch_client.py. Every client here is synthetic and lives in a temp dir.

The real build-3396210 executable hash is swapped for a fake one, so no CCP file
is needed or read. Run from the repository root:
    python3 -m unittest discover -s tools/client-patch/tests -v
"""

import contextlib
import hashlib
import io
import os
import sys
import tempfile
import unittest

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.dirname(HERE))
import patch_client as pc  # noqa: E402

FAKE_EXE = b"synthetic executable for a unit test, not a CCP file\n" * 8
START_INI = (
    "build = 3396210\n"
    "server = live.example.invalid\n"
    "cryptoPack = Other\n"
    "; synthetic settings line\n"
)


def quiet(func, *args, **kwargs):
    with contextlib.redirect_stdout(io.StringIO()):
        return func(*args, **kwargs)


class PatchClientTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.client = os.path.join(self.tmp.name, "tq")
        os.makedirs(os.path.join(self.client, "bin64"))
        with open(os.path.join(self.client, "bin64", "exefile.exe"), "wb") as f:
            f.write(FAKE_EXE)
        self._saved = (pc.EXE_SIZE, pc.EXE_SHA256)
        pc.EXE_SIZE = len(FAKE_EXE)
        pc.EXE_SHA256 = hashlib.sha256(FAKE_EXE).hexdigest()
        self.ini = os.path.join(self.client, "start.ini")
        self.backup = os.path.join(self.client, pc.BACKUP_NAME)
        self.write_ini(START_INI)

    def tearDown(self):
        pc.EXE_SIZE, pc.EXE_SHA256 = self._saved
        self.tmp.cleanup()

    def write_ini(self, text, newline=None):
        with open(self.ini, "wb") as f:
            f.write(text.encode("latin-1") if newline is None else text.replace("\n", newline).encode("latin-1"))

    def read_bytes(self, path):
        with open(path, "rb") as f:
            return f.read()

    def test_apply_sets_server_and_crypto_pack_and_keeps_other_lines(self):
        self.assertEqual(0, quiet(pc.apply, self.client, "127.0.0.1", 26000))
        text = self.read_bytes(self.ini).decode("latin-1")
        self.assertEqual(
            "build = 3396210\nserver = 127.0.0.1\ncryptoPack = Placebo\n; synthetic settings line\n",
            text,
        )

    def test_crlf_endings_are_preserved(self):
        self.write_ini(START_INI, newline="\r\n")
        quiet(pc.apply, self.client, "127.0.0.1", 26000)
        self.assertEqual(
            b"build = 3396210\r\nserver = 127.0.0.1\r\ncryptoPack = Placebo\r\n; synthetic settings line\r\n",
            self.read_bytes(self.ini),
        )

    def test_missing_keys_are_appended_and_serverip_alias_is_updated(self):
        self.write_ini("build = 3396210\nserverip = old.example.invalid")
        quiet(pc.apply, self.client, "192.0.2.10", 26000)
        self.assertEqual(
            b"build = 3396210\nserverip = 192.0.2.10\ncryptoPack = Placebo\n",
            self.read_bytes(self.ini),
        )

    def test_backup_holds_original_bytes_and_is_taken_once(self):
        original = self.read_bytes(self.ini)
        quiet(pc.apply, self.client, "127.0.0.1", 26000)
        self.assertEqual(original, self.read_bytes(self.backup))
        quiet(pc.apply, self.client, "127.0.0.1", 26000)  # second run: no-op
        self.assertEqual(original, self.read_bytes(self.backup))

    def test_second_apply_is_a_noop(self):
        quiet(pc.apply, self.client, "127.0.0.1", 26000)
        first = self.read_bytes(self.ini)
        self.assertEqual(0, quiet(pc.apply, self.client, "127.0.0.1", 26000))
        self.assertEqual(first, self.read_bytes(self.ini))

    def test_restore_returns_exact_original_bytes_and_removes_backup(self):
        original = self.read_bytes(self.ini)
        quiet(pc.apply, self.client, "127.0.0.1", 26000)
        self.assertEqual(0, quiet(pc.restore, self.client))
        self.assertEqual(original, self.read_bytes(self.ini))
        self.assertFalse(os.path.exists(self.backup))

    def test_restore_without_backup_is_refused(self):
        with self.assertRaises(pc.ClientError):
            quiet(pc.restore, self.client)

    def test_wrong_build_is_refused_and_nothing_changes(self):
        self.write_ini(START_INI.replace("3396210", "3396209"))
        original = self.read_bytes(self.ini)
        with self.assertRaises(pc.ClientError):
            quiet(pc.apply, self.client, "127.0.0.1", 26000)
        self.assertEqual(original, self.read_bytes(self.ini))
        self.assertFalse(os.path.exists(self.backup))

    def test_modified_executable_is_refused_and_nothing_changes(self):
        with open(os.path.join(self.client, "bin64", "exefile.exe"), "wb") as f:
            f.write(FAKE_EXE + b"patched")
        original = self.read_bytes(self.ini)
        with self.assertRaises(pc.ClientError):
            quiet(pc.apply, self.client, "127.0.0.1", 26000)
        self.assertEqual(original, self.read_bytes(self.ini))
        self.assertFalse(os.path.exists(self.backup))

    def test_dry_run_changes_nothing(self):
        original = self.read_bytes(self.ini)
        self.assertEqual(0, quiet(pc.apply, self.client, "127.0.0.1", 26000, dry_run=True))
        self.assertEqual(original, self.read_bytes(self.ini))
        self.assertFalse(os.path.exists(self.backup))

    def test_server_argument_rules(self):
        self.assertEqual(("127.0.0.1", 26000), pc.parse_server("127.0.0.1"))
        self.assertEqual(("192.0.2.5", 26100), pc.parse_server("192.0.2.5:26100"))
        for bad in ("localhost", "0.0.0.0", "127.0.0.1:0", "127.0.0.1:70000", "127.0.0.1:x", "::1"):
            with self.assertRaises(pc.ClientError, msg=bad):
                pc.parse_server(bad)


if __name__ == "__main__":
    unittest.main()
