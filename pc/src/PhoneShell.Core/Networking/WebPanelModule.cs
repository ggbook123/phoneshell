using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using PhoneShell.Core.Services;
using PhoneShell.Core.Models;

namespace PhoneShell.Core.Networking;

/// <summary>
/// Serves the web management panel HTML, xterm.js static assets, and REST API endpoints.
/// Integrated into RelayServer's HTTP request pipeline when WebPanelEnabled is true.
/// </summary>
internal sealed class WebPanelModule
{
    private readonly Lazy<byte[]> _xtermJs;
    private readonly Lazy<byte[]> _xtermCss;
    private readonly Lazy<byte[]> _fitAddonJs;
    private readonly byte[] _panelHtmlBytes;
    private readonly QrCodePngService _qrCodeService = new();
    private string? _cachedQrPayload;
    private byte[]? _cachedQrPng;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public WebPanelModule()
    {
        _panelHtmlBytes = Encoding.UTF8.GetBytes(WebPanelHtml.PanelHtml);
        _xtermJs = new Lazy<byte[]>(() => LoadEmbeddedResource("xterm.min.js"));
        _xtermCss = new Lazy<byte[]>(() => LoadEmbeddedResource("xterm.min.css"));
        _fitAddonJs = new Lazy<byte[]>(() => LoadEmbeddedResource("addon-fit.min.js"));
    }

    /// <summary>
    /// Returns true if this module can handle the given request path.
    /// </summary>
    public bool CanHandle(string path)
    {
        return path == "/" ||
               path == "/panel" ||
               path.StartsWith("/panel/", StringComparison.Ordinal) ||
               path.StartsWith("/api/", StringComparison.Ordinal);
    }

    /// <summary>
    /// Handle an HTTP request. Returns true if handled, false if the path is not recognized.
    /// </summary>
    public async Task<bool> HandleAsync(
        HttpListenerContext context,
        string path,
        Func<bool> isAuthorized,
        Func<object> buildStatusPayload,
        Func<List<Protocol.DeviceInfo>> getDeviceList,
        Func<string, List<Protocol.SessionInfo>?> getSessionsForDevice,
        Func<object> getPanelPairingPayload,
        Func<string?> getPanelQrPayload,
        Func<HttpListenerRequest, Task<object>> startPanelLogin,
        Func<string, object?> getPanelLoginStatus,
        Func<GroupInfo?> getGroupInfo,
        Func<List<Protocol.GroupMemberInfo>> getGroupMembers)
    {
        switch (path)
        {
            case "/":
            case "/panel":
                await ServePanelHtmlAsync(context.Response);
                return true;

            case "/panel/xterm.min.js":
                await ServeStaticAssetAsync(context.Response, _xtermJs.Value, "application/javascript");
                return true;

            case "/panel/xterm.min.css":
                await ServeStaticAssetAsync(context.Response, _xtermCss.Value, "text/css");
                return true;

            case "/panel/addon-fit.min.js":
                await ServeStaticAssetAsync(context.Response, _fitAddonJs.Value, "application/javascript");
                return true;
        }

        // Panel bootstrap endpoints (no auth)
        if (path.StartsWith("/api/panel/", StringComparison.Ordinal))
        {
            return await HandlePanelApiAsync(
                context,
                path,
                getPanelPairingPayload,
                getPanelQrPayload,
                startPanelLogin,
                getPanelLoginStatus);
        }

        // API endpoints require auth
        if (path.StartsWith("/api/", StringComparison.Ordinal))
        {
            if (!isAuthorized())
            {
                await WriteJsonAsync(context.Response, HttpStatusCode.Unauthorized, new
                {
                    type = "error",
                    code = "unauthorized",
                    message = "Missing or invalid relay token."
                });
                return true;
            }

            return await HandleApiAsync(
                context,
                path,
                buildStatusPayload,
                getDeviceList,
                getSessionsForDevice,
                getGroupInfo,
                getGroupMembers);
        }

        return false;
    }

    private async Task<bool> HandleApiAsync(
        HttpListenerContext context,
        string path,
        Func<object> buildStatusPayload,
        Func<List<Protocol.DeviceInfo>> getDeviceList,
        Func<string, List<Protocol.SessionInfo>?> getSessionsForDevice,
        Func<GroupInfo?> getGroupInfo,
        Func<List<Protocol.GroupMemberInfo>> getGroupMembers)
    {
        if (path == "/api/status")
        {
            await WriteJsonAsync(context.Response, HttpStatusCode.OK, buildStatusPayload());
            return true;
        }

        if (path == "/api/devices")
        {
            await WriteJsonAsync(context.Response, HttpStatusCode.OK, getDeviceList());
            return true;
        }

        if (path == "/api/group")
        {
            var group = getGroupInfo();
            if (group is null)
            {
                await WriteJsonAsync(context.Response, HttpStatusCode.NotFound, new
                {
                    type = "error",
                    code = "not_found",
                    message = "Group not initialized."
                });
                return true;
            }

            await WriteJsonAsync(context.Response, HttpStatusCode.OK, new
            {
                groupId = group.GroupId,
                serverDeviceId = group.ServerDeviceId,
                boundMobileId = group.BoundMobileId,
                createdAt = group.CreatedAt,
                members = getGroupMembers()
            });
            return true;
        }

        // /api/sessions/{deviceId}
        if (path.StartsWith("/api/sessions/", StringComparison.Ordinal))
        {
            var deviceId = path["/api/sessions/".Length..];
            if (string.IsNullOrWhiteSpace(deviceId))
            {
                await WriteJsonAsync(context.Response, HttpStatusCode.BadRequest, new
                {
                    type = "error",
                    code = "bad_request",
                    message = "Device ID is required."
                });
                return true;
            }

            var sessions = getSessionsForDevice(deviceId);
            if (sessions is null)
            {
                await WriteJsonAsync(context.Response, HttpStatusCode.NotFound, new
                {
                    type = "error",
                    code = "not_found",
                    message = "Device not found."
                });
                return true;
            }

            await WriteJsonAsync(context.Response, HttpStatusCode.OK, new
            {
                deviceId,
                sessions
            });
            return true;
        }

        await WriteJsonAsync(context.Response, HttpStatusCode.NotFound, new
        {
            type = "error",
            code = "not_found",
            message = "Unknown API endpoint."
        });
        return true;
    }

