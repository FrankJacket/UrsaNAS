using Nas.Contracts;

namespace Nas.Web;

/// <summary>Appels vers l'agent privilégié (socket Unix). Aucun accès direct au système depuis l'interface.</summary>
public class AgentClient(HttpClient http)
{
    public async Task<IReadOnlyList<DiskInfo>> GetDisksAsync(CancellationToken ct = default) =>
        await http.GetFromJsonAsync<List<DiskInfo>>("disks", ct) ?? [];
}
