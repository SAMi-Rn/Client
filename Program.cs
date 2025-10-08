namespace Cracker;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// password cracker  that uses libxcrypt's crypt_ra API
/// for verifying password candidates against Unix-style password hashes (e.g. bcrypt, sha512-crypt).
/// </summary>
class Program
{

    /// <summary>
    /// Delegate signature for libxcrypt's crypt_ra
    /// char *crypt_ra(const char *key, const char *setting, void **data, int *size)
    /// it is called via P/Invoke
    /// </summary>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr CryptRaDelegate(
        [MarshalAs(UnmanagedType.LPStr)] string key,
        [MarshalAs(UnmanagedType.LPStr)] string setting,
        ref IntPtr dataPtr,
        ref int size);

    // Holder for the bound function
    private static CryptRaDelegate? cryptRaFunc;

    // Native library handle kept alive for process lifetime
    private static IntPtr nativeLibHandle = IntPtr.Zero;

    // Initialization guard for binding the native function
    private static bool cryptInitialized = false;
    private static readonly object cryptInitLock = new object();
    
    // Global index assigned to workers
    private static long globalStartIndex = 0;

    // Atomic stop flag: 0 = running, 1 = found/stop
    private static int stopFlag = 0;
    private static bool IsStopped => Volatile.Read(ref stopFlag) != 0;
    
    private static string? resultPassword = null;


    /// <summary>
    /// Per-thread state passed to crypt_ra
    /// will allocate/realloc as needed when crypt_ra is used.
    /// 
    /// keep one instance per thread using ThreadLocal.
    /// </summary>
    private sealed class ThreadCryptState
    {
        public IntPtr OpaquePointer = IntPtr.Zero;
        public int Size = 0;
    }

    // Thread-local container for per-thread crypt state.
    private static ThreadLocal<ThreadCryptState> threadState =
        new ThreadLocal<ThreadCryptState>(() => new ThreadCryptState());

    // Ensure thread-local buffers are cleaned up
    static Program()
    {
        AppDomain.CurrentDomain.ProcessExit += (_, __) =>
        {
            try
            {
                foreach (var st in threadState.Values)
                {
                    if (st.OpaquePointer != IntPtr.Zero)
                    {
                        // freeing here helps avoid small leaks in long run
                        Marshal.FreeHGlobal(st.OpaquePointer);
                        st.OpaquePointer = IntPtr.Zero;
                    }
                }
                threadState.Dispose();
            }
            catch
            {
                // empty
            }

            if (nativeLibHandle != IntPtr.Zero)
            {
                try { NativeLibrary.Free(nativeLibHandle); } catch { /* ignore */ }
            }
        };
    }
    

    /// <summary>
    /// Ensure crypt_ra is bound from an available native library.
    /// thread-safe via a lock. .
    /// </summary>
    private static void EnsureCryptRaLoaded()
    {
        if (cryptInitialized)
        {
            return;
        }

        lock (cryptInitLock)
        {
            if (cryptInitialized)
            {
                return;
            }
            cryptInitialized = true;

            // libraries to probe
            string[] candidates =
            {
                "libxcrypt.so.2",
                "libxcrypt.so.1",
                "libxcrypt.so.0",
                "libcrypt.so.1",
                "libcrypt.so",
                "libc.so.6"
            };

            foreach (var lib in candidates)
            {
                try
                {
                    if (!NativeLibrary.TryLoad(lib, out var handle))
                    {
                        continue;
                    }

                    if (NativeLibrary.TryGetExport(handle, "crypt_ra", out var ptr))
                    {
                        cryptRaFunc = Marshal.GetDelegateForFunctionPointer<CryptRaDelegate>(ptr);
                        // keep handle alive
                        nativeLibHandle = handle;
                        Console.WriteLine($"bound crypt_ra from {lib}");
                        return;
                    }

                    // free and try next
                    NativeLibrary.Free(handle);
                }
                catch
                {
                    // empty
                }
            }
            throw new DllNotFoundException("crypt_ra not found in probed libraries. Install libxcrypt.");
        }
    }
    
    /// <summary>
    /// Returns a non-null CryptRaDelegate or throws if the native symbol is not bound.
    ///  avoids nullable reference warnings
    /// </summary>
    private static CryptRaDelegate GetCryptRa()
    {
        EnsureCryptRaLoaded();
        var f = cryptRaFunc;
        if (f == null)
        {
            throw new InvalidOperationException("crypt_ra not bound");
        }
        return f;
    }

    /// <summary>
    /// Returns the non-null thread-local crypt state for the current thread.
    /// </summary>
    private static ThreadCryptState GetThreadState()
    {
        var s = threadState.Value;
        if (s == null)
        {
            throw new InvalidOperationException("thread-local state unavailable");
        }
        return s;
    }
    
    /// <summary>
    /// Compute the crypt-style hash for `plaintext` using the parameters in `storedHash`
    /// Uses crypt_ra and per-thread state.
    /// Returns the resulting hash string or null on error.
    /// 
    /// - `storedHash` is the stored "setting" (salt + algorithm prefix), e.g. "$2b$12$..."
    /// </summary>
    public static string? CryptWrap(string plaintext, string? storedHash)
    {
        if (storedHash == null) return null;

        // Get non-null delegate and thread state
        var cryptRa = GetCryptRa();
        var state = GetThreadState();

        // rypt_ra returns pointer to result (C string).
        IntPtr resultPtr = cryptRa(plaintext, storedHash, ref state.OpaquePointer, ref state.Size);
        if (resultPtr == IntPtr.Zero) return null;
        return Marshal.PtrToStringAnsi(resultPtr);
    }
    
    /// <summary>
    /// Compute power B^E into an unsigned long
    /// Returns true if computed exactly; false if overflow would occur
    /// </summary>
    public static bool TryPowULong(int @base, int exponent, out ulong result)
    {
        result = 1;
        for (int i = 0; i < exponent; ++i)
        {
            if (@base == 0) { result = 0; return true; }
            if (result > ulong.MaxValue / (ulong)@base) { result = ulong.MaxValue; return false; }
            result *= (ulong)@base;
        }
        return true;
    }

    /// <summary>
    /// Atomically fetch a block of work for a worker.
    /// Returns start index (ulong). If no work remains, returns ulong.MaxValue.
    /// </summary>
    public static ulong FetchWorkBlock(int blockSize)
    {
        long start = Interlocked.Add(ref globalStartIndex, blockSize) - blockSize;
        if (start < 0) return ulong.MaxValue;
        return (ulong)start;
    }

    /// <summary>
    /// Convert an index in base-(alphabet.Length) to the candidate password string of length passLen.
    /// Produces the password with the most-significant digit first.
    /// </summary>
    public static string IndexToPassword(ulong index, int passLen, char[] alphabet)
    {
        int alphabetLen = alphabet.Length;
        var buffer = new char[passLen];
        for (int i = passLen - 1; i >= 0; i--)
        {
            int digit = (int)(index % (ulong)alphabetLen);
            buffer[i] = alphabet[digit];
            index /= (ulong)alphabetLen;
        }
        return new string(buffer);
    }

    /// <summary>
    /// Worker loop executed by each thread
    /// - verifier: object that provides Verify(candidate)
    /// - passwordLength: length
    /// - alphabet: alphabet
    /// - total: number of candidates
    /// - blockSize: number of candidates each worker has

    /// </summary>
    public static void WorkerLoop(HashVerifier verifier, int passwordLength, char[] alphabet, long total, int blockSize)
    {
        while (!IsStopped)
        {
            ulong startU = FetchWorkBlock(blockSize);
            if (startU == ulong.MaxValue)
            {
                break;
            }
            long start = (long)startU;
            if (start >= total)
            {
                break;
            }

            long end = Math.Min(total, start + blockSize);
            for (long j = start; j < end && !IsStopped; ++j)
            {
                string candidate = IndexToPassword((ulong)j, passwordLength, alphabet);
                if (verifier.Verify(candidate))
                {
                    resultPassword = candidate;
                    Interlocked.Exchange(ref globalStartIndex, total);
                    Volatile.Write(ref stopFlag, 1);
                    break;
                }
            }
        }
    }

    /// <summary>
    /// Multithreaded cracker for a single password length.
    /// Returns the found password or null if not found.
    /// </summary>
    public static string? CrackMultiThread(string storedHash, int passwordLength, char[] alphabet, int numThreads, int blockSize = 256)
    {
        // Validate search space
        if (!TryPowULong(alphabet.Length, passwordLength, out var totalUL) || totalUL > (ulong)long.MaxValue)
            throw new OverflowException("Search space exceeds supported maximum. Reduce length or alphabet.");

        long total = (long)totalUL;
        var verifier = new HashVerifier(storedHash);

        Interlocked.Exchange(ref globalStartIndex, 0);
        Volatile.Write(ref stopFlag, 0);
        resultPassword = null;

        var tasks = new Task[numThreads];
        for (int t = 0; t < numThreads; ++t)
            tasks[t] = Task.Run(() => WorkerLoop(verifier, passwordLength, alphabet, total, blockSize));

        Task.WaitAll(tasks);
        return resultPassword;
    }

    /// <summary>
    /// Multithreaded cracker across a range of lengths 
    /// Calls CrackMultiThread for each length in increasing order and returns the first found password
    /// </summary>
    public static string? CrackRangeMultiThread(string storedHash, int minLen, int maxLen, char[] alphabet, int numThreads, int blockSize = 256)
    {
        for (int len = minLen; len <= maxLen; ++len)
        {
            var found = CrackMultiThread(storedHash, len, alphabet, numThreads, blockSize);
            if (found != null) return found;
        }
        return null;
    }

    /// <summary>
    /// Single-threaded
    /// </summary>
    public static string? CrackSingleThread(string storedHash, int passwordLength, char[] alphabet)
    {
        var verifier = new HashVerifier(storedHash);
        if (!TryPowULong(alphabet.Length, passwordLength, out var totalUL)) return null;

        for (ulong i = 0; i < totalUL; i++)
        {
            string candidate = IndexToPassword(i, passwordLength, alphabet);
            if (verifier.Verify(candidate)) return candidate;
        }
        return null;
    }

    /// <summary>
    /// Read a "shadow" file and return the password/hash field for the specified username.
    /// </summary>
    public static string? GetHashForUser(string path, string username)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        foreach (var rawLine in File.ReadAllLines(path))
        {
            var line = rawLine.Trim();
            // skip blank
            if (line.Length == 0) continue;  
            // skip comment
            if (line.StartsWith("#")) continue;     

            var fields = line.Split(':');
            if (fields.Length < 2) continue;
            if (fields[0] != username) continue;

            var hashField = fields[1];
            if (hashField == "!" || hashField == "*" || hashField == "x") return null; // locked or placeholder
            return hashField;
        }

        return null;
    }
    
    public static void Main(string[] args)
    {
        if (args.Length < 2)
        {
            Console.WriteLine("Usage: dotnet run -- <shadow_file> <username> [threads|'all']");
            return;
        }

        string shadowFile = args[0];
        string username = args[1];

        bool runAll = false;
        int specificThreads = -1;
        if (args.Length >= 3)
        {
            if (args[2].Equals("all", StringComparison.OrdinalIgnoreCase))
            {
                runAll = true;
            }
            else if (!int.TryParse(args[2], out specificThreads) || specificThreads <= 0)
            {
                Console.WriteLine("Invalid threads argument; use a positive integer or 'all'");
                return;
            }
        }

        var hash = GetHashForUser(shadowFile, username);
        if (hash == null)
        {
            Console.WriteLine("No hash found for user: " + username);
            return;
        }

        try
        {
            EnsureCryptRaLoaded();
        }
        catch (Exception ex)
        {
            Console.WriteLine("Failed to initialize crypt_ra: " + ex.Message);
            return;
        }
        
        var alphabet = ("ABCDEFGHIJKLMNOPQRSTUVWXYZ" +
                        "abcdefghijklmnopqrstuvwxyz" +
                        "0123456789" +
                        "@#%^&*()_+-=.,:;?").ToCharArray();
        
        int minLen = 3;
        int maxLen = 3;

        // Build thread list
        var threadCounts = new List<int>();
        if (runAll)
        {
            int nproc = Environment.ProcessorCount;
            threadCounts.AddRange(new[] { 1, 2, 3, 4 }.Where(x => x <= nproc));
            if (!threadCounts.Contains(nproc))
            {
                threadCounts.Add(nproc);
            }
        }
        else if (specificThreads > 0)
        {
            threadCounts.Add(specificThreads);
        }
        else
        {
            threadCounts.AddRange(new[] { 2, 3, 4 });
            int nproc = Environment.ProcessorCount;
            if (!threadCounts.Contains(nproc))
            {
                threadCounts.Add(nproc);
            }
        }

        Console.WriteLine();
        Console.WriteLine("threads,found,time_seconds");
        

        foreach (var numThreads in threadCounts)
        {
            int blockSize = 32;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var found = CrackRangeMultiThread(hash, minLen, maxLen, alphabet, numThreads, blockSize);
            sw.Stop();
            Console.WriteLine($"{numThreads},{(found ?? "NOTFOUND")},{sw.Elapsed.TotalSeconds:F3}");
        }
    }
}
