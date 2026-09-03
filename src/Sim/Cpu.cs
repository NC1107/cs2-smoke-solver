namespace SmokeSolver.Sim;

public static class Cpu
{
    // Every heavy loop in the simulator is CPU-bound, and Parallel's default
    // degree of parallelism is *unbounded* - it keeps adding replicas for as
    // long as the ThreadPool hands out threads. That both oversubscribes the
    // cores and, worse, leaves nothing for anything else running on the pool:
    // under `serve`, a solve would take every worker thread and starve both its
    // own progress stream and any other user's request for the whole solve.
    // One worker per core, and no more.
    public static ParallelOptions Bound => new() { MaxDegreeOfParallelism = Environment.ProcessorCount };

    // The same, cancellable: a solve whose client has gone must stop, not
    // finish on every core for nobody.
    public static ParallelOptions BoundWith(CancellationToken ct) =>
        new() { MaxDegreeOfParallelism = Environment.ProcessorCount, CancellationToken = ct };
}
