using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Org.BouncyCastle.Security;

namespace SimpleLotto.App.Services;

public sealed class LicenseService
{
    private const string DefaultLicenseUrl = "https://license.k2patel.in/api/v1/callHome";
    private const string ProductId = "simple_lotto";
    private const int ProtocolVersion = 2;
    private const string LicenseSigningKeyId = "license-ed25519-2026-06";
    private const string DefaultPepper = "lottomachine-v1-replace-in-prod";
    private const int MaxPhoneHomeRetries = 2;

    private const string LicensePublicKeyPem = """
        -----BEGIN PUBLIC KEY-----
        MCowBQYDK2VwAyEAvkU2R+C03Hd2CXzwrCqhQMiD4tGtI/rbG29642gbido=
        -----END PUBLIC KEY-----
        """;

    public const int GracePeriodDays = 7;

    public event Action<LicenseStatus>? StatusChanged;

    private LicenseState? _cached;
    private readonly Lazy<string> _registrationId = new(
        ComputeDeviceHash,
        LazyThreadSafetyMode.ExecutionAndPublication);

    public string RegistrationId => _registrationId.Value;

    // Compatibility contract: this must remain byte-for-byte identical to the
    // previously released WindowsPOS/SimpleLotto registration-ID algorithm.
    // Existing license-server registrations depend on the resulting hash.
    public static string ComputeDeviceHash()
    {
        var serial = ReadHardwareSerial();
        var mac = ReadPrimaryMac();
        var identity = string.IsNullOrEmpty(serial) && string.IsNullOrEmpty(mac)
            ? $"dev:{Environment.MachineName}"
            : $"{serial}:{mac}";

        using var hmac = new HMACSHA256(GetLicensePepper());
        return Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant();
    }

    public LicenseStatus CheckLicense()
    {
        var state = LoadState();
        var today = DateTime.UtcNow.Date;
        var (subscriptionExpiresAt, subscriptionDaysRemaining) = ComputeSubscriptionFields(
            state.SubscriptionExpiresAt,
            today);

        var reference = state.LastAuthorizedAt ?? state.FirstAttemptAt;
        if (reference is null)
        {
            return new LicenseStatus
            {
                Status = "pending",
                DaysRemaining = GracePeriodDays,
                RegistrationId = this.RegistrationId,
                LastCheck = state.LastCheckAt,
                SubscriptionExpiresAt = subscriptionExpiresAt,
                SubscriptionDaysRemaining = subscriptionDaysRemaining,
                Message = "License check has not run yet on this device."
            };
        }

        var elapsed = (today - reference.Value.Date).Days;
        var daysRemaining = Math.Max(0, GracePeriodDays - elapsed);
        string status;
        string message;
        if (elapsed <= 0 && state.LastAuthorizedAt is not null)
        {
            status = "valid";
            message = "License authorized.";
        }
        else if (elapsed <= 0)
        {
            status = "pending";
            message = PendingLicenseMessage(state.LastResult);
        }
        else if (daysRemaining > 0)
        {
            status = "grace";
            message = $"Registration renewal required. {daysRemaining} day{(daysRemaining == 1 ? string.Empty : "s")} remaining.";
        }
        else
        {
            status = "expired";
            message = "Registration expired. SimpleLotto is locked. Contact your provider.";
        }

        return new LicenseStatus
        {
            Status = status,
            DaysRemaining = daysRemaining,
            RegistrationId = this.RegistrationId,
            LastAuthorized = state.LastAuthorizedAt,
            LastCheck = state.LastCheckAt,
            SubscriptionExpiresAt = subscriptionExpiresAt,
            SubscriptionDaysRemaining = subscriptionDaysRemaining,
            Message = message
        };
    }

    private static string PendingLicenseMessage(string? lastResult)
    {
        if (string.IsNullOrWhiteSpace(lastResult))
            return "License check in progress.";

        if (string.Equals(lastResult, "unknown", StringComparison.OrdinalIgnoreCase))
            return "This registration ID is not registered on the license server yet.";

        if (string.Equals(lastResult, "authorized", StringComparison.OrdinalIgnoreCase))
            return "License authorized.";

        return $"License check did not authorize this device: {lastResult}.";
    }

