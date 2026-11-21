using Microsoft.AspNetCore.Components.Authorization;
using Nas.Contracts;
using Nas.Web.Data;

namespace Nas.Web;

/// <summary>
/// État de Docker partagé avec l'interface : le menu Docker n'apparaît que lorsque Docker est installé et que son service répond.
/// Inconnu (pas encore vérifié, ou agent injoignable au démarrage) compte comme « pas prêt ».
/// </summary>
public sealed class DockerAvailability
{
    public DockerStatus? Status { get; private set; }
    public bool Ready => Status is { State: DockerState.Ready };
    public event Action? Changed;

    public void Set(DockerStatus? status)
    {
        if (Equals(status, Status)) return;
        Status = status;
        Changed?.Invoke();
    }
}

/// <summary>Interroge l'agent toutes les 10 secondes : le menu suit donc l'installation, le démarrage ou l'arrêt de Docker.</summary>
public sealed class DockerMonitor(AgentClient agent, DockerAvailability state, IConfiguration cfg, ILogger<DockerMonitor> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(cfg.GetValue("Docker:IntervalSeconds", 10)));
        do
        {
            try { state.Set(await agent.GetDockerStatusAsync(ct)); }
            catch (Exception e) when (e is AgentException or HttpRequestException) { log.LogDebug(e, "Docker status skipped (agent unreachable)"); } // on garde le dernier état connu
            catch (Exception e) when (e is not OperationCanceledException) { log.LogError(e, "Docker status failed"); }
        }
        while (await WaitAsync(timer, ct));
    }

    static async Task<bool> WaitAsync(PeriodicTimer t, CancellationToken ct)
    {
        try { return await t.WaitForNextTickAsync(ct); }
        catch (OperationCanceledException) { return false; }
    }
}

/// <summary>Les opérations Docker faites depuis l'interface : valide, demande à l'agent d'agir, journalise avec le nom de l'administrateur connecté.</summary>
public class DockerOperations(AgentClient agent, Repo repo, AuthenticationStateProvider auth)
{
    async Task<string> ActorAsync() => (await auth.GetAuthenticationStateAsync()).User.Identity?.Name ?? "unknown";

    static readonly Dictionary<string, string> ContainerAudit = new()
    {
        ["start"] = "Docker container started", ["stop"] = "Docker container stopped",
        ["restart"] = "Docker container restarted", ["remove"] = "Docker container deleted",
    };

    static readonly Dictionary<string, string> StackAudit = new()
    {
        ["up"] = "Docker stack started", ["down"] = "Docker stack stopped",
        ["update"] = "Docker stack updated", ["remove"] = "Docker stack deleted",
    };

    public async Task ContainerAsync(string id, string name, string action)
    {
        await agent.ContainerActionAsync(id, action);
        await repo.AuditAsync(await ActorAsync(), ContainerAudit[action], name);
    }

    public async Task RunContainerAsync(RunContainerRequest r)
    {
        if (Rules.ValidateRunContainer(r) is { } e) throw new AgentException(e);
        await agent.RunContainerAsync(r);
        await repo.AuditAsync(await ActorAsync(), "Docker container created", string.IsNullOrEmpty(r.Name) ? r.Image : $"{r.Name} ({r.Image})");
    }

    public async Task UpdateContainerAsync(string id, string name, RunContainerRequest r)
    {
        if (Rules.ValidateRunContainer(r) is { } e) throw new AgentException(e);
        await agent.UpdateContainerAsync(id, r);
        await repo.AuditAsync(await ActorAsync(), "Docker container modified", name);
    }

    public async Task PullImageAsync(string image)
    {
        if (Rules.ValidateImageRef(image) is { } e) throw new AgentException(e);
        await agent.PullImageAsync(image);
        await repo.AuditAsync(await ActorAsync(), "Docker image downloaded", image);
    }

    public async Task RemoveImageAsync(string id, string label)
    {
        await agent.RemoveImageAsync(id);
        await repo.AuditAsync(await ActorAsync(), "Docker image deleted", label);
    }

    public async Task<string> PruneImagesAsync()
    {
        var summary = await agent.PruneImagesAsync();
        await repo.AuditAsync(await ActorAsync(), "Docker images cleaned", null);
        return summary;
    }

    public async Task CreateVolumeAsync(string name)
    {
        if (Rules.ValidateDockerName(name) is { } e) throw new AgentException(e);
        await agent.CreateDockerVolumeAsync(name);
        await repo.AuditAsync(await ActorAsync(), "Docker volume created", name);
    }

    public async Task RemoveVolumeAsync(string name)
    {
        await agent.RemoveDockerVolumeAsync(name);
        await repo.AuditAsync(await ActorAsync(), "Docker volume deleted", name);
    }

    public async Task CreateNetworkAsync(CreateNetworkRequest r)
    {
        if (Rules.ValidateCreateNetwork(r) is { } e) throw new AgentException(e);
        await agent.CreateDockerNetworkAsync(r);
        await repo.AuditAsync(await ActorAsync(), "Docker network created", $"{r.Name} ({r.Driver})");
    }

    static readonly Dictionary<string, string> ServiceAudit = new()
    {
        ["start"] = "Docker service started", ["stop"] = "Docker service stopped", ["restart"] = "Docker service restarted",
    };

    /// <summary>Démarre, arrête ou redémarre le service Docker (<paramref name="action"/> : start, stop, restart).</summary>
    public async Task ServiceAsync(string action)
    {
        await agent.DockerServiceAsync(action);
        await repo.AuditAsync(await ActorAsync(), ServiceAudit[action], null);
    }

    public async Task RemoveNetworkAsync(string name)
    {
        await agent.RemoveDockerNetworkAsync(name);
        await repo.AuditAsync(await ActorAsync(), "Docker network deleted", name);
    }

    public async Task SaveStackAsync(string name, string content)
    {
        if (Rules.ValidateStackName(name) is { } e1) throw new AgentException(e1);
        if (Rules.ValidateStackContent(content) is { } e2) throw new AgentException(e2);
        await agent.SaveStackAsync(name, content);
        await repo.AuditAsync(await ActorAsync(), "Docker stack saved", name);
    }

    public async Task StackAsync(string name, string action)
    {
        await agent.StackActionAsync(name, action);
        await repo.AuditAsync(await ActorAsync(), StackAudit[action], name);
    }
}
