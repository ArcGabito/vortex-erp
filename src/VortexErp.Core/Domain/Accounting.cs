namespace VortexErp.Core.Domain;

public enum AccountType
{
    Asset,
    Liability,
    Equity,
    Income,
    Expense,
}

/// <summary>Cuenta contable. Las de naturaleza deudora (activo, gasto) crecen con el debe; las demás, con el haber.</summary>
public sealed record LedgerAccount(string Code, string Name, AccountType Type, bool IsActive = true)
{
    public bool DebitNature => Type is AccountType.Asset or AccountType.Expense;
}

/// <summary>Para qué se usa una cuenta en los asientos automáticos.</summary>
public static class AccountRoles
{
    public const string Cash = "cash";
    public const string Bank = "bank";
    public const string Transit = "transit";
    public const string Receivables = "receivables";
    public const string Payables = "payables";
    public const string TaxPayable = "tax_payable";
    public const string TaxCredit = "tax_credit";
    public const string Revenue = "revenue";
    public const string Purchases = "purchases";
    public const string FxGain = "fx_gain";
    public const string FxLoss = "fx_loss";
    public const string BankFees = "bank_fees";
    public const string InterestIncome = "interest_income";
    public const string Other = "other";
    public const string RetainedEarnings = "retained_earnings";
    public const string Capital = "capital";

    /// <summary>Mercaderías (fase 6a): entra lo recibido y sale lo vendido, al costo promedio del WMS.</summary>
    public const string Inventory = "inventory";

    /// <summary>Costo de ventas: lo que sale del inventario al vender o despachar.</summary>
    public const string CostOfSales = "cost_of_sales";

    /// <summary>Variación de existencias: contrapartida de lo recibido (las compras ya se cargaron como gasto).</summary>
    public const string InventoryVariation = "inventory_variation";

    /// <summary>Mermas, faltantes y sobrantes de inventario (ajustes, conteos, devoluciones a proveedor).</summary>
    public const string InventoryAdjustment = "inventory_adjustment";

    public static readonly string[] All = [Cash, Bank, Transit, Receivables, Payables, TaxPayable, TaxCredit, Revenue, Purchases, FxGain, FxLoss,
        BankFees, InterestIncome, Other, RetainedEarnings, Capital, Inventory, CostOfSales, InventoryVariation, InventoryAdjustment];
}

/// <summary>
/// Planes de cuentas base por país (ERP-45): Perú (PCGE), México (código agrupador del SAT), Colombia (PUC), EE. UU. (US
/// GAAP), España (PGC) y Brasil; el resto, uno general en español. Son el punto de partida: se pueden agregar cuentas.
/// </summary>
public static class ChartTemplates
{
    public sealed record Template(string Name, IReadOnlyList<LedgerAccount> Accounts, IReadOnlyDictionary<string, string> Roles);

    public static Template For(string? country) => (country ?? "").ToUpperInvariant() switch
    {
        "PE" => Peru,
        "MX" => Mexico,
        "CO" => Colombia,
        "US" => UnitedStates,
        "ES" => Spain,
        "BR" => Brazil,
        _ => Generic,
    };

    private static Template Make(string name, (string Code, string Name, AccountType Type, string? Role)[] rows) =>
        new(name, rows.Select(r => new LedgerAccount(r.Code, r.Name, r.Type)).ToList(),
            rows.Where(r => r.Role is not null).ToDictionary(r => r.Role!, r => r.Code));

    private const AccountType A = AccountType.Asset, L = AccountType.Liability, E = AccountType.Equity, I = AccountType.Income, X = AccountType.Expense;

