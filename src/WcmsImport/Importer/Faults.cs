namespace WcmsImport.Importer;

/// <summary>
/// DEMO ONLY. Switches that let you trigger failures on demand through the job's "options".
/// They exist so the README scenarios (rollback, crash/resume) can be shown. Real code would not ship these;
/// failure-injection belongs in test tooling (for example a fault-injecting proxy in front of Postgres).
/// </summary>
public static class Faults
{
    /// <summary>Fail batch N once, after its pages were written but before commit, to show the rollback.</summary>
    public static bool ShouldFailBatch(Job job, int batchNo, int attempt) => job.Options.FailBatch == batchNo && attempt == 1;

    /// <summary>Pretend the reply to a successful COMMIT was lost: the batch IS saved, but the worker is told it failed.</summary>
    public static bool ShouldLoseReply(Job job, int batchNo, int attempt) => job.Options.LostReplyBatch == batchNo && attempt == 1;

    /// <summary>
    /// Fail the closing transaction on its first N attempts, after its writes but before COMMIT, to show it rolls back
    /// whole and is retried. The batches are already saved, so the job must still end COMPLETED, not FAILED.
    /// </summary>
    public static bool ShouldFailClose(Job job, int attempt) => attempt <= (job.Options.FailClose ?? 0);

    /// <summary>Kill the process once, right after batch N committed, to show resume from the checkpoint.</summary>
    public static bool ShouldCrash(Job job, int batchNo) => job.Options.CrashAfterBatch == batchNo && !job.Options.Crashed;
}