    private async Task<bool> HandlePanelApiAsync(
        HttpListenerContext context,
        string path,
        Func<object> getPanelPairingPayload,
        Func<string?> getPanelQrPayload,
        Func<HttpListenerRequest, Task<object>> startPanelLogin,
        Func<string, object?> getPanelLoginStatus)
    {
        if (path == "/api/panel/pairing")
        {
            await WriteJsonAsync(context.Response, HttpStatusCode.OK, getPanelPairingPayload());
            return true;
        }

        if (path == "/api/panel/qr.png")
        {
            var payload = getPanelQrPayload();
            if (string.IsNullOrWhiteSpace(payload))
            {
                await WriteJsonAsync(context.Response, HttpStatusCode.NotFound, new
                {
                    type = "error",
                    code = "not_found",
                    message = "QR payload not available."
                });
                return true;
            }

            var png = GetQrPngBytes(payload);
            await ServePngAsync(context.Response, png);
            return true;
        }

        if (path == "/api/panel/login/start")
        {
            var payload = await startPanelLogin(context.Request);
            await WriteJsonAsync(context.Response, HttpStatusCode.OK, payload);
            return true;
        }

        if (path.StartsWith("/api/panel/login/status/", StringComparison.Ordinal))
        {
            var requestId = path["/api/panel/login/status/".Length..];
            if (string.IsNullOrWhiteSpace(requestId))
            {
                await WriteJsonAsync(context.Response, HttpStatusCode.BadRequest, new
                {
                    type = "error",
                    code = "bad_request",
                    message = "Request ID is required."
                });
                return true;
            }

            var statusPayload = getPanelLoginStatus(requestId);
            if (statusPayload is null)
            {
                await WriteJsonAsync(context.Response, HttpStatusCode.NotFound, new
                {
                    type = "error",
                    code = "not_found",
                    message = "Login request not found."
                });
                return true;
            }

            await WriteJsonAsync(context.Response, HttpStatusCode.OK, statusPayload);
            return true;
        }

        await WriteJsonAsync(context.Response, HttpStatusCode.NotFound, new
        {
            type = "error",
            code = "not_found",
            message = "Unknown panel endpoint."
        });
        return true;
    }

    private byte[] GetQrPngBytes(string payload)
    {
        if (_cachedQrPayload == payload && _cachedQrPng is not null)
            return _cachedQrPng;

        _cachedQrPayload = payload;
        _cachedQrPng = _qrCodeService.Generate(payload, pixelsPerModule: 6);
        return _cachedQrPng;
    }

    private async Task ServePanelHtmlAsync(HttpListenerResponse response)
    {
        response.StatusCode = 200;
        response.ContentType = "text/html; charset=utf-8";
        response.ContentEncoding = Encoding.UTF8;
        response.ContentLength64 = _panelHtmlBytes.Length;
        AddCacheHeaders(response, maxAge: 0);
        await response.OutputStream.WriteAsync(_panelHtmlBytes);
        response.Close();
    }

    private static async Task ServeStaticAssetAsync(HttpListenerResponse response, byte[] data, string contentType)
    {
        response.StatusCode = 200;
        response.ContentType = contentType;
        response.ContentLength64 = data.Length;
        AddCacheHeaders(response, maxAge: 86400); // Cache static assets for 1 day
        await response.OutputStream.WriteAsync(data);
        response.Close();
    }

    private static async Task ServePngAsync(HttpListenerResponse response, byte[] data)
    {
        response.StatusCode = 200;
        response.ContentType = "image/png";
        response.ContentLength64 = data.Length;
        AddCacheHeaders(response, maxAge: 0);
        await response.OutputStream.WriteAsync(data);
        response.Close();
    }

    private static async Task WriteJsonAsync(HttpListenerResponse response, HttpStatusCode statusCode, object payload)
    {
        var json = JsonSerializer.Serialize(payload, JsonOptions);
        var bytes = Encoding.UTF8.GetBytes(json);
        response.StatusCode = (int)statusCode;
        response.ContentType = "application/json; charset=utf-8";
        response.ContentEncoding = Encoding.UTF8;
        response.ContentLength64 = bytes.Length;
        response.Headers.Set("Access-Control-Allow-Origin", "*");
        await response.OutputStream.WriteAsync(bytes);
        response.Close();
    }

    private static void AddCacheHeaders(HttpListenerResponse response, int maxAge)
    {
        if (maxAge > 0)
        {
            response.Headers.Set("Cache-Control", $"public, max-age={maxAge}");
        }
        else
        {
            response.Headers.Set("Cache-Control", "no-cache, no-store, must-revalidate");
        }
    }

    private static byte[] LoadEmbeddedResource(string name)
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream(name);
        if (stream is null)
            throw new InvalidOperationException(
                $"Embedded resource '{name}' not found. Available: {string.Join(", ", assembly.GetManifestResourceNames())}");

        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }
}
