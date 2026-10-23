"""A loopback EVE SSO issuer for the live handshake test.

The RSA key is generated for the process and never written to logs. The host reads only this
issuer's discovery document and JWKS; the test signs a short-lived character access token.
"""

from __future__ import annotations

import base64
import json
import os
import subprocess
import tempfile
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from threading import Thread
from urllib.parse import urlsplit


def _b64url(value: bytes) -> str:
    return base64.urlsafe_b64encode(value).rstrip(b"=").decode("ascii")


def _der_length(data: bytes, offset: int) -> tuple[int, int]:
    first = data[offset]
    offset += 1
    if first < 0x80:
        return first, offset
    size = first & 0x7F
    length = int.from_bytes(data[offset:offset + size], "big")
    return length, offset + size


def _der_value(data: bytes, offset: int, expected_tag: int) -> tuple[bytes, int]:
    if data[offset] != expected_tag:
        raise ValueError("unexpected RSA public key encoding")
    length, body = _der_length(data, offset + 1)
    end = body + length
    return data[body:end], end


class SsoTestIssuer:
    def __init__(self, port: int):
        self.issuer = f"http://127.0.0.1:{port}/"
        self.metadata_address = f"http://127.0.0.1:{port}/.well-known/oauth-authorization-server"
        self._directory = tempfile.TemporaryDirectory(prefix="eve-sso-issuer-")
        self._private_key = os.path.join(self._directory.name, "issuer.pem")
        subprocess.run(
            ["openssl", "genpkey", "-algorithm", "RSA", "-pkeyopt", "rsa_keygen_bits:2048", "-out", self._private_key],
            check=True, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
        )
        encoded = subprocess.run(
            ["openssl", "pkey", "-in", self._private_key, "-pubout", "-outform", "DER"],
            check=True, capture_output=True,
        ).stdout
        outer, _ = _der_value(encoded, 0, 0x30)
        _, bitstring_end = _der_value(outer, 0, 0x30)
        bitstring, _ = _der_value(outer, bitstring_end, 0x03)
        if not bitstring or bitstring[0] != 0:
            raise ValueError("unexpected RSA public key bit string")
        rsa_key, _ = _der_value(bitstring[1:], 0, 0x30)
        modulus, cursor = _der_value(rsa_key, 0, 0x02)
        exponent, _ = _der_value(rsa_key, cursor, 0x02)
        self._jwks = {
            "keys": [{
                "kid": "headless-test-key",
                "kty": "RSA",
                "use": "sig",
                "alg": "RS256",
                "n": _b64url(modulus.lstrip(b"\x00")),
                "e": _b64url(exponent.lstrip(b"\x00")),
            }],
        }
        self._metadata = {
            "issuer": self.issuer,
            "jwks_uri": f"http://127.0.0.1:{port}/jwks",
        }
        self._server = ThreadingHTTPServer(("127.0.0.1", port), self._handler())
        self._server.daemon_threads = True
        self._thread = Thread(target=self._server.serve_forever, daemon=True)

    def _handler(self):
        metadata = json.dumps(self._metadata).encode("utf-8")
        jwks = json.dumps(self._jwks).encode("utf-8")

        class Handler(BaseHTTPRequestHandler):
            def do_GET(self):
                path = urlsplit(self.path).path
                if path == "/.well-known/oauth-authorization-server":
                    body = metadata
                elif path == "/jwks":
                    body = jwks
                else:
                    self.send_error(404)
                    return
                self.send_response(200)
                self.send_header("Content-Type", "application/json")
                self.send_header("Content-Length", str(len(body)))
                self.end_headers()
                self.wfile.write(body)

            def log_message(self, _format, *_args):
                return

        return Handler

    def __enter__(self) -> "SsoTestIssuer":
        self._thread.start()
        return self

    def __exit__(self, *_exc) -> None:
        self._server.shutdown()
        self._server.server_close()
        self._thread.join(timeout=2)
        self._directory.cleanup()

    def issue(self, subject: str) -> str:
        header = _b64url(json.dumps(
            {"alg": "RS256", "kid": "headless-test-key", "typ": "JWT"},
            separators=(",", ":"),
        ).encode("utf-8"))
        payload = _b64url(json.dumps(
            {
                "iss": self.issuer,
                "aud": ["EVE Online", "eve.rs-headless-client"],
                "exp": int(time.time()) + 300,
                "sub": subject,
            },
            separators=(",", ":"),
        ).encode("utf-8"))
        signing_input = f"{header}.{payload}".encode("ascii")
        signature = subprocess.run(
            ["openssl", "dgst", "-sha256", "-sign", self._private_key],
            input=signing_input, check=True, capture_output=True,
        ).stdout
        return f"{header}.{payload}.{_b64url(signature)}"
