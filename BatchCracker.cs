namespace Client;

using System.Threading;

public static class BatchCracker
{
    private static readonly char[] Alphabet = (
        "ABCDEFGHIJKLMNOPQRSTUVWXYZ" +
        "abcdefghijklmnopqrstuvwxyz" +
        "0123456789" +
        "@#%^&*()_+-=.,:;?"
    ).ToCharArray();
    
    private static long globalNext;
    private static long triedTotal;
    private static long maxIndexSeen;
    private static int stopFlag;
    private static string? foundPassword;

    public static async Task<WorkResult> Run(AssignWork job, int threads, int checkpointEvery,
        Func<long, long, Task> onCheckpoint,
        CancellationToken cancellationToken)
    {
        if (job.Count <= 0)
        {
            return new WorkResult(job.JobId, false, null, 0, 0);
        }
        
        int alphabetLength = Alphabet.Length;
        long start = Math.Max(0, job.StartIndex);
        long end = start + job.Count;

        // reset per-job shared state
        Interlocked.Exchange(ref globalNext, start);
        Interlocked.Exchange(ref triedTotal, 0);
        Interlocked.Exchange(ref maxIndexSeen, start - 1);
        Volatile.Write(ref stopFlag, 0);
        foundPassword = null;
        
        // TODO: remove useCrypt and always use HashVerifier
        bool useCrypt = !string.IsNullOrEmpty(job.StoredHash) && job.StoredHash.StartsWith("$");
        var  verifier = useCrypt ? new HashVerifier(job.StoredHash) : null;

        var tasks = new List<Task>(threads);
        var perWorker = new long[threads];
        for (int t = 0; t < threads; t++)
        {
            int worker = t;
            tasks.Add(Task.Run(async () =>
            {
                Log.Info($"[W{worker}] start tid={Environment.CurrentManagedThreadId}");
                
                var (length, totalBefore, countAtLength) = FindStartBlock(start, alphabetLength);
                while (Volatile.Read(ref stopFlag) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    long index = Interlocked.Increment(ref globalNext) - 1;
                    if (index >= end)
                    {
                        break;
                    }

                    AdvanceToIndex(ref length, ref totalBefore, ref countAtLength, alphabetLength, index);

                    long offset = index - totalBefore;
                    string candidate = IndexToWord(offset, length, Alphabet);

                    bool ok = useCrypt
                        ? verifier!.Verify(candidate) // crypt hash comparison
                        : string.Equals(candidate, job.StoredHash, StringComparison.Ordinal);

                    long triedNow = Interlocked.Increment(ref triedTotal);
                    Interlocked.Increment(ref perWorker[worker]); 
                    UpdateMax(ref maxIndexSeen, index);

                    if (ok)
                    {  
                        foundPassword = candidate;
                        Log.Out(
                            $"Found password: '{candidate}' for job {job.JobId} after trying {triedNow} candidates.");
                        Volatile.Write(ref stopFlag, 1);
                        break;
                    }

                    if (checkpointEvery > 0 && (triedNow % checkpointEvery) == 0)
                    {
                        long last = Volatile.Read(ref maxIndexSeen);
                        
                        var snapshot = string.Join(" ",
                            Enumerable.Range(0, perWorker.Length)
                                .Select(i => $"W{i}:{Interlocked.Read(ref perWorker[i])}"));
                        Log.Info($"[local] per-worker tried: {snapshot}");
                        await onCheckpoint(triedNow, last); 
                    }
                }
            }, cancellationToken));
        }
        
        // wait for all workers to finish
        try
        {
            await Task.WhenAll(tasks);
        }
        catch (OperationCanceledException)
        {
             /* propagate below via token */
        }

        bool found = foundPassword is not null;
        return new WorkResult(job.JobId, found, foundPassword, Interlocked.Read(ref triedTotal), 0);
    }
    
    // Advance length, totalBefore, countAtLength to cover index
    private static void AdvanceToIndex(ref int length, ref long totalBefore, ref long countAtLength, int alphabetLength, long index)
    {
        while (totalBefore + countAtLength <= index)
        {
            totalBefore += countAtLength;
            length += 1;
            countAtLength = PowLong(alphabetLength, length);
            
            // overflow protection
            if (countAtLength == long.MaxValue) {
                break; 
            }
        }
    }

    // Atomic max() for long
    private static void UpdateMax(ref long target, long value)
    {
        long snapshot;
        do
        {
            snapshot = Interlocked.Read(ref target);
            if (value <= snapshot)
            {
                return;
            }
        } while (Interlocked.CompareExchange(ref target, value, snapshot) != snapshot);
    }   

    // Convert index to word using the given alphabet
    private static string IndexToWord(long currentIndex, int length, char[] alphabet)
    {
        int alphabetLength = alphabet.Length;
        var chars = new char[length];
        for (int pos = length - 1; pos >= 0; pos--)
        {
            int digit = (int)(currentIndex % alphabetLength);
            chars[pos] = alphabet[digit];
            currentIndex /= alphabetLength;
        }
        return new string(chars);
    }
    
    private static (int length, long TotalCandidatesBefore, long countAtLength) FindStartBlock(long startIndex, int alphabetLength)
    {
        if (startIndex < 0)
        {
            startIndex = 0;
        }
        int length = 1;
        long totalBefore = 0;
        
        // AlphabetLength^length
        long countAtLength = PowLong(alphabetLength, length);

        while (totalBefore + countAtLength <= startIndex)
        {
            totalBefore += countAtLength;
            length++;
            countAtLength = PowLong(alphabetLength, length);
        }
        return (length, totalBefore, countAtLength);
    }
    
    private static long PowLong(int @base, int exp)
    {
        long result = 1;
        for (int i = 0; i < exp; i++)
        {
            if( result > long.MaxValue / @base)
            {
                return long.MaxValue;
            }
            result *= @base;
        }
        return result;
    }
}