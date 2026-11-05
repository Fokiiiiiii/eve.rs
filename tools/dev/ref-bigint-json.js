// Preloaded into the reference server (tools/dev/ref-server.sh): the reference builds its debug log lines with JSON.stringify of the bound context of an
// inventory, which holds a BigInt (`boundAtFileTime`) for a ship's inventory; JSON.stringify throws on a BigInt, the handler fails and the client is
// answered null for List, Add, GetCapacity and MultiAdd of a ship's cargo. A BigInt that JSON can write as a string lets those handlers run, as they do
// on the live server this reference stands for. Nothing else of the reference changes.
BigInt.prototype.toJSON = function toJSON() {
  return this.toString();
};
