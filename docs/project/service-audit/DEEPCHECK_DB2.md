# DB2 CallProc coverage

## Whole-client callers

A whole-project search for DB2 CallProc sites found these internal server call chains:

- /home/user/eve-logic/carbon/common/script/net/http.py:29-33 calls zcluster.Nodes_PolarisID to resolve the Polaris node before building an ESP tunnel URL.
- /home/user/eve-logic/carbon/common/script/net/httpService.py:805 calls zsystem.Versions_Select for an EBS version.
- /home/user/eve-logic/carbon/common/script/net/machoNetTransport.py:460-461 calls zcluster.Attacks_Insert to record malformed or hostile transport input.
- /home/user/eve-logic/carbon/common/script/net/machoNet.py:3389-3398 calls zuser.Users_SelectByRole(ROLE_VIPLOGIN) to populate the internal VIP login allowlist, storing user IDs and username hashes.

The RPC-00659–RPC-00662 coverage rows describe the first and last procedures. These callsites are invoked by HTTP/server startup, transport security handling, and internal VIP authentication. They do not represent player gameplay calls or a player-visible response contract. EveJS 261028.1 has no generic DB2 CallProc gameplay route, and the Host has no authoritative stored-procedure registry to back these names.

## Disposition

Leave the four rows open. A generic CallProc Host route would expose arbitrary internal database procedures and privileged account data; implementing only guessed procedure outputs would fabricate success. Close this gap only with an explicit internal service boundary and authoritative implementations for the named procedures, including their existing server-only callers and access checks.
