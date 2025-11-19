namespace Client;

using System.Diagnostics;
using System.Threading;

public sealed class Cracker
{
    private readonly char[] _alphabet;
    private readonly int _threads;

    // pool
    private readonly Thread[] _pool;
    private readonly ManualResetEventSlim _start = new(false);
    private volatile int _jobVersion = 0;
    // per-worker last seen
    private int[] _seen;                       

    // current job
    private volatile JobState? _job;
    private volatile CountdownEvent? _jobDone;

    public Cracker(char[] alphabet, int threads)
    {
        _alphabet = alphabet ?? throw new ArgumentNullException(nameof(alphabet));
        _threads  = Math.Max(1, threads);

        _pool = new Thread[_threads];
        _seen = new int[_threads];

        for (int i = 0; i < _threads; i++)
        {
            int slot = i;
            _pool[i] = new Thread(() => WorkerLoop(slot))
            {
                IsBackground = true,
                Name = $"W{slot}"
            };
            _pool[i].Start();
        }
    }

    public readonly record struct SliceResult(bool Found, string? Password, long Tried, long DurationMs);

    // pool: publish a job, signal _start, wait for _jobDone
    public SliceResult RunSlice(
        string storedHash,
        long startIndex,
        long count,
        int checkpoint,
        // log "[Wi] start tid=*"
        Action<int,int> onWorkerStart,  
        // tried + per-worker
        Action<long,long[]> onCheckpoint,
        Func<bool> isStopRequested)
    {
        // create job state
        var job = new JobState(_alphabet, storedHash, startIndex, count, checkpoint, _threads, onCheckpoint, isStopRequested);
        
        _jobDone = new CountdownEvent(_threads);
        _job     = job;

        // workers pick this job
        Interlocked.Increment(ref _jobVersion);

        // each worker prints its tid per job
        job.OnWorkerStart = onWorkerStart;

        job.Sw.Restart();
        // release workers
        _start.Set(); 
        // wait for workers to finish
        _jobDone.Wait();          
        _start.Reset();
        job.Sw.Stop();

        return new SliceResult(job.ResultPassword is not null, job.ResultPassword,
                               Volatile.Read(ref job.TotalTried),
                               (long)job.Sw.Elapsed.TotalMilliseconds);
    }
    

    private sealed class JobState
    {
        public readonly char[] Alphabet;
        public readonly string StoredHash;
        public readonly long Start, Count;
        public readonly int  Checkpoint;
        public readonly int  Threads;
        
        // 0/1
        public int StopFlag;              
        public long NextRel;
        public long TotalTried;
        public string? ResultPassword;
        
        // 1 == tried
        public readonly int[]  DoneMap;    
        // contiguous from 0
        public long DonePrefix;                        
        public long LastCheckpoint;
        public readonly object ProgressLock = new();

        public readonly long[] PerWorkerTried;

        public Action<int,int>? OnWorkerStart;
        public readonly Action<long,long[]> OnCheckpoint;
        public readonly Func<bool> IsStopRequested;

        public readonly Stopwatch Sw = new();

        public JobState(
            char[] alphabet, string storedHash, long start, long count, int checkpoint, int threads,
            Action<long,long[]> onCheckpoint, Func<bool> isStopRequested)
        {
            Alphabet = alphabet;
            StoredHash = storedHash;
            Start = start; 
            Count = count; 
            Checkpoint = Math.Max(1, checkpoint);
            Threads = Math.Max(1, threads);
            DoneMap = new int[(int)count];
            PerWorkerTried = new long[Threads];
            OnCheckpoint = onCheckpoint;
            IsStopRequested = isStopRequested;
            NextRel = 0;
        }
    }

    private void WorkerLoop(int slot)
    {
        // stable tid for this worker
        int tid = Environment.CurrentManagedThreadId;

        while (true)
        {
            _start.Wait();
            int ver = _jobVersion;
            // already processed this version
            if (_seen[slot] == ver)
            {
                Thread.Sleep(1); 
                continue;
            }

            var job = _job;
            var done = _jobDone;
            if (job == null || done == null)
            {
                Thread.Sleep(1);
                continue;
            }
            job.OnWorkerStart?.Invoke(slot, tid);

            try
            {
                while (Volatile.Read(ref job.StopFlag) == 0 && !job.IsStopRequested())
                {
                    long rel = Interlocked.Increment(ref job.NextRel) - 1;
                    
                    // relative index exceeded, nothing left to do
                    if (rel >= job.Count)
                    {
                        break;
                    }

                    long idx = job.Start + rel;
                    string cand = IndexToCandidate(idx, job.Alphabet);

                    bool ok = Verify(cand, job.StoredHash);

                    Interlocked.Increment(ref job.PerWorkerTried[slot]);
                    Interlocked.Increment(ref job.TotalTried);
                    Volatile.Write(ref job.DoneMap[rel], 1);

                    UpdateProgress(job);

                    if (ok)
                    {
                        job.ResultPassword = cand;
                        Interlocked.Exchange(ref job.StopFlag, 1);
                        break;
                    }
                }
            }
            finally
            {
                _seen[slot] = ver;    // mark this job version as processed
                done.Signal();        // let publisher proceed
            }
        }
    }

    private static void UpdateProgress(JobState job)
    {
        long[]? toSend = null;

        lock (job.ProgressLock)
        {
            // contiguous done prefix from 0 to DonePrefix
            while (job.DonePrefix < job.Count && Volatile.Read(ref job.DoneMap[(int)job.DonePrefix]) == 1)
            {
                job.DonePrefix++;
            }

            var list = new List<long>();
            long next = job.LastCheckpoint + job.Checkpoint;
            while (next <= job.DonePrefix)
            {
                list.Add(next);
                next += job.Checkpoint;
            }

            if (job.DonePrefix == job.Count && (list.Count == 0 || list[^1] != job.Count))
            {
                list.Add(job.Count);
            }

            if (list.Count > 0)
            {
                job.LastCheckpoint = list[^1];
                toSend = list.ToArray();
            }
        }

        if (toSend is null)
        {
            return;
        }

        var snapshot = (long[])job.PerWorkerTried.Clone();
        foreach (var m in toSend)
        {
            job.OnCheckpoint(m, snapshot);
        }
    }
    

    private static bool Verify(string candidate, string storedHash)
    {
        string? produced = CryptRa.CryptWrap(candidate, storedHash);
        return produced is not null && string.Equals(produced, storedHash, StringComparison.Ordinal);
    }

    private static string IndexToCandidate(long index, char[] alphabet)
    {
        if (index < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }
        long alphabetLength = alphabet.Length;
        if (alphabetLength <= 1)
        {
            throw new InvalidOperationException("Alphabet must have length >= 2.");
        }

        long len = 1;
        long pow = alphabetLength;
        long lastTotal = 0;
        checked
        {
            while (true)
            {
                long total = lastTotal + pow;
                if (index < total)
                {
                    long offset = index - lastTotal;
                    return ToFixedBase(offset, len, alphabetLength, alphabet);
                }
                lastTotal = total;
                if (pow > long.MaxValue / alphabetLength)
                {
                    throw new OverflowException("Index too large.");
                }
                pow *= alphabetLength; len++;
            }
        }
    }

    private static string ToFixedBase(long value, long width, long baseN, char[] alphabet)
    {
        var buffer = new char[width];
        for (long i = width - 1; i >= 0; --i)
        {
            long digit = value % baseN;
            buffer[i] = alphabet[(int)digit];
            value /= baseN;
        }
        return new string(buffer);
    }
}
