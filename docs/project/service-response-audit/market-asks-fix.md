# Market ask response re-audit

## Decision

The proposed PyObject for marketutil.BestByOrder is held and has been removed from production code. The V24 class is a collections.namedtuple, but the available sources do not establish that the native blue.marshal.Load resolver can instantiate that class from a PyObject whose Args is a dictionary. No actual V24 decode trace or client run proves it. The task is unresolved; this note does not claim full client compatibility.

## V24 client consumers and wrappers

- At eve-logic repository, HEAD 1dbb5711a072a146e1d228365668ac05655eefe1, marketutil/__init__.py:7-10 defines BestByOrder as a namedtuple with price, volRemaining, typeID, stationID, and ConvertTuplesToBestByOrders calls BestByOrder(*order).
- marketbase.py:657-666 calls GetStationAsks or GetSystemAsks and converts every value with that helper. The values must be iterable in that four-field order.
- quote.py:94-100 calls GetStationAsks directly, reads ask.typeID, then passes the row to Buy; Buy reads order.stationID at lines 253-282. A raw tuple cannot serve this direct path.
- marketsvc.py:251-254 returns sm.ProxySvc('marketProxy'). Its GetStationAsks and GetSystemAsks wrappers validate session context and return the remote result directly at lines 296-304. The invalidation entries at lines 119-120 invalidate those method-cache keys; they do not change the row type.
- ServiceProxyCall forwards the remote method result. CachedMethodCallResult.GetResult returns blue.marshal.Load(self.result), with no Python-level row conversion visible in the V24 source.

## EveJS and current marshal path

At EveJS repository, HEAD 828823a75f211fdecb945b087a26c225f111491f, both MarketProxy handlers pass tuple values to buildCachedMethodCallResult. buildBestByTuple creates price, quantity, typeID, stationID, and marshal.js encodes arrays as PyTuple. The cache builder marshals that same result into a substream or cached-object reference; it does not turn values into named objects.

In eve.rs, ObjectCaching.MethodResult similarly wraps the supplied result in a PySubStream. MarshalEncoder writes a PyObject as its Name and Args; MarshalDecoder reads those fields back into a generic PyObject. EveJS decodeValue likewise returns a generic object record with name and args. These server-side readers/writers do not implement V24 client class resolution.

The extracted V24 source defines util.KeyVal with __passbyvalue__ and a dictionary-capable initializer, while marketutil.BestByOrder is just the namedtuple declaration. The checked-in sources do not show how the native client restores PyObject state for that namedtuple. Existing PyCPickled support carries raw pickle bytes; the located pickled-value helpers only parse fixed integer sets and do not construct a BestByOrder instance. PyObjectEx also has no verified BestByOrder restoration path.

## Current eve.rs state

The canonical checkout is repository root, main at 52b8421f34e635900a18459da4403f26f2dfaec0. The not confirmed AskBestByOrder production path was removed; GetStationAsks and GetSystemAsks currently retain the prior util.KeyVal result. This supports named field access but does not satisfy the independently observed MarketBase tuple expansion. EveJS's raw tuple supports that expansion but not the direct quote.py attribute access. No server-side encoding that is proven to satisfy both consumers is established in the allowed scope.

MarketRpcTests checks the current serialized util.KeyVal name and its fields. Its name comparison uses JsSemantics.PlainText because the marshal round trip may represent the text as PyBytes. That assertion validates the server-side wire record only; it does not prove client restoration.

## Verification status

The parent reported 99 Market-suite tests: 97 passed, 1 failed on PyStr versus PyBytes at the class-name assertion, and 1 benchmark skipped. The assertion now compares through JsSemantics.PlainText; this turn did not rerun the suite. No build, test, or Git mutation was performed. A native V24 client decode remains necessary to establish the class-resolution contract.