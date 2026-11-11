using System.Net;
using System.Net.Sockets;

namespace Nas.Web;

/// <summary>Comment l'interface joint l'agent : par le socket Unix local, ou par une URL en développement.</summary>
public static class AgentTransport
{
    public static SocketsHttpHandler CreateHandler(IConfiguration cfg)
    {
        var socketPath = cfg["Agent:SocketPath"];
        var handler = new SocketsHttpHandler();
        if (string.IsNullOrWhiteSpace(cfg["Agent:Url"]) && !string.IsNullOrWhiteSpace(socketPath))
        {
            handler.ConnectCallback = async (_, ct) =>
            {
                var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                try
                {
                    await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), ct);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            };
        }
        return handler;
    }

    /// <summary>Adresse WebSocket d'une page de l'agent (« ws://agent/… » par le socket, ou l'URL de développement).</summary>
    public static Uri WebSocketUri(IConfiguration cfg, string pathAndQuery)
    {
        var b = new UriBuilder(cfg["Agent:Url"] is { Length: > 0 } u ? new Uri(u) : new Uri("http://agent/"));
        b.Scheme = b.Scheme == "https" ? "wss" : "ws";
        var pq = pathAndQuery.Split('?', 2);
        b.Path = pq[0];
        b.Query = pq.Length > 1 ? pq[1] : "";
        return b.Uri;
    }
}
