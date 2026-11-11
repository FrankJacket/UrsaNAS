using MudBlazor;
using Nas.Contracts;
using Nas.Web.Components.Pages;
using Nas.Web.Data;
using Nas.Web.Localization;

namespace Nas.Web;

/// <summary>
/// Choisit un dossier d'un partage du NAS et renvoie son chemin réel sur la machine (ce que Docker attend pour un volume).
/// Le chemin réel est retrouvé par l'agent : l'interface ne connaît que les noms de partage.
/// </summary>
public class NasFolderPicker(IDialogService dialogs, AgentClient agent, Repo repo, Loc L)
{
    static readonly DialogOptions Small = new() { MaxWidth = MaxWidth.Small, FullWidth = true, CloseButton = true, BackdropClick = false };

    /// <summary>Renvoie le chemin choisi, ou null si l'utilisateur annule (ou s'il n'y a aucun partage).</summary>
    public async Task<string?> PickAsync()
    {
        var shares = await repo.GetSharesAsync();
        // Les stockages distants montés (SMB, NFS, OneDrive, Dropbox) servent aussi d'emplacement ; leur « partage » est leur point de montage.
        var remotes = new List<(string Value, string Label)>();
        try { remotes = (await agent.GetRemotesAsync()).Where(r => r.Mounted).Select(r => (r.MountPoint, L["Remote storage"] + " — " + r.Name)).ToList(); }
        catch (Exception e) when (e is AgentException or HttpRequestException) { } // facultatif
        if (shares.Count == 0 && remotes.Count == 0) return null;
        var share = shares.Count > 0 ? shares[0].Name : remotes[0].Value;
        if (shares.Count + remotes.Count > 1)
        {
            var pick = new DialogParameters<ShareSelectDialog> { { x => x.Shares, shares.Select(s => s.Name).ToList() }, { x => x.Remotes, remotes } };
            var first = await dialogs.ShowAsync<ShareSelectDialog>(L["Choose a share"], pick, Small);
            if (await first.Result is not { Canceled: false, Data: string chosen }) return null;
            share = chosen;
        }

        var parameters = new DialogParameters<FolderPickerDialog>
        {
            { x => x.Share, share }, { x => x.Message, L["Folder of the NAS"] }, { x => x.ButtonText, "Choose" },
        };
        var dialog = await dialogs.ShowAsync<FolderPickerDialog>(L["Choose a folder"], parameters, Small);
        if (await dialog.Result is not { Canceled: false, Data: FolderPick { Path: var relative } }) return null;

        var root = (remotes.Any(r => r.Value == share) ? share : await agent.GetShareRootAsync(share)).TrimEnd('/');
        return relative.Length == 0 ? root : $"{root}/{relative.Trim('/')}";
    }
}

/// <summary>Le choix fait dans la fenêtre de sélection de dossier : l'emplacement (partage ou disque monté) et le chemin dans celui-ci.</summary>
public sealed record FolderPick(string Location, string Path);
