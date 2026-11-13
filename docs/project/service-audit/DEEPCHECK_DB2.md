# DB2 CallProc coverage

## Whole-client callers

A whole-project search for DB2 CallProc sites found these internal server call chains:

- /home/user/eve-logic/carbon/common/script/net/http.py:29-33 calls zcluster.Nodes_PolarisID to resolve the Polaris node before building an ESP tunnel URL.
- /home/user/eve-logic/carbon/common/script/net/httpService.py:805 calls zsystem.Versions_Select for an EBS version.
- /home/user/eve-logic/carbon/common/script/net/machoNetTransport.py:460-461 calls zcluster.Attacks_Insert to record malformed or hostile transport input.
- /home/user/eve-logic/carbon/common/script/net/machoNet.py:3389-3398 calls zuser.Users_SelectByRole(ROLE_VIPLOGIN) to populate the internal VIP login allowlist, storing user IDs and username hashes.

The RPC-00659–RPC-00662 coverage rows describe the first and last procedures. These callsites are invoked by server-side HTTP handling and internal VIP authentication. The full V24.01 client tree contains no player-client call to these DB2 procedures. EveJS v0.12.9 (`/home/user/evejs-261108.1`) has no generic DB2 `CallProc` gameplay route, and the Host has no authoritative stored-procedure registry to back these names.

## Disposition

Mark RPC-00659–RPC-00662 `CLIENT-DEAD`. They are server-internal callers rather than client RPC obligations, so eve.rs does not add a generic `CallProc` Host route or guessed procedure outputs. The server-side HTTP and VIP-list functionality remains outside this player-client coverage scope.
