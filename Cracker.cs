namespace Cracker;

using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

public static class Cracker
{
    // binding to crypt_ra
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr CryptRaDelegate(
        [MarshalAs(UnmanagedType.LPStr)] string key,
        [MarshalAs(UnmanagedType.LPStr)] string setting,
        ref IntPtr dataPtr,
        ref int size);
    
    private static CryptRaDelegate? cryptRaFunc;
    private static IntPtr nativeLibHandle = IntPtr.Zero;
    private static bool cryptInitialized = false;
    private static readonly object cryptInitLock = new object();
    
    private static long globalStartIndex = 0;
    private static int stopFlag = 0;
    private static bool IsStopped => Volatile.Read(ref stopFlag) != 0;
    private static string? resultPassword = null;

    private sealed class ThreadCryptState
    {
        public IntPtr OpaquePointer = IntPtr.Zero;
        public int Size = 0;
    }

    private static ThreadLocal<ThreadCryptState> threadState =
        new ThreadLocal<ThreadCryptState>(() => new ThreadCryptState());

    static Cracker()
    {
        AppDomain.CurrentDomain.ProcessExit += (_, __) =>
        {
            try
            {
                foreach (var st in threadState.Values)
                {
                    if (st.OpaquePointer != IntPtr.Zero)
                    {
                        Marshal.FreeHGlobal(st.OpaquePointer);
                        st.OpaquePointer = IntPtr.Zero;
                    }
                }

                threadState.Dispose();
            }
            catch
            {
                 // no op
            }

            if (nativeLibHandle != IntPtr.Zero)
            {
                try
                {
                    NativeLibrary.Free(nativeLibHandle);
                }
                catch
                {
                     // no op
                }
            }
        };
    }

    public static void EnsureCryptRaLoaded()
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
                        nativeLibHandle = handle;
                        return;
                    }

                    NativeLibrary.Free(handle);
                }
                catch
                {
                    // no op
                }
            }

            throw new DllNotFoundException("crypt_ra not found. Install libxcrypt.");
        }
    }

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

    private static ThreadCryptState GetThreadState()
    {
        var s = threadState.Value;
        
        if (s == null)
        {
            throw new InvalidOperationException("thread-local state unavailable");
        }
        return s;
    }

    public static string? CryptWrap(string plaintext, string? storedHash)
    {
        if (storedHash == null)
        {
            return null;
        }
        
        var cryptRa = GetCryptRa();
        var state = GetThreadState();
        IntPtr resultPtr = cryptRa(plaintext, storedHash, ref state.OpaquePointer, ref state.Size);
        if (resultPtr == IntPtr.Zero)
        {
            return null;
        }
        return Marshal.PtrToStringAnsi(resultPtr);
    }
    
    public static ulong FetchWorkBlock(int blockSize)
    {
        long start = Interlocked.Add(ref globalStartIndex, blockSize) - blockSize;
        
        if (start < 0)
        {
            return ulong.MaxValue;
        }
        
        return (ulong)start;
    }

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
    
    public static string? CrackMultiThread(string storedHash, int passwordLength, char[] alphabet, int numThreads, int blockSize = 1)
    {
        int a = alphabet.Length;
        long total = 1;
        for (int i = 0; i < passwordLength; i++)
        {
            total *= a;
        }
        var verifier = new HashVerifier(storedHash);

        Interlocked.Exchange(ref globalStartIndex, 0);
        Volatile.Write(ref stopFlag, 0);
        resultPassword = null;

        var tasks = new Task[numThreads];
        for (int t = 0; t < numThreads; ++t)
        {
            tasks[t] = Task.Run(() => WorkerLoop(verifier, passwordLength, alphabet, total, blockSize));
        }

        Task.WaitAll(tasks);
        return resultPassword;
    }
    
    public static string? CrackRangeMultiThread(string storedHash, char[] alphabet, int numThreads, int blockSize)
    {
        for (int len = 1;; ++len)
        {
            var found = CrackMultiThread(storedHash, len, alphabet, numThreads, blockSize);
            if (found != null)
            {
                return found;
            }
        }
    }
    
    public static string? GetHashForUser(string path, string username)
    {
        if (!File.Exists(path))
        {
            return null;
        }
        
        foreach (var rawLine in File.ReadAllLines(path))
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
            {
                continue;
            }
            
            // comments
            if (line.StartsWith("#"))
            {
                continue;
            }
            var fields = line.Split(':');

            if (fields.Length < 2)
            {
                continue;
            }

            if (fields[0] != username)
            {
                continue;
            }
            var hashField = fields[1];
            if (hashField == "!" || hashField == "*" || hashField == "x")
            {
                return null;
            }
            
            return hashField;
        }
        
        return null;
    }
}
