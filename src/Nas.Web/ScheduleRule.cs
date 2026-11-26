using System.Globalization;
using Nas.Contracts;

namespace Nas.Web;

public static class RepeatKind
{
    public const string Once = "once", Hours = "hours", Daily = "daily", Weekly = "weekly", MonthlyDay = "monthly-day", MonthlyWeekday = "monthly-weekday";
    public static readonly IReadOnlyList<string> All = [Once, Hours, Daily, Weekly, MonthlyDay, MonthlyWeekday];
}

/// <summary>L'horaire d'une tâche planifiée, à l'heure du serveur. Seuls les champs utiles à <see cref="Repeat"/> comptent.</summary>
public sealed record ScheduleRule
{
    /// <summary>Valeur de <see cref="MonthDay"/> pour « dernier jour du mois ».</summary>
    public const int LastDay = 0;
    /// <summary>Valeur de <see cref="WeekRank"/> pour « dernier » (dernier dimanche du mois, par exemple).</summary>
    public const int LastRank = 5;

    public static readonly IReadOnlyList<int> HourSteps = [1, 2, 3, 4, 6, 8, 12];

    public string Repeat { get; init; } = RepeatKind.Daily;
    /// <summary>« HH:mm » ; ne sert pas à la répétition aux N heures.</summary>
    public string Time { get; init; } = "02:00";
    /// <summary>Exécution unique seulement.</summary>
    public DateOnly? Date { get; init; }
    public int EveryHours { get; init; } = 6;
    /// <summary>Minute de l'heure, pour la répétition aux N heures (comptées à partir de minuit).</summary>
    public int Minute { get; init; }
    /// <summary>Jours de la répétition hebdomadaire : bit n = <see cref="DayOfWeek"/> n.</summary>
    public int Days { get; init; } = 1 << (int)DayOfWeek.Sunday;
    /// <summary>1 à 31, ou <see cref="LastDay"/>. Un mois trop court prend son dernier jour.</summary>
    public int MonthDay { get; init; } = 1;
    /// <summary>1 à 4, ou <see cref="LastRank"/>.</summary>
    public int WeekRank { get; init; } = 1;
    public DayOfWeek WeekDay { get; init; } = DayOfWeek.Sunday;

    public bool HasDay(DayOfWeek d) => (Days & (1 << (int)d)) != 0;

    public static bool TryTime(string? text, out TimeOnly time) =>
        TimeOnly.TryParseExact(text, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out time);

    public string? Validate()
    {
        if (!RepeatKind.All.Contains(Repeat)) return Msg.F("Invalid schedule.");
        if (Repeat == RepeatKind.Hours)
            return HourSteps.Contains(EveryHours) && Minute is >= 0 and <= 59 ? null : Msg.F("Invalid schedule.");
        if (!TryTime(Time, out _)) return Msg.F("Invalid time.");
        return Repeat switch
        {
            RepeatKind.Once when Date is null => Msg.F("Choose a date."),
            RepeatKind.Weekly when (Days & 0x7F) == 0 => Msg.F("Choose at least one day."),
            RepeatKind.MonthlyDay when MonthDay is < 0 or > 31 => Msg.F("Invalid schedule."),
            RepeatKind.MonthlyWeekday when WeekRank is < 1 or > LastRank || !Enum.IsDefined(WeekDay) => Msg.F("Invalid schedule."),
            _ => null,
        };
    }

    /// <summary>
    /// La première exécution strictement après <paramref name="afterUtc"/> ; <c>null</c> s'il n'y en a plus (exécution unique passée) ou si l'horaire est invalide.
    /// Changement d'heure : une heure sautée s'exécute dès que l'horloge reprend, une heure répétée seulement à son premier passage.
    /// </summary>
    public DateTime? NextAfter(DateTime afterUtc, TimeZoneInfo zone)
    {
        if (Validate() is not null) return null;
        var after = DateTime.SpecifyKind(afterUtc, DateTimeKind.Utc);
        TryTime(Time, out var time);
        if (Repeat == RepeatKind.Once)
        {
            var only = ToUtc(Date!.Value.ToDateTime(time), zone);
            return only > after ? only : null;
        }

        // La veille aussi : une heure sautée près de minuit peut tomber le lendemain.
        var first = TimeZoneInfo.ConvertTimeFromUtc(after, zone).Date.AddDays(-1);
        for (var i = 0; i < 400; i++)
            foreach (var local in TimesOn(first.AddDays(i), time))
            {
                var utc = ToUtc(local, zone);
                if (utc > after) return utc;
            }
        return null;
    }

    IEnumerable<DateTime> TimesOn(DateTime day, TimeOnly time)
    {
        if (Repeat == RepeatKind.Hours)
        {
            for (var h = 0; h < 24; h += EveryHours) yield return day.AddHours(h).AddMinutes(Minute);
            yield break;
        }
        var length = DateTime.DaysInMonth(day.Year, day.Month);
        var matches = Repeat switch
        {
            RepeatKind.Daily => true,
            RepeatKind.Weekly => HasDay(day.DayOfWeek),
            RepeatKind.MonthlyDay => day.Day == (MonthDay == LastDay ? length : Math.Min(MonthDay, length)),
            RepeatKind.MonthlyWeekday => day.DayOfWeek == WeekDay && (WeekRank == LastRank ? day.Day + 7 > length : (day.Day - 1) / 7 + 1 == WeekRank),
            _ => false,
        };
        if (matches) yield return day.Add(time.ToTimeSpan());
    }

    static DateTime ToUtc(DateTime local, TimeZoneInfo zone)
    {
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        while (zone.IsInvalidTime(local)) local = local.AddMinutes(1);
        if (zone.IsAmbiguousTime(local)) // le plus grand décalage correspond au premier passage
            return DateTime.SpecifyKind(local - zone.GetAmbiguousTimeOffsets(local).Max(), DateTimeKind.Utc);
        return TimeZoneInfo.ConvertTimeToUtc(local, zone);
    }
}