    public static readonly Template Peru = Make("PCGE (Perú)",
    [
        ("101", "Caja", A, AccountRoles.Cash), ("1041", "Cuentas corrientes operativas", A, AccountRoles.Bank),
        ("103", "Efectivo en tránsito", A, AccountRoles.Transit), ("1212", "Facturas, boletas y otros comprobantes por cobrar - emitidas en cartera", A, AccountRoles.Receivables),
        ("20111", "Mercaderías", A, AccountRoles.Inventory), ("6111", "Variación de existencias - Mercaderías", X, AccountRoles.InventoryVariation), ("6911", "Costo de ventas - Mercaderías", X, AccountRoles.CostOfSales), ("6593", "Mermas y faltantes de existencias", X, AccountRoles.InventoryAdjustment), ("40111", "IGV - Cuenta propia", L, AccountRoles.TaxPayable),
        ("40112", "IGV - Crédito fiscal", A, AccountRoles.TaxCredit), ("4212", "Facturas, boletas y otros comprobantes por pagar - emitidas", L, AccountRoles.Payables),
        ("501", "Capital social", E, AccountRoles.Capital), ("591", "Utilidades no distribuidas", E, AccountRoles.RetainedEarnings),
        ("6011", "Compras de mercaderías", X, AccountRoles.Purchases), ("6391", "Gastos bancarios", X, AccountRoles.BankFees),
        ("6761", "Pérdida por diferencia de cambio", X, AccountRoles.FxLoss), ("659", "Otros gastos de gestión", X, AccountRoles.Other),
        ("70121", "Venta de mercaderías - terceros", I, AccountRoles.Revenue), ("7721", "Intereses sobre depósitos", I, AccountRoles.InterestIncome),
        ("7761", "Ganancia por diferencia de cambio", I, AccountRoles.FxGain),
    ]);

    public static readonly Template Mexico = Make("Código agrupador SAT (México)",
    [
        ("101.01", "Caja y efectivo", A, AccountRoles.Cash), ("102.01", "Bancos nacionales", A, AccountRoles.Bank),
        ("102.03", "Efectivo en tránsito", A, AccountRoles.Transit), ("105.01", "Clientes nacionales", A, AccountRoles.Receivables),
        ("118.01", "IVA acreditable pagado", A, AccountRoles.TaxCredit), ("115.01", "Inventario", A, AccountRoles.Inventory), ("501.01", "Costo de venta", X, AccountRoles.CostOfSales), ("502.90", "Variación de inventario", X, AccountRoles.InventoryVariation), ("703.20", "Mermas de inventario", X, AccountRoles.InventoryAdjustment),
        ("201.01", "Proveedores nacionales", L, AccountRoles.Payables), ("208.01", "IVA trasladado cobrado", L, AccountRoles.TaxPayable),
        ("301.01", "Capital fijo", E, AccountRoles.Capital), ("304.01", "Utilidad de ejercicios anteriores", E, AccountRoles.RetainedEarnings),
        ("401.01", "Ventas y/o servicios gravados a la tasa general", I, AccountRoles.Revenue), ("502.01", "Compras nacionales", X, AccountRoles.Purchases),
        ("701.01", "Pérdida cambiaria", X, AccountRoles.FxLoss), ("701.10", "Comisiones bancarias", X, AccountRoles.BankFees),
        ("703.21", "Otros gastos", X, AccountRoles.Other), ("702.01", "Utilidad cambiaria", I, AccountRoles.FxGain),
        ("702.10", "Intereses a favor bancarios", I, AccountRoles.InterestIncome),
    ]);

    public static readonly Template Colombia = Make("PUC (Colombia)",
    [
        ("110505", "Caja general", A, AccountRoles.Cash), ("111005", "Bancos - moneda nacional", A, AccountRoles.Bank),
        ("112005", "Efectivo en tránsito", A, AccountRoles.Transit), ("130505", "Clientes nacionales", A, AccountRoles.Receivables),
        ("143501", "Mercancías no fabricadas por la empresa", A, AccountRoles.Inventory), ("613505", "Costo de ventas - comercio", X, AccountRoles.CostOfSales), ("620599", "Variación de inventarios", X, AccountRoles.InventoryVariation), ("539520", "Mermas de inventario", X, AccountRoles.InventoryAdjustment), ("240810", "IVA descontable", A, AccountRoles.TaxCredit),
        ("220505", "Proveedores nacionales", L, AccountRoles.Payables), ("240805", "IVA generado", L, AccountRoles.TaxPayable),
        ("310505", "Capital autorizado", E, AccountRoles.Capital), ("370505", "Utilidades acumuladas", E, AccountRoles.RetainedEarnings),
        ("413505", "Comercio al por mayor y al por menor", I, AccountRoles.Revenue), ("421005", "Intereses", I, AccountRoles.InterestIncome),
        ("421020", "Diferencia en cambio", I, AccountRoles.FxGain), ("620501", "Compras de mercancías", X, AccountRoles.Purchases),
        ("530505", "Gastos bancarios", X, AccountRoles.BankFees), ("530525", "Diferencia en cambio", X, AccountRoles.FxLoss),
        ("539595", "Otros gastos", X, AccountRoles.Other),
    ]);