    public async Task<LicenseStatus> PhoneHomeAsync(
        LicenseStoreInfo storeInfo,
        string? serverUrl = null,
        CancellationToken cancellationToken = default)
    {
        var state = LoadState();
        var checkedAtUtc = DateTime.UtcNow;
        state.FirstAttemptAt ??= checkedAtUtc;
        var url = string.IsNullOrWhiteSpace(serverUrl) ? DefaultLicenseUrl : serverUrl;
        Trace($"PhoneHomeAsync entered url={url} firstAttempt={state.FirstAttemptAt:o}");

        string registrationId;
        try
        {
            registrationId = RegistrationId;
            Trace($"computed registration_id len={registrationId.Length} prefix={registrationId[..Math.Min(8, registrationId.Length)]}");
        }
        catch (Exception ex)
        {
            Trace($"ComputeDeviceHash failed {ex.GetType().Name}: {ex.Message}");
            state.LastCheckAt = checkedAtUtc;
            state.LastResult = $"compute-hash error: {ex.Message}";
            SaveState(state);
            return CheckLicense();
        }

        var payload = new LicenseCallHomeRequest(registrationId);
        var bodyBytes = JsonSerializer.SerializeToUtf8Bytes(
            payload,
            LicenseJsonContext.Default.LicenseCallHomeRequest);

        HttpResponseMessage? response = null;
        Exception? lastTransient = null;
        using var http = new HttpClient();
        try
        {
            var v2Status = await TryPhoneHomeV2Async(
                http,
                state,
                registrationId,
                checkedAtUtc,
                url,
                cancellationToken);
            if (v2Status is not null)
                return v2Status;

            for (var attempt = 1; attempt <= MaxPhoneHomeRetries; attempt++)
            {
                try
                {
                    Trace($"about to POST {url} attempt={attempt}/{MaxPhoneHomeRetries}");
                    using var content = BuildJsonContent(bodyBytes);
                    response = await http.PostAsync(url, content, cancellationToken);
                    Trace($"response received http={(int)response.StatusCode} attempt={attempt}");
                    lastTransient = null;
                    break;
                }
                catch (HttpRequestException ex) when (ex.InnerException is HttpIOException or IOException)
                {
                    lastTransient = ex;
                    Trace($"attempt {attempt} transient {ex.GetType().Name}: {ex.Message}");
                    if (attempt < MaxPhoneHomeRetries)
                        await Task.Delay(250, cancellationToken);
                }
            }

            if (response is null)
                throw lastTransient ?? new HttpRequestException("license request failed before response");

            state.LastCheckAt = checkedAtUtc;
            if (!response.IsSuccessStatusCode)
            {
                state.LastResult = $"http {(int)response.StatusCode}";
                SaveState(state);
                return CheckLicense();
            }

            LicenseCallHomeResponse? body;
            try
            {
                body = await response.Content.ReadFromJsonAsync(
                    LicenseJsonContext.Default.LicenseCallHomeResponse,
                    cancellationToken);
            }
            catch (Exception ex)
            {
                Trace($"response parse failed {ex.GetType().Name}: {ex.Message}");
                state.LastResult = $"parse error: {ex.Message}";
                SaveState(state);
                return CheckLicense();
            }

            var signed = VerifyCallHomeResponse(body, registrationId, checkedAtUtc, out var verifyError);
            if (signed is null)
            {
                Trace($"response verify failed {verifyError}");
                state.LastResult = $"signature invalid: {verifyError}";
                SaveState(state);
                return CheckLicense();
            }

            state.SubscriptionExpiresAt = (signed.ExpiresAt ?? string.Empty).Trim();
            var infoUpdateToken = body?.LicenseInfoUpdateToken;

            if (signed.Authorized == true)
            {
                state.LastAuthorizedAt = checkedAtUtc;
                state.LastResult = "authorized";
                state.SignedBlob = body!.Blob;
            }
            else
            {
                state.LastResult = signed.Status ?? body?.Reason ?? "unauthorized";
            }

            SaveState(state);

            if (signed.Authorized == true && !string.IsNullOrWhiteSpace(infoUpdateToken))
                await TryPostLicenseInfoAsync(http, url, registrationId, infoUpdateToken, storeInfo, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Trace($"POST failed {ex.GetType().Name}: {ex.Message}");
            state.LastCheckAt = checkedAtUtc;
            state.LastResult = $"error: {ex.Message}";
            SaveState(state);
        }
        finally
        {
            response?.Dispose();
        }

        return CheckLicense();
    }

    private async Task<LicenseStatus?> TryPhoneHomeV2Async(
        HttpClient http,
        LicenseState state,
        string registrationId,
        DateTime checkedAtUtc,
        string callHomeUrl,
        CancellationToken cancellationToken)
    {
        LicenseDeviceIdentity identity;
        try
        {
            identity = LoadOrCreateDeviceIdentity();
        }
        catch (Exception ex)
        {
            Trace($"v2 identity unavailable {ex.GetType().Name}: {ex.Message}");
            return null;
        }

        LicenseChallengeResponse? challenge;
        try
        {
            using var challengeResponse = await http.PostAsJsonAsync(
                VersionedEndpointFor(callHomeUrl, "/api/v2/challenge"),
                new LicenseChallengeRequest(registrationId, ProductId, identity.InstallationId),
                LicenseJsonContext.Default.LicenseChallengeRequest,
                cancellationToken);
            if (challengeResponse.StatusCode is System.Net.HttpStatusCode.NotFound or
                System.Net.HttpStatusCode.MethodNotAllowed)
            {
                return state.StrictV2Seen ? FailStrictV2(state, checkedAtUtc, "v2 endpoint unavailable") : null;
            }
            if (!challengeResponse.IsSuccessStatusCode)
                return state.StrictV2Seen
                    ? FailStrictV2(state, checkedAtUtc, $"v2 challenge http {(int)challengeResponse.StatusCode}")
                    : null;
            challenge = await challengeResponse.Content.ReadFromJsonAsync(
                LicenseJsonContext.Default.LicenseChallengeResponse,
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Trace($"v2 challenge failed {ex.GetType().Name}: {ex.Message}");
            return state.StrictV2Seen ? FailStrictV2(state, checkedAtUtc, ex.Message) : null;
        }

        if (string.IsNullOrWhiteSpace(challenge?.Challenge))
            return state.StrictV2Seen ? FailStrictV2(state, checkedAtUtc, "invalid v2 challenge") : null;

        var request = new LicenseCallHomeV2Request
        {
            RegistrationId = registrationId,
            ProductId = ProductId,
            InstallationId = identity.InstallationId,
            ClientVersion = AppUpdateService.GetInstalledVersion(),
            BuildId = typeof(LicenseService).Assembly.GetName().Version?.ToString() ?? "unknown",
            Platform = "windows-x64",
            ProtocolVersion = ProtocolVersion,
            Capabilities = ["game-image-cache-v2", "license-proof-v2"],
            PublicKey = identity.PublicKey,
            ProofJti = Guid.NewGuid().ToString("D"),
            Challenge = challenge.Challenge
        };
        request.ProofSignature = SignDeviceProof(identity, BuildV2ProofMessage(request));

        try
        {
            using var response = await http.PostAsJsonAsync(
                VersionedEndpointFor(callHomeUrl, "/api/v2/callHome"),
                request,
                LicenseJsonContext.Default.LicenseCallHomeV2Request,
                cancellationToken);
            if (!response.IsSuccessStatusCode)
                return state.StrictV2Seen
                    ? FailStrictV2(state, checkedAtUtc, $"v2 callHome http {(int)response.StatusCode}")
                    : null;
            var body = await response.Content.ReadFromJsonAsync(
                LicenseJsonContext.Default.LicenseCallHomeResponse,
                cancellationToken);
            var signed = VerifyCallHomeResponse(
                body,
                registrationId,
                checkedAtUtc,
                out var verifyError,
                ProductId,
                identity.InstallationId,
                challenge.Challenge);
            if (signed is null)
            {
                state.LastCheckAt = checkedAtUtc;
                state.LastResult = $"signature invalid: {verifyError}";
                SaveState(state);
                return CheckLicense();
            }

            state.LastCheckAt = checkedAtUtc;
            state.SubscriptionExpiresAt = (signed.ExpiresAt ?? string.Empty).Trim();
            state.ProductId = signed.ProductId;
            state.ProductStatus = signed.ProductStatus;
            state.LicenseType = signed.LicenseType;
            state.EntitlementExpiresAt = signed.EntitlementExpiresAt;
            state.EnforcementMode = signed.EnforcementMode;
            state.Features = signed.Features ?? [];
            if (string.Equals(signed.EnforcementMode, "strict", StringComparison.Ordinal))
                state.StrictV2Seen = true;

            if (signed.Authorized != true &&
                signed.LegacyFallbackAllowed == true &&
                !state.StrictV2Seen)
            {
                Trace("v2 product is not entitled; using server-authorized legacy compatibility path");
                return null;
            }

            if (signed.Authorized == true)
            {
                state.LastAuthorizedAt = checkedAtUtc;
                state.LastResult = "authorized-v2";
                state.SignedBlob = body!.Blob;
            }
            else
            {
                state.LastResult = signed.Status ?? body?.Reason ?? "unauthorized";
            }
            SaveState(state);
            return CheckLicense();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Trace($"v2 callHome failed {ex.GetType().Name}: {ex.Message}");
            return state.StrictV2Seen ? FailStrictV2(state, checkedAtUtc, ex.Message) : null;
        }
    }

    private LicenseStatus FailStrictV2(LicenseState state, DateTime checkedAtUtc, string reason)
    {
        state.LastCheckAt = checkedAtUtc;
        state.LastResult = $"v2 required: {reason}";
        SaveState(state);
        return CheckLicense();
    }

    private static string VersionedEndpointFor(string callHomeUrl, string path)
    {
        if (!Uri.TryCreate(callHomeUrl, UriKind.Absolute, out var uri))
            return path;
        return new UriBuilder(uri.Scheme, uri.Host, uri.Port, path).Uri.AbsoluteUri;
    }

    private LicenseState LoadState()
    {
        if (_cached is not null)
            return _cached;

        try
        {
            if (File.Exists(StatePath))
            {
                _cached = JsonSerializer.Deserialize(
                    File.ReadAllText(StatePath),
                    LicenseJsonContext.Default.LicenseState) ?? new LicenseState();
                return _cached;
            }
        }
        catch (Exception ex)
        {
            Trace($"state load failed {ex.GetType().Name}: {ex.Message}");
        }

        _cached = new LicenseState();
        return _cached;
    }

    private void SaveState(LicenseState state)
    {
        _cached = state;
        Directory.CreateDirectory(StateDirectory);
        var tmp = StatePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(state, LicenseJsonContext.Default.LicenseState));
        File.Move(tmp, StatePath, overwrite: true);

        try
        {
            StatusChanged?.Invoke(CheckLicense());
        }
        catch
        {
            // State persistence is authoritative; UI refresh is best effort.
        }
    }

    private static byte[] GetLicensePepper()
    {
        var fromEnv = Environment.GetEnvironmentVariable("LOTTO_LICENSE_PEPPER");
        return Encoding.UTF8.GetBytes(string.IsNullOrEmpty(fromEnv) ? DefaultPepper : fromEnv);
    }

    private static string ReadHardwareSerial()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography", writable: false);
            return (key?.GetValue("MachineGuid") as string)?.Trim() ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string ReadPrimaryMac()
    {
        NetworkInterface[] interfaces;
        try
        {
            interfaces = NetworkInterface.GetAllNetworkInterfaces();
        }
        catch
        {
            return string.Empty;
        }

        var orderedTypes = new[]
        {
            NetworkInterfaceType.Ethernet,
            NetworkInterfaceType.Wireless80211
        };

        foreach (var preferredType in orderedTypes)
        {
            foreach (var networkInterface in interfaces)
            {
                if (networkInterface.NetworkInterfaceType != preferredType ||
                    networkInterface.OperationalStatus != OperationalStatus.Up)
                    continue;

                var hex = networkInterface.GetPhysicalAddress().ToString();
                if (!string.IsNullOrEmpty(hex))
                    return FormatMac(hex);
            }
        }

        foreach (var networkInterface in interfaces)
        {
            if (networkInterface.NetworkInterfaceType == NetworkInterfaceType.Loopback ||
                networkInterface.OperationalStatus != OperationalStatus.Up)
                continue;

            var hex = networkInterface.GetPhysicalAddress().ToString();
            if (!string.IsNullOrEmpty(hex))
                return FormatMac(hex);
        }

        return string.Empty;
    }

    private static string FormatMac(string hex)
    {
        if (hex.Length != 12)
            return hex.ToLowerInvariant();

        return string.Join(
            ":",
            Enumerable.Range(0, 6).Select(i => hex.Substring(i * 2, 2))).ToLowerInvariant();
    }

    private static (string Display, int? DaysRemaining) ComputeSubscriptionFields(string? stored, DateTime today)
    {
        var value = (stored ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(value))
            return (string.Empty, null);

        if (DateTime.TryParseExact(
                value,
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var expiresAt))
        {
            return (value, (int)(expiresAt.Date - today.Date).TotalDays);
        }

        return (value, null);
    }

    private static LicenseSignedPayload? VerifyCallHomeResponse(
        LicenseCallHomeResponse? response,
        string registrationId,
        DateTime nowUtc,
        out string reason,
        string? expectedProductId = null,
        string? expectedInstallationId = null,
        string? expectedChallenge = null)
    {
        reason = string.Empty;
        if (response is null)
        {
            reason = "license response was empty";
            return null;
        }

        if (!string.Equals(response.Alg, "Ed25519", StringComparison.Ordinal))
        {
            reason = "license response missing Ed25519 algorithm";
            return null;
        }

        if (!string.Equals(response.KeyId, LicenseSigningKeyId, StringComparison.Ordinal))
        {
            reason = "license response signed by unknown key";
            return null;
        }

        if (string.IsNullOrWhiteSpace(response.Blob) ||
            string.IsNullOrWhiteSpace(response.Signature))
        {
            reason = "license response missing signed blob or signature";
            return null;
        }

        if (!VerifyEd25519(response.Blob, response.Signature))
        {
            reason = "license response signature invalid";
            return null;
        }

        LicenseSignedPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize(
                response.Blob,
                LicenseJsonContext.Default.LicenseSignedPayload);
        }
        catch (Exception ex)
        {
            reason = $"license response blob was not JSON: {ex.Message}";
            return null;
        }

        if (payload is null)
        {
            reason = "license response blob was empty";
            return null;
        }

        if (payload.Schema is not (1 or 2))
        {
            reason = "license response schema unsupported";
            return null;
        }

        if (!string.Equals(payload.RegistrationId, registrationId, StringComparison.Ordinal))
        {
            reason = "license response registration mismatch";
            return null;
        }

        if (payload.Schema == 2 &&
            (!string.Equals(payload.ProductId, expectedProductId, StringComparison.Ordinal) ||
             !string.Equals(payload.InstallationId, expectedInstallationId, StringComparison.Ordinal) ||
             !string.Equals(payload.RequestNonce, expectedChallenge, StringComparison.Ordinal)))
        {
            reason = "license response v2 binding mismatch";
            return null;
        }

        if (string.IsNullOrWhiteSpace(payload.ValidUntil) ||
            !DateTimeOffset.TryParse(
                payload.ValidUntil,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var validUntil))
        {
            reason = "license response valid_until invalid";
            return null;
        }

        if (new DateTimeOffset(nowUtc, TimeSpan.Zero) > validUntil)
        {
            reason = "license response expired";
            return null;
        }

        if (payload.Authorized is null)
        {
            reason = "license response authorized field invalid";
            return null;
        }

        var expiresAt = (payload.ExpiresAt ?? string.Empty).Trim();
        if (expiresAt.Length > 0 &&
            !DateTime.TryParseExact(
                expiresAt,
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out _))
        {
            reason = "license response expires_at invalid";
            return null;
        }

        return payload;
    }

