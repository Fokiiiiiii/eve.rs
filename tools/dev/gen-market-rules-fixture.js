// Records EVE.js marketRules.js without starting the reference server. No implementation is copied.
"use strict";
const fs = require("node:fs");
const path = require("node:path");
const Module = require("node:module");
const { execFileSync } = require("node:child_process");
// The sales tax this checkout records: 7.5, CCP patch 2025-03-12.1 (decision Q1 in market-decisions.md). EVE.js hard-codes 8.0
// (marketRules.js:43); that one constant is replaced in the reference source before it is compiled; every other formula is the reference's.
const TAX_PERCENT = 7.5;
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
try {
  const rulesPath = path.join(root, "server/src/services/market/marketRules.js");
  const referenceSource = fs.readFileSync(rulesPath, "utf8");
  const taxLine = "const MARKET_TRANSACTION_TAX_DEFAULT_PERCENT = 8.0;";
  if (referenceSource.split(taxLine).length !== 2) throw new Error("the reference's transaction tax constant is not where this generator expects it");
  const rulesModule = new Module(rulesPath, module);
  rulesModule.filename = rulesPath;
  rulesModule.paths = Module._nodeModulePaths(path.dirname(rulesPath));
  rulesModule._compile(referenceSource.replace(taxLine, `const MARKET_TRANSACTION_TAX_DEFAULT_PERCENT = ${TAX_PERCENT};`), rulesPath);
  rules = rulesModule.exports;
}
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
fs.writeFileSync(output, JSON.stringify({ source: `EVE.js server/src/services/market/marketRules.js, transaction tax constant set to ${TAX_PERCENT} (eve.rs decision Q1)`, sourceCommit: execFileSync("git",["-C",root,"rev-parse","HEAD"], { encoding:"utf8" }).trim(), cases }, null, 2) + "\n");
console.log(cases.length + " contexts, " + cases.reduce((n,c) => n + c.fees.length,0) + " fee cases");
