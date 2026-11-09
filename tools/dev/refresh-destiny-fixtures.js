#!/usr/bin/env node
"use strict";

const fs = require("node:fs");
const path = require("node:path");

const repositoryRoot = path.resolve(__dirname, "../..");
const fixturePath = path.join(repositoryRoot, "oracle/fixtures/destiny/cases.json");
const evejsRoot = process.argv[2];
const outputPath = process.argv[3] ? path.resolve(process.argv[3]) : fixturePath;

if (!evejsRoot) {
  process.stderr.write("usage: node tools/dev/refresh-destiny-fixtures.js <evejs-root> [output-path]\n");
  process.exit(2);
}

const { encodeEntityBall } = require(path.join(
  path.resolve(evejsRoot),
  "server/src/space/destiny/stream/ballEncoding.js",
));

function fromEjson(value) {
  if (Array.isArray(value)) {
    return value.map(fromEjson);
  }
  if (value === null || typeof value !== "object") {
    return value;
  }

  const keys = Object.keys(value);
  if (keys.length !== 1) {
    throw new TypeError(`bad EJSON node: ${JSON.stringify(value).slice(0, 120)}`);
  }
  const [tag] = keys;
  const body = value[tag];
  switch (tag) {
    case "$int":
      return Number(body);
    case "$f64": {
      const bytes = Buffer.alloc(8);
      bytes.writeBigUInt64LE(BigInt(`0x${body}`));
      return bytes.readDoubleLE();
    }
    case "$big":
      return BigInt(body);
    case "$buf":
      return Buffer.from(body, "hex");
    case "$obj":
      return Object.fromEntries(Object.entries(body).map(([key, entry]) => [key, fromEjson(entry)]));
    default:
      throw new TypeError(`unknown EJSON tag ${tag}`);
  }
}

function errorKind(error) {
  return error.code ? `${error.name}:${error.code}` : error.name;
}

const cases = JSON.parse(fs.readFileSync(fixturePath, "utf8"));
let refreshed = 0;
for (const record of cases) {
  if (record.group !== "ball.encode") {
    continue;
  }

  try {
    const encoded = encodeEntityBall(fromEjson(record.input));
    record.output = { $buf: encoded.toString("hex") };
    delete record.error;
  } catch (error) {
    delete record.output;
    record.error = errorKind(error);
  }
  refreshed += 1;
}

const content = `[\n${cases.map((record) => JSON.stringify(record)).join(",\n")}\n]\n`;
fs.writeFileSync(outputPath, content);
process.stdout.write(`refreshed ${refreshed} ball.encode cases from ${path.resolve(evejsRoot)}\n`);
