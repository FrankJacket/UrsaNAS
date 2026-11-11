using System.Net.WebSockets;
using System.Text.Json;

namespace Nas.Agent.Services;

/// <summary>
/// Terminal sur la machine : un shell root sur un pseudo-terminal, relié à un WebSocket. Les trames binaires sont les octets du terminal
/// (dans les deux sens) ; une trame texte <c>{"type":"resize","cols":80,"rows":24}</c> redimensionne le terminal.
/// Seule l'interface web (groupe ursanas, socket local) peut l'atteindre, et elle exige un administrateur connecté.
/// </summary>
public static class TerminalEndpoint
{
    public static async Task<IResult> HandleAsync(HttpContext ctx, IConfiguration cfg)
    {
        if (!ctx.WebSockets.IsWebSocketRequest) return Results.BadRequest();
        if (!OperatingSystem.IsLinux() || cfg["URSANAS_FAKE"] == "1")
            return Results.Problem("The terminal is only available on the NAS itself.", statusCode: StatusCodes.Status501NotImplemented);

        int.TryParse(ctx.Request.Query["cols"], out var cols);
        int.TryParse(ctx.Request.Query["rows"], out var rows);
        using var session = PtySession.Start(File.Exists("/bin/bash") ? "/bin/bash" : "/bin/sh", ["-l"], cols > 0 ? cols : 80, rows > 0 ? rows : 24,
            Directory.Exists("/root") ? "/root" : "/",
            new Dictionary<string, string> { ["HOME"] = "/root", ["USER"] = "root", ["LOGNAME"] = "root", ["SHELL"] = "/bin/bash" });
        using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted);

        var fromBrowser = Task.Run(async () =>
        {
            var buf = new byte[16 * 1024];
            while (ws.State == WebSocketState.Open)
            {
                var msg = new MemoryStream();
                WebSocketReceiveResult r;
                do
                {
                    r = await ws.ReceiveAsync(buf, cts.Token);
                    if (r.MessageType == WebSocketMessageType.Close) return;
                    msg.Write(buf, 0, r.Count);
                } while (!r.EndOfMessage);
                if (r.MessageType == WebSocketMessageType.Text)
                {
                    if (TryParseResize(msg.ToArray(), out var c, out var rw)) session.Resize(c, rw);
                }
                else
                {
                    await session.Input.WriteAsync(msg.GetBuffer().AsMemory(0, (int)msg.Length), cts.Token);
                    await session.Input.FlushAsync(cts.Token);
                }
            }
        }, cts.Token);

        var toBrowser = Task.Run(async () =>
        {
            var buf = new byte[16 * 1024];
            while (true)
            {
                int n;
                try { n = await session.Output.ReadAsync(buf, cts.Token); }
                catch (IOException) { break; } // le shell s'est terminé : le côté maître du terminal renvoie EIO
                if (n == 0) break;
                await ws.SendAsync(buf.AsMemory(0, n), WebSocketMessageType.Binary, true, cts.Token);
            }
        }, cts.Token);

        try { await Task.WhenAny(fromBrowser, toBrowser); }
        catch (Exception e) when (e is OperationCanceledException or IOException or WebSocketException or ObjectDisposedException) { }
        session.Dispose(); // termine le shell, ce qui débloque la lecture en cours
        await cts.CancelAsync();
        try { await Task.WhenAll(fromBrowser, toBrowser); }
        catch (Exception e) when (e is OperationCanceledException or IOException or WebSocketException or ObjectDisposedException) { }
        if (ws.State is WebSocketState.Open or WebSocketState.CloseReceived)
            try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None); } catch (WebSocketException) { }
        return Results.Empty;
    }

    public static bool TryParseResize(byte[] json, out int cols, out int rows)
    {
        cols = rows = 0;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.GetProperty("type").GetString() != "resize") return false;
            cols = root.GetProperty("cols").GetInt32();
            rows = root.GetProperty("rows").GetInt32();
            return cols > 0 && rows > 0;
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or FormatException) { return false; }
    }
}