    private static LicenseDeviceIdentity LoadOrCreateDeviceIdentity()
    {
        Directory.CreateDirectory(StateDirectory);
        if (File.Exists(DeviceIdentityPath))
        {
            var stored = JsonSerializer.Deserialize(
                File.ReadAllText(DeviceIdentityPath),
                LicenseJsonContext.Default.LicenseDeviceIdentityStore);
            if (stored is not null && Guid.TryParse(stored.InstallationId, out _) &&
                !string.IsNullOrWhiteSpace(stored.ProtectedPrivateKey))
            {
                var privateBytes = ProtectedData.Unprotect(
                    Convert.FromBase64String(stored.ProtectedPrivateKey),
                    Encoding.UTF8.GetBytes(ProductId),
                    DataProtectionScope.CurrentUser);
                var privateKey = new Ed25519PrivateKeyParameters(privateBytes, 0);
                return new LicenseDeviceIdentity(
                    stored.InstallationId,
                    privateKey,
                    Convert.ToBase64String(privateKey.GeneratePublicKey().GetEncoded()));
            }
        }

        var generated = new Ed25519PrivateKeyParameters(new SecureRandom());
        var installationId = Guid.NewGuid().ToString("D");
        var protectedBytes = ProtectedData.Protect(
            generated.GetEncoded(),
            Encoding.UTF8.GetBytes(ProductId),
            DataProtectionScope.CurrentUser);
        var toStore = new LicenseDeviceIdentityStore
        {
            InstallationId = installationId,
            ProtectedPrivateKey = Convert.ToBase64String(protectedBytes)
        };
        var temporary = DeviceIdentityPath + ".tmp";
        File.WriteAllText(
            temporary,
            JsonSerializer.Serialize(toStore, LicenseJsonContext.Default.LicenseDeviceIdentityStore));
        File.Move(temporary, DeviceIdentityPath, overwrite: true);
        return new LicenseDeviceIdentity(
            installationId,
            generated,
            Convert.ToBase64String(generated.GeneratePublicKey().GetEncoded()));
    }

