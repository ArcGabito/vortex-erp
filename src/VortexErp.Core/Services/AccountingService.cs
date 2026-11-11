using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using VortexApi.Client;
using VortexApi.Suite.Money;
using VortexErp.Core.Data;
using VortexErp.Core.Domain;
using VortexErp.Core.Integration;
using VortexErp.Core.Sync;

using static VortexErp.Core.Texts.CoreText;

namespace VortexErp.Core.Services;

/// <summary>
/// Ajustes contables: la cuenta de cada rol de los asientos automáticos, la cuenta contable de cada cuenta de tesorería y
/// hasta qué fecha está cerrado el periodo (no se aceptan documentos con fecha anterior).
/// </summary>
/// <param name="SiteCostCenters">Centro de costo de cada sede (ERP-50): a él van los ingresos de sus ventas y comprobantes.</param>
public sealed record AccountingSettings(IReadOnlyDictionary<string, string> Roles, IReadOnlyDictionary<Guid, string>? TreasuryLedger = null,
    DateOnly? ClosedThrough = null, IReadOnlyDictionary<Guid, string>? SiteCostCenters = null);

/// <summary>
/// Contabilidad (fase 5a): plan de cuentas por país, asientos automáticos desde los documentos (ventas, notas, cobros,
/// ventas de caja, compras, pagos, tesorería y diferencia de cambio realizada), asientos manuales con aprobación, libro
/// diario y mayor, balance de comprobación, estados financieros y cierre de periodo.
/// <para>
/// Los asientos automáticos no se replican: cada equipo los arma de los documentos (que sí se replican) con un Id
/// determinista por documento, así todos llegan a los mismos asientos y nadie contabiliza dos veces. Los manuales, el plan
/// y los ajustes son registros del ERP. Los asientos son inmutables: una corrección es otro asiento.
/// </para>
/// </summary>
public sealed class AccountingService(ErpDatabase db, IErpContext context, TimeProvider clock) : ErpService(db, context, clock)
{
    public const string SettingsKey = "accounting";

    // ---------- Plan de cuentas ----------

    public async Task<IReadOnlyList<LedgerAccount>> GetAccountsAsync(CancellationToken ct = default)
    {
        await using var c = await Db.OpenAsync(ct);
        return await AccountsAsync(c, null);
    }

    private static async Task<List<LedgerAccount>> AccountsAsync(SqliteConnection c, SqliteTransaction? tx)
    {
        var stored = await c.QueryAsync("SELECT * FROM ledger_accounts ORDER BY code",
            r => new LedgerAccount(r.Str("code"), r.Str("name"), Enum.Parse<AccountType>(r.Str("type")), r.Bool("is_active")), tx);
        if (stored.Count > 0)
            return stored;
        // Sin plan guardado rige el del país de la empresa.
        return (await TemplateAsync(c, tx)).Accounts.ToList();
    }

    private static async Task<ChartTemplates.Template> TemplateAsync(SqliteConnection c, SqliteTransaction? tx) =>
        ChartTemplates.For(await c.ScalarAsync<string?>("SELECT country_code FROM company WHERE id = 1", tx));

    public Task SaveAccountAsync(LedgerAccount account, CancellationToken ct = default)
    {
        RequirePermission(ErpPermissions.ManageAccounting);
        Require(!string.IsNullOrWhiteSpace(account.Code) && !string.IsNullOrWhiteSpace(account.Name), T("error.cuenta_contable_datos"));
        var clean = account with { Code = account.Code.Trim(), Name = account.Name.Trim() };
        return InTransactionAsync(async (c, tx, now) =>
        {
            await MaterializeChartAsync(c, tx, now);
            await PublishAsync(c, tx, now, ErpRecordKinds.LedgerAccount, clean.Code, clean);
            await AuditAsync(c, tx, now, "accounting.account_saved", "ledger_account", null, clean.Code, $"{clean.Name} ({clean.Type})");
            return 0;
        }, ct);
    }

    /// <summary>La primera vez que se cambia el plan, se guarda entero el del país (así se replica tal cual).</summary>
    private static async Task MaterializeChartAsync(SqliteConnection c, SqliteTransaction tx, DateTimeOffset now)
    {
        if (await c.ScalarAsync<long>("SELECT COUNT(*) FROM ledger_accounts", tx) > 0)
            return;
        foreach (var a in (await TemplateAsync(c, tx)).Accounts)
            await PublishAsync(c, tx, now, ErpRecordKinds.LedgerAccount, a.Code, a);
    }

    // ---------- Centros de costo (ERP-50) ----------

    public async Task<IReadOnlyList<CostCenter>> GetCostCentersAsync(CancellationToken ct = default)
    {
        await using var c = await Db.OpenAsync(ct);
        return await CostCentersAsync(c, null);
    }

    private static Task<List<CostCenter>> CostCentersAsync(SqliteConnection c, SqliteTransaction? tx) =>
        c.QueryAsync("SELECT * FROM cost_centers ORDER BY code", r => new CostCenter(r.Str("code"), r.Str("name"), r.Bool("is_active")), tx);

    public Task SaveCostCenterAsync(CostCenter center, CancellationToken ct = default)
    {
        RequirePermission(ErpPermissions.ManageAccounting);
        Require(!string.IsNullOrWhiteSpace(center.Code) && !string.IsNullOrWhiteSpace(center.Name), T("error.centro_costo_datos"));
        var clean = center with { Code = center.Code.Trim().ToUpperInvariant(), Name = center.Name.Trim() };
        Require(clean.Code.Length <= 20, T("error.centro_costo_datos"));
        return InTransactionAsync(async (c, tx, now) =>
        {
            await PublishAsync(c, tx, now, ErpRecordKinds.CostCenter, clean.Code, clean);
            await AuditAsync(c, tx, now, "accounting.cost_center_saved", "cost_center", null, clean.Code, clean.Name);
            return 0;
        }, ct);
    }

    /// <summary>Centro de costo de cada sede: a él van los ingresos de las ventas de caja y de los comprobantes de sus pedidos.</summary>
    public Task SaveSiteCostCentersAsync(IReadOnlyDictionary<Guid, string> map, CancellationToken ct = default)
    {
        RequirePermission(ErpPermissions.ManageAccounting);
        return InTransactionAsync(async (c, tx, now) =>
        {
            var centers = (await CostCentersAsync(c, tx)).Where(x => x.IsActive).Select(x => x.Code).ToHashSet();
            var clean = map.Where(m => !string.IsNullOrWhiteSpace(m.Value)).ToDictionary(m => m.Key, m => m.Value.Trim().ToUpperInvariant());
            Require(clean.Values.All(centers.Contains), T("error.centro_costo_no_existe", clean.Values.FirstOrDefault(v => !centers.Contains(v))));
            var current = await SettingsAsync(c, tx);
            await PublishAsync(c, tx, now, ErpRecordKinds.AccountingSettings, SettingsKey, current with { SiteCostCenters = clean });
            await AuditAsync(c, tx, now, "accounting.settings", "settings", null, null, string.Join(", ", clean.Select(m => $"{m.Key}={m.Value}")));
            return 0;
        }, ct);
    }

