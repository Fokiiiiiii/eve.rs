# Public gateway transport for local development

V24.01 uses TLS for the tier-discovery address `<tier>-public-gateway.evetech.net`. It uses clear-text HTTP/2 (h2c) whenever `EVE_PUBLIC_GATEWAY_ADDRESS` overrides that address. This behavior is in `eve-logic/publicGateway/grpc/connectionConfig.py:get_address`.

For the unmodified local client, point the override at the Host's h2c listener and leave the certificate variables unset:

```sh
export EVE_PUBLIC_GATEWAY=127.0.0.1:26102
export EVE_PUBLIC_GATEWAY_ADDRESS=127.0.0.1:26102
unset EVE_PUBLIC_GATEWAY_CERT_PATH
unset EVE_PUBLIC_GATEWAY_CERT_PASSWORD
```

The Host can also serve TLS on its gateway listener. Set `EVE_PUBLIC_GATEWAY_CERT_PATH` to a PKCS#12 file. If it is protected, set `EVE_PUBLIC_GATEWAY_CERT_PASSWORD` in the Host environment. To create a throwaway local certificate outside the repository:

```sh
openssl req -x509 -newkey rsa:2048 -nodes -days 30 \
  -subj '/CN=localhost' \
  -keyout /tmp/eve-gateway-local.key \
  -out /tmp/eve-gateway-local.crt
openssl pkcs12 -export \
  -inkey /tmp/eve-gateway-local.key \
  -in /tmp/eve-gateway-local.crt \
  -out /tmp/eve-gateway-local.pfx
export EVE_PUBLIC_GATEWAY_CERT_PATH=/tmp/eve-gateway-local.pfx
```

OpenSSL prompts for a PKCS#12 password; provide the same value through `EVE_PUBLIC_GATEWAY_CERT_PASSWORD` when starting the Host. The certificate and key stay outside the repository. A self-signed certificate is suitable for exercising the TLS listener with a test client configured to trust it. The V24.01 local address override always selects h2c, so it does not test TLS certificate validation. The tier-discovery TLS path requires a certificate chain trusted by the client's CA bundle and a matching host name.

Gateway tokens use HS256. Set `EVE_PUBLIC_GATEWAY_TOKEN_KEY` to a base64-encoded key of at least 32 bytes to keep token verification stable across Host restarts and instances. When unset, the Host creates a random process-local key; tokens from a previous process then stop verifying after restart.