    private static byte[] BuildV2ProofMessage(LicenseCallHomeV2Request request)
    {
        var capabilities = string.Join(",", request.Capabilities.Order(StringComparer.Ordinal));
        return Encoding.UTF8.GetBytes(string.Join("\n", new[]
        {
            "LOTTO-LICENSE-V2",
            "POST",
            "/api/v2/callHome",
            request.RegistrationId,
            request.ProductId,
            request.InstallationId,
            request.ClientVersion,
            request.BuildId,
            request.Platform,
            request.ProtocolVersion.ToString(CultureInfo.InvariantCulture),
            capabilities,
            request.ProofJti,
            request.Challenge
        }));
    }

    private static string SignDeviceProof(LicenseDeviceIdentity identity, byte[] message)
    {
        var signer = new Ed25519Signer();
        signer.Init(true, identity.PrivateKey);
        signer.BlockUpdate(message, 0, message.Length);
        return Convert.ToBase64String(signer.GenerateSignature());
    }

    private static bool VerifyEd25519(string blob, string signatureBase64)
    {
        try
        {
            var publicKey = (Ed25519PublicKeyParameters)PublicKeyFactory.CreateKey(DecodePublicKeyPem(LicensePublicKeyPem));
            var signature = Convert.FromBase64String(signatureBase64);
            var data = Encoding.UTF8.GetBytes(blob);

            var verifier = new Ed25519Signer();
            verifier.Init(false, publicKey);
            verifier.BlockUpdate(data, 0, data.Length);
            return verifier.VerifySignature(signature);
        }
        catch
        {
            return false;
        }
    }

