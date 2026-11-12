namespace VortexErp.Core.Domain;

/// <summary>Categorías de activo fijo con su tasa anual máxima de depreciación por país (ERP-58).</summary>
public static class AssetCategories
{
    public const string Buildings = "buildings";
    public const string Vehicles = "vehicles";
    public const string Machinery = "machinery";
    public const string Computers = "computers";
    public const string Furniture = "furniture";
    public const string Other = "other";

    public static readonly string[] All = [Buildings, Vehicles, Machinery, Computers, Furniture, Other];

    /// <summary>
    /// Tasa anual (%) de depreciación lineal que admite el fisco: Perú (Ley del IR y su reglamento), México (LISR art. 34),
    /// Colombia (Decreto 1625), EE. UU. (vida MACRS en línea recta), España (tablas de amortización), Brasil (IN RFB 1700);
    /// el resto, valores habituales.
    /// </summary>
    public static decimal AnnualRate(string? country, string category) => ((country ?? "").ToUpperInvariant(), category) switch
    {
        ("PE", Buildings) => 5, ("PE", Vehicles) => 20, ("PE", Machinery) => 10, ("PE", Computers) => 25, ("PE", Furniture) => 10,
        ("MX", Buildings) => 5, ("MX", Vehicles) => 25, ("MX", Machinery) => 10, ("MX", Computers) => 30, ("MX", Furniture) => 10,
        ("CO", Buildings) => 2.22m, ("CO", Vehicles) => 10, ("CO", Machinery) => 10, ("CO", Computers) => 20, ("CO", Furniture) => 10,
        ("US", Buildings) => 2.564m, ("US", Vehicles) => 20, ("US", Machinery) => 14.29m, ("US", Computers) => 20, ("US", Furniture) => 14.29m,
        ("ES", Buildings) => 3, ("ES", Vehicles) => 16, ("ES", Machinery) => 12, ("ES", Computers) => 25, ("ES", Furniture) => 10,
        ("BR", Buildings) => 4, ("BR", Vehicles) => 20, ("BR", Machinery) => 10, ("BR", Computers) => 20, ("BR", Furniture) => 10,
        (_, Buildings) => 5, (_, Vehicles) => 20, (_, Machinery) => 10, (_, Computers) => 25, (_, Furniture) => 10,
        _ => 10,
    };

    /// <summary>Vida útil en meses que corresponde a la tasa máxima del país.</summary>
    public static int DefaultLifeMonths(string? country, string category) => (int)Math.Ceiling(1200m / AnnualRate(country, category));
}

/// <summary>
/// Activo fijo (ERP-58): se deprecia en línea recta cada mes completo desde el mes siguiente a su alta hasta su baja.
/// </summary>
/// <param name="FromPurchase">La compra ya se cargó como gasto (factura de proveedor): el alta la pasa al activo. Si no, entra
/// contra capital (saldo inicial o aporte).</param>
/// <param name="CostCenter">A él va la depreciación.</param>
public sealed record FixedAsset(Guid Id, string Code, string Name, string Category, DateOnly AcquiredOn, decimal Cost, decimal Residual,
    int UsefulLifeMonths, bool FromPurchase = true, string? CostCenter = null, DateOnly? DisposedOn = null, decimal? DisposalProceeds = null,
    string? Notes = null)
{
    public decimal Depreciable => Math.Max(0, Cost - Residual);

    /// <summary>Primer mes que se deprecia (el siguiente al del alta).</summary>
    public DateOnly FirstMonth => new DateOnly(AcquiredOn.Year, AcquiredOn.Month, 1).AddMonths(1);

    /// <summary>
    /// La cuota de cada mes completo hasta <paramref name="through"/> (y hasta la baja): la depreciable entre los meses de vida,
    /// redondeada; el último mes lleva el ajuste del redondeo.
    /// </summary>
    public IEnumerable<(DateOnly Month, decimal Amount)> Schedule(DateOnly through)
    {
        var accumulated = 0m;
        var monthly = UsefulLifeMonths > 0 ? Math.Round(Depreciable / UsefulLifeMonths, 2, MidpointRounding.AwayFromZero) : 0;
        var last = DisposedOn is { } d ? new DateOnly(d.Year, d.Month, 1).AddMonths(-1) : DateOnly.MaxValue;
        for (var i = 0; i < UsefulLifeMonths; i++)
        {
            var month = FirstMonth.AddMonths(i);
            if (month.AddMonths(1).AddDays(-1) > through || month > last)
                yield break;
            var amount = i == UsefulLifeMonths - 1 ? Depreciable - accumulated : Math.Min(monthly, Depreciable - accumulated);
            if (amount <= 0)
                yield break;
            accumulated += amount;
            yield return (month, amount);
        }
    }

    public decimal AccumulatedAt(DateOnly through) => Schedule(through).Sum(s => s.Amount);
}

/// <summary>Un activo con su depreciación acumulada y su valor en libros a una fecha.</summary>
public sealed record AssetRow(FixedAsset Asset, decimal Accumulated, decimal BookValue, decimal MonthlyAmount)
{
    public bool Disposed => Asset.DisposedOn is not null;
    public bool FullyDepreciated => !Disposed && Accumulated >= Asset.Depreciable;
}

/// <summary>
/// Presupuesto de una cuenta de ingresos o gastos (y, si se quiere, de un centro de costo) para un año, mes a mes (ERP-59).
/// Id determinista: año + cuenta + centro.
/// </summary>
public sealed record Budget(Guid Id, int Year, string AccountCode, string? CostCenter, IReadOnlyList<decimal> Months)
{
    public decimal Total => Months.Sum();
}

/// <summary>Presupuesto contra lo real de una cuenta en un rango de meses.</summary>
public sealed record BudgetLine(string Code, string Name, AccountType Type, string? CostCenter, decimal Budget, decimal Actual)
{
    /// <summary>Real menos presupuesto: en gastos, positivo es gastar de más; en ingresos, vender de más.</summary>
    public decimal Variance => Actual - Budget;

    public decimal? Percent => Budget != 0 ? Math.Round(100 * Actual / Budget, 1) : null;

    /// <summary>Si la diferencia es buena (vender más o gastar menos de lo previsto).</summary>
    public bool Favorable => Type == AccountType.Income ? Variance >= 0 : Variance <= 0;
}
