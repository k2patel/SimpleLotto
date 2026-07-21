using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace SimpleLotto.App.Services;

internal static class PinHashService
{
    private const string AlgorithmName = "pbkdf2-sha256";
    private const string FormatVersion = "1";
    private const int IterationCount = 600_000;
    private const int MaximumAcceptedIterationCount = 2_000_000;
    private const int SaltSize = 16;
    private const int HashSize = 32;

    public static bool IsValidPin(string pin)
    {
        if (pin.Length != 4)
            return false;

        foreach (var character in pin)
        {
            if (character is < '0' or > '9')
                return false;
        }

        return true;
    }

    private static bool IsVersionedHash(string storedHash) =>
        storedHash.StartsWith($"{AlgorithmName}$", StringComparison.Ordinal);

    public static string CreateHash(string pin)
    {
        if (!IsValidPin(pin))
            throw new ArgumentException("PIN must contain exactly four digits.", nameof(pin));

        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var pinBytes = Encoding.UTF8.GetBytes(pin);
        try
        {
            var hash = Rfc2898DeriveBytes.Pbkdf2(
                pinBytes,
                salt,
                IterationCount,
                HashAlgorithmName.SHA256,
                HashSize);
            try
            {
                return $"{AlgorithmName}${FormatVersion}${IterationCount.ToString(CultureInfo.InvariantCulture)}$" +
                    $"{Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
            }
            finally
            {
                CryptographicOperations.ZeroMemory(hash);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pinBytes);
            CryptographicOperations.ZeroMemory(salt);
        }
    }

    public static bool Verify(string pin, string storedHash)
    {
        if (!IsValidPin(pin) || string.IsNullOrEmpty(storedHash) || !IsVersionedHash(storedHash))
            return false;

        return VerifyVersionedHash(pin, storedHash);
    }

    private static bool VerifyVersionedHash(string pin, string storedHash)
    {
        var parts = storedHash.Split('$');
        if (parts.Length != 5 ||
            !string.Equals(parts[0], AlgorithmName, StringComparison.Ordinal) ||
            !string.Equals(parts[1], FormatVersion, StringComparison.Ordinal) ||
            !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var iterations) ||
            iterations <= 0 ||
            iterations > MaximumAcceptedIterationCount)
        {
            return false;
        }

        try
        {
            var salt = Convert.FromBase64String(parts[3]);
            var expectedHash = Convert.FromBase64String(parts[4]);
            try
            {
                if (salt.Length != SaltSize || expectedHash.Length != HashSize)
                    return false;

                var pinBytes = Encoding.UTF8.GetBytes(pin);
                try
                {
                    var actualHash = Rfc2898DeriveBytes.Pbkdf2(
                        pinBytes,
                        salt,
                        iterations,
                        HashAlgorithmName.SHA256,
                        HashSize);
                    try
                    {
                        return CryptographicOperations.FixedTimeEquals(actualHash, expectedHash);
                    }
                    finally
                    {
                        CryptographicOperations.ZeroMemory(actualHash);
                    }
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(pinBytes);
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(salt);
                CryptographicOperations.ZeroMemory(expectedHash);
            }
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
