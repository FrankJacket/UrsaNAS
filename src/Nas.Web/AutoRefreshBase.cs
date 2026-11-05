using Microsoft.AspNetCore.Components;

namespace Nas.Web;

/// <summary>Page qui recharge ses données à intervalle régulier tant qu'elle est affichée.</summary>
public abstract class AutoRefreshBase : ComponentBase, IDisposable
{
    readonly CancellationTokenSource _cts = new();
    protected abstract Task LoadAsync();
    protected virtual TimeSpan Interval => TimeSpan.FromSeconds(10);

    protected override async Task OnInitializedAsync()
    {
        await LoadAsync();
        _ = LoopAsync();
    }

    async Task LoopAsync()
    {
        using var t = new PeriodicTimer(Interval);
        try
        {
            while (await t.WaitForNextTickAsync(_cts.Token))
            {
                await LoadAsync();
                await InvokeAsync(StateHasChanged);
            }
        }
        catch (OperationCanceledException) { }
    }

    public void Dispose() => _cts.Cancel();
}
