using System;
using System.Collections.Concurrent;
using System.Threading;

namespace BlackJacket.OptimalPlay
{
    /// <summary>
    /// Result slot filled by the worker thread and polled from the Unity main thread.
    /// <see cref="Done"/> is volatile: a true read publishes <see cref="Result"/> and
    /// <see cref="Error"/> written before it.
    /// </summary>
    internal sealed class SolverJob<T>
    {
        internal volatile bool Done;
        internal T Result;
        internal Exception Error;
    }

    /// <summary>
    /// One background thread runs all native solver calls so searches never stall the draw
    /// phase. Callers post a job and later poll <see cref="SolverJob{T}.Done"/> from Update;
    /// dropping the job reference cancels nothing, the result is simply discarded.
    /// </summary>
    internal static class SolverWorker
    {
        private static readonly BlockingCollection<Action> Queue = new BlockingCollection<Action>();
        private static readonly object Gate = new object();
        private static Thread _thread;

        internal static SolverJob<T> Post<T>(Func<T> work)
        {
            var job = new SolverJob<T>();
            EnsureStarted();
            Queue.Add(() =>
            {
                try
                {
                    job.Result = work();
                }
                catch (Exception e)
                {
                    job.Error = e;
                }
                finally
                {
                    job.Done = true;
                }
            });
            return job;
        }

        private static void EnsureStarted()
        {
            if (_thread != null)
            {
                return;
            }

            lock (Gate)
            {
                if (_thread != null)
                {
                    return;
                }

                _thread = new Thread(Loop)
                {
                    IsBackground = true,
                    Name = "OptimalPlaySolver",
                };
                _thread.Start();
            }
        }

        private static void Loop()
        {
            foreach (Action job in Queue.GetConsumingEnumerable())
            {
                try
                {
                    job();
                }
                catch (Exception e)
                {
                    OptimalPlayPlugin.Log.LogError($"solver worker job failed: {e}");
                }
            }
        }
    }
}
