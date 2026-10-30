using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Wolverine;
using Wolverine.ErrorHandling;

namespace Fworker.Workflows.Faults;

// W9.10o: what a failed save or read of the database means. The stores used to catch every DbUpdateException as "another writer was first", so a transient fault of the database (a failover of the
// S0, throttling, a deadlock victim) was answered as a revision conflict and the handler applied the input again to a state that had not changed; and the only global retry rule gave a SqlException about
// a minute (2, 10, 60 seconds) before the dead letters, so an episode longer than that lost timers, grants and train inputs. Two questions are answered here and nowhere else:
//  - IsConflict: the only exceptions a store turns into "false, read again": DbUpdateConcurrencyException and a duplicate key (2627, 2601). Anything else is a fault and is thrown.
//  - IsTransient: a fault a later try gets past (the SQL error numbers below), wherever it sits in the chain of inner exceptions.
public static class SqlFaults
{
    /// <summary>-2 command timeout, 1205 deadlock victim, 4060 database not available, 40197 service error, 40501 service busy, 40613 database not currently available, 49918 not enough resources, 10928 and 10929 resource limits.</summary>
    public static readonly IReadOnlySet<int> TransientNumbers = new HashSet<int> { -2, 1205, 4060, 40197, 40501, 40613, 49918, 10928, 10929 };

    /// <summary>2627 primary key violation, 2601 unique index violation: the insert lost to another writer.</summary>
    public static bool IsDuplicateKey(Exception e) => Chain(e).Any(x => x is SqlException { Number: 2627 or 2601 });

    /// <summary>The exceptions that mean "another writer was first": a stale revision, or the same key inserted at once. Everything else a save throws is a fault.</summary>
    public static bool IsConflict(Exception e) => e is DbUpdateConcurrencyException || (e is DbUpdateException && IsDuplicateKey(e));

    /// <summary>A fault of the database that a later try gets past. An exception of EF or of the pool wraps the SqlException, so the whole chain is looked at.</summary>
    public static bool IsTransient(Exception e) => Chain(e).Any(x => x is SqlException s && (TransientNumbers.Contains(s.Number) || s.Errors.Cast<SqlError>().Any(er => TransientNumbers.Contains(er.Number))));

    /// <summary>The exceptions a handler throws when two writers moved the same state more often than its own retries allow: the input is run again after a pause, it is not lost.</summary>
    public static bool IsHandlerConflict(Exception e) => e is WorkItem.WorkItemConflictException or Capacity.CapacityConflictException or Merge.MergeTrainConflictException or Delivery.TrainConflictException;

    static IEnumerable<Exception> Chain(Exception e)
    {
        for (var x = e; x is not null; x = x.InnerException)
        {
            yield return x;
            if (x is AggregateException a) foreach (var inner in a.InnerExceptions.SelectMany(Chain)) yield return inner;
        }
    }
}

/// <summary>
/// The retry rule for the faults above, on the Wolverine options of the host (the first rule of the host, so that no rule before it takes the same exceptions): three quick tries (2, 10 and 60 seconds), then the message is
/// scheduled again after 5 minutes, 15 minutes, 1 hour and 3 hours, and only then moved to the dead letters, which /api/v1/status counts. A transient fault of the database or a conflict that outlasts the handler's own
/// retries is a delay, not a loss; the schedule is finite, so a fault that never ends reaches the dead letters where it can be seen.
/// </summary>
public static class FaultPolicy
{
    public static readonly TimeSpan[] Quick = [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(60)];
    public static readonly TimeSpan[] Long = [TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15), TimeSpan.FromHours(1), TimeSpan.FromHours(3)];

    /// <summary>The longest a message is retried before the dead letters: the sum of both schedules.</summary>
    public static TimeSpan Total => Quick.Aggregate(TimeSpan.Zero, (a, b) => a + b) + Long.Aggregate(TimeSpan.Zero, (a, b) => a + b);

    public static void Apply(WolverineOptions w)
    {
        w.Policies.OnException(e => SqlFaults.IsTransient(e) || SqlFaults.IsHandlerConflict(e), "transient database fault or handler conflict")
            .RetryWithCooldown(Quick).Then.ScheduleRetry(Long[0]).Then.ScheduleRetry(Long[1]).Then.ScheduleRetry(Long[2]).Then.ScheduleRetry(Long[3]).Then.MoveToErrorQueue();
    }
}
