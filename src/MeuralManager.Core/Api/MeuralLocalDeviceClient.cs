using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using MeuralManager.Core.Models;

namespace MeuralManager.Core.Api;

// Talks to the Canvas frame's own local HTTP API - its embedded web server, listening at
// http://{device.LocalIp}/remote/... - not api.meural.com. This is a completely separate,
// unauthenticated surface, reverse-engineered from Meural's official Home Assistant
// integration's LocalMeural client (custom_components/meural/pymeural.py,
// github.com/GuySie/ha-meural), per CLAUDE.md's rule to verify undocumented endpoints there
// rather than guess. It's what next/prev image, power (suspend/resume), and "what's on screen
// right now" go through - the cloud API has no equivalent for any of these. Requires the app to
// be network-reachable to the frame's LAN IP, unlike everything else in Core, so every call here
// treats a connection failure as "unreachable" (returns null/false) rather than throwing - a
// frame that's off or off the LAN is an expected, not exceptional, outcome.
public static class MeuralLocalDeviceClient
{
    // LAN calls should fail fast rather than hang for anywhere near MeuralApiClient's 30s cloud
    // timeout - a frame that's off or unreachable should read as "unreachable" quickly, not hang
    // the toolbar's poll loop.
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(6) };

    // The embedded Canvas web server is known to reset keep-alive connections on occasion
    // (observed by ha-meural); asking for a fresh connection every time avoids intermittent
    // failures against a connection the frame has since dropped.
    private static HttpRequestMessage BuildRequest(string localIp, string path)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"http://{localIp}/remote/{path}");
        request.Headers.Add("Connection", "close");
        return request;
    }

    private static async Task<bool> SendCommandAsync(string localIp, string path, CancellationToken ct)
    {
        try
        {
            using var request = BuildRequest(localIp, path);
            using var response = await Http.SendAsync(request, ct);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return false;
        }
    }

    public static Task<bool> NextImageAsync(string localIp, CancellationToken ct = default) =>
        SendCommandAsync(localIp, "control_command/set_key/right/", ct);

    public static Task<bool> PreviousImageAsync(string localIp, CancellationToken ct = default) =>
        SendCommandAsync(localIp, "control_command/set_key/left/", ct);

    public static Task<bool> SuspendAsync(string localIp, CancellationToken ct = default) =>
        SendCommandAsync(localIp, "control_command/suspend", ct);

    public static Task<bool> ResumeAsync(string localIp, CancellationToken ct = default) =>
        SendCommandAsync(localIp, "control_command/resume", ct);

    // Switches to a gallery already loaded on the frame - instant, since the frame already has
    // its images cached locally. This is NOT the same operation as MeuralApiClient's cloud
    // AddGalleryToDeviceAsync ("load gallery"), which installs a gallery onto the frame for the
    // first time and makes it re-download that gallery's images even if it's already there -
    // confirmed against ha-meural's media_player.py (async_select_source), which only falls back
    // to the cloud call for a gallery that isn't already on the device.
    public static Task<bool> ChangeGalleryAsync(string localIp, long galleryId, CancellationToken ct = default) =>
        SendCommandAsync(localIp, $"control_command/change_gallery/{galleryId}", ct);

    // Null means the frame couldn't be reached at all (off, unplugged, off the LAN) - distinct
    // from a reachable frame that reports itself asleep (false vs true).
    public static async Task<bool?> GetSleepStatusAsync(string localIp, CancellationToken ct = default)
    {
        try
        {
            using var request = BuildRequest(localIp, "control_check/sleep/");
            using var response = await Http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
                return null;

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(ct));
            return doc.RootElement.TryGetProperty("response", out var value) && value.ValueKind == JsonValueKind.True;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return null;
        }
    }

    public static async Task<MeuralLocalGalleryStatus?> GetGalleryStatusAsync(string localIp, CancellationToken ct = default)
    {
        try
        {
            using var request = BuildRequest(localIp, "get_gallery_status_json/");
            using var response = await Http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
                return null;

            var envelope = await response.Content.ReadFromJsonAsync<LocalResponseEnvelope>(cancellationToken: ct);
            return envelope?.Response;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or NotSupportedException)
        {
            return null;
        }
    }

    private sealed record LocalResponseEnvelope(
        [property: JsonPropertyName("response")] MeuralLocalGalleryStatus? Response);
}
