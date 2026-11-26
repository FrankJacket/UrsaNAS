using MudBlazor;
using Nas.Web.Data;
using Nas.Web.Localization;

namespace Nas.Web;

/// <summary>Libellé et couleur d'un résultat de tâche planifiée, communs à la liste et à l'historique.</summary>
public static class TaskRunDisplay
{
    public static string Text(string status, Loc L) => status switch
    {
        TaskRunStatus.Running => L["In progress"],
        TaskRunStatus.Success => L["Success"],
        TaskRunStatus.Failed => L["Failed"],
        TaskRunStatus.Skipped => L["Skipped"],
        TaskRunStatus.Missed => L["Missed"],
        TaskRunStatus.Cancelled => L["Cancelled"],
        TaskRunStatus.Interrupted => L["Interrupted"],
        _ => status,
    };

    public static Color Color(string status) => status switch
    {
        TaskRunStatus.Success => MudBlazor.Color.Success,
        TaskRunStatus.Failed => MudBlazor.Color.Error,
        TaskRunStatus.Running => MudBlazor.Color.Info,
        TaskRunStatus.Missed or TaskRunStatus.Interrupted => MudBlazor.Color.Warning,
        _ => MudBlazor.Color.Default,
    };
}
