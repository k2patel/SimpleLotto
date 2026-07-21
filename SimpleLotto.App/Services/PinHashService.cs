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

    public static bool IsVersionedHash(string storedHash) =>
        storedHash.StartsWith($"{AlgorithmName}$", StringComparison.Ordinal);

    public static bool IsLegacyHash(string storedHash) =>
        !IsVersionedHash(storedHash) && storedHash.Contains(':');

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

    public static PinVerificationResult Verify(string credential, string storedHash)
    {
        if (string.IsNullOrEmpty(storedHash))
            return PinVerificationResult.Invalid;

        return IsVersionedHash(storedHash)
            ? VerifyVersionedHash(credential, storedHash)
            : VerifyLegacyHash(credential, storedHash);
    }

    private static PinVerificationResult VerifyVersionedHash(string pin, string storedHash)
    {
        var parts = storedHash.Split('$');
        if (parts.Length != 5 ||
            !string.Equals(parts[0], AlgorithmName, StringComparison.Ordinal) ||
            !string.Equals(parts[1], FormatVersion, StringComparison.Ordinal) ||
            !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var iterations) ||
            iterations <= 0 ||
            iterations > MaximumAcceptedIterationCount)
        {
            return PinVerificationResult.Invalid;
        }

        try
        {
            var salt = Convert.FromBase64String(parts[3]);
            var expectedHash = Convert.FromBase64String(parts[4]);
            try
            {
                if (salt.Length != SaltSize || expectedHash.Length != HashSize)
                    return PinVerificationResult.Invalid;

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
                        var isValid = CryptographicOperations.FixedTimeEquals(actualHash, expectedHash);
                        return isValid
                            ? new PinVerificationResult(true, iterations < IterationCount, false)
                            : PinVerificationResult.Invalid;
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
            return PinVerificationResult.Invalid;
        }
    }

    private static PinVerificationResult VerifyLegacyHash(string credential, string storedHash)
    {
        var parts = storedHash.Split(':', 2);
        if (parts.Length != 2)
            return PinVerificationResult.Invalid;

        try
        {
            var salt = Convert.FromBase64String(parts[0]);
            var expectedHash = Convert.FromBase64String(parts[1]);
            try
            {
                if (salt.Length != SaltSize || expectedHash.Length != HashSize)
                    return PinVerificationResult.Invalid;

                var credentialBytes = Encoding.UTF8.GetBytes(credential);
                var saltedCredential = new byte[salt.Length + credentialBytes.Length];
                try
                {
                    Buffer.BlockCopy(salt, 0, saltedCredential, 0, salt.Length);
                    Buffer.BlockCopy(credentialBytes, 0, saltedCredential, salt.Length, credentialBytes.Length);
                    var actualHash = SHA256.HashData(saltedCredential);
                    try
                    {
                        return CryptographicOperations.FixedTimeEquals(actualHash, expectedHash)
                            ? new PinVerificationResult(true, true, true)
                            : PinVerificationResult.Invalid;
                    }
                    finally
                    {
                        CryptographicOperations.ZeroMemory(actualHash);
                    }
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(credentialBytes);
                    CryptographicOperations.ZeroMemory(saltedCredential);
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
            return PinVerificationResult.Invalid;
        }
    }
}

internal readonly record struct PinVerificationResult(bool IsValid, bool NeedsUpgrade, bool IsLegacy)
{
    public static PinVerificationResult Invalid { get; } = new(false, false, false);
}