    /// <summary>Resultado por centro de costo (ingresos menos gastos del periodo; sin el cierre anual).</summary>
    public async Task<IReadOnlyList<CostCenterResult>> GetCostCenterResultsAsync(DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        await PostPendingAsync(ct);
        await using var c = await Db.OpenAsync(ct);
        var names = (await CostCentersAsync(c, null)).ToDictionary(x => x.Code, x => x.Name);
        var types = (await AccountsAsync(c, null)).ToDictionary(a => a.Code, a => a.Type);
        var rows = await c.QueryAsync("""
            SELECT l.cost_center, l.account_code, SUM(l.debit) AS debit, SUM(l.credit) AS credit FROM journal_lines l JOIN journal_entries e ON e.id = l.entry_id
            WHERE e.status = 'posted' AND e.date >= $from AND e.date <= $to AND e.source <> 'year_close' GROUP BY l.cost_center, l.account_code
            """, r => (Center: r.StrOrNull("cost_center"), Code: r.Str("account_code"), Debit: r.Dec("debit"), Credit: r.Dec("credit")), null,
            ("$from", from.ToString("yyyy-MM-dd")), ("$to", to.ToString("yyyy-MM-dd")));
        return rows.Where(r => types.GetValueOrDefault(r.Code) is AccountType.Income or AccountType.Expense)
            .GroupBy(r => r.Center)
            .Select(g => new CostCenterResult(g.Key, g.Key is { } k ? names.GetValueOrDefault(k, k) : T("contab.sin_centro"),
                g.Where(r => types[r.Code] == AccountType.Income).Sum(r => r.Credit - r.Debit),
                g.Where(r => types[r.Code] == AccountType.Expense).Sum(r => r.Debit - r.Credit)))
            .Where(r => r.Income != 0 || r.Expense != 0)
            .OrderBy(r => r.Code is null).ThenBy(r => r.Code).ToList();
    }

    // ---------- Ajustes y cierre ----------

    public async Task<AccountingSettings> GetSettingsAsync(CancellationToken ct = default)
    {
        await using var c = await Db.OpenAsync(ct);
        return await SettingsAsync(c, null);
    }

    internal static async Task<AccountingSettings> SettingsAsync(SqliteConnection c, SqliteTransaction? tx)
    {
        var template = await TemplateAsync(c, tx);
        var stored = await c.ScalarAsync<string?>("SELECT value FROM erp_settings WHERE key = $k", tx, ("$k", SettingsKey)) is { } json
            ? JsonSerializer.Deserialize<AccountingSettings>(json, VortexJson.Options)
            : null;
        var roles = new Dictionary<string, string>(template.Roles);
        foreach (var (role, code) in stored?.Roles ?? new Dictionary<string, string>())
            roles[role] = code;
        return new AccountingSettings(roles, stored?.TreasuryLedger ?? new Dictionary<Guid, string>(), stored?.ClosedThrough,
            stored?.SiteCostCenters ?? new Dictionary<Guid, string>());
    }

    public Task SaveSettingsAsync(IReadOnlyDictionary<string, string> roles, IReadOnlyDictionary<Guid, string> treasuryLedger, CancellationToken ct = default)
    {
        RequirePermission(ErpPermissions.ManageAccounting);
        return InTransactionAsync(async (c, tx, now) =>
        {
            var accounts = (await AccountsAsync(c, tx)).Select(a => a.Code).ToHashSet();
            var missing = roles.Values.Concat(treasuryLedger.Values).FirstOrDefault(code => !accounts.Contains(code));
            Require(missing is null, T("error.cuenta_contable_no_existe", missing));
            var current = await SettingsAsync(c, tx);
            await PublishAsync(c, tx, now, ErpRecordKinds.AccountingSettings, SettingsKey, current with { Roles = roles, TreasuryLedger = treasuryLedger });
            await AuditAsync(c, tx, now, "accounting.settings", "settings", null, null, string.Join(", ", roles.Select(r => $"{r.Key}={r.Value}")));
            return 0;
        }, ct);
    }

    /// <summary>
    /// Cierra el periodo hasta <paramref name="through"/> (inclusive): no se aceptan documentos ni asientos con fecha
    /// anterior. Reabrir (una fecha anterior o null) queda en la auditoría.
    /// </summary>
    public Task ClosePeriodAsync(DateOnly? through, CancellationToken ct = default)
    {
        RequirePermission(ErpPermissions.ManageAccounting);
        return InTransactionAsync(async (c, tx, now) =>
        {
            var current = await SettingsAsync(c, tx);
            await PublishAsync(c, tx, now, ErpRecordKinds.AccountingSettings, SettingsKey, current with { ClosedThrough = through });
            var reopen = current.ClosedThrough is { } before && (through is null || through < before);
            await AuditAsync(c, tx, now, reopen ? "accounting.period_reopened" : "accounting.period_closed", "settings", null,
                through?.ToString("yyyy-MM-dd"), current.ClosedThrough is { } b ? $"antes: {b:yyyy-MM-dd}" : null);
            return 0;
        }, ct);
    }

