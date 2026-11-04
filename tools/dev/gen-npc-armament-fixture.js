#!/usr/bin/env node
// Writes oracle/fixtures/npc/hull_weapons.json: what the EVE.js reference's npcEquipment makes of the weapon of every NPC ship type (category 11) that
// has one (and of one in twelve of those that have none), and the missile each hull that launches missiles launches (`getNpcEntityMissileWeaponSource`), with the attributes and effects of the type that it worked from, so that the port in
// dotnet/src/Eve.Npc/NpcArmament.cs is checked against it without the static data.
//
//   SDE=.cache/sde/eve-online-static-data-<build>-jsonl EVEJS_DATA_ROOT=$PWD/.cache/ref EVEJS_GAMESTORE_DATA_DIR=$PWD/.cache/ref/gameStore/data \
//     node tools/dev/gen-npc-armament-fixture.js
"use strict";

const fs = require("fs");
const path = require("path");

const EVEJS = process.env.EVEJS || "/home/user/evejs-v0.12.9";
const SDE = process.env.SDE;
if (!SDE) {
  console.error("SDE (the directory of the SDE's JSONL files) is required");
  process.exit(2);
}
const OUT = path.join(__dirname, "..", "..", "oracle", "fixtures", "npc", "hull_weapons.json");
const equipment = require(path.join(EVEJS, "server/src/space/npc/npcEquipment"));
const live = require(path.join(EVEJS, "server/src/services/fitting/liveFittingState"));

const rows = (name) =>
  fs.readFileSync(path.join(SDE, `${name}.jsonl`), "utf8").split("\n").filter(Boolean).map((line) => JSON.parse(line));

const category = new Map(rows("groups").map((g) => [g._key, g.categoryID]));
const npcs = rows("types").filter((t) => category.get(t.groupID) === 11);
// what the port reads of a type: the attributes of a weapon, and the range an NPC attacks at
// (and of a hull that launches missiles: the type of its missile, its multipliers and the launch duration)
const ATTRIBUTES = [6, 51, 54, 64, 114, 116, 117, 118, 158, 160, 212, 247, 506, 507, 620, 645, 646, 858, 859];
// what the port reads of a missile type
const MISSILE_ATTRIBUTES = [4, 9, 37, 70, 114, 116, 117, 118, 162, 281, 653, 654, 1353, 1354];
const SHIP_ID = 4300000000000001;

const cases = [];
const effectTable = {};
const missileTypes = {};
let without = 0;
for (const type of npcs) {
  const typeID = type._key;
  const entity = { kind: "ship", nativeNpc: true, itemID: SHIP_ID, typeID, groupID: type.groupID, categoryID: 11, systemID: 30000142 };
  const modules = equipment.getNpcWeaponModules(entity);
  const missileSource = equipment.getNpcEntityMissileWeaponSource(entity);
  if (modules.length === 0 && !missileSource && without++ % 12 !== 0) {
    continue;
  }
  const map = live.getTypeAttributeMap(typeID) || {};
  const attributes = {};
  for (const id of ATTRIBUTES) {
    if (map[id] !== undefined) {
      attributes[id] = map[id];
    }
  }
  // the effects of a type in the order the reference reads them; what each is is told once, in `effects`
  const effectIds = [];
  for (const effectID of live.getTypeDogmaEffects(typeID) || new Set()) {
    const e = live.getEffectTypeRecord(effectID);
    if (e) {
      effectIds.push(e.effectID);
      effectTable[e.effectID] = { name: e.name, guid: e.guid || "", category: e.effectCategoryID };
    }
  }
  const weapons = modules.map((module) => {
    const s = equipment.buildNpcWeaponModuleSnapshot(entity, module);
    return {
      effectID: module.npcEffectID, family: s.family, effectGUID: s.effectGUID, durationMs: s.durationMs, capNeed: s.capNeed,
      optimalRange: s.optimalRange, falloff: s.falloff, trackingSpeed: s.trackingSpeed, optimalSigRadius: s.optimalSigRadius,
      rawShotDamage: [s.rawShotDamage.em, s.rawShotDamage.thermal, s.rawShotDamage.kinetic, s.rawShotDamage.explosive],
    };
  });
  let missile = null;
  if (missileSource) {
    const missileMap = live.getTypeAttributeMap(missileSource.chargeTypeID) || {};
    const missileAttributes = {};
    for (const id of MISSILE_ATTRIBUTES) {
      if (missileMap[id] !== undefined) {
        missileAttributes[id] = missileMap[id];
      }
    }
    missileTypes[missileSource.chargeTypeID] = { groupID: missileSource.chargeGroupID, name: missileSource.chargeName, attributes: missileAttributes };
    missile = {
      typeID: missileSource.chargeTypeID, durationMs: missileSource.durationMs, maxVelocity: missileSource.maxVelocity, flightTimeMs: missileSource.flightTimeMs,
      explosionRadius: missileSource.explosionRadius, explosionVelocity: missileSource.explosionVelocity, damageReductionFactor: missileSource.damageReductionFactor,
      damageReductionSensitivity: missileSource.damageReductionSensitivity, approxRange: missileSource.approxRange,
      rawShotDamage: [missileSource.rawShotDamage.em, missileSource.rawShotDamage.thermal, missileSource.rawShotDamage.kinetic, missileSource.rawShotDamage.explosive],
    };
  }
  cases.push({ typeID, attributes, effectIds, weapons, missile });
}
const commit = require("child_process").execSync("git rev-parse HEAD", { cwd: EVEJS }).toString().trim();
fs.mkdirSync(path.dirname(OUT), { recursive: true });
fs.writeFileSync(
  OUT,
  JSON.stringify({ note: "Generated by tools/dev/gen-npc-armament-fixture.js from the reference's npcEquipment", referenceCommit: commit, effects: effectTable, missileTypes, cases }) + "\n",
);
console.log(`${cases.length} NPC types (${cases.filter((c) => c.weapons.length).length} armed, ${cases.filter((c) => c.missile).length} with missiles) -> ${OUT}`);
