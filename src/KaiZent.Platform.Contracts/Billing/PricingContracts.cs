namespace KaiZent.Platform.Contracts.Billing;

/// <summary>
/// Preisstand des einen Abo-Pakets, gültig ab <see cref="ValidFrom"/> bis zum nächsten Stand. Beträge netto in Euro.
/// Die Einführungsphase gilt je Mandant ab seinem Abo-Beginn: zuerst <see cref="FreeMonths"/> kostenlose Monate, danach
/// <see cref="ReducedMonths"/> Monate zum <see cref="ReducedMonthlyNet"/>, dann der reguläre Monatspreis. Maßgeblich sind die Bedingungen
/// des Stands, der am Abo-Beginn gilt.
/// </summary>
public sealed record PackagePrice(Guid Id, DateOnly ValidFrom, decimal MonthlyNet, decimal YearlyNet, int FreeMonths, int ReducedMonths,
    decimal? ReducedMonthlyNet, string? Note, DateTimeOffset CreatedAt, string CreatedBy)
{
    /// <summary>Monatsbetrag im <paramref name="month"/>-ten Monat seit Abo-Beginn (1 = erster Monat), bei monatlicher Zahlung.</summary>
    public decimal MonthlyAmountInMonth(int month)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(month, 1);
        if (month <= FreeMonths) return 0m;
        if (month <= FreeMonths + ReducedMonths) return ReducedMonthlyNet ?? MonthlyNet;
        return MonthlyNet;
    }
}

/// <summary>Übersicht für die Plattform-Administration: der heute gültige Stand, geplante und frühere Stände.</summary>
public sealed record PackagePricing(DateOnly Today, PackagePrice? Current, IReadOnlyList<PackagePrice> Scheduled, IReadOnlyList<PackagePrice> Past);

/// <summary>Neuer Preisstand ab einem Datum (heute oder später). <see cref="ReducedMonthlyNet"/> nur zusammen mit <see cref="ReducedMonths"/>.</summary>
public sealed record CreatePackagePriceRequest(DateOnly ValidFrom, decimal MonthlyNet, decimal YearlyNet, int FreeMonths = 0, int ReducedMonths = 0,
    decimal? ReducedMonthlyNet = null, string? Note = null);

/// <summary>Regeln für Preisstände, gleich in App und API.</summary>
public static class PackagePriceRules
{
    public const int MaxIntroMonths = 36;
    public const decimal MaxAmount = 100_000m;
    public const int MaxNoteLength = 500;

    public static IEnumerable<(string Field, string Message)> Check(CreatePackagePriceRequest r, DateOnly today)
    {
        if (r.ValidFrom < today) yield return (nameof(r.ValidFrom), "Ein neuer Preis kann frühestens ab heute gelten.");
        if (!IsAmount(r.MonthlyNet) || r.MonthlyNet == 0) yield return (nameof(r.MonthlyNet), "Bitte einen Monatspreis über 0 € mit höchstens zwei Nachkommastellen angeben.");
        if (!IsAmount(r.YearlyNet) || r.YearlyNet == 0) yield return (nameof(r.YearlyNet), "Bitte einen Jahrespreis über 0 € mit höchstens zwei Nachkommastellen angeben.");
        if (r.FreeMonths < 0 || r.ReducedMonths < 0 || r.FreeMonths + r.ReducedMonths > MaxIntroMonths)
            yield return (nameof(r.FreeMonths), $"Kostenlose und reduzierte Monate zusammen: 0 bis {MaxIntroMonths}.");
        if (r.ReducedMonths > 0 && (r.ReducedMonthlyNet is not { } reduced || !IsAmount(reduced) || reduced >= r.MonthlyNet))
            yield return (nameof(r.ReducedMonthlyNet), "Der reduzierte Preis muss unter dem Monatspreis liegen.");
        if (r.ReducedMonths == 0 && r.ReducedMonthlyNet is not null)
            yield return (nameof(r.ReducedMonthlyNet), "Ein reduzierter Preis braucht mindestens einen reduzierten Monat.");
        if (r.Note is { Length: > MaxNoteLength }) yield return (nameof(r.Note), $"Die Notiz darf höchstens {MaxNoteLength} Zeichen haben.");
    }

    private static bool IsAmount(decimal value) => value >= 0 && value <= MaxAmount && decimal.Round(value, 2) == value;

    /// <summary>Der Stand, der an <paramref name="day"/> gilt: der jüngste mit Gültig-ab bis einschließlich dieses Tages.</summary>
    public static PackagePrice? ActiveOn(IEnumerable<PackagePrice> prices, DateOnly day) =>
        prices.Where(p => p.ValidFrom <= day).MaxBy(p => p.ValidFrom);
}
