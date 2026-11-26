using System.Globalization;
using Nas.Web.Localization;

namespace Nas.Web;

/// <summary>Un horaire en toutes lettres, dans la langue de l'interface.</summary>
public static class ScheduleDescription
{
    /// <summary>Ordre d'affichage des jours : la semaine commence le lundi.</summary>
    public static readonly IReadOnlyList<DayOfWeek> WeekDays =
        [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday];

    public static string Rank(int rank, Loc L) => rank switch
    {
        1 => L["first"],
        2 => L["second"],
        3 => L["third"],
        4 => L["fourth"],
        _ => L["last"],
    };

    public static string Describe(ScheduleRule r, Loc L)
    {
        if (r.Validate() is { } error) return L.Translate(error);
        var culture = CultureInfo.GetCultureInfo(L.Lang);
        var days = culture.DateTimeFormat;
        return r.Repeat switch
        {
            RepeatKind.Once => L["Once, on {0} at {1}", r.Date!.Value.ToString("d MMMM yyyy", culture), r.Time],
            RepeatKind.Hours when r.EveryHours == 1 => L["Every hour, at minute {0}", r.Minute.ToString("00")],
            RepeatKind.Hours => L["Every {0} hours, at minute {1}", r.EveryHours, r.Minute.ToString("00")],
            RepeatKind.Daily => L["Every day at {0}", r.Time],
            RepeatKind.Weekly => L["Every week on {0} at {1}", string.Join(", ", WeekDays.Where(r.HasDay).Select(days.GetDayName)), r.Time],
            RepeatKind.MonthlyDay when r.MonthDay == ScheduleRule.LastDay => L["On the last day of every month at {0}", r.Time],
            RepeatKind.MonthlyDay => L["Every month on day {0} at {1}", r.MonthDay, r.Time],
            _ => L["On the {0} {1} of every month at {2}", Rank(r.WeekRank, L), days.GetDayName(r.WeekDay), r.Time],
        };
    }
}
