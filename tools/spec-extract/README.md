# spec-extract

Scripts that read a **local copy of the decompiled client** as a read-only
reference and write facts about the protocol.

The client source is CCP's property. These scripts take its path as an argument;
nothing from it is copied into this repository, and output that reproduces its
schemas is written to `out/` (git-ignored) rather than committed.

| Script | Output |
|---|---|
| `services.py <client>` | the service names and methods the client calls (`docs/spec/services.tsv`, committed: names and counts only) |
| `protos.py <client>` | a protobuf `FileDescriptorSet` rebuilt from the generated `*_pb2.py` modules (`out/eve_proto.desc`, not committed). Needs `pip install protobuf` |
