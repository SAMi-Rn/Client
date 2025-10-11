// HashVerifier.cs
namespace Cracker;

using System;

public sealed class HashVerifier
{
    private readonly string _storedHash;
    
    public HashVerifier(string storedHash)
    {
        _storedHash = storedHash ?? throw new ArgumentNullException(nameof(storedHash));
    }
    
    public bool Verify(string candidatePassword)
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
}