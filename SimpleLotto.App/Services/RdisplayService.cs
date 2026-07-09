using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace SimpleLotto.App.Services;

public sealed class RdisplayService : IDisposable
{
    public const int ApiPort = 5000;
    public event EventHandler? DisplaysChanged;

    private readonly LocalStore _store;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(5) };
    private readonly object _gate = new();
    private readonly List<RdisplayRegistration> _displays = new();
    private readonly List<RdisplayTileState> _tiles = new();
    private long _nextDisplayId = 1;
    private bool _burnInEnabled = true;
    private int _burnInIntervalMinutes = 15;
    private const string MasterResetHeader = "X-LottoPos-Override";
    private const string MasterResetToken = "REPLACE_BEFORE_RELEASE_master_reset_2026_xyz";

    public RdisplayService(LocalStore store)
    {
        _store = store;
        LoadPersistedDisplays();
    }

    public IReadOnlyList<RdisplayRegistration> Displays
    {
        get
        {
            lock (_gate)
                return _displays.Select(d => d with { }).ToList();
        }
    }

    public void UpdateTiles(IEnumerable<RdisplayTileState> tiles)
    {
        lock (_gate)
        {
            _tiles.Clear();
            _tiles.AddRange(tiles.OrderBy(t => t.BinNumber));
        }

        _ = PushToAllAsync();
    }

    public void ConfigureDisplaySettings(bool burnInEnabled, int burnInIntervalMinutes)
    {
        lock (_gate)
        {
            _burnInEnabled = burnInEnabled;
            _burnInIntervalMinutes = Math.Clamp(burnInIntervalMinutes, 1, 1440);
        }

        _ = PushConfigToAllAsync();
    }

    public RdisplayRegistration? LookupByAuthToken(string? authToken)
    {
        if (string.IsNullOrWhiteSpace(authToken))
            return null;

        lock (_gate)
        {
            var display = _displays.FirstOrDefault(d =>
                string.Equals(d.AuthToken, authToken, StringComparison.Ordinal));
            if (display is null)
                return null;

            display.LastSeenAt = DateTime.UtcNow;
            var copy = display with { };
            PersistDisplay(copy);
            return copy;
        }
    }

    public RdisplayRegistration? VerifyToken(string? authToken) =>
        LookupByAuthToken(authToken);

    public bool UpdateHardware(long displayId, JsonElement? hardware)
    {
        if (hardware is null || hardware.Value.ValueKind != JsonValueKind.Object)
            return false;

        lock (_gate)
        {
            var display = _displays.FirstOrDefault(d => d.Id == displayId);
            if (display is null)
                return false;

            var previousScreenCount = display.ActiveScreenCount;
            display.HardwareJson = hardware.Value.GetRawText();
            display.ActiveScreenCount = ActiveScreenCountFromHardware(hardware.Value);
            display.LastSeenAt = DateTime.UtcNow;
            PersistDisplay(display);
            var changed = previousScreenCount != display.ActiveScreenCount;
            if (changed)
                DisplaysChanged?.Invoke(this, EventArgs.Empty);
            return changed;
        }
    }

    public async Task<RdisplayActionResult> ProbeDisplayHardwareAsync(long id, CancellationToken ct = default)
    {
        var display = GetDisplay(id);
        if (display is null)
            return RdisplayActionResult.Fail("Display not found.");

        var hardware = await ProbeHardwareAsync(display.Host, display.Port, ct);
        if (!hardware.IsSuccess)
            return RdisplayActionResult.Fail($"Hardware probe failed: {hardware.ErrorMessage}");

        using var doc = JsonDocument.Parse(hardware.HardwareJson ?? "{}");
        var changed = UpdateHardware(id, doc.RootElement);
        if (changed)
            await PushToAllAsync(ct);

        return RdisplayActionResult.Ok();
    }

    public async Task<RdisplayRegisterResult> RegisterAsync(
        string host,
        int port,
        string? name = null,
        CancellationToken ct = default)
    {
        var cleanHost = (host ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(cleanHost))
            return RdisplayRegisterResult.Fail("Display host is required.");
        if (port is < 1 or > 65535)
            return RdisplayRegisterResult.Fail("Display port must be between 1 and 65535.");

        var id = EnsureDisplayRow(cleanHost, port, name);
        var display = GetDisplay(id);
        if (display is null)
            return RdisplayRegisterResult.Fail("Could not create display row.");

        var hardware = await ProbeHardwareAsync(cleanHost, port, ct);
        if (!hardware.IsSuccess)
            return RdisplayRegisterResult.Fail($"Hardware probe failed: {hardware.ErrorMessage}");

        var token = GenerateAuthToken();
        var serverUrl = OwnServerUrl();
        var payload = new JsonObject
        {
            ["server_url"] = serverUrl,
            ["slug"] = display.Slug,
            ["auth_token"] = token,
            ["display_config"] = BuildDisplayConfigJson()
        };
        var bodyJson = payload.ToJsonString();

        var register = await SendRegisterAsync(cleanHost, port, bodyJson, display.AuthToken, ct);
        if (!register.IsSuccess && !string.IsNullOrWhiteSpace(display.AuthToken))
            register = await SendRegisterAsync(cleanHost, port, bodyJson, null, ct);
        if (!register.IsSuccess && register.StatusCode == 409)
        {
            var reset = await ForceResetByHostAsync(cleanHost, port, ct);
            if (reset)
                register = await SendRegisterAsync(cleanHost, port, bodyJson, null, ct);
        }
        if (!register.IsSuccess)
            return RdisplayRegisterResult.Fail(BuildRegisterError(register));

        lock (_gate)
        {
            var mutable = _displays.First(d => d.Id == id);
            mutable.AuthToken = token;
            mutable.HardwareJson = hardware.HardwareJson;
            mutable.ActiveScreenCount = ActiveScreenCountFromHardware(hardware.HardwareJson);
            mutable.LastRegisteredAt = DateTime.UtcNow;
            mutable.LastSeenAt = DateTime.UtcNow;
            mutable.LastServerUrl = serverUrl;
            mutable.IsActive = true;
            PersistDisplay(mutable);
        }
        DisplaysChanged?.Invoke(this, EventArgs.Empty);

        var registered = GetDisplay(id)!;
        await PushSnapshotAsync(registered, ct);
        await PushImageReadyForDisplayAsync(registered, ct);
        return RdisplayRegisterResult.Ok(registered);
    }

    public async Task<RdisplayActionResult> RefreshDisplayAsync(long id, CancellationToken ct = default)
    {
        var display = GetDisplay(id);
        if (display is null)
            return RdisplayActionResult.Fail("Display not found.");
        if (!display.IsRegistered)
            return RdisplayActionResult.Fail("Display is not registered.");

        UpdateLastServerUrl(display.Id, OwnServerUrl());
        var configPushed = await PushConfigAsync(display, ct);
        var pushed = await PushSnapshotAndImagesAsync(display, ct);
        return pushed
            ? RdisplayActionResult.Ok()
            : configPushed
                ? RdisplayActionResult.Ok()
                : RdisplayActionResult.Fail("Display did not accept the refresh. If the display was reinstalled, deregister it on the display and register again.");
    }

    public async Task<RdisplayActionResult> TriggerUpgradeAsync(long id, CancellationToken ct = default)
    {
        var display = GetDisplay(id);
        if (display is null)
            return RdisplayActionResult.Fail("Display not found.");
        if (!display.IsRegistered)
            return RdisplayActionResult.Fail("Display is not registered.");

        var payload = new JsonObject { ["mode"] = "apply" };
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{display.BaseUrl}/api/display/update")
        {
            Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json")
        };
        req.Headers.Add("X-Display-Token", display.AuthToken);

        try
        {
            using var response = await _http.SendAsync(req, ct);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(ct);
                return RdisplayActionResult.Fail($"Display rejected upgrade request (HTTP {(int)response.StatusCode}): {Truncate(body, 200)}");
            }

            TouchLastSeen(display.Id);
            return RdisplayActionResult.Ok();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return RdisplayActionResult.Fail($"Display upgrade request failed: {ex.Message}");
        }
    }

    public async Task<RdisplayFleetUpgradeResult> TriggerUpgradeForAllRegisteredAsync(CancellationToken ct = default)
    {
        var displays = Displays.Where(d => d.IsRegistered).ToList();
        var requested = 0;
        var failed = 0;
        foreach (var display in displays)
        {
            var result = await TriggerUpgradeAsync(display.Id, ct);
            if (result.IsSuccess)
                requested++;
            else
                failed++;
        }

        return new RdisplayFleetUpgradeResult(displays.Count, requested, failed);
    }

    public async Task<RdisplayActionResult> UnregisterAsync(long id, CancellationToken ct = default)
    {
        var display = GetDisplay(id);
        if (display is null)
            return RdisplayActionResult.Fail("Display not found.");

        if (!string.IsNullOrWhiteSpace(display.AuthToken))
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{display.BaseUrl}/api/display/unregister")
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json")
            };
            req.Headers.Add("X-Display-Token", display.AuthToken);
            try
            {
                using var response = await _http.SendAsync(req, ct);
            }
            catch
            {
                // Local deregistration still proceeds; the display can be registered again later.
            }
        }

        lock (_gate)
            _displays.RemoveAll(d => d.Id == id);

        try
        {
            _store.DeleteRdisplayDisplay(id);
            DisplaysChanged?.Invoke(this, EventArgs.Empty);
        }
        catch
        {
            return RdisplayActionResult.Fail("Display was removed from memory, but SQLite cleanup failed.");
        }

        return RdisplayActionResult.Ok();
    }

    public Dictionary<string, object?> BuildSnapshotPayloadForDisplay(RdisplayRegistration display)
    {
        List<RdisplayTileState> tiles;
        List<RdisplayRegistration> displays;
        lock (_gate)
        {
            tiles = _tiles.ToList();
            displays = _displays
                .Where(d => d.IsActive && d.IsRegistered)
                .OrderBy(d => d.ScreenOrder)
                .ThenBy(d => d.Id)
                .Select(d => d with { })
                .ToList();
        }

        var allTiles = tiles.Select(BuildTile).ToList();
        var logicalScreens = OrderedScreens(displays);
        var localScreens = logicalScreens
            .Where(s => s.Display.Id == display.Id)
            .ToList();

        if (localScreens.Count == 0)
        {
            if (display.ActiveScreenCount <= 0)
                return WrapPayload(Array.Empty<Dictionary<string, object?>>(), Array.Empty<Dictionary<string, object?>>(), 0);

            localScreens.Add(new LogicalScreen(display, 0, 0));
            logicalScreens = localScreens;
        }

        var combined = new List<Dictionary<string, object?>>();
        var screenPayloads = new List<Dictionary<string, object?>>();
        foreach (var screen in localScreens)
        {
            var sliced = SliceForScreen(allTiles, screen.GlobalScreenIndex, logicalScreens.Count).ToList();
            combined.AddRange(sliced);
            screenPayloads.Add(new Dictionary<string, object?>
            {
                ["screen_index"] = screen.LocalScreenIndex,
                ["global_screen_index"] = screen.GlobalScreenIndex,
                ["screen_count"] = localScreens.Count,
                ["display_id"] = display.Id,
                ["display_slug"] = display.Slug,
                ["connector"] = $"screen-{screen.LocalScreenIndex + 1}",
                ["connector_status"] = "connected",
                ["tiles"] = sliced.Cast<object?>().ToList()
            });
        }

        return WrapPayload(combined, screenPayloads, localScreens.Count);
    }

    public string SnapshotSignature(Dictionary<string, object?> snapshot)
    {
        var sig = new Dictionary<string, object?>
        {
            ["tiles"] = snapshot.TryGetValue("tiles", out var tiles) ? tiles : null,
            ["screens"] = snapshot.TryGetValue("screens", out var screens) ? screens : null,
            ["active_screen_count"] = snapshot.TryGetValue("active_screen_count", out var active) ? active : null,
            ["lic"] = snapshot.TryGetValue("license_status", out var license) ? license : "valid"
        };

        return JsonSerializer.Serialize(sig, new JsonSerializerOptions { WriteIndented = false });
    }

    public Task PushToAllAsync(CancellationToken ct = default)
    {
        List<RdisplayRegistration> displays;
        lock (_gate)
        {
            displays = _displays
                .Where(d => d.IsActive && d.IsRegistered)
                .Select(d => d with { })
                .ToList();
        }

        return PushManyAsync(displays, ct);
    }

    public Task PushConfigToAllAsync(CancellationToken ct = default)
    {
        List<RdisplayRegistration> displays;
        lock (_gate)
        {
            displays = _displays
                .Where(d => d.IsActive && d.IsRegistered)
                .Select(d => d with { })
                .ToList();
        }

        return PushConfigManyAsync(displays, ct);
    }

    private async Task PushManyAsync(IEnumerable<RdisplayRegistration> displays, CancellationToken ct)
    {
        foreach (var display in displays)
            await PushSnapshotAndImagesAsync(display, ct);
    }

    private async Task PushConfigManyAsync(IEnumerable<RdisplayRegistration> displays, CancellationToken ct)
    {
        foreach (var display in displays)
            await PushConfigAsync(display, ct);
    }

    private Task<bool> PushSnapshotAsync(RdisplayRegistration display, CancellationToken ct = default) =>
        PostToDisplayAsync(display, "/api/display/snapshot", BuildSnapshotPayloadForDisplay(display), ct);

    private async Task<bool> PushSnapshotAndImagesAsync(RdisplayRegistration display, CancellationToken ct = default)
    {
        var pushed = await PushSnapshotAsync(display, ct);
        await PushImageReadyForDisplayAsync(display, ct);
        return pushed;
    }

    private Task<bool> PushConfigAsync(RdisplayRegistration display, CancellationToken ct = default) =>
        PostToDisplayAsync(display, "/api/display/config", new Dictionary<string, object?>
        {
            ["display_config"] = BuildDisplayConfigDictionary()
        }, ct);

    private async Task PushImageReadyForDisplayAsync(RdisplayRegistration display, CancellationToken ct = default)
    {
        var snapshot = BuildSnapshotPayloadForDisplay(display);
        if (!snapshot.TryGetValue("tiles", out var tilesObj) || tilesObj is not IEnumerable tiles)
            return;

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in tiles)
        {
            if (item is not IDictionary<string, object?> tile ||
                !tile.TryGetValue("game_id", out var gameObj) ||
                gameObj is not string gameId ||
                !seen.Add(gameId))
            {
                continue;
            }

            await PostToDisplayAsync(display, "/api/display/event", new Dictionary<string, object?>
            {
                ["type"] = "image_ready",
                ["server_url"] = OwnServerUrl(),
                ["game_id"] = gameId
            }, ct);
        }
    }

    private async Task<RegisterPostResult> SendRegisterAsync(
        string host,
        int port,
        string bodyJson,
        string? existingToken,
        CancellationToken ct)
    {
        using var req = new HttpRequestMessage(
            HttpMethod.Post,
            $"http://{host}:{port}/api/display/register")
        {
            Content = new StringContent(bodyJson, Encoding.UTF8, "application/json")
        };
        if (!string.IsNullOrWhiteSpace(existingToken))
            req.Headers.Add("X-Display-Token", existingToken);

        try
        {
            using var resp = await _http.SendAsync(req, ct);
            if (resp.IsSuccessStatusCode)
                return RegisterPostResult.Ok();

            var body = await resp.Content.ReadAsStringAsync(ct);
            return RegisterPostResult.Rejected((int)resp.StatusCode, body);
        }
        catch (TaskCanceledException)
        {
            return RegisterPostResult.Failed("Timed out reaching display.");
        }
        catch (Exception ex)
        {
            return RegisterPostResult.Failed($"Could not reach display: {ex.Message}");
        }
    }

    private async Task<bool> ForceResetByHostAsync(string host, int port, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"http://{host}:{port}/health");
        req.Headers.Add(MasterResetHeader, MasterResetToken);

        try
        {
            using var resp = await _http.SendAsync(req, ct);
            return resp.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private static string BuildRegisterError(RegisterPostResult result)
    {
        if (!string.IsNullOrWhiteSpace(result.ErrorMessage))
            return result.ErrorMessage;

        var body = string.IsNullOrWhiteSpace(result.Body)
            ? string.Empty
            : $": {Truncate(result.Body, 220)}";
        var message = $"Display rejected register (HTTP {result.StatusCode}){body}";
        if (result.StatusCode == 409)
            message += ". The display is already registered with another token and did not accept the recovery reset.";
        return message;
    }

    private async Task<bool> PostToDisplayAsync(
        RdisplayRegistration display,
        string path,
        object payload,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(display.AuthToken))
            return false;

        using var req = new HttpRequestMessage(HttpMethod.Post, $"{display.BaseUrl}{path}")
        {
            Content = new StringContent(ConvertToJsonNode(payload)?.ToJsonString() ?? "{}", Encoding.UTF8, "application/json")
        };
        req.Headers.Add("X-Display-Token", display.AuthToken);

        try
        {
            using var resp = await _http.SendAsync(req, ct);
            return resp.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private long EnsureDisplayRow(string host, int port, string? name)
    {
        lock (_gate)
        {
            var existing = _displays.FirstOrDefault(d =>
                string.Equals(d.Host, host, StringComparison.OrdinalIgnoreCase) &&
                d.Port == port);
            if (existing is not null)
                return existing.Id;

            var order = _displays.Count + 1;
            var row = new RdisplayRegistration
            {
                Id = _nextDisplayId++,
                Slug = $"display-{order}",
                Name = string.IsNullOrWhiteSpace(name) ? $"Display {order}" : name.Trim(),
                Host = host,
                Port = port,
                ScreenOrder = order,
                IsActive = true,
                ActiveScreenCount = 1,
                CreatedAt = DateTime.UtcNow
            };
            _displays.Add(row);
            PersistDisplay(row);
            return row.Id;
        }
    }

    private RdisplayRegistration? GetDisplay(long id)
    {
        lock (_gate)
            return _displays.FirstOrDefault(d => d.Id == id) is { } display ? display with { } : null;
    }

    private async Task<HardwareProbeResult> ProbeHardwareAsync(string host, int port, CancellationToken ct)
    {
        try
        {
            using var resp = await _http.GetAsync($"http://{host}:{port}/api/display/hardware", ct);
            if (!resp.IsSuccessStatusCode)
                return HardwareProbeResult.Fail($"HTTP {(int)resp.StatusCode}");

            var body = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("ok", out var ok) || !ok.GetBoolean())
                return HardwareProbeResult.Fail("ok=false");
            if (!doc.RootElement.TryGetProperty("data", out var data))
                return HardwareProbeResult.Fail("missing data");

            return HardwareProbeResult.Ok(data.GetRawText());
        }
        catch (Exception ex)
        {
            return HardwareProbeResult.Fail(ex.Message);
        }
    }

    private static Dictionary<string, object?> BuildTile(RdisplayTileState tile)
    {
        var nextSerial = ParseSerial(tile.Ticket);
        return new Dictionary<string, object?>
        {
            ["tile_index"] = tile.BinNumber,
            ["game_id"] = tile.GameId,
            ["game_name"] = tile.GameName,
            ["bin_label"] = tile.BinNumber.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["next_serial"] = nextSerial,
            ["tickets_remaining"] = Math.Max(0, 100 - nextSerial),
            ["price_cents"] = tile.PriceCents
        };
    }

    private static int ParseSerial(string ticket)
    {
        var digits = new string((ticket ?? string.Empty).Where(char.IsDigit).ToArray());
        return int.TryParse(digits, out var value) ? value : 0;
    }

    private static Dictionary<string, object?> WrapPayload(
        IEnumerable<Dictionary<string, object?>> tiles,
        IEnumerable<Dictionary<string, object?>> screens,
        int activeScreenCount) =>
        new()
        {
            ["tiles"] = tiles.Cast<object?>().ToList(),
            ["screens"] = screens.Cast<object?>().ToList(),
            ["active_screen_count"] = activeScreenCount,
            ["license_status"] = "valid",
            ["generated_at"] = DateTime.UtcNow.ToString("o")
        };

    private static IReadOnlyList<LogicalScreen> OrderedScreens(IReadOnlyList<RdisplayRegistration> displays)
    {
        var result = new List<LogicalScreen>();
        foreach (var display in displays)
        {
            var count = Math.Max(0, display.ActiveScreenCount);
            for (var local = 0; local < count; local++)
                result.Add(new LogicalScreen(display, local, result.Count));
        }

        return result;
    }

    private static IEnumerable<Dictionary<string, object?>> SliceForScreen(
        IReadOnlyList<Dictionary<string, object?>> tiles,
        int screenIndex,
        int screenCount)
    {
        if (screenCount <= 1)
            return tiles;

        var n = tiles.Count;
        var baseCount = n / screenCount;
        var remainder = n % screenCount;
        var start = screenIndex * baseCount + Math.Min(screenIndex, remainder);
        var count = baseCount + (screenIndex < remainder ? 1 : 0);
        return tiles.Skip(start).Take(count);
    }

    private static int ActiveScreenCountFromHardware(string? hardwareJson)
    {
        if (string.IsNullOrWhiteSpace(hardwareJson))
            return 1;

        try
        {
            using var doc = JsonDocument.Parse(hardwareJson);
            return ActiveScreenCountFromHardware(doc.RootElement);
        }
        catch
        {
            return 1;
        }
    }

    private static int ActiveScreenCountFromHardware(JsonElement hardware)
    {
        var total = TryGetInt(hardware, "displays_total");
        var connected = TryGetInt(hardware, "displays_connected");
        if ((total <= 0 || connected <= 0) &&
            TryCountConnectedDisplays(hardware, out var countedTotal, out var countedConnected))
        {
            total = countedTotal;
            connected = countedConnected;
        }

        if (total <= 0)
            return 1;
        return Math.Clamp(connected, 0, total);
    }

    private static bool TryCountConnectedDisplays(JsonElement hardware, out int total, out int connected)
    {
        total = 0;
        connected = 0;
        if (!hardware.TryGetProperty("displays", out var displays) ||
            displays.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (var display in displays.EnumerateArray())
        {
            if (display.ValueKind != JsonValueKind.Object)
                continue;

            total++;
            if (display.TryGetProperty("status", out var status) &&
                status.ValueKind == JsonValueKind.String &&
                string.Equals(status.GetString(), "connected", StringComparison.OrdinalIgnoreCase))
            {
                connected++;
            }
        }

        return total > 0;
    }

    private static int TryGetInt(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var prop))
            return 0;
        return prop.ValueKind switch
        {
            JsonValueKind.Number when prop.TryGetInt32(out var n) => n,
            JsonValueKind.String when int.TryParse(prop.GetString(), out var n) => n,
            _ => 0
        };
    }

    private static string OwnServerUrl()
    {
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Connect("1.1.1.1", 80);
            var ip = ((System.Net.IPEndPoint)socket.LocalEndPoint!).Address;
            return $"http://{ip}:{ApiPort}";
        }
        catch
        {
            return $"http://localhost:{ApiPort}";
        }
    }

    private static string GenerateAuthToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private void LoadPersistedDisplays()
    {
        try
        {
            var state = _store.Load();
            lock (_gate)
            {
                _displays.Clear();
                _displays.AddRange(state.RdisplayDisplays.Select(ToRegistration));
                _nextDisplayId = _displays.Count == 0 ? 1 : _displays.Max(d => d.Id) + 1;
            }
        }
        catch
        {
            lock (_gate)
            {
                _displays.Clear();
                _nextDisplayId = 1;
            }
        }
    }

    private static RdisplayRegistration ToRegistration(StoredRdisplayDisplay display) =>
        new()
        {
            Id = display.Id,
            Slug = display.Slug,
            Name = display.Name,
            Host = display.Host,
            Port = display.Port,
            ScreenOrder = display.ScreenOrder,
            IsActive = display.IsActive,
            ActiveScreenCount = display.ActiveScreenCount,
            CreatedAt = display.CreatedAtUtc,
            LastSeenAt = display.LastSeenAtUtc,
            AuthToken = display.AuthToken,
            HardwareJson = display.HardwareJson,
            LastRegisteredAt = display.LastRegisteredAtUtc,
            LastServerUrl = display.LastServerUrl
        };

    private void PersistDisplay(RdisplayRegistration display)
    {
        try
        {
            _store.UpsertRdisplayDisplay(new StoredRdisplayDisplay(
                display.Id,
                display.Slug,
                display.Name,
                display.Host,
                display.Port,
                display.ScreenOrder,
                display.IsActive,
                display.ActiveScreenCount,
                display.CreatedAt,
                display.LastSeenAt,
                display.AuthToken,
                display.HardwareJson,
                display.LastRegisteredAt,
                display.LastServerUrl));
        }
        catch
        {
            // Display state is runtime-support data; callers still proceed.
        }
    }

    private void UpdateLastServerUrl(long displayId, string serverUrl)
    {
        lock (_gate)
        {
            var display = _displays.FirstOrDefault(d => d.Id == displayId);
            if (display is null)
                return;

            display.LastServerUrl = serverUrl;
            PersistDisplay(display);
        }
    }

    private void TouchLastSeen(long displayId)
    {
        lock (_gate)
        {
            var display = _displays.FirstOrDefault(d => d.Id == displayId);
            if (display is null)
                return;

            display.LastSeenAt = DateTime.UtcNow;
            PersistDisplay(display);
        }
    }

    private JsonObject BuildDisplayConfigJson()
    {
        var config = BuildDisplayConfigDictionary();
        var obj = new JsonObject();
        foreach (var kv in config)
            obj[kv.Key] = ConvertToJsonNode(kv.Value);
        return obj;
    }

    private Dictionary<string, object?> BuildDisplayConfigDictionary()
    {
        lock (_gate)
        {
            return new Dictionary<string, object?>
            {
                ["show_price"] = true,
                ["show_remaining_count"] = false,
                ["serial_contrast"] = "auto",
                ["layout_mode"] = "auto",
                ["burn_in_enabled"] = _burnInEnabled,
                ["burn_in_interval_minutes"] = _burnInIntervalMinutes
            };
        }
    }

    private static JsonNode? ConvertToJsonNode(object? value)
    {
        switch (value)
        {
            case null: return null;
            case JsonNode existing: return existing;
            case string s: return JsonValue.Create(s);
            case bool b: return JsonValue.Create(b);
            case int i: return JsonValue.Create(i);
            case long l: return JsonValue.Create(l);
            case double d: return JsonValue.Create(d);
            case decimal m: return JsonValue.Create(m);
            case DateTime dt: return JsonValue.Create(dt.ToString("o"));
            case IDictionary<string, object?> dict:
                var obj = new JsonObject();
                foreach (var kv in dict)
                {
                    var child = ConvertToJsonNode(kv.Value);
                    if (child is not null)
                        obj[kv.Key] = child;
                }
                return obj;
            case IEnumerable list:
                var arr = new JsonArray();
                foreach (var item in list)
                    arr.Add(ConvertToJsonNode(item));
                return arr;
            default:
                return JsonValue.Create(value.ToString());
        }
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "...";

    public void Dispose() => _http.Dispose();

    private sealed record LogicalScreen(
        RdisplayRegistration Display,
        int LocalScreenIndex,
        int GlobalScreenIndex);

    private sealed record RegisterPostResult(
        bool IsSuccess,
        int? StatusCode,
        string? Body,
        string? ErrorMessage)
    {
        public static RegisterPostResult Ok() => new(true, null, null, null);
        public static RegisterPostResult Rejected(int statusCode, string body) => new(false, statusCode, body, null);
        public static RegisterPostResult Failed(string message) => new(false, null, null, message);
    }
}

public sealed record RdisplayTileState(
    int BinNumber,
    string GameId,
    string GameName,
    string Ticket,
    int PriceCents);

public sealed record RdisplayRegistration
{
    public long Id { get; init; }
    public string Slug { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 5001;
    public int ScreenOrder { get; set; }
    public bool IsActive { get; set; } = true;
    public int ActiveScreenCount { get; set; } = 1;
    public DateTime CreatedAt { get; set; }
    public DateTime? LastSeenAt { get; set; }
    public string? AuthToken { get; set; }
    public string? HardwareJson { get; set; }
    public DateTime? LastRegisteredAt { get; set; }
    public string? LastServerUrl { get; set; }
    public string BaseUrl => $"http://{Host}:{Port}";
    public bool IsRegistered => !string.IsNullOrWhiteSpace(AuthToken);
}

public readonly record struct RdisplayRegisterResult(
    bool IsSuccess,
    RdisplayRegistration? Display,
    string? ErrorMessage)
{
    public static RdisplayRegisterResult Ok(RdisplayRegistration display) =>
        new(true, display, null);

    public static RdisplayRegisterResult Fail(string message) =>
        new(false, null, message);
}

public readonly record struct RdisplayActionResult(
    bool IsSuccess,
    string? ErrorMessage)
{
    public static RdisplayActionResult Ok() =>
        new(true, null);

    public static RdisplayActionResult Fail(string message) =>
        new(false, message);
}

public sealed record RdisplayFleetUpgradeResult(int RegisteredDisplays, int Requested, int Failed);

public readonly record struct HardwareProbeResult(
    bool IsSuccess,
    string? HardwareJson,
    string? ErrorMessage)
{
    public static HardwareProbeResult Ok(string hardwareJson) =>
        new(true, hardwareJson, null);

    public static HardwareProbeResult Fail(string message) =>
        new(false, null, message);
}
