// Records EVE.js marketRules.js without starting the reference server. No implementation is copied.
"use strict";
const fs = require("node:fs");
const path = require("node:path");
const Module = require("node:module");
const { execFileSync } = require("node:child_process");
const root = process.env.EVEJS_ROOT;
if (!root) throw new Error("set EVEJS_ROOT to the reference checkout");
const originalLoad = Module._load;
let level = 0;
let station = {};
let standings = {};
Module._load = function (request, ...args) {
  const name = String(request).replaceAll("\\", "/");
  if (name.endsWith("/character/characterState")) return { getCharacterRecord: () => ({}) };
  if (name.endsWith("/character/standingRuntime")) return { getCharacterRawStanding: (_, owner) => standings[owner] || 0 };
  if (name.endsWith("/skills/skillState")) return { getCachedCharacterSkillMap: () => new Map([3443,3444,3446,3447,16595,16596,16597,16598,16594,16622,18580].map(id => [id, { effectiveSkillLevel: level }])) };
  if (name.endsWith("/_shared/stationStaticData")) return { getStationRecord: () => station };
  if (name.endsWith("/marketTopology")) return { RANGE_STATION: -1, RANGE_SOLAR_SYSTEM: 0, RANGE_REGION: 32767, getStationSolarSystemID: id => id === 1 ? 30000001 : 0, getOrderJumpDistance: () => 0 };
  return originalLoad.call(this, request, ...args);
};
let rules;
try { rules = require(path.join(root, "server/src/services/market/marketRules.js")); }
finally { Module._load = originalLoad; }
const cases = [];
for (level = 0; level <= 5; level++) {
  for (const isStation of [true, false]) {
    for (const standing of [-10, 0, 10]) {
      for (const warFaction of [0, 500018]) {
        const solarSystem = warFaction ? 30100000 : 30000001;
        station = { ownerID: 10, factionID: 20, solarSystemID: solarSystem };
        standings = { 10: standing, 20: standing };
        const context = rules.getMarketContext({ characterID: 100, stationID: isStation ? 1 : 2, session: { warfactionid: warFaction } });
        const fees = [[null,0], [null,100], [null,123456.78], [10000,5000], [10000,20000], [null,-10], [null,1.125]].map(([oldAmount,newAmount]) => ({
          oldAmount, newAmount,
          broker: rules.computeBrokerFeeInfo(context, oldAmount, newAmount),
          scc: rules.computeSccSurchargeInfo(context, oldAmount, newAmount),
          tax: rules.computeSalesTaxAmount(context, newAmount),
        }));
        cases.push({ level, isStation, factionStanding: standing, corpStanding: standing, warFaction, solarSystem,
          context: { brokerCommissionRate: context.brokerCommissionRate, sccSurchargeRate: context.sccSurchargeRate,
            modificationFeeDiscount: context.modificationFeeDiscount, limits: context.limits }, fees });
      }
    }
  }
}
const output = path.join(__dirname, "../../oracle/fixtures/market/rules.json");
fs.mkdirSync(path.dirname(output), { recursive: true });
fs.writeFileSync(output, JSON.stringify({ source: "EVE.js server/src/services/market/marketRules.js", sourceCommit: execFileSync("git",["-C",root,"rev-parse","HEAD"], { encoding:"utf8" }).trim(), cases }, null, 2) + "\n");
console.log(cases.length + " contexts, " + cases.reduce((n,c) => n + c.fees.length,0) + " fee cases");