    private static byte[] DecodePublicKeyPem(string pem)
    {
        var base64 = pem
            .Replace("-----BEGIN PUBLIC KEY-----", string.Empty, StringComparison.Ordinal)
            .Replace("-----END PUBLIC KEY-----", string.Empty, StringComparison.Ordinal)
            .Replace("\r", string.Empty, StringComparison.Ordinal)
            .Replace("\n", string.Empty, StringComparison.Ordinal)
            .Replace(" ", string.Empty, StringComparison.Ordinal)
            .Trim();
        return Convert.FromBase64String(base64);
    }

    private static ByteArrayContent BuildJsonContent(byte[] bodyBytes)
    {
        var content = new ByteArrayContent(bodyBytes);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json")
        {
            CharSet = "utf-8"
        };
        return content;
    }

    private static async Task TryPostLicenseInfoAsync(
        HttpClient http,
        string callHomeUrl,
        string registrationId,
        string updateToken,
        LicenseStoreInfo storeInfo,
        CancellationToken cancellationToken)
    {
        var infoUrl = LicenseInfoUrlFor(callHomeUrl);
        try
        {
            var result = await PostLicenseInfoAsync(http, infoUrl, registrationId, updateToken, storeInfo, cancellationToken);
            if (result != LicenseInfoPostResult.TokenExpired)
                return;

            Trace("licenseInfo token expired; refreshing callHome token once");
            var refreshedToken = await RefreshLicenseInfoTokenAsync(http, callHomeUrl, registrationId, cancellationToken);
            if (!string.IsNullOrWhiteSpace(refreshedToken))
                await PostLicenseInfoAsync(http, infoUrl, registrationId, refreshedToken, storeInfo, cancellationToken);
        }
        catch (Exception ex)
        {
            Trace($"licenseInfo post skipped after error {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static async Task<LicenseInfoPostResult> PostLicenseInfoAsync(
        HttpClient http,
        string infoUrl,
        string registrationId,
        string updateToken,
        LicenseStoreInfo storeInfo,
        CancellationToken cancellationToken)
    {
        var request = new LicenseInfoUpdateRequest(
            registrationId,
            updateToken,
            storeInfo.Name.Trim(),
            storeInfo.Address.Trim(),
            storeInfo.City.Trim(),
            storeInfo.State.Trim().ToUpperInvariant(),
            storeInfo.Zip.Trim());
        var bodyBytes = JsonSerializer.SerializeToUtf8Bytes(
            request,
            LicenseJsonContext.Default.LicenseInfoUpdateRequest);

        using var content = BuildJsonContent(bodyBytes);
        Trace($"about to POST licenseInfo {infoUrl}");
        using var response = await http.PostAsync(infoUrl, content, cancellationToken);
        Trace($"licenseInfo response received http={(int)response.StatusCode}");

        if (response.IsSuccessStatusCode)
            return LicenseInfoPostResult.Success;

        var errorText = await response.Content.ReadAsStringAsync(cancellationToken);
        var normalized = errorText.Trim().ToLowerInvariant();
        Trace($"licenseInfo rejected http={(int)response.StatusCode} body={errorText}");

        if ((int)response.StatusCode == 401 && normalized.Contains("token_expired", StringComparison.Ordinal))
            return LicenseInfoPostResult.TokenExpired;
        if ((int)response.StatusCode == 401 && normalized.Contains("invalid_token", StringComparison.Ordinal))
            return LicenseInfoPostResult.InvalidToken;
        if ((int)response.StatusCode == 403 && normalized.Contains("not_authorized", StringComparison.Ordinal))
            return LicenseInfoPostResult.NotAuthorized;
        return LicenseInfoPostResult.Failed;
    }

    private static async Task<string?> RefreshLicenseInfoTokenAsync(
        HttpClient http,
        string callHomeUrl,
        string registrationId,
        CancellationToken cancellationToken)
    {
        var payload = new LicenseCallHomeRequest(registrationId);
        var bodyBytes = JsonSerializer.SerializeToUtf8Bytes(
            payload,
            LicenseJsonContext.Default.LicenseCallHomeRequest);
        using var content = BuildJsonContent(bodyBytes);
        using var response = await http.PostAsync(callHomeUrl, content, cancellationToken);
        Trace($"licenseInfo refresh callHome response http={(int)response.StatusCode}");
        if (!response.IsSuccessStatusCode)
            return null;

        var body = await response.Content.ReadFromJsonAsync(
            LicenseJsonContext.Default.LicenseCallHomeResponse,
            cancellationToken);
        var signed = VerifyCallHomeResponse(body, registrationId, DateTime.UtcNow, out var verifyError);
        if (signed?.Authorized != true)
        {
            Trace($"licenseInfo refresh token rejected {verifyError}");
            return null;
        }

        return body?.LicenseInfoUpdateToken;
    }

    private static string LicenseInfoUrlFor(string callHomeUrl)
    {
        if (!Uri.TryCreate(callHomeUrl, UriKind.Absolute, out var uri))
            return callHomeUrl.Replace("/callHome", "/licenseInfo", StringComparison.OrdinalIgnoreCase);

        var path = uri.AbsolutePath;
        var replacement = path.EndsWith("/callHome", StringComparison.OrdinalIgnoreCase)
            ? path[..^"/callHome".Length] + "/licenseInfo"
            : path.TrimEnd('/') + "/licenseInfo";
        var builder = new UriBuilder(uri)
        {
            Path = replacement,
            Query = string.Empty
        };
        return builder.Uri.ToString();
    }

    private static void Trace(string message)
    {
        try
        {
            var line = $"{DateTime.UtcNow:yyyy-MM-ddTHH:mm:ssZ} {message}{Environment.NewLine}";
            File.AppendAllText(LicenseLogPath, line, Encoding.UTF8);
        }
        catch
        {
            // License logging must never change app availability.
        }
    }

    private static string StateDirectory
    {
        get
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SimpleLotto");
            Directory.CreateDirectory(directory);
            return directory;
        }
    }

    private static string StatePath => Path.Combine(StateDirectory, "license.json");

    private static string DeviceIdentityPath => Path.Combine(StateDirectory, "license-device-v2.json");

    private static string LicenseLogPath => Path.Combine(AppLog.LogDirectory, "license.log");
}

public sealed record LicenseStoreInfo(
    string Name,
    string Address,
    string City,
    string State,
    string Zip);

public sealed class LicenseStatus
{
    public string Status { get; set; } = "pending";
    public int DaysRemaining { get; set; }
    public string RegistrationId { get; set; } = string.Empty;
    public DateTime? LastAuthorized { get; set; }
    public DateTime? LastCheck { get; set; }
    public string Message { get; set; } = string.Empty;
    public string SubscriptionExpiresAt { get; set; } = string.Empty;
    public int? SubscriptionDaysRemaining { get; set; }
}

internal sealed class LicenseState
{
    [JsonPropertyName("first_attempt_at")]
    public DateTime? FirstAttemptAt { get; set; }

