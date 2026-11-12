using System.Security.Cryptography;

namespace FoldEdge;

/// <summary>PBKDF2-SHA256 password hashing (120k iterations, per-user salt).</summary>
public static class PasswordHasher
{
    private const int Iterations = 120_000;
    private const int SaltBytes = 16;
    private const int HashBytes = 32;

    public static (string Salt, string Hash) Hash(string password)
    {
        byte[] salt = RandomNumberGenerator.GetBytes(SaltBytes);
        byte[] hash = Rfc2898DeriveBytes.Pbkdf2(
            password, salt, Iterations, HashAlgorithmName.SHA256, HashBytes);
        return (Convert.ToBase64String(salt), Convert.ToBase64String(hash));
    }

    public static bool Verify(string password, string saltBase64, string hashBase64)
    {
        try
        {
            byte[] salt = Convert.FromBase64String(saltBase64);
            byte[] expected = Convert.FromBase64String(hashBase64);
            byte[] actual = Rfc2898DeriveBytes.Pbkdf2(
                password, salt, Iterations, HashAlgorithmName.SHA256, expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch
        {
            return false;
        }
    }
}
