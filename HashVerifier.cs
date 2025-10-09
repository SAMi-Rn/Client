namespace Cracker;

using System.Diagnostics;

public sealed class HashVerifier
{

    public enum HashMode
    {
        // Apache MD5 ("apr1") format: "$apr1$&lt;salt&gt;$&lt;hash&gt;".
        Apr1,
        
        // any crypt(3) format supported by libxcrypt
        Libxcrypt
    }
    
    private readonly string _storedHash;
    
    private readonly HashMode _mode;
    
    private readonly string _setting;
    
    private readonly string? _apr1Salt;
    
    public HashVerifier(string storedHash)
    {
        _storedHash = storedHash ?? throw new ArgumentNullException(nameof(storedHash));

        if (_storedHash.StartsWith("$apr1$", StringComparison.Ordinal))
        {
            _mode = HashMode.Apr1;
            _setting = _storedHash;
            _apr1Salt = ExtractApr1Salt(_storedHash);
        }
        else
        {
            _mode = HashMode.Libxcrypt;
            _setting = _storedHash;
            _apr1Salt = null;
        }
    }
    
    public bool Verify(string candidatePassword)
    {
        switch (_mode)
        {
            case HashMode.Apr1:
            {
                // APR1 salt to reproduce hash via openssl
                if (string.IsNullOrEmpty(_apr1Salt))
                {
                    return false;
                }

                if (!TryApr1WithOpenSsl(candidatePassword, _apr1Salt, out string? produced))
                {
                    return false;
                }
                
                return string.Equals(produced, _storedHash, StringComparison.Ordinal);
            }

            case HashMode.Libxcrypt:
            {
                // delegate to crypt_ra
                string? produced = Cracker.CryptWrap(candidatePassword, _setting);
                if (produced is null) return false;
                return string.Equals(produced, _storedHash, StringComparison.Ordinal);
            }

            default:
                return false;
        }
    }
    
    private static string? ExtractApr1Salt(string apr1Hash)
    {
        // "apr1","<salt>","<rest>"
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
    
    public static bool TryApr1WithOpenSsl(string candidate, string salt, out string? resultHash, int timeoutMs = 5000)
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

            if (!proc.WaitForExit(timeoutMs))
            {
                try
                {
                    proc.Kill(entireProcessTree: true);
                }
                catch
                {
                     // no op
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
