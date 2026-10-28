# Deep check: contextualOfferMgr

## Client calls and arguments

The V24 whole-tree caller is contextualOffers/client/contextualOffersSvc.py. Its six wrappers pass one positional offerID and ignore the reply: TrackOfferDelivered at lines 233-235, TrackOfferSeen at 237-239, TrackOfferBuyPress at 241-243, TrackOfferClosed at 245-247, TrackOfferPurchased at 249-251, and TrackOfferPurchasedSeen at 253-255. No call sends a method keyword; the Host strips transport machoVersion. No later UI logic consumes an RPC result.

## Host path and behavior

CoreServices.Create registers ContextualOfferMgrService; the real Host test resolves it through the registry. Each method validates a positive integral offer ID (PyInt or PyLong), records event name, offer ID, character, account and timestamp in a bounded in-process analytics queue, and returns None. Invalid IDs raise the exact CONTEXTUAL_OFFER_ID_INVALID UserError. The retained queue is capped at 1,024 events; this is telemetry, not player state or a payment/offer fulfillment owner. No follow-up notification is required by the client.

EveJS v0.12.9 implements the same six methods in server/src/services/newEdenStore/contextualOfferMgrService.js:71-93. Each delegates to recordTrackingEvent at lines 48-64 and returns None. Both implementations record an ephemeral analytics event; neither persists it as gameplay state.

## Wire evidence

ContextualOfferMgrRpcHostWireTests.cs:12-55 uses the registered Host socket route, includes machoVersion, checks None replies, and verifies the five methods listed in that test plus recorded character/account fields. TrackOfferBuyPress is covered by ClientVerifiedBatch28CdHostWireTests.cs:83-84 on the same Host packet path. The CSV contains two equivalent source rows per method; both rows map to these tests.

No database or graphical V24 client was used in this pass. No code change was needed.
