namespace Client;

using System;

public sealed class HashVerifier
{
    private readonly string _storedHash;
    
    public HashVerifier(string storedHash)
    {
        _storedHash = storedHash ?? throw new ArgumentNullException(nameof(storedHash));
    }
    
    public bool Verify1(string candidatePassword)
    {
        if (candidatePassword is null)
        {
            return false;
        }

        // Delegate to crypt_ra via CryptWrap
        string? produced = Cracker.CryptWrap(candidatePassword, _storedHash);
        
        if (produced is null)
        {
            return false;
        }

        return string.Equals(produced, _storedHash, StringComparison.Ordinal);
    }
    
    public static bool Verify(string candidate, string storedHash)
    {
        // crypt_ra returns the encoded hash of 'candidate' using 'storedHash' as the setting.
        // Match when it equals the storedHash.
        string? produced = Cracker.CryptWrap(candidate, storedHash);
        return produced is not null && string.Equals(produced, storedHash, StringComparison.Ordinal);
    }
}