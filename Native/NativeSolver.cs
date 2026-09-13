using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace BlackJacket.OptimalPlay
{
    /// <summary>
    /// Loads OptimalPlaySolver.dll and calls the flat-buffer C ABI described in
    /// native/src/protocol.rs. Calls are synchronous and take anywhere from microseconds to
    /// seconds, so they must run on the solver worker thread, never on the Unity main thread.
    /// </summary>
    internal static class NativeSolver
    {
        private const string LibraryName = "OptimalPlaySolver.dll";
        private const int StatusOk = 0;
        private const int StatusTooSmall = 1;

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate uint VersionFn();

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int CallFn(
            IntPtr input,
            UIntPtr inputLength,
            ulong nodeBudget,
            ulong timeMs,
            IntPtr output,
            UIntPtr outputCapacity,
            out UIntPtr outputLength);

        private static VersionFn _version;
        private static CallFn _solve;
        private static CallFn _evaluate;
        private static CallFn _evaluateBatch;

        /// <summary>True once the library is loaded and its protocol version matches.</summary>
        internal static bool Available { get; private set; }

        /// <summary>Why loading failed; null when <see cref="Available"/> is true.</summary>
        internal static string LoadError { get; private set; } = "not initialized";

        internal static void Initialize()
        {
            if (Available)
            {
                return;
            }

            try
            {
                string directory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? ".";
                string path = Path.Combine(directory, LibraryName);
                if (!File.Exists(path))
                {
                    LoadError = $"missing {path}";
                    return;
                }

                IntPtr library = LoadLibraryW(path);
                if (library == IntPtr.Zero)
                {
                    LoadError = $"LoadLibrary failed (win32 error {Marshal.GetLastWin32Error()})";
                    return;
                }

                _version = Bind<VersionFn>(library, "opl_version");
                _solve = Bind<CallFn>(library, "opl_solve");
                _evaluate = Bind<CallFn>(library, "opl_evaluate");
                _evaluateBatch = Bind<CallFn>(library, "opl_evaluate_batch");

                uint version = _version();
                if (version != SolverProtocol.Version)
                {
                    LoadError = $"protocol mismatch (native {version}, plugin {SolverProtocol.Version})";
                    return;
                }

                LoadError = null;
                Available = true;
            }
            catch (Exception e)
            {
                LoadError = e.Message;
            }
        }

        /// <summary>Searches one draw phase position and returns the best move plus diagnostics.</summary>
        internal static SolveResult Solve(SolverState state, int nodeBudget, int timeMs)
        {
            EnsureAvailable();
            byte[] output = Call(_solve, SolverProtocol.WriteState(state), nodeBudget, timeMs, 1024);
            var reader = new SolverReader(output);
            reader.U32(); // protocol version, verified when the library was loaded
            int status = reader.I32();
            if (status != StatusOk)
            {
                throw new NativeSolverException(status);
            }

            var result = new SolveResult
            {
                Best = reader.Move(),
                Value = (float)reader.F64(),
                Nodes = (int)reader.U64(),
                Aborted = reader.U32() != 0,
                ProgressTieBreak = reader.U32() != 0,
                PValue = reader.I32(),
                OValue = reader.I32(),
                PTarget = reader.I32(),
                OTarget = reader.I32(),
                Payable = reader.I32(),
                SleeveCost = reader.I32(),
            };

            uint flags = reader.U32();
            result.CanPass = (flags & 1) != 0;
            result.CanSleeve = (flags & 2) != 0;
            result.TopNull = (flags & 4) != 0;
            result.OwnTableFull = (flags & 8) != 0;
            result.OpponentTableFull = (flags & 16) != 0;
            result.SleeveReason = (SleeveReason)reader.U32();

            int legalCount = (int)reader.U32();
            int evaluationCount = (int)reader.U32();
            result.Legal = new List<SolverMove>(legalCount);
            for (int i = 0; i < legalCount; i++)
            {
                result.Legal.Add(reader.Move());
            }

            // Evaluations come back in the same order as the leading legal moves.
            result.Evaluations = new List<MoveEvaluation>(evaluationCount);
            for (int i = 0; i < evaluationCount; i++)
            {
                result.Evaluations.Add(new MoveEvaluation
                {
                    Move = result.Legal[i],
                    Value = (float)reader.F64(),
                });
            }
            return result;
        }

        /// <summary>Evaluates a single position: the value of acting now and then playing on.</summary>
        internal static float Evaluate(SolverState state, int nodeBudget, int timeMs)
        {
            EnsureAvailable();
            byte[] output = Call(_evaluate, SolverProtocol.WriteState(state), nodeBudget, timeMs, 64);
            var reader = new SolverReader(output);
            reader.U32();
            int status = reader.I32();
            if (status != StatusOk)
            {
                throw new NativeSolverException(status);
            }
            return (float)reader.F64();
        }

        /// <summary>Evaluates every candidate of a prebuilt batch input, in parallel.</summary>
        internal static float[] EvaluateBatch(byte[] input, int candidateCount, int nodeBudget, int timeMs)
        {
            EnsureAvailable();
            byte[] output = Call(_evaluateBatch, input, nodeBudget, timeMs, 12 + (8 * candidateCount) + 16);
            var reader = new SolverReader(output);
            reader.U32();
            int status = reader.I32();
            if (status != StatusOk)
            {
                throw new NativeSolverException(status);
            }

            int count = (int)reader.U32();
            var values = new float[count];
            for (int i = 0; i < count; i++)
            {
                values[i] = (float)reader.F64();
            }
            return values;
        }

        /// <summary>
        /// Serializes a state and one candidate per entry. Each candidate mutates a private
        /// copy of the state inside the native solver, so the managed state stays untouched.
        /// </summary>
        internal static byte[] WriteBatch(SolverState state, List<Action<SolverBuffer>> candidates)
        {
            var buffer = new SolverBuffer();
            SolverProtocol.WriteState(buffer, state);
            SolverProtocol.WriteCandidateCount(buffer, candidates.Count);
            foreach (Action<SolverBuffer> candidate in candidates)
            {
                SolverProtocol.WriteCandidate(buffer, candidate);
            }
            return buffer.ToArray();
        }

        /// <summary>
        /// Positions of `order` inside `source` as a permutation, matched by card identity so
        /// duplicate cards still map one to one.
        /// </summary>
        internal static int[] IndexOrder(List<SolverCard> order, List<SolverCard> source)
        {
            var available = new Dictionary<SolverCard, Queue<int>>(source.Count);
            for (int i = 0; i < source.Count; i++)
            {
                if (!available.TryGetValue(source[i], out Queue<int> indices))
                {
                    indices = new Queue<int>();
                    available.Add(source[i], indices);
                }
                indices.Enqueue(i);
            }

            var result = new int[order.Count];
            for (int i = 0; i < order.Count; i++)
            {
                if (!available.TryGetValue(order[i], out Queue<int> indices) || indices.Count == 0)
                {
                    return null;
                }
                result[i] = indices.Dequeue();
            }
            return result;
        }

        private static byte[] Call(CallFn call, byte[] input, int nodeBudget, int timeMs, int initialCapacity)
        {
            // The native side clamps both budgets; keep them positive so the clamped values
            // stay sane even if a config value goes missing.
            ulong nodes = (ulong)Math.Max(1, (long)nodeBudget);
            ulong time = (ulong)Math.Max(1, (long)timeMs);
            int capacity = Math.Max(16, initialCapacity);

            while (true)
            {
                byte[] output = new byte[capacity];
                GCHandle inputPin = GCHandle.Alloc(input, GCHandleType.Pinned);
                GCHandle outputPin = GCHandle.Alloc(output, GCHandleType.Pinned);
                try
                {
                    int status = call(
                        inputPin.AddrOfPinnedObject(),
                        (nuint)input.Length,
                        nodes,
                        time,
                        outputPin.AddrOfPinnedObject(),
                        (nuint)output.Length,
                        out UIntPtr written);
                    if (status == StatusTooSmall)
                    {
                        capacity = checked((int)written);
                        continue;
                    }
                    if (status != StatusOk)
                    {
                        throw new NativeSolverException(status);
                    }

                    int length = checked((int)written);
                    if (length != output.Length)
                    {
                        Array.Resize(ref output, length);
                    }
                    return output;
                }
                finally
                {
                    inputPin.Free();
                    outputPin.Free();
                }
            }
        }

        private static void EnsureAvailable()
        {
            if (!Available)
            {
                throw new InvalidOperationException($"native solver unavailable: {LoadError}");
            }
        }

        private static T Bind<T>(IntPtr library, string name)
            where T : Delegate
        {
            IntPtr pointer = GetProcAddress(library, name);
            if (pointer == IntPtr.Zero)
            {
                throw new EntryPointNotFoundException($"native entry point {name} not found");
            }
            return Marshal.GetDelegateForFunctionPointer<T>(pointer);
        }

        [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
        private static extern IntPtr LoadLibraryW(string fileName);

        [DllImport("kernel32", CharSet = CharSet.Ansi, SetLastError = true, ExactSpelling = true)]
        private static extern IntPtr GetProcAddress(IntPtr library, string name);
    }

    /// <summary>Negative native status codes, mapped to the protocol constants.</summary>
    internal sealed class NativeSolverException : Exception
    {
        internal NativeSolverException(int status)
            : base(Describe(status))
        {
            Status = status;
        }

        internal int Status { get; }

        private static string Describe(int status)
        {
            switch (status)
            {
                case -1: return "native solver rejected the protocol version";
                case -2: return "native solver received a truncated buffer";
                case -3: return "native solver received an invalid card";
                case -4: return "native solver received an invalid operation";
                case -100: return "native solver panicked";
                default: return $"native solver failed (status {status})";
            }
        }
    }

    /// <summary>Little-endian reader for native responses; sized by the output buffers.</summary>
    internal sealed class SolverReader
    {
        private readonly byte[] _data;
        private int _position;

        internal SolverReader(byte[] data)
        {
            _data = data;
        }

        internal int I32()
        {
            int value = BitConverter.ToInt32(_data, _position);
            _position += 4;
            return value;
        }

        internal uint U32()
        {
            uint value = BitConverter.ToUInt32(_data, _position);
            _position += 4;
            return value;
        }

        internal ulong U64()
        {
            ulong value = BitConverter.ToUInt64(_data, _position);
            _position += 8;
            return value;
        }

        internal double F64()
        {
            double value = BitConverter.ToDouble(_data, _position);
            _position += 8;
            return value;
        }

        internal SolverMove Move()
        {
            return new SolverMove
            {
                Kind = (MoveKind)I32(),
                SleeveIndex = I32(),
                ToOpponent = U32() != 0,
            };
        }
    }
}
