using System;
using System.Diagnostics;

namespace Cracker;

/// <summary>
/// Verifies a candidate password against a stored Unix-style hash string.
/// </summary>
public sealed class HashVerifier
{
    /// <summary>
    /// Verification strategy for the provided stored hash.
    /// </summary>
    public enum HashMode
    {
        // Apache MD5 ("apr1") format: "$apr1$&lt;salt&gt;$&lt;hash&gt;".
        Apr1,
        
        // Any crypt(3) format supported by libxcrypt (bcrypt, sha512-crypt, yescrypt, etc.).
        Libxcrypt
    }
    
    // stored hash string as read from the file
    private readonly string _storedHash;

    // verification mode based on the stored hash prefix
    private readonly HashMode _mode;

    // the "setting" passed to crypt_ra
    private readonly string _setting;
    
    // For APR1 mode
    private readonly string? _apr1Salt;
    
    /// <summary>
    /// Build a verifier for a single stored hash string.
    /// </summary>
    public HashVerifier(string storedHash)
    {
        _storedHash = storedHash ?? throw new ArgumentNullException(nameof(storedHash));

        if (_storedHash.StartsWith("$apr1$", StringComparison.Ordinal))
        {
            _mode = HashMode.Apr1;
            _setting = _storedHash;
            _apr1Salt = ExtractApr1SaltOrNull(_storedHash);
        }
        else
        {
            _mode = HashMode.Libxcrypt;
            _setting = _storedHash;
            _apr1Salt = null;
        }
    }
    

    /// <summary>
    /// Verify that <paramref name="candidatePassword"/> matches the stored hash passed at construction.
    /// </summary>
    public bool Verify(string candidatePassword)
    {
        switch (_mode)
        {
            case HashMode.Apr1:
            {
                // Need APR1 salt to reproduce hash via openssl
                if (string.IsNullOrEmpty(_apr1Salt))
                {
                    return false;
                }

                if (!TryComputeApr1WithOpenSsl(candidatePassword, _apr1Salt, out string? produced))
                {
                    return false;
                }
                
                return string.Equals(produced, _storedHash, StringComparison.Ordinal);
            }

            case HashMode.Libxcrypt:
            {
                // Delegate to crypt_ra via Program.CryptWrap 
                string? produced = Program.CryptWrap(candidatePassword, _setting);
                if (produced is null) return false;
                return string.Equals(produced, _storedHash, StringComparison.Ordinal);
            }

            default:
                return false;
        }
    }

    // ------------ APR1 helpers ------------

    /// <summary>
    /// Extract the APR1 salt from a string of the form "$apr1$&lt;salt&gt;$&lt;rest&gt;".
    /// Returns null if the format is not valid.
    /// </summary>
    private static string? ExtractApr1SaltOrNull(string apr1Hash)
    {
        // Expected tokens ["apr1","<salt>","<rest>"]
        var parts = apr1Hash.Split('$', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            return null;
        }
        if (!string.Equals(parts[0], "apr1", StringComparison.Ordinal))
        {
            return null;
        }
        
        // the salt
        return parts[1]; 
    }

    /// <summary>
    /// Compute an APR1 hash using the openssl
    /// </summary>
    /// </remarks>
    public static bool TryComputeApr1WithOpenSsl(string candidate, string salt, out string? resultHash, int timeoutMs = 5000)
    {
        resultHash = null;

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "openssl",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            // openssl passwd -apr1 -salt <salt> <password>
            psi.ArgumentList.Add("passwd");
            psi.ArgumentList.Add("-apr1");
            psi.ArgumentList.Add("-salt");
            psi.ArgumentList.Add(salt);
            psi.ArgumentList.Add(candidate);

            using var proc = Process.Start(psi);
            if (proc is null) return false;

            // Read output
            string stdout = proc.StandardOutput.ReadToEnd().Trim();
            string stderr = proc.StandardError.ReadToEnd().Trim();

            if (!proc.WaitForExit(timeoutMs))
            {
                try
                {
                    proc.Kill(entireProcessTree: true);
                }
                catch
                {
                     // empty
                }
                return false;
            }

            if (proc.ExitCode != 0) return false;
            if (string.IsNullOrWhiteSpace(stdout)) return false;

            resultHash = stdout;
            return true;
        }
        catch
        {
            return false;
        }
    }
}