    [JsonPropertyName("last_check_at")]
    public DateTime? LastCheckAt { get; set; }

    [JsonPropertyName("last_authorized_at")]
    public DateTime? LastAuthorizedAt { get; set; }

    [JsonPropertyName("last_result")]
    public string? LastResult { get; set; }

    [JsonPropertyName("signed_blob")]
    public string? SignedBlob { get; set; }

    [JsonPropertyName("subscription_expires_at")]
    public string? SubscriptionExpiresAt { get; set; }

    [JsonPropertyName("product_id")]
    public string? ProductId { get; set; }

    [JsonPropertyName("product_status")]
    public string? ProductStatus { get; set; }

    [JsonPropertyName("license_type")]
    public string? LicenseType { get; set; }

    [JsonPropertyName("entitlement_expires_at")]
    public string? EntitlementExpiresAt { get; set; }

    [JsonPropertyName("features")]
    public string[] Features { get; set; } = [];

    [JsonPropertyName("enforcement_mode")]
    public string? EnforcementMode { get; set; }

    [JsonPropertyName("strict_v2_seen")]
    public bool StrictV2Seen { get; set; }
}

internal sealed record LicenseCallHomeRequest(
    [property: JsonPropertyName("registration_id")] string RegistrationId);

internal sealed record LicenseChallengeRequest(
    [property: JsonPropertyName("registration_id")] string RegistrationId,
    [property: JsonPropertyName("product_id")] string ProductId,
    [property: JsonPropertyName("installation_id")] string InstallationId);

internal sealed class LicenseChallengeResponse
{
    [JsonPropertyName("challenge")]
    public string? Challenge { get; set; }
}

internal sealed class LicenseCallHomeV2Request
{
    [JsonPropertyName("registration_id")]
    public string RegistrationId { get; set; } = string.Empty;

    [JsonPropertyName("product_id")]
    public string ProductId { get; set; } = string.Empty;

    [JsonPropertyName("installation_id")]
    public string InstallationId { get; set; } = string.Empty;

    [JsonPropertyName("client_version")]
    public string ClientVersion { get; set; } = string.Empty;

    [JsonPropertyName("build_id")]
    public string BuildId { get; set; } = string.Empty;

    [JsonPropertyName("platform")]
    public string Platform { get; set; } = string.Empty;

    [JsonPropertyName("protocol_version")]
    public int ProtocolVersion { get; set; }

    [JsonPropertyName("capabilities")]
    public string[] Capabilities { get; set; } = [];

    [JsonPropertyName("public_key")]
    public string PublicKey { get; set; } = string.Empty;

    [JsonPropertyName("proof_jti")]
    public string ProofJti { get; set; } = string.Empty;

    [JsonPropertyName("challenge")]
    public string Challenge { get; set; } = string.Empty;

    [JsonPropertyName("proof_signature")]
    public string ProofSignature { get; set; } = string.Empty;
}

internal sealed class LicenseDeviceIdentityStore
{
    [JsonPropertyName("installation_id")]
    public string InstallationId { get; set; } = string.Empty;

    [JsonPropertyName("protected_private_key")]
    public string ProtectedPrivateKey { get; set; } = string.Empty;
}

internal sealed record LicenseDeviceIdentity(
    string InstallationId,
    Ed25519PrivateKeyParameters PrivateKey,
    string PublicKey);

internal sealed class LicenseCallHomeResponse
{
    [JsonPropertyName("authorized")]
    public bool? Authorized { get; set; }

