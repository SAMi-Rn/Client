namespace Client;

using System;
using System.Runtime.InteropServices;
using System.Threading;

public static class Cracker
{
    // ---- native bindings ----
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr CryptRaDelegate(string key, string setting, ref IntPtr dataPtr, ref int size);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr CryptDelegate(string key, string setting); // classic crypt(key, salt)

    private static CryptRaDelegate? s_cryptRa;
    private static CryptDelegate?    s_crypt;       // fallback if crypt_ra unavailable
    private static IntPtr            s_libHandle = IntPtr.Zero;
    private static bool              s_inited;
    private static readonly object   s_initLock = new();

    // Per-thread opaque state for crypt_ra (re-entrant)
    private sealed class ThreadCryptState { public IntPtr Opaque = IntPtr.Zero; public int Size = 0; }
    private static readonly ThreadLocal<ThreadCryptState> s_tls =
        new(() => new ThreadCryptState(), trackAllValues: true);

    private static readonly object s_cryptLock = new(); // guard classic crypt (not re-entrant)

    static Cracker()
    {
        AppDomain.CurrentDomain.ProcessExit += (_, __) =>
        {
            try
            {
                foreach (var st in s_tls.Values)
                {
                    if (st.Opaque != IntPtr.Zero) Marshal.FreeHGlobal(st.Opaque);
                    st.Opaque = IntPtr.Zero;
                }
                s_tls.Dispose();
            } catch { /* ignore */ }

            try { if (s_libHandle != IntPtr.Zero) NativeLibrary.Free(s_libHandle); } catch { /* ignore */ }
        };
    }

    private static void EnsureLoaded()
    {
        if (s_inited) return;
        lock (s_initLock)
        {
            if (s_inited) return;
            s_inited = true;

            string[] candidates = {
                "libxcrypt.so.2","libxcrypt.so.1","libxcrypt.so.0",
                "libcrypt.so.2","libcrypt.so.1","libcrypt.so",
                "libc.so.6"
            };

            foreach (var lib in candidates)
            {
                try
                {
                    if (!NativeLibrary.TryLoad(lib, out var h)) continue;

                    // Prefer crypt_ra
                    if (NativeLibrary.TryGetExport(h, "crypt_ra", out var pRa))
                    {
                        s_cryptRa  = Marshal.GetDelegateForFunctionPointer<CryptRaDelegate>(pRa);
                        s_libHandle = h;
                        return;
                    }

                    // Fallback to classic crypt
                    if (NativeLibrary.TryGetExport(h, "crypt", out var pCrypt))
                    {
                        s_crypt    = Marshal.GetDelegateForFunctionPointer<CryptDelegate>(pCrypt);
                        s_libHandle = h;
                        return;
                    }

                    NativeLibrary.Free(h);
                }
                catch { /* try next */ }
            }

            throw new DllNotFoundException("No suitable crypt function found (crypt_ra/crypt). Install libxcrypt.");
        }
    }

    private static ThreadCryptState GetTls()
    {
        var st = s_tls.Value;
        if (st is null) throw new InvalidOperationException("thread-local state unavailable");
        return st;
    }

    /// <summary>Returns the encoded hash of <paramref name="plaintext"/> using <paramref name="storedHash"/> as the setting.</summary>
    public static string? CryptWrap(string plaintext, string? storedHash)
    {
        if (string.IsNullOrEmpty(storedHash)) return null;
        EnsureLoaded();

        // Preferred: crypt_ra (MT-friendly)
        if (s_cryptRa is not null)
        {
            var st = GetTls();
            IntPtr p = s_cryptRa(plaintext, storedHash!, ref st.Opaque, ref st.Size);
            return p == IntPtr.Zero ? null : Marshal.PtrToStringAnsi(p);
        }

        // Fallback: classic crypt (non re-entrant)
        if (s_crypt is not null)
        {
            lock (s_cryptLock)
            {
                IntPtr p = s_crypt(plaintext, storedHash!);
                return p == IntPtr.Zero ? null : Marshal.PtrToStringAnsi(p);
            }
        }

        return null;
    }

    /// <summary>Returns true if crypt(plaintext, storedHash) == storedHash.</summary>
    public static bool Verify(string candidate, string storedHash)
    {
        string? produced = CryptWrap(candidate, storedHash);
        return produced is not null && string.Equals(produced, storedHash, StringComparison.Ordinal);
    }

    // (Optional) keep if you still use it elsewhere
    public static string? GetHashForUser(string path, string username)
    {
        if (!System.IO.File.Exists(path)) throw new System.IO.FileNotFoundException("Shadow file not found", path);
        foreach (var raw in System.IO.File.ReadLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("#")) continue;
            var fields = line.Split(':');
            if (fields.Length < 2) continue;
            if (!string.Equals(fields[0], username, StringComparison.Ordinal)) continue;

            var hashField = fields[1];
            if (hashField is "!" or "*" or "x" or "") return null;
            return hashField;
        }
        return null;
    }
}
