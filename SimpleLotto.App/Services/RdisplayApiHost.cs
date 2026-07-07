using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;

namespace SimpleLotto.App.Services;

public sealed class RdisplayApiHost
{
    private readonly RdisplayService _rdisplay;
    private readonly LocalStore _store;
    private WebApplication? _app;

    public RdisplayApiHost(RdisplayService rdisplay, LocalStore store)
    {
        _rdisplay = rdisplay;
        _store = store;
    }

    public async Task StartAsync(CancellationToken ct = default)
    {
        _app = BuildApp();
        await _app.StartAsync(ct);
    }

    public async Task StopAsync()
    {
        if (_app is null)
            return;

        await _app.StopAsync();
        await _app.DisposeAsync();
        _app = null;
    }

    private WebApplication BuildApp()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.ConfigureKestrel(options => options.ListenAnyIP(RdisplayService.ApiPort));
        var app = builder.Build();

        app.MapGet("/api/health", () => Ok(new
        {
            service = "SimpleLotto",
            version = "0.0.1",
            rdisplay = true
        }));

        app.MapPost("/api/displays/hello", async (HttpRequest req) =>
        {
            DisplayHelloPayload? body;
            try { body = await req.ReadFromJsonAsync<DisplayHelloPayload>(); }
            catch { return Error("INVALID_JSON", "Request body must be JSON.", 400); }

            if (body is null || string.IsNullOrWhiteSpace(body.AuthToken))
                return Error("MISSING_AUTH_TOKEN", "auth_token is required.", 400);

            var display = _rdisplay.LookupByAuthToken(body.AuthToken);
            if (display is null)
                return Error("INVALID_TOKEN", "auth_token did not match any registered display.", 401);

            var screensChanged = _rdisplay.UpdateHardware(display.Id, body.hardware);
            if (screensChanged)
                await _rdisplay.PushToAllAsync();
            display = _rdisplay.LookupByAuthToken(body.AuthToken) ?? display;

            var snapshot = _rdisplay.BuildSnapshotPayloadForDisplay(display);
            var serverSignature = _rdisplay.SnapshotSignature(snapshot);
            var clientSignature = body.SnapshotSignature ?? string.Empty;
            var activeScreenCount = snapshot.TryGetValue("active_screen_count", out var count) && count is int n
                ? n
                : display.ActiveScreenCount;

            return Ok(new
            {
                snapshot = serverSignature == clientSignature ? null : snapshot,
                snapshot_signature = serverSignature,
                heartbeat = body.Heartbeat ?? false,
                acknowledged = true,
                active_screen_count = activeScreenCount
            });
        });

        app.MapPost("/api/displays/hardware-changed", async (HttpRequest req) =>
        {
            DisplayHardwareChangedPayload? body;
            try { body = await req.ReadFromJsonAsync<DisplayHardwareChangedPayload>(); }
            catch { return Error("INVALID_JSON", "Request body must be JSON.", 400); }

            if (body is null || string.IsNullOrWhiteSpace(body.AuthToken))
                return Error("MISSING_AUTH_TOKEN", "auth_token is required.", 400);

            var display = _rdisplay.LookupByAuthToken(body.AuthToken);
            if (display is null)
                return Error("INVALID_TOKEN", "auth_token did not match any registered display.", 401);

            var screensChanged = _rdisplay.UpdateHardware(display.Id, body.hardware);
            if (screensChanged)
                await _rdisplay.PushToAllAsync();
            display = _rdisplay.LookupByAuthToken(body.AuthToken) ?? display;
            var snapshot = _rdisplay.BuildSnapshotPayloadForDisplay(display);
            var serverSignature = _rdisplay.SnapshotSignature(snapshot);

            return Ok(new
            {
                snapshot,
                snapshot_signature = serverSignature,
                heartbeat = false,
                acknowledged = true,
                event_id = body.EventId,
                active_screen_count = display.ActiveScreenCount
            });
        });

        app.MapPost("/api/displays/test-callback", async (HttpRequest req) =>
        {
            var token = req.Headers["X-Display-Token"].ToString();
            if (_rdisplay.LookupByAuthToken(token) is null)
                return Error("INVALID_TOKEN", "X-Display-Token missing or unrecognized.", 401);

            DisplayTestCallbackPayload? body;
            try { body = await req.ReadFromJsonAsync<DisplayTestCallbackPayload>(); }
            catch { return Error("INVALID_JSON", "Request body must be JSON.", 400); }

            if (body is null || string.IsNullOrWhiteSpace(body.TestId))
                return Error("MISSING_TEST_ID", "test_id is required.", 400);

            return OkMessage("Test callback recorded.");
        });

        app.MapGet("/api/images/{gameId}", async (string gameId, HttpRequest req) =>
        {
            var token = req.Headers["X-Display-Token"].ToString();
            if (_rdisplay.VerifyToken(token) is null)
                return Error("INVALID_TOKEN", "X-Display-Token missing or unrecognized.", 401);

            if (string.IsNullOrWhiteSpace(gameId) || gameId.Length > 64)
                return Error("INVALID_GAME_ID", "game_id is empty or too long.", 400);

            var path = ResolveCachedImagePath(gameId);
            if (path is null)
                return Error("NOT_CACHED", "Image not yet cached for this game.", 404);

            var etag = await ComputeEtagAsync(path);
            var ifNoneMatch = req.Headers.IfNoneMatch.ToString().Trim('"');
            if (!string.IsNullOrWhiteSpace(ifNoneMatch) &&
                string.Equals(ifNoneMatch, etag, StringComparison.OrdinalIgnoreCase))
            {
                return Results.StatusCode(StatusCodes.Status304NotModified);
            }

            return Results.File(
                path,
                contentType: ContentTypeForPath(path),
                fileDownloadName: null,
                lastModified: null,
                entityTag: new Microsoft.Net.Http.Headers.EntityTagHeaderValue($"\"{etag}\""));
        });

        return app;
    }

    private string? ResolveCachedImagePath(string gameId)
    {
        var cached = CachedGameImagePath(gameId);
        if (cached is not null)
            return cached;

        try
        {
            var state = _store.Load();
            var game = state.ManualGames.FirstOrDefault(g =>
                string.Equals(g.GameId, gameId, StringComparison.OrdinalIgnoreCase));
            if (game is null)
                return null;

            return CachedFileImagePath(game.ImageUri);
        }
        catch
        {
            return null;
        }
    }

    private static string? CachedGameImagePath(string gameId)
    {
        var safe = SafeGameImageKey(gameId);
        var jpg = Path.Combine(GameImageCacheDir, $"{safe}.jpg");
        if (File.Exists(jpg))
            return jpg;

        var png = Path.Combine(GameImageCacheDir, $"{safe}.png");
        return File.Exists(png) ? png : null;
    }

    private static string? CachedFileImagePath(string imageUri)
    {
        if (!Uri.TryCreate(imageUri, UriKind.Absolute, out var uri) ||
            !uri.IsFile ||
            !File.Exists(uri.LocalPath))
        {
            return null;
        }

        return uri.LocalPath;
    }

    private static string SafeGameImageKey(string gameId)
    {
        var chars = gameId
            .Where(c => char.IsLetterOrDigit(c) || c == '-' || c == '_')
            .ToArray();
        return chars.Length == 0 ? "unknown" : new string(chars);
    }

    private static string GameImageCacheDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SimpleLotto",
        "game-images");

    private static string ContentTypeForPath(string path) =>
        string.Equals(Path.GetExtension(path), ".png", StringComparison.OrdinalIgnoreCase)
            ? "image/png"
            : "image/jpeg";

    private static async Task<string> ComputeEtagAsync(string path)
    {
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true);
        using var sha = SHA256.Create();
        var hash = await sha.ComputeHashAsync(fs);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static IResult Ok(object? data) =>
        Results.Json(new Dictionary<string, object?>
        {
            ["ok"] = true,
            ["data"] = data
        });

    private static IResult OkMessage(string message) =>
        Results.Json(new Dictionary<string, object?>
        {
            ["ok"] = true,
            ["message"] = message
        });

    private static IResult Error(string code, string message, int status) =>
        Results.Json(new Dictionary<string, object?>
        {
            ["ok"] = false,
            ["error"] = code,
            ["message"] = message
        }, statusCode: status);
}

public sealed record DisplayHelloPayload(
    [property: JsonPropertyName("slug")] string? Slug,
    [property: JsonPropertyName("auth_token")] string? AuthToken,
    [property: JsonPropertyName("heartbeat")] bool? Heartbeat,
    [property: JsonPropertyName("snapshot_signature")] string? SnapshotSignature,
    [property: JsonPropertyName("hardware")] JsonElement? hardware);

public sealed record DisplayHardwareChangedPayload(
    [property: JsonPropertyName("auth_token")] string? AuthToken,
    [property: JsonPropertyName("event_id")] string? EventId,
    [property: JsonPropertyName("hardware")] JsonElement? hardware);

public sealed record DisplayTestCallbackPayload(
    [property: JsonPropertyName("test_id")] string? TestId);
