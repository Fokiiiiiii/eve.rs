using System.Collections.Concurrent;
using Eve.Crimewatch;
using Eve.Marshal;
using Eve.Scheduler;
using Eve.Universe;

namespace Eve.Services.Crimewatch;

public sealed record AggressionResult(AggressionRequest Request, AggressionOutcome Outcome);