    [JsonPropertyName("reason")]
    public string? Reason { get; set; }

    [JsonPropertyName("blob")]
    public string? Blob { get; set; }

    [JsonPropertyName("expires_at")]
    public string? ExpiresAt { get; set; }

    [JsonPropertyName("alg")]
    public string? Alg { get; set; }

    [JsonPropertyName("key_id")]
    public string? KeyId { get; set; }

    [JsonPropertyName("signature")]
    public string? Signature { get; set; }

    [JsonPropertyName("license_info_update_token")]
    public string? LicenseInfoUpdateToken { get; set; }
}

internal sealed record LicenseInfoUpdateRequest(
    [property: JsonPropertyName("registration_id")] string RegistrationId,
    [property: JsonPropertyName("license_info_update_token")] string LicenseInfoUpdateToken,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("address")] string Address,
    [property: JsonPropertyName("city")] string City,
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("zip")] string Zip);

internal enum LicenseInfoPostResult
{
    Success,
    TokenExpired,
    InvalidToken,
    NotAuthorized,
    Failed
}

internal sealed class LicenseSignedPayload
{
    [JsonPropertyName("schema")]
    public int Schema { get; set; }

    [JsonPropertyName("registration_id")]
    public string? RegistrationId { get; set; }

    [JsonPropertyName("authorized")]
    public bool? Authorized { get; set; }

    [JsonPropertyName("expires_at")]
    public string? ExpiresAt { get; set; }

    [JsonPropertyName("status")]
    public string? Status { get; set; }

    [JsonPropertyName("issued_at")]
    public string? IssuedAt { get; set; }

    [JsonPropertyName("valid_until")]
    public string? ValidUntil { get; set; }

    [JsonPropertyName("product_id")]
    public string? ProductId { get; set; }

    [JsonPropertyName("installation_id")]
    public string? InstallationId { get; set; }

    [JsonPropertyName("request_nonce")]
    public string? RequestNonce { get; set; }

    [JsonPropertyName("product_status")]
    public string? ProductStatus { get; set; }

    [JsonPropertyName("license_type")]
    public string? LicenseType { get; set; }

    [JsonPropertyName("entitlement_expires_at")]
    public string? EntitlementExpiresAt { get; set; }

    [JsonPropertyName("features")]
    public string[]? Features { get; set; }

    [JsonPropertyName("enforcement_mode")]
    public string? EnforcementMode { get; set; }

    [JsonPropertyName("legacy_fallback_allowed")]
    public bool? LegacyFallbackAllowed { get; set; }
}

[JsonSourceGenerationOptions(
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(LicenseCallHomeRequest))]
[JsonSerializable(typeof(LicenseChallengeRequest))]
[JsonSerializable(typeof(LicenseChallengeResponse))]
[JsonSerializable(typeof(LicenseCallHomeV2Request))]
[JsonSerializable(typeof(LicenseCallHomeResponse))]
[JsonSerializable(typeof(LicenseDeviceIdentityStore))]
[JsonSerializable(typeof(LicenseInfoUpdateRequest))]
[JsonSerializable(typeof(LicenseSignedPayload))]
[JsonSerializable(typeof(LicenseState))]
internal partial class LicenseJsonContext : JsonSerializerContext
{
}
