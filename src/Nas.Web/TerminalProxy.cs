using System.Net.WebSockets;

namespace Nas.Web;

/// <summary>
/// Relie le WebSocket du navigateur (xterm.js) au terminal de l'agent (un shell root). Comme la console des machines virtuelles, la connexion
/// exige la session d'un administrateur (politique par défaut) et une origine identique à l'hôte. Ouverture et fermeture sont inscrites au journal.
/// </summary>
public static class TerminalProxy
{
    public static async Task<IResult> HandleAsync(HttpContext ctx, IConfiguration cfg, Data.Repo repo)
    {
        if (!ctx.WebSockets.IsWebSocketRequest) return Results.BadRequest();
        if (ctx.Request.Headers.Origin.FirstOrDefault() is { } origin
            && (!Uri.TryCreate(origin, UriKind.Absolute, out var o) || !string.Equals(o.Authority, ctx.Request.Host.Value, StringComparison.OrdinalIgnoreCase)))
            return Results.StatusCode(StatusCodes.Status403Forbidden);

        int.TryParse(ctx.Request.Query["cols"], out var cols);
        int.TryParse(ctx.Request.Query["rows"], out var rows);
        var uri = AgentTransport.WebSocketUri(cfg, $"/terminal?cols={Math.Clamp(cols, 1, 1000)}&rows={Math.Clamp(rows, 1, 500)}");
        using var invoker = new HttpMessageInvoker(AgentTransport.CreateHandler(cfg));
        using var agent = new ClientWebSocket();
        try { await agent.ConnectAsync(uri, invoker, ctx.RequestAborted); }
        catch (Exception e) when (e is WebSocketException or HttpRequestException or IOException)
        { return Results.Problem("Terminal unavailable.", statusCode: StatusCodes.Status502BadGateway); }

        var actor = ctx.User.Identity?.Name ?? "unknown";
        await repo.AuditAsync(actor, "Terminal opened", ctx.Connection.RemoteIpAddress?.ToString());
        using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted);

        static async Task Pump(WebSocket from, WebSocket to, CancellationToken ct)
        {
            var buf = new byte[16 * 1024];
            while (from.State == WebSocketState.Open)
            {
                var r = await from.ReceiveAsync(buf, ct);
                if (r.MessageType == WebSocketMessageType.Close) break;
                await to.SendAsync(buf.AsMemory(0, r.Count), r.MessageType, r.EndOfMessage, ct);
            }
        }
        var up = Task.Run(() => Pump(ws, agent, cts.Token), cts.Token);
        var down = Task.Run(() => Pump(agent, ws, cts.Token), cts.Token);

        try { await Task.WhenAny(up, down); }
        catch (Exception e) when (e is OperationCanceledException or IOException or WebSocketException or ObjectDisposedException) { }
        await cts.CancelAsync();
        try { await Task.WhenAll(up, down); }
        catch (Exception e) when (e is OperationCanceledException or IOException or WebSocketException or ObjectDisposedException) { }
        foreach (var s in new WebSocket[] { agent, ws })
            if (s.State is WebSocketState.Open or WebSocketState.CloseReceived)
                try { await s.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None); } catch (Exception e) when (e is WebSocketException or OperationCanceledException) { }
        await repo.AuditAsync(actor, "Terminal closed", null);
        return Results.Empty;
    }
}