    /// <summary>Un documento o asiento con fecha en un periodo cerrado no se registra.</summary>
    internal static async Task RequireOpenPeriodAsync(SqliteConnection c, SqliteTransaction? tx, DateOnly date)
    {
        if ((await SettingsAsync(c, tx)).ClosedThrough is { } closed && date <= closed)
            throw new ErpValidationException(T("error.periodo_cerrado", closed.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture)));
    }

    // ---------- Asientos manuales ----------

    /// <summary>
    /// Asiento manual (ERP-47): cuadrado y con cuentas del plan. Quien puede aprobar lo contabiliza de una vez; los demás lo
    /// dejan por aprobar.
    /// </summary>
    public Task<Guid> CreateEntryAsync(DateOnly date, string description, IReadOnlyList<JournalLine> lines, CancellationToken ct = default)
    {
        RequirePermission(ErpPermissions.ManageAccounting);
        Require(!string.IsNullOrWhiteSpace(description), T("error.asiento_descripcion"));
        var clean = lines.Where(l => l.Debit != 0 || l.Credit != 0).Select(l => l with { AccountCode = l.AccountCode.Trim() }).ToList();
        Require(clean.Count >= 2 && clean.All(l => l.Debit >= 0 && l.Credit >= 0 && (l.Debit == 0 || l.Credit == 0)), T("error.asiento_lineas"));
        return InTransactionAsync(async (c, tx, now) =>
        {
            await RequireOpenPeriodAsync(c, tx, date);
            var accounts = (await AccountsAsync(c, tx)).Where(a => a.IsActive).Select(a => a.Code).ToHashSet();
            Require(clean.All(l => accounts.Contains(l.AccountCode)), T("error.cuenta_contable_no_existe", clean.FirstOrDefault(l => !accounts.Contains(l.AccountCode))?.AccountCode));
            var centers = (await CostCentersAsync(c, tx)).Where(x => x.IsActive).Select(x => x.Code).ToHashSet();
            clean = clean.Select(l => l with { CostCenter = string.IsNullOrWhiteSpace(l.CostCenter) ? null : l.CostCenter.Trim().ToUpperInvariant() }).ToList();
            Require(clean.All(l => l.CostCenter is null || centers.Contains(l.CostCenter)),
                T("error.centro_costo_no_existe", clean.FirstOrDefault(l => l.CostCenter is not null && !centers.Contains(l.CostCenter))?.CostCenter));
            var entry = new JournalEntry(Guid.NewGuid(), date, "manual", null, description.Trim(), clean,
                Context.Can(ErpPermissions.ApproveEntries) ? EntryStatuses.Posted : EntryStatuses.Pending, Context.UserName,
                Context.Can(ErpPermissions.ApproveEntries) ? Context.UserName : null);
            Require(entry.Balanced, T("error.asiento_descuadrado", entry.Debit, entry.Credit));
            await PublishAsync(c, tx, now, ErpRecordKinds.JournalEntry, entry.Id.ToString(), entry);
            await AuditAsync(c, tx, now, "accounting.entry_created", "journal_entry", entry.Id, entry.Status, $"{description.Trim()}: {entry.Debit:0.00}");
            return entry.Id;
        }, ct);
    }

    public Task ApproveEntryAsync(Guid id, bool approve, CancellationToken ct = default)
    {
        RequirePermission(ErpPermissions.ApproveEntries);
        return InTransactionAsync(async (c, tx, now) =>
        {
            var entry = await EntryAsync(c, tx, id);
            Require(entry.Status == EntryStatuses.Pending, T("error.asiento_no_pendiente"));
            if (approve)
                await RequireOpenPeriodAsync(c, tx, entry.Date);
            await PublishAsync(c, tx, now, ErpRecordKinds.JournalEntry, id.ToString(),
                entry with { Status = approve ? EntryStatuses.Posted : EntryStatuses.Rejected, ApprovedBy = Context.UserName });
            await AuditAsync(c, tx, now, approve ? "accounting.entry_approved" : "accounting.entry_rejected", "journal_entry", id, null, entry.Description);
            return 0;
        }, ct);
    }

    /// <summary>Corrige un asiento contabilizado con otro que lo revierte, en la fecha indicada (los asientos no se editan).</summary>
    public Task<Guid> ReverseEntryAsync(Guid id, DateOnly date, CancellationToken ct = default)
    {
        RequirePermission(ErpPermissions.ApproveEntries);
        return InTransactionAsync(async (c, tx, now) =>
        {
            await RequireOpenPeriodAsync(c, tx, date);
            var entry = await EntryAsync(c, tx, id);
            Require(entry.Status == EntryStatuses.Posted, T("error.asiento_no_contabilizado"));
            var reversal = new JournalEntry(Guid.NewGuid(), date, "manual", null, T("contab.reversion", entry.Description),
                entry.Lines.Select(l => l with { Debit = l.Credit, Credit = l.Debit }).ToList(), EntryStatuses.Posted, Context.UserName, Context.UserName, id);
            await PublishAsync(c, tx, now, ErpRecordKinds.JournalEntry, reversal.Id.ToString(), reversal);
            await AuditAsync(c, tx, now, "accounting.entry_reversed", "journal_entry", id, null, entry.Description);
            return reversal.Id;
        }, ct);
    }

    private static async Task<JournalEntry> EntryAsync(SqliteConnection c, SqliteTransaction? tx, Guid id) =>
        (await LoadEntriesAsync(c, tx, "WHERE e.id = $id", ("$id", id))).FirstOrDefault()
        ?? throw new ErpValidationException(T("error.asiento_no_existe"));

    // ---------- Asientos automáticos ----------

    /// <summary>
    /// Arma los asientos de los documentos que todavía no los tienen (ERP-46). Lo llaman los reportes; se puede llamar las
    /// veces que sea (Id determinista por documento). Un documento con fecha en un periodo cerrado entra el primer día abierto.
    /// </summary>
    public async Task<int> PostPendingAsync(CancellationToken ct = default)
    {
        await using var c = await Db.OpenAsync(ct);
        await using var tx = c.BeginTransaction();
        var created = 0;
        var s = await SettingsAsync(c, tx);
        var company = await Fx.CompanyCurrencyAsync(c, tx);
        var round = Currencies.Get(company);
        var accounts = await TreasuryService.AccountsAsync(c, tx);
        string Role(string role) => s.Roles.TryGetValue(role, out var code) ? code : role;
        string? Center(Guid? siteId) => siteId is { } site && s.SiteCostCenters is { } map && map.TryGetValue(site, out var cc) ? cc : null;
        DateOnly Open(DateOnly d) => s.ClosedThrough is { } closed && d <= closed ? closed.AddDays(1) : d;
        async Task<decimal> ToCompanyAsync(decimal amount, string currency, DateOnly on, decimal? known = null)
        {
            if (currency == company)
                return amount;
            var rate = known ?? await Fx.RateAsync(c, tx, currency, company, on) ?? 1;
            return round.Round(amount * rate);
        }
        string TreasuryCode(Guid? accountId, string method, string currency)
        {
            var account = accountId is { } id ? accounts.FirstOrDefault(a => a.Id == id) : TreasuryService.DefaultAccount(accounts, method, currency);
            if (account is not null && s.TreasuryLedger is { } map && map.TryGetValue(account.Id, out var mapped))
                return mapped;
            var cash = account?.Kind == TreasuryAccountKinds.Cash || (account is null && method == PaymentMethods.Cash);
            return Role(cash ? AccountRoles.Cash : AccountRoles.Bank);
        }
        async Task AddAsync(Guid id, DateOnly date, string source, string? reference, string description, List<JournalLine> lines)
        {
            lines = lines.Where(l => l.Debit != 0 || l.Credit != 0).Select(l => l.Debit < 0 || l.Credit < 0
                ? l with { Debit = Math.Max(0, l.Debit) + Math.Max(0, -l.Credit), Credit = Math.Max(0, l.Credit) + Math.Max(0, -l.Debit) }
                : l).ToList();
            if (lines.Count == 0)
                return;
            var diff = lines.Sum(l => l.Debit) - lines.Sum(l => l.Credit);
            if (diff != 0)
                lines.Add(diff > 0 ? new JournalLine(Role(AccountRoles.Other), 0, diff, T("contab.redondeo")) : new JournalLine(Role(AccountRoles.Other), -diff, 0, T("contab.redondeo")));
            var entry = new JournalEntry(id, Open(date), source, reference, description, lines);
            if (await InsertEntryAsync(c, tx, entry, null))
                created++;
        }
        static DateOnly Day(string iso) => DateOnly.Parse(iso[..10], CultureInfo.InvariantCulture);
        DateOnly Local(string time) => DateOnly.FromDateTime(DateTimeOffset.Parse(time, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal)
            .ToOffset(ErpClock.Offset).DateTime);
        async Task<bool> PostedAsync(Guid id) => await c.ScalarAsync<long>("SELECT COUNT(*) FROM journal_entries WHERE id = $id", tx, ("$id", id)) > 0;

        // Comprobantes del ERP (los de una venta de caja ya se contabilizaron con la venta).
        foreach (var inv in await c.QueryAsync("SELECT i.*, o.site_id FROM invoices i LEFT JOIN sales_orders o ON o.id = i.order_id WHERE i.sale_id IS NULL", r => new
        {
            Id = r.Guid("id"), Number = r.Str("number"), Issued = r.Str("issued_at"), Currency = r.Str("currency_code"), Subtotal = r.Dec("subtotal"),
            Tax = r.Dec("tax"), Total = r.Dec("total"), Status = r.Str("status"), Voided = r.StrOrNull("voided_at"), Rate = r.DecOrNull("exchange_rate"),
            Site = r.GuidOrNull("site_id"),
        }, tx))
        {
            var on = Local(inv.Issued);
            var total = await ToCompanyAsync(inv.Total, inv.Currency, on, inv.Rate);
            var tax = await ToCompanyAsync(inv.Tax, inv.Currency, on, inv.Rate);
            var id = DeterministicId.From("je|invoice", inv.Id);
            List<JournalLine> lines = [new(Role(AccountRoles.Receivables), total, 0), new(Role(AccountRoles.Revenue), 0, total - tax, null, Center(inv.Site)),
                new(Role(AccountRoles.TaxPayable), 0, tax)];
            if (!await PostedAsync(id))
                await AddAsync(id, on, "invoice", inv.Number, T("contab.venta", inv.Number), lines);
            if (inv.Status == "voided" && inv.Voided is { } voided && !await PostedAsync(DeterministicId.From("je|invoice_void", inv.Id)))
                await AddAsync(DeterministicId.From("je|invoice_void", inv.Id), Local(voided), "invoice_void", inv.Number, T("contab.anulacion", inv.Number),
                    lines.Select(l => l with { Debit = l.Credit, Credit = l.Debit }).ToList());
        }

        // Notas de crédito y débito, al tipo de cambio de su comprobante.
        foreach (var note in await c.QueryAsync("""
                     SELECT n.*, i.exchange_rate AS rate, COALESCE(o.site_id, s.site_id) AS site_id FROM invoice_notes n JOIN invoices i ON i.id = n.invoice_id
                     LEFT JOIN sales_orders o ON o.id = i.order_id LEFT JOIN pos_sales s ON s.id = i.sale_id
                     """,
                     r => new { Id = r.Guid("id"), Number = r.Str("number"), Kind = r.Str("kind"), Issued = r.Str("issued_at"), Currency = r.Str("currency_code"),
                         Tax = r.Dec("tax"), Total = r.Dec("total"), Rate = r.DecOrNull("rate"), Site = r.GuidOrNull("site_id") }, tx))
        {
            var id = DeterministicId.From("je|note", note.Id);
            if (await PostedAsync(id))
                continue;
            var on = Local(note.Issued);
            var total = await ToCompanyAsync(note.Total, note.Currency, on, note.Rate);
            var tax = await ToCompanyAsync(note.Tax, note.Currency, on, note.Rate);
            var credit = note.Kind == NoteKinds.Credit;
            List<JournalLine> lines = [new(Role(AccountRoles.Receivables), total, 0), new(Role(AccountRoles.Revenue), 0, total - tax, null, Center(note.Site)),
                new(Role(AccountRoles.TaxPayable), 0, tax)];
            await AddAsync(id, on, note.Kind, note.Number, T(credit ? "contab.nota_credito" : "contab.nota_debito", note.Number),
                credit ? lines.Select(l => l with { Debit = l.Credit, Credit = l.Debit }).ToList() : lines);
        }

        // Cobros: entra a caja o banco y baja la cuenta por cobrar al tipo del comprobante; la diferencia es diferencia de cambio realizada.
        foreach (var p in await c.QueryAsync("SELECT p.*, i.exchange_rate AS invoice_rate, i.currency_code AS invoice_currency FROM payments p LEFT JOIN invoices i ON i.id = p.invoice_id",
                     r => new { Id = r.Guid("id"), Number = r.Str("number"), At = r.Str("received_at"), Amount = r.Dec("amount"), Currency = r.Str("currency_code"),
                         Paid = r.DecOrNull("paid_amount"), PaidCurrency = r.StrOrNull("paid_currency"), Method = r.Str("method"),
                         Account = r.GuidOrNull("account_id"), InvoiceRate = r.DecOrNull("invoice_rate") }, tx))
        {
            var id = DeterministicId.From("je|payment", p.Id);
            if (await PostedAsync(id))
                continue;
            var on = Local(p.At);
            var receivable = await ToCompanyAsync(p.Amount, p.Currency, on, p.InvoiceRate);
            var cash = p.PaidCurrency == company && p.Paid is { } paid ? paid : await ToCompanyAsync(p.Amount, p.Currency, on);
            var fx = cash - receivable;
            await AddAsync(id, on, "payment", p.Number, T("contab.cobro", p.Number),
            [
                new(TreasuryCode(p.Account, p.Method, p.PaidCurrency ?? p.Currency), cash, 0), new(Role(AccountRoles.Receivables), 0, receivable),
                fx > 0 ? new(Role(AccountRoles.FxGain), 0, fx) : new(Role(AccountRoles.FxLoss), -fx, 0),
            ]);
        }

        // Ventas de caja del POS: lo que no es al crédito entra a la caja; las devoluciones y anulaciones se revierten.
        foreach (var sale in await c.QueryAsync("SELECT * FROM pos_sales", r => new
        {
            Id = r.Guid("id"), Number = r.Str("number"), At = r.Str("created_at"), Currency = r.Str("currency_code"), Total = r.Dec("total"),
            Tax = r.Dec("tax"), Credit = r.Dec("credit"), Returned = r.Dec("returned"), Status = r.Str("status"), Site = r.GuidOrNull("site_id"),
        }, tx))
        {
            var on = Local(sale.At);
            var total = await ToCompanyAsync(sale.Total, sale.Currency, on);
            var tax = await ToCompanyAsync(sale.Tax, sale.Currency, on);
            var credit = await ToCompanyAsync(sale.Credit, sale.Currency, on);
            List<JournalLine> lines = [new(Role(AccountRoles.Cash), total - credit, 0), new(Role(AccountRoles.Receivables), credit, 0),
                new(Role(AccountRoles.Revenue), 0, total - tax, null, Center(sale.Site)), new(Role(AccountRoles.TaxPayable), 0, tax)];
            var id = DeterministicId.From("je|pos_sale", sale.Id);
            if (!await PostedAsync(id))
                await AddAsync(id, on, "pos_sale", sale.Number, T("contab.venta_caja", sale.Number), lines);
            if (sale.Status == "voided" && !await PostedAsync(DeterministicId.From("je|pos_void", sale.Id)))
                await AddAsync(DeterministicId.From("je|pos_void", sale.Id), on, "pos_void", sale.Number, T("contab.anulacion", sale.Number),
                    lines.Select(l => l with { Debit = l.Credit, Credit = l.Debit }).ToList());
            if (sale.Returned > 0 && sale.Total > 0)
            {
                // Una devolución más (la suma crece): se contabiliza lo nuevo, con Id por el total devuelto hasta ahora.
                var key = DeterministicId.From("je|pos_return|" + sale.Returned.ToString("0.00", CultureInfo.InvariantCulture), sale.Id);
                if (!await PostedAsync(key))
                {
                    var before = await c.ScalarAsync<decimal>("SELECT COALESCE(SUM(l.debit), 0) FROM journal_entries e JOIN journal_lines l ON l.entry_id = e.id WHERE e.source = 'pos_return' AND e.source_ref = $n AND l.account_code IN ($rev, $tax)",
                        tx, ("$n", sale.Number), ("$rev", Role(AccountRoles.Revenue)), ("$tax", Role(AccountRoles.TaxPayable)));
                    var returned = await ToCompanyAsync(sale.Returned, sale.Currency, on) - before;
                    var returnedTax = round.Round(returned * sale.Tax / sale.Total);
                    if (returned > 0)
                        await AddAsync(key, DateOnly.FromDateTime(Clock.GetUtcNow().ToOffset(ErpClock.Offset).DateTime), "pos_return", sale.Number,
                            T("contab.devolucion_caja", sale.Number),
                            [new(Role(AccountRoles.Revenue), returned - returnedTax, 0, null, Center(sale.Site)), new(Role(AccountRoles.TaxPayable), returnedTax, 0),
                                new(Role(AccountRoles.Cash), 0, returned)]);
                }
            }
        }

        // Facturas de proveedor (aprobadas o retenidas) y su anulación.
        foreach (var inv in await c.QueryAsync("SELECT * FROM supplier_invoices", r => new
        {
            Id = r.Guid("id"), Number = r.Str("document_number"), Issued = r.Str("issued_on"), Currency = r.Str("currency_code"), Subtotal = r.Dec("subtotal"),
            Tax = r.Dec("tax"), Total = r.Dec("total"), Status = r.Str("status"), Updated = r.Str("updated_at"),
        }, tx))
        {
            var on = Day(inv.Issued);
            var total = await ToCompanyAsync(inv.Total, inv.Currency, on);
            var tax = await ToCompanyAsync(inv.Tax, inv.Currency, on);
            List<JournalLine> lines = [new(Role(AccountRoles.Purchases), total - tax, 0), new(Role(AccountRoles.TaxCredit), tax, 0), new(Role(AccountRoles.Payables), 0, total)];
            var id = DeterministicId.From("je|supplier_invoice", inv.Id);
            if (!await PostedAsync(id))
                await AddAsync(id, on, "supplier_invoice", inv.Number, T("contab.compra", inv.Number), lines);
            if (inv.Status == SupplierInvoiceStatuses.Void && !await PostedAsync(DeterministicId.From("je|supplier_void", inv.Id)))
                await AddAsync(DeterministicId.From("je|supplier_void", inv.Id), Local(inv.Updated), "supplier_void", inv.Number, T("contab.anulacion", inv.Number),
                    lines.Select(l => l with { Debit = l.Credit, Credit = l.Debit }).ToList());
        }

        // Pagos a proveedores: baja la cuenta por pagar al tipo de la factura y sale de caja o banco al del día del pago.
        foreach (var p in await c.QueryAsync("SELECT sp.*, si.currency_code, si.issued_on, si.document_number FROM supplier_payments sp JOIN supplier_invoices si ON si.id = sp.invoice_id",
                     r => new { Id = r.Guid("id"), Paid = r.Str("paid_on"), Amount = r.Dec("amount"), Currency = r.Str("currency_code"), Issued = r.Str("issued_on"),
                         Method = r.Str("method"), Account = r.GuidOrNull("account_id"), Number = r.Str("document_number") }, tx))
        {
            var id = DeterministicId.From("je|supplier_payment", p.Id);
            if (await PostedAsync(id))
                continue;
            var on = Day(p.Paid);
            var payable = await ToCompanyAsync(p.Amount, p.Currency, Day(p.Issued));
            var cash = await ToCompanyAsync(p.Amount, p.Currency, on);
            var fx = payable - cash;
            await AddAsync(id, on, "supplier_payment", p.Number, T("contab.pago", p.Number),
            [
                new(Role(AccountRoles.Payables), payable, 0), new(TreasuryCode(p.Account, p.Method, p.Currency), 0, cash),
                fx > 0 ? new(Role(AccountRoles.FxGain), 0, fx) : new(Role(AccountRoles.FxLoss), -fx, 0),
            ]);
        }

        // Movimientos propios de tesorería: transferencias y depósitos pasan por "efectivo en tránsito"; comisiones, intereses y ajustes.
        foreach (var m in await c.QueryAsync("SELECT * FROM treasury_movements", r => new
        {
            Id = r.Guid("id"), Account = r.Guid("account_id"), On = r.Str("on_date"), Amount = r.Dec("amount"), Kind = r.Str("kind"), Description = r.Str("description"),
        }, tx))
        {
            var id = DeterministicId.From("je|treasury", m.Id);
            if (await PostedAsync(id))
                continue;
            var on = Day(m.On);
            var account = accounts.FirstOrDefault(a => a.Id == m.Account);
            var amount = await ToCompanyAsync(m.Amount, account?.CurrencyCode ?? company, on);
            var counter = m.Kind switch
            {
                TreasuryMovementKinds.Fee => Role(AccountRoles.BankFees),
                TreasuryMovementKinds.Interest => Role(AccountRoles.InterestIncome),
                TreasuryMovementKinds.Adjustment => Role(AccountRoles.Other),
                _ => Role(AccountRoles.Transit),
            };
            var code = TreasuryCode(m.Account, account?.Kind == TreasuryAccountKinds.Cash ? PaymentMethods.Cash : PaymentMethods.Transfer, account?.CurrencyCode ?? company);
            await AddAsync(id, on, "treasury", null, m.Description, [new(code, amount, 0), new(counter, 0, amount)]);
        }

        await tx.CommitAsync(ct);
        return created;
    }

    // ---------- Libros y estados ----------

    /// <summary>Libro diario (ERP-48): los asientos contabilizados entre dos fechas (y, aparte, los manuales por aprobar).</summary>
    public async Task<IReadOnlyList<JournalEntry>> GetJournalAsync(DateOnly from, DateOnly to, bool includePending = false, CancellationToken ct = default)
    {
        await PostPendingAsync(ct);
        await using var c = await Db.OpenAsync(ct);
        return await LoadEntriesAsync(c, null, $"WHERE e.date >= $from AND e.date <= $to AND (e.status = 'posted'{(includePending ? " OR e.status = 'pending'" : "")})",
            ("$from", from.ToString("yyyy-MM-dd")), ("$to", to.ToString("yyyy-MM-dd")));
    }

    public async Task<IReadOnlyList<JournalEntry>> GetPendingEntriesAsync(CancellationToken ct = default)
    {
        await using var c = await Db.OpenAsync(ct);
        return await LoadEntriesAsync(c, null, "WHERE e.status = 'pending'");
    }

    /// <summary>Libro mayor de una cuenta: saldo anterior y cada movimiento con el saldo que deja.</summary>
    public async Task<IReadOnlyList<LedgerRow>> GetLedgerAsync(string code, DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        await PostPendingAsync(ct);
        await using var c = await Db.OpenAsync(ct);
        var account = (await AccountsAsync(c, null)).FirstOrDefault(a => a.Code == code);
        var sign = account?.DebitNature ?? true ? 1 : -1;
        var opening = await c.ScalarAsync<decimal>("""
            SELECT COALESCE(SUM(l.debit - l.credit), 0) FROM journal_lines l JOIN journal_entries e ON e.id = l.entry_id
            WHERE l.account_code = $code AND e.status = 'posted' AND e.date < $from
            """, null, ("$code", code), ("$from", from.ToString("yyyy-MM-dd"))) * sign;
        var rows = new List<LedgerRow> { new(from, Guid.Empty, "opening", null, T("contab.saldo_anterior"), 0, 0, opening) };
        var balance = opening;
        foreach (var r in await c.QueryAsync("""
            SELECT e.id, e.date, e.source, e.source_ref, e.description, l.debit, l.credit FROM journal_lines l JOIN journal_entries e ON e.id = l.entry_id
            WHERE l.account_code = $code AND e.status = 'posted' AND e.date >= $from AND e.date <= $to ORDER BY e.date, e.created_at, l.line_no
            """, r => (Id: r.Guid("id"), Date: DateOnly.Parse(r.Str("date"), CultureInfo.InvariantCulture), Source: r.Str("source"), Ref: r.StrOrNull("source_ref"),
                Description: r.Str("description"), Debit: r.Dec("debit"), Credit: r.Dec("credit")), null,
            ("$code", code), ("$from", from.ToString("yyyy-MM-dd")), ("$to", to.ToString("yyyy-MM-dd"))))
        {
            balance += (r.Debit - r.Credit) * sign;
            rows.Add(new LedgerRow(r.Date, r.Id, r.Source, r.Ref, r.Description, r.Debit, r.Credit, balance));
        }
        return rows;
    }

    /// <summary>Balance de comprobación hasta una fecha (desde el inicio o desde <paramref name="from"/>): sumas del debe y del haber por cuenta.</summary>
    public async Task<IReadOnlyList<TrialBalanceRow>> GetTrialBalanceAsync(DateOnly to, DateOnly? from = null, CancellationToken ct = default)
    {
        await PostPendingAsync(ct);
        await using var c = await Db.OpenAsync(ct);
        return await TrialAsync(c, null, to, from);
    }

    private static async Task<List<TrialBalanceRow>> TrialAsync(SqliteConnection c, SqliteTransaction? tx, DateOnly to, DateOnly? from,
        string? costCenter = null, bool excludeClosing = false)
    {
        var names = (await AccountsAsync(c, tx)).ToDictionary(a => a.Code);
        var rows = await c.QueryAsync("""
            SELECT l.account_code, SUM(l.debit) AS debit, SUM(l.credit) AS credit FROM journal_lines l JOIN journal_entries e ON e.id = l.entry_id
            WHERE e.status = 'posted' AND e.date <= $to AND ($from IS NULL OR e.date >= $from)
              AND ($center IS NULL OR l.cost_center = $center) AND ($noClose = 0 OR e.source <> 'year_close')
            GROUP BY l.account_code ORDER BY l.account_code
            """, r => (Code: r.Str("account_code"), Debit: r.Dec("debit"), Credit: r.Dec("credit")), tx,
            ("$to", to.ToString("yyyy-MM-dd")), ("$from", from?.ToString("yyyy-MM-dd")), ("$center", costCenter), ("$noClose", excludeClosing));
        return rows.Select(r => names.TryGetValue(r.Code, out var a)
            ? new TrialBalanceRow(r.Code, a.Name, a.Type, r.Debit, r.Credit)
            : new TrialBalanceRow(r.Code, "?", AccountType.Asset, r.Debit, r.Credit)).ToList();
    }

    // ---------- Cierre anual y apertura (ERP-51) ----------

    /// <summary>
    /// Cierra el ejercicio: un asiento al 31 de diciembre salda las cuentas de ingresos y gastos contra utilidades acumuladas y el
    /// periodo queda cerrado hasta esa fecha. Se puede volver a ejecutar (tras reabrir y corregir): el asiento, de Id fijo por año,
    /// se recalcula.
    /// </summary>
    public async Task<YearClose> CloseYearAsync(int year, CancellationToken ct = default)
    {
        RequirePermission(ErpPermissions.ApproveEntries);
        await PostPendingAsync(ct);
        return await InTransactionAsync(async (c, tx, now) =>
        {
            var end = new DateOnly(year, 12, 31);
            Require(DateOnly.FromDateTime(now.ToOffset(ErpClock.Offset).DateTime) > end, T("error.ejercicio_en_curso", year));
            var s = await SettingsAsync(c, tx);
            var trial = await TrialAsync(c, tx, end, new DateOnly(year, 1, 1), excludeClosing: true);
            var lines = new List<JournalLine>();
            foreach (var r in trial.Where(r => r.Type is AccountType.Income or AccountType.Expense && r.Balance != 0))
                lines.Add(r.Balance > 0 ? new JournalLine(r.Code, 0, r.Balance) : new JournalLine(r.Code, -r.Balance, 0));
            var result = trial.Where(r => r.Type == AccountType.Income).Sum(r => r.Credit - r.Debit)
                         - trial.Where(r => r.Type == AccountType.Expense).Sum(r => r.Debit - r.Credit);
            var retained = s.Roles.GetValueOrDefault(AccountRoles.RetainedEarnings, AccountRoles.RetainedEarnings);
            if (result != 0)
                lines.Add(result > 0 ? new JournalLine(retained, 0, result, T("contab.resultado_ejercicio", year))
                    : new JournalLine(retained, -result, 0, T("contab.resultado_ejercicio", year)));
            var id = DeterministicId.From($"je|year_close|{year}");
            var entry = new JournalEntry(id, end, EntrySources.YearClose, year.ToString(CultureInfo.InvariantCulture), T("contab.cierre_ejercicio", year),
                lines, EntryStatuses.Posted, Context.UserName, Context.UserName);
            await PublishAsync(c, tx, now, ErpRecordKinds.JournalEntry, id.ToString(), entry);
            if (s.ClosedThrough is not { } closed || closed < end)
                await PublishAsync(c, tx, now, ErpRecordKinds.AccountingSettings, SettingsKey, s with { ClosedThrough = end });
            await AuditAsync(c, tx, now, "accounting.year_closed", "journal_entry", id, year.ToString(CultureInfo.InvariantCulture),
                $"Resultado {result:0.00}");
            return new YearClose(year, result, id);
        }, ct);
    }

    /// <summary>
    /// Asiento de apertura del ejercicio: los saldos de activo, pasivo y patrimonio al cierre del anterior (con su resultado en
    /// utilidades acumuladas si aún no se cerró). No se guarda: el mayor ya los arrastra como saldo anterior.
    /// </summary>
    public async Task<JournalEntry> GetOpeningEntryAsync(int year, CancellationToken ct = default)
    {
        await PostPendingAsync(ct);
        await using var c = await Db.OpenAsync(ct);
        var s = await SettingsAsync(c, null);
        var trial = await TrialAsync(c, null, new DateOnly(year - 1, 12, 31), null);
        var lines = trial.Where(r => r.Type is AccountType.Asset or AccountType.Liability or AccountType.Equity && r.Balance != 0)
            .Select(r => r.Balance > 0 ? new JournalLine(r.Code, r.Balance, 0) : new JournalLine(r.Code, 0, -r.Balance)).ToList();
        var pending = trial.Where(r => r.Type == AccountType.Income).Sum(r => r.Credit - r.Debit)
                      - trial.Where(r => r.Type == AccountType.Expense).Sum(r => r.Debit - r.Credit);
        var retained = s.Roles.GetValueOrDefault(AccountRoles.RetainedEarnings, AccountRoles.RetainedEarnings);
        if (pending != 0)
            lines.Add(pending > 0 ? new JournalLine(retained, 0, pending, T("contab.resultados_no_cerrados")) : new JournalLine(retained, -pending, 0, T("contab.resultados_no_cerrados")));
        return new JournalEntry(DeterministicId.From($"je|opening|{year}"), new DateOnly(year, 1, 1), EntrySources.Opening, year.ToString(CultureInfo.InvariantCulture),
            T("contab.apertura_ejercicio", year), lines);
    }

    // ---------- Diferencia de cambio no realizada (ERP-52) ----------

    /// <summary>
    /// Revalúa al tipo de cambio de <paramref name="asOf"/> lo pendiente en otra moneda (comprobantes por cobrar y facturas de
    /// proveedor por pagar) contra el tipo con que se registró. Con <paramref name="post"/>, lo contabiliza en esa fecha y lo
    /// revierte al día siguiente (así la diferencia realizada del cobro o pago sigue saliendo contra el tipo original). Repetirlo
    /// para la misma fecha lo recalcula.
    /// </summary>
    public async Task<FxRevaluation> RevalueFxAsync(DateOnly asOf, bool post, CancellationToken ct = default)
    {
        if (post)
            RequirePermission(ErpPermissions.ManageAccounting);
        await using var c = await Db.OpenAsync(ct);
        await using var tx = c.BeginTransaction();
        var now = Clock.GetUtcNow();
        var company = await Fx.CompanyCurrencyAsync(c, tx);
        var round = Currencies.Get(company);
        var day = asOf.ToString("yyyy-MM-dd");
        var lines = new List<FxRevaluationLine>();
        async Task<decimal> RateAsync(string currency, DateOnly on) =>
            await Fx.RateAsync(c, tx, currency, company, on) ?? throw new ErpValidationException(T("error.sin_tipo_de_cambio_fecha", currency, on.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture)));

        foreach (var inv in await c.QueryAsync("""
            SELECT i.number, i.currency_code, i.issued_at, i.exchange_rate,
                   i.total + COALESCE((SELECT SUM(CASE n.kind WHEN 'debit' THEN n.total ELSE -n.total END) FROM invoice_notes n
                                       WHERE n.invoice_id = i.id AND substr(n.issued_at, 1, 10) <= $day), 0)
                           - COALESCE((SELECT SUM(p.amount) FROM payments p WHERE p.invoice_id = i.id AND substr(p.received_at, 1, 10) <= $day), 0) AS outstanding
            FROM invoices i
            WHERE i.currency_code <> $company AND substr(i.issued_at, 1, 10) <= $day
              AND (i.status <> 'voided' OR substr(i.voided_at, 1, 10) > $day)
            """, r => (Number: r.Str("number"), Currency: r.Str("currency_code"), Issued: r.Str("issued_at"), Rate: r.DecOrNull("exchange_rate"),
                Outstanding: r.Dec("outstanding")), tx, ("$day", day), ("$company", company)))
        {
            if (inv.Outstanding <= 0)
                continue;
            var booked = inv.Rate ?? await RateAsync(inv.Currency, DateOnly.Parse(inv.Issued[..10], CultureInfo.InvariantCulture));
            var rate = await RateAsync(inv.Currency, asOf);
            var adjustment = round.Round(inv.Outstanding * rate) - round.Round(inv.Outstanding * booked);
            if (adjustment != 0)
                lines.Add(new FxRevaluationLine("receivable", inv.Number, inv.Currency, inv.Outstanding, booked, rate, adjustment));
        }

        foreach (var inv in await c.QueryAsync("""
            SELECT si.document_number, si.currency_code, si.issued_on,
                   si.total - COALESCE((SELECT SUM(sp.amount) FROM supplier_payments sp WHERE sp.invoice_id = si.id AND sp.paid_on <= $day), 0) AS outstanding
            FROM supplier_invoices si WHERE si.currency_code <> $company AND si.issued_on <= $day AND si.status <> 'void'
            """, r => (Number: r.Str("document_number"), Currency: r.Str("currency_code"), Issued: r.Str("issued_on"), Outstanding: r.Dec("outstanding")), tx,
            ("$day", day), ("$company", company)))
        {
            if (inv.Outstanding <= 0)
                continue;
            var booked = await RateAsync(inv.Currency, DateOnly.Parse(inv.Issued, CultureInfo.InvariantCulture));
            var rate = await RateAsync(inv.Currency, asOf);
            var adjustment = round.Round(inv.Outstanding * rate) - round.Round(inv.Outstanding * booked);
            if (adjustment != 0)
                lines.Add(new FxRevaluationLine("payable", inv.Number, inv.Currency, inv.Outstanding, booked, rate, adjustment));
        }

        var receivables = lines.Where(l => l.Kind == "receivable").Sum(l => l.Adjustment);
        var payables = lines.Where(l => l.Kind == "payable").Sum(l => l.Adjustment);
        var result = new FxRevaluation(asOf, lines, receivables, payables, post);
        if (!post)
            return result;

        await RequireOpenPeriodAsync(c, tx, asOf);
        var s = await SettingsAsync(c, tx);
        string Role(string role) => s.Roles.GetValueOrDefault(role, role);
        var net = receivables - payables;
        var entryLines = new List<JournalLine>
        {
            receivables >= 0 ? new(Role(AccountRoles.Receivables), receivables, 0) : new(Role(AccountRoles.Receivables), 0, -receivables),
            payables >= 0 ? new(Role(AccountRoles.Payables), 0, payables) : new(Role(AccountRoles.Payables), -payables, 0),
            net >= 0 ? new(Role(AccountRoles.FxGain), 0, net) : new(Role(AccountRoles.FxLoss), -net, 0),
        }.Where(l => l.Debit != 0 || l.Credit != 0).ToList();
        var id = DeterministicId.From($"je|fx_reval|{day}");
        var reversalId = DeterministicId.From($"je|fx_reval_rev|{day}");
        var entry = new JournalEntry(id, asOf, EntrySources.FxRevaluation, day, T("contab.dif_cambio_no_realizada", asOf.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture)),
            entryLines, EntryStatuses.Posted, Context.UserName, Context.UserName);
        var reversal = new JournalEntry(reversalId, asOf.AddDays(1), EntrySources.FxRevaluationReversal, day,
            T("contab.reversion", entry.Description), entryLines.Select(l => l with { Debit = l.Credit, Credit = l.Debit }).ToList(),
            EntryStatuses.Posted, Context.UserName, Context.UserName, id);
        await PublishAsync(c, tx, now, ErpRecordKinds.JournalEntry, id.ToString(), entry);
        await PublishAsync(c, tx, now, ErpRecordKinds.JournalEntry, reversalId.ToString(), reversal);
        await AuditAsync(c, tx, now, "accounting.fx_revalued", "journal_entry", id, day, $"Resultado {net:0.00} ({lines.Count} documentos)");
        await tx.CommitAsync(ct);
        return result;
    }

    /// <summary>Estado de resultados (ERP-49): ingresos menos gastos del periodo.</summary>
    /// <param name="costCenter">Solo ese centro de costo (null: todos). El cierre anual no cuenta: el resultado es el de la operación.</param>
    public async Task<IncomeStatement> GetIncomeStatementAsync(DateOnly from, DateOnly to, string? costCenter = null, CancellationToken ct = default)
    {
        await PostPendingAsync(ct);
        await using var c = await Db.OpenAsync(ct);
        var trial = await TrialAsync(c, null, to, from, costCenter, excludeClosing: true);
        return new IncomeStatement(from, to, Section(trial, AccountType.Income), Section(trial, AccountType.Expense));
    }

    /// <summary>Balance general a una fecha: activo = pasivo + patrimonio + resultado acumulado no cerrado.</summary>
    public async Task<BalanceSheet> GetBalanceSheetAsync(DateOnly asOf, CancellationToken ct = default)
    {
        var trial = await GetTrialBalanceAsync(asOf, null, ct);
        var result = Section(trial, AccountType.Income).Total - Section(trial, AccountType.Expense).Total;
        return new BalanceSheet(asOf, Section(trial, AccountType.Asset), Section(trial, AccountType.Liability), Section(trial, AccountType.Equity), result);
    }

    private static StatementSection Section(IReadOnlyList<TrialBalanceRow> trial, AccountType type)
    {
        var debitNature = type is AccountType.Asset or AccountType.Expense;
        var accounts = trial.Where(r => r.Type == type).Select(r => (r.Code, r.Name, Amount: debitNature ? r.Debit - r.Credit : r.Credit - r.Debit))
            .Where(r => r.Amount != 0).ToList();
        return new StatementSection(type, accounts, accounts.Sum(a => a.Amount));
    }

    // ---------- Persistencia ----------

    private static async Task<List<JournalEntry>> LoadEntriesAsync(SqliteConnection c, SqliteTransaction? tx, string where, params (string, object?)[] args)
    {
        var entries = await c.QueryAsync($"SELECT e.* FROM journal_entries e {where} ORDER BY e.date, e.created_at", r => new JournalEntry(
            r.Guid("id"), DateOnly.Parse(r.Str("date"), CultureInfo.InvariantCulture), r.Str("source"), r.StrOrNull("source_ref"), r.Str("description"), [],
            r.Str("status"), r.StrOrNull("created_by"), r.StrOrNull("approved_by"), r.GuidOrNull("reverses")), tx, args);
        if (entries.Count == 0)
            return entries;
        var lines = (await c.QueryAsync("SELECT * FROM journal_lines WHERE entry_id IN (SELECT value FROM json_each($ids)) ORDER BY line_no",
                r => (Entry: r.Guid("entry_id"), Line: new JournalLine(r.Str("account_code"), r.Dec("debit"), r.Dec("credit"), r.StrOrNull("memo"),
                    r.StrOrNull("cost_center"))), tx,
                ("$ids", JsonSerializer.Serialize(entries.Select(e => e.Id.ToString())))))
            .ToLookup(x => x.Entry, x => x.Line);
        return entries.Select(e => e with { Lines = lines[e.Id].ToList() }).ToList();
    }

    /// <summary>Guarda el asiento (si ya está, con <paramref name="updatedAt"/> solo si es más nuevo). Devuelve si lo guardó.</summary>
    private static async Task<bool> InsertEntryAsync(SqliteConnection c, SqliteTransaction tx, JournalEntry e, DateTimeOffset? updatedAt)
    {
        var current = await c.ScalarAsync<string?>("SELECT updated_at FROM journal_entries WHERE id = $id", tx, ("$id", e.Id));
        if (current is not null && (updatedAt is null || string.CompareOrdinal(current, SqliteExtensions.Time(updatedAt.Value)) >= 0))
            return false;
        var at = updatedAt ?? DateTimeOffset.UnixEpoch;
        await c.ExecAsync("""
            INSERT OR REPLACE INTO journal_entries (id, date, source, source_ref, description, status, created_by, approved_by, reverses, created_at, updated_at)
            VALUES ($id, $date, $source, $ref, $description, $status, $by, $approved, $reverses, $created, $at)
            """, tx, ("$id", e.Id), ("$date", e.Date.ToString("yyyy-MM-dd")), ("$source", e.Source), ("$ref", e.SourceRef), ("$description", e.Description),
            ("$status", e.Status), ("$by", e.CreatedBy), ("$approved", e.ApprovedBy), ("$reverses", e.Reverses), ("$created", at), ("$at", at));
        await c.ExecAsync("DELETE FROM journal_lines WHERE entry_id = $id", tx, ("$id", e.Id));
        var no = 0;
        foreach (var l in e.Lines)
            await c.ExecAsync("""
                INSERT INTO journal_lines (entry_id, line_no, account_code, debit, credit, memo, cost_center)
                VALUES ($id, $no, $code, $debit, $credit, $memo, $center)
                """, tx, ("$id", e.Id), ("$no", ++no), ("$code", l.AccountCode), ("$debit", l.Debit), ("$credit", l.Credit), ("$memo", l.Memo),
                ("$center", l.CostCenter));
        return true;
    }

    private static async Task PublishAsync(SqliteConnection c, SqliteTransaction tx, DateTimeOffset now, string kind, string id, object data)
    {
        var json = JsonSerializer.SerializeToElement(data, VortexJson.Options);
        await ApplyAsync(c, tx, new ErpRecord(kind, id, now, json), force: true);
        await Outbox.EnqueueEventAsync(c, tx, now, SuiteEvents.RecordUpserted, new ErpRecord(kind, id, now, json), subjectType: kind, subjectId: id);
    }

    public static bool Handles(string kind) =>
        kind is ErpRecordKinds.LedgerAccount or ErpRecordKinds.AccountingSettings or ErpRecordKinds.JournalEntry or ErpRecordKinds.CostCenter;

    internal static async Task<bool> ApplyAsync(SqliteConnection c, SqliteTransaction tx, ErpRecord r, bool force = false)
    {
        var at = SqliteExtensions.Time(r.UpdatedAt);
        switch (r.Kind)
        {
            case ErpRecordKinds.LedgerAccount:
            {
                var a = r.Data.Deserialize<LedgerAccount>(VortexJson.Options)!;
                var current = await c.ScalarAsync<string?>("SELECT updated_at FROM ledger_accounts WHERE code = $code", tx, ("$code", a.Code));
                if (!force && current is not null && string.CompareOrdinal(current, at) >= 0)
                    return false;
                await c.ExecAsync("""
                    INSERT OR REPLACE INTO ledger_accounts (code, name, type, is_active, updated_at) VALUES ($code, $name, $type, $active, $at)
                    """, tx, ("$code", a.Code), ("$name", a.Name), ("$type", a.Type.ToString()), ("$active", a.IsActive), ("$at", r.UpdatedAt));
                return true;
            }
            case ErpRecordKinds.CostCenter:
            {
                var cc = r.Data.Deserialize<CostCenter>(VortexJson.Options)!;
                var current = await c.ScalarAsync<string?>("SELECT updated_at FROM cost_centers WHERE code = $code", tx, ("$code", cc.Code));
                if (!force && current is not null && string.CompareOrdinal(current, at) >= 0)
                    return false;
                await c.ExecAsync("""
                    INSERT OR REPLACE INTO cost_centers (code, name, is_active, updated_at) VALUES ($code, $name, $active, $at)
                    """, tx, ("$code", cc.Code), ("$name", cc.Name), ("$active", cc.IsActive), ("$at", r.UpdatedAt));
                return true;
            }
            case ErpRecordKinds.AccountingSettings:
            {
                var current = await c.ScalarAsync<string?>("SELECT updated_at FROM erp_settings WHERE key = $k", tx, ("$k", SettingsKey));
                if (!force && current is not null && string.CompareOrdinal(current, at) >= 0)
                    return false;
                await c.ExecAsync("""
                    INSERT INTO erp_settings (key, value, updated_at) VALUES ($k, $v, $at) ON CONFLICT (key) DO UPDATE SET value = $v, updated_at = $at
                    """, tx, ("$k", SettingsKey), ("$v", r.Data.GetRawText()), ("$at", r.UpdatedAt));
                return true;
            }
            case ErpRecordKinds.JournalEntry:
            {
                var e = r.Data.Deserialize<JournalEntry>(VortexJson.Options)!;
                if (force)
                    await c.ExecAsync("DELETE FROM journal_entries WHERE id = $id", tx, ("$id", e.Id));
                return await InsertEntryAsync(c, tx, e, r.UpdatedAt);
            }
            default:
                return false;
        }
    }
}