    public static readonly Template UnitedStates = Make("US GAAP",
    [
        ("1000", "Cash on hand", A, AccountRoles.Cash), ("1010", "Checking account", A, AccountRoles.Bank),
        ("1050", "Cash in transit", A, AccountRoles.Transit), ("1200", "Accounts receivable", A, AccountRoles.Receivables),
        ("1300", "Inventory", A, AccountRoles.Inventory), ("5100", "Cost of goods sold", X, AccountRoles.CostOfSales), ("5010", "Inventory change", X, AccountRoles.InventoryVariation), ("5200", "Inventory shrinkage", X, AccountRoles.InventoryAdjustment), ("1400", "Input tax receivable", A, AccountRoles.TaxCredit),
        ("2000", "Accounts payable", L, AccountRoles.Payables), ("2200", "Sales tax payable", L, AccountRoles.TaxPayable),
        ("3000", "Owner's equity", E, AccountRoles.Capital), ("3200", "Retained earnings", E, AccountRoles.RetainedEarnings),
        ("4000", "Sales revenue", I, AccountRoles.Revenue), ("4800", "Interest income", I, AccountRoles.InterestIncome),
        ("4900", "Foreign exchange gain", I, AccountRoles.FxGain), ("5000", "Purchases", X, AccountRoles.Purchases),
        ("6100", "Bank fees", X, AccountRoles.BankFees), ("6900", "Foreign exchange loss", X, AccountRoles.FxLoss),
        ("6990", "Other expenses", X, AccountRoles.Other),
    ]);

    public static readonly Template Spain = Make("PGC (España)",
    [
        ("570", "Caja, euros", A, AccountRoles.Cash), ("572", "Bancos, cuentas corrientes", A, AccountRoles.Bank),
        ("5790", "Efectivo en tránsito", A, AccountRoles.Transit), ("430", "Clientes", A, AccountRoles.Receivables),
        ("300", "Mercaderías", A, AccountRoles.Inventory), ("610", "Variación de existencias de mercaderías", X, AccountRoles.InventoryVariation), ("6101", "Coste de las mercaderías vendidas", X, AccountRoles.CostOfSales), ("6102", "Mermas de existencias", X, AccountRoles.InventoryAdjustment), ("472", "Hacienda Pública, IVA soportado", A, AccountRoles.TaxCredit),
        ("400", "Proveedores", L, AccountRoles.Payables), ("477", "Hacienda Pública, IVA repercutido", L, AccountRoles.TaxPayable),
        ("100", "Capital social", E, AccountRoles.Capital), ("113", "Reservas voluntarias", E, AccountRoles.RetainedEarnings),
        ("700", "Ventas de mercaderías", I, AccountRoles.Revenue), ("769", "Otros ingresos financieros", I, AccountRoles.InterestIncome),
        ("768", "Diferencias positivas de cambio", I, AccountRoles.FxGain), ("600", "Compras de mercaderías", X, AccountRoles.Purchases),
        ("626", "Servicios bancarios y similares", X, AccountRoles.BankFees), ("668", "Diferencias negativas de cambio", X, AccountRoles.FxLoss),
        ("678", "Gastos excepcionales", X, AccountRoles.Other),
    ]);

