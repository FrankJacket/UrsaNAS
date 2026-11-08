using System.Net.Sockets;
using System.Net.WebSockets;
using Nas.Contracts;

namespace Nas.Web;

/// <summary>
/// Relie le WebSocket du navigateur (noVNC) à l'écran VNC d'une machine, qui n'écoute que sur 127.0.0.1. La connexion exige la session
/// de l'administrateur (politique par défaut) et une origine identique à l'hôte, contre le détournement par un autre site.
/// </summary>
public static class VmConsoleProxy
{
    public static async Task<IResult> HandleAsync(string name, HttpContext ctx, AgentClient agent, string? vncHost = null)
    {
        if (!ctx.WebSockets.IsWebSocketRequest) return Results.BadRequest();
        if (Rules.ValidateVmName(name) is not null) return Results.BadRequest();
        if (ctx.Request.Headers.Origin.FirstOrDefault() is { } origin
            && (!Uri.TryCreate(origin, UriKind.Absolute, out var o) || !string.Equals(o.Authority, ctx.Request.Host.Value, StringComparison.OrdinalIgnoreCase)))
            return Results.StatusCode(StatusCodes.Status403Forbidden);

        int port;
        try { port = await agent.GetVmVncPortAsync(name, ctx.RequestAborted); }
        catch (Exception e) when (e is AgentException or HttpRequestException) { return Results.Problem(e.Message, statusCode: StatusCodes.Status502BadGateway); }

        using var tcp = new TcpClient { NoDelay = true };
        try { await tcp.ConnectAsync(vncHost ?? "127.0.0.1", port, ctx.RequestAborted); }
        catch (SocketException) { return Results.Problem("Console unavailable.", statusCode: StatusCodes.Status502BadGateway); }

        var sub = ctx.WebSockets.WebSocketRequestedProtocols.Contains("binary") ? "binary" : null;
        using var ws = await ctx.WebSockets.AcceptWebSocketAsync(sub);
        var stream = tcp.GetStream();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted);

        var up = Task.Run(async () =>
        {
            var buf = new byte[16 * 1024];
            while (ws.State == WebSocketState.Open)
            {
                var r = await ws.ReceiveAsync(buf, cts.Token);
                if (r.MessageType == WebSocketMessageType.Close) break;
                await stream.WriteAsync(buf.AsMemory(0, r.Count), cts.Token);
            }
        }, cts.Token);
        var down = Task.Run(async () =>
        {
            var buf = new byte[16 * 1024];
            int n;
            while ((n = await stream.ReadAsync(buf, cts.Token)) > 0)
                await ws.SendAsync(buf.AsMemory(0, n), WebSocketMessageType.Binary, true, cts.Token);
        }, cts.Token);

        try { await Task.WhenAny(up, down); }
        catch (Exception e) when (e is OperationCanceledException or IOException or WebSocketException or ObjectDisposedException) { }
        await cts.CancelAsync();
        try { await Task.WhenAll(up, down); }
        catch (Exception e) when (e is OperationCanceledException or IOException or WebSocketException or ObjectDisposedException) { }
        if (ws.State is WebSocketState.Open or WebSocketState.CloseReceived)
            try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None); } catch (WebSocketException) { }
        return Results.Empty;
    }
}
