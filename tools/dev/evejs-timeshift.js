// Research clock for the EVE.js reference run (loaded with NODE_OPTIONS=--require by tools/dev/run-evejs-live.sh).
// Research points accrue from Date.now() in services/agent/researchRuntime.js (getNowMs). Only that caller sees the offset in
// the file named by EVE_TIMESHIFT_FILE (milliseconds, a plain integer), so a walk can let R&D time pass without waiting.
// Every other caller keeps the real clock: the persistence owner lease reads Date.now too, and a jump would expire it.
"use strict";

const fs = require("fs");
const realNow = Date.now.bind(Date);
const RealDate = Date;
const file = process.env.EVE_TIMESHIFT_FILE || "";
const RESEARCH_CALLER = /services[\\/]agent[\\/]researchRuntime\.js$/;
let offsetMs = 0;
let checkedAt = 0;

// The file is read at most every 100 ms of real time.
function currentOffsetMs() {
  const real = realNow();
  if (real - checkedAt >= 100) {
    checkedAt = real;
    try {
      offsetMs = Number(fs.readFileSync(file, "utf8").trim()) || 0;
    } catch {
      offsetMs = 0;
    }
  }
  return offsetMs;
}

// True when the frame that called the clock (below `fn`) is the research runtime.
function calledFromResearch(fn) {
  const saved = Error.prepareStackTrace;
  Error.prepareStackTrace = (_, frames) => frames;
  const holder = {};
  Error.captureStackTrace(holder, fn);
  const frames = holder.stack;
  Error.prepareStackTrace = saved;
  const caller = Array.isArray(frames) && frames[0];
  const name = caller && caller.getFileName && caller.getFileName();
  return Boolean(name && RESEARCH_CALLER.test(name));
}

class ShiftedDate extends RealDate {
  constructor(...args) {
    if (args.length === 0) {
      super(realNow() + (calledFromResearch(ShiftedDate) ? currentOffsetMs() : 0));
    } else {
      super(...args);
    }
  }
  static now() {
    return realNow() + (calledFromResearch(ShiftedDate.now) ? currentOffsetMs() : 0);
  }
}

if (file) {
  globalThis.Date = ShiftedDate;
}