    public static readonly Template Brazil = Make("Plano de contas (Brasil)",
    [
        ("1.1.1.01", "Caixa", A, AccountRoles.Cash), ("1.1.1.02", "Bancos conta movimento", A, AccountRoles.Bank),
        ("1.1.1.03", "Numerário em trânsito", A, AccountRoles.Transit), ("1.1.2.01", "Clientes", A, AccountRoles.Receivables),
        ("1.1.3.01", "Mercadorias", A, AccountRoles.Inventory), ("4.1.2.01", "Custo das mercadorias vendidas", X, AccountRoles.CostOfSales), ("4.1.1.02", "Variação de estoques", X, AccountRoles.InventoryVariation), ("4.2.9.02", "Perdas de estoque", X, AccountRoles.InventoryAdjustment), ("1.1.4.01", "Impostos a recuperar", A, AccountRoles.TaxCredit),
        ("2.1.1.01", "Fornecedores", L, AccountRoles.Payables), ("2.1.3.01", "Impostos a recolher", L, AccountRoles.TaxPayable),
        ("2.3.1.01", "Capital social", E, AccountRoles.Capital), ("2.3.3.01", "Lucros acumulados", E, AccountRoles.RetainedEarnings),
        ("3.1.1.01", "Receita de vendas", I, AccountRoles.Revenue), ("3.2.2.01", "Receitas financeiras", I, AccountRoles.InterestIncome),
        ("3.2.1.01", "Variação cambial ativa", I, AccountRoles.FxGain), ("4.1.1.01", "Compras de mercadorias", X, AccountRoles.Purchases),
        ("4.2.2.01", "Despesas bancárias", X, AccountRoles.BankFees), ("4.2.1.01", "Variação cambial passiva", X, AccountRoles.FxLoss),
        ("4.2.9.01", "Outras despesas", X, AccountRoles.Other),
    ]);

    public static readonly Template Generic = Make("General",
    [
        ("1101", "Caja", A, AccountRoles.Cash), ("1102", "Bancos", A, AccountRoles.Bank), ("1103", "Efectivo en tránsito", A, AccountRoles.Transit),
        ("1201", "Cuentas por cobrar comerciales", A, AccountRoles.Receivables), ("1301", "Inventario de mercaderías", A, AccountRoles.Inventory), ("5102", "Variación de inventarios", X, AccountRoles.InventoryVariation), ("5201", "Costo de ventas", X, AccountRoles.CostOfSales), ("6902", "Mermas de inventario", X, AccountRoles.InventoryAdjustment),
        ("1401", "Impuesto al valor agregado - crédito fiscal", A, AccountRoles.TaxCredit), ("2101", "Cuentas por pagar comerciales", L, AccountRoles.Payables),
        ("2201", "Impuesto al valor agregado por pagar", L, AccountRoles.TaxPayable), ("3101", "Capital", E, AccountRoles.Capital),
        ("3201", "Resultados acumulados", E, AccountRoles.RetainedEarnings), ("4101", "Ventas", I, AccountRoles.Revenue),
        ("4201", "Ingresos financieros", I, AccountRoles.InterestIncome), ("4301", "Ganancia por diferencia de cambio", I, AccountRoles.FxGain),
        ("5101", "Compras", X, AccountRoles.Purchases), ("6101", "Gastos bancarios", X, AccountRoles.BankFees),
        ("6201", "Pérdida por diferencia de cambio", X, AccountRoles.FxLoss), ("6901", "Otros gastos", X, AccountRoles.Other),
    ]);
}

/// <param name="CostCenter">Centro de costo (ERP-50): en los asientos manuales lo elige el contador; en los automáticos sale de la sede.</param>
public sealed record JournalLine(string AccountCode, decimal Debit, decimal Credit, string? Memo = null, string? CostCenter = null);

/// <summary>Centro de costo (ERP-50): tienda, área o proyecto al que se imputan ingresos y gastos.</summary>
public sealed record CostCenter(string Code, string Name, bool IsActive = true);

/// <summary>Resultado de un centro de costo en un periodo (sin centro: <c>Code</c> null).</summary>
public sealed record CostCenterResult(string? Code, string Name, decimal Income, decimal Expense)
{
    public decimal Result => Income - Expense;
}

/// <summary>Orígenes de los asientos que arma la contabilidad misma (no un documento).</summary>
public static class EntrySources
{
    public const string Manual = "manual";

    /// <summary>Cierre anual: lleva el resultado del ejercicio a utilidades acumuladas (ERP-51).</summary>
    public const string YearClose = "year_close";

    /// <summary>Diferencia de cambio no realizada a una fecha y su reversión al día siguiente (ERP-52).</summary>
    public const string FxRevaluation = "fx_revaluation";
    public const string FxRevaluationReversal = "fx_revaluation_reversal";

    /// <summary>Asiento de apertura del ejercicio (se arma de los saldos al cierre; no se guarda).</summary>
    public const string Opening = "opening";
}

/// <summary>Un documento pendiente en otra moneda revaluado al tipo de cambio de la fecha.</summary>
/// <param name="Kind"><c>receivable</c> (comprobante por cobrar) o <c>payable</c> (factura de proveedor por pagar).</param>
/// <param name="Adjustment">En la moneda de la empresa: positivo, la cuenta crece (más por cobrar o más por pagar).</param>
public sealed record FxRevaluationLine(string Kind, string Number, string CurrencyCode, decimal Outstanding, decimal BookedRate, decimal Rate,
    decimal Adjustment);

/// <summary>Diferencia de cambio no realizada a una fecha: el detalle por documento y el efecto neto en resultados.</summary>
public sealed record FxRevaluation(DateOnly AsOf, IReadOnlyList<FxRevaluationLine> Lines, decimal Receivables, decimal Payables, bool Posted)
{
    /// <summary>Ganancia (positiva) o pérdida (negativa): crecer lo por cobrar gana, crecer lo por pagar pierde.</summary>
    public decimal Result => Receivables - Payables;
}

/// <summary>Cierre de un ejercicio: el resultado que pasó a utilidades acumuladas.</summary>
public sealed record YearClose(int Year, decimal Result, Guid EntryId);

public static class EntryStatuses
{
    /// <summary>Asiento manual que espera la aprobación de un contador.</summary>
    public const string Pending = "pending";
    public const string Posted = "posted";
    public const string Rejected = "rejected";
}

/// <summary>Asiento: inmutable una vez contabilizado (una corrección es otro asiento). <c>Source</c>: de qué documento sale.</summary>
public sealed record JournalEntry(Guid Id, DateOnly Date, string Source, string? SourceRef, string Description, IReadOnlyList<JournalLine> Lines,
    string Status = EntryStatuses.Posted, string? CreatedBy = null, string? ApprovedBy = null, Guid? Reverses = null)
{
    public decimal Debit => Lines.Sum(l => l.Debit);
    public decimal Credit => Lines.Sum(l => l.Credit);
    public bool Balanced => Math.Abs(Debit - Credit) < 0.005m;
}

public sealed record LedgerRow(DateOnly Date, Guid EntryId, string Source, string? SourceRef, string Description, decimal Debit, decimal Credit, decimal Balance);

public sealed record TrialBalanceRow(string Code, string Name, AccountType Type, decimal Debit, decimal Credit)
{
    public decimal Balance => Debit - Credit;
}

/// <summary>Una sección del estado financiero (ingresos, gastos, activo…) con sus cuentas y su total.</summary>
public sealed record StatementSection(AccountType Type, IReadOnlyList<(string Code, string Name, decimal Amount)> Accounts, decimal Total);

public sealed record IncomeStatement(DateOnly From, DateOnly To, StatementSection Income, StatementSection Expenses)
{
    public decimal Result => Income.Total - Expenses.Total;
}

public sealed record BalanceSheet(DateOnly AsOf, StatementSection Assets, StatementSection Liabilities, StatementSection Equity, decimal CurrentResult)
{
    public bool Balanced => Math.Abs(Assets.Total - (Liabilities.Total + Equity.Total + CurrentResult)) < 0.01m;
}
