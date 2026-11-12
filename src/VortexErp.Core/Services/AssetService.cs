using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using VortexApi.Client;
using VortexErp.Core.Data;
using VortexErp.Core.Domain;
using VortexErp.Core.Integration;
using VortexErp.Core.Sync;

using static VortexErp.Core.Texts.CoreText;

namespace VortexErp.Core.Services;

/// <summary>
/// Activos fijos y presupuestos (fase 6b). Los activos y presupuestos son registros del ERP (se replican); los asientos de
/// alta, depreciación mensual y baja los arma cada equipo con Id determinista, como los demás automáticos.
/// </summary>
public sealed class AssetService(ErpDatabase db, IErpContext context, TimeProvider clock) : ErpService(db, context, clock)
{
    // ---------- Activos fijos (ERP-58) ----------

    public async Task<IReadOnlyList<AssetRow>> GetAssetsAsync(DateOnly? asOf = null, bool includeDisposed = true, CancellationToken ct = default)
    {
        await using var c = await Db.OpenAsync(ct);
        var day = asOf ?? Today(Clock.GetUtcNow());
        return (await AssetsAsync(c, null))
            .Where(a => includeDisposed || a.DisposedOn is null)
            .Select(a =>
            {
                var acc = a.AccumulatedAt(day);
                var next = a.Schedule(DateOnly.MaxValue).Skip(a.Schedule(day).Count()).FirstOrDefault().Amount;
                return new AssetRow(a, acc, a.DisposedOn is null ? a.Cost - acc : 0, a.DisposedOn is null ? next : 0);
            })
            .OrderBy(r => r.Asset.Code, StringComparer.OrdinalIgnoreCase).ToList();
    }

    internal static Task<List<FixedAsset>> AssetsAsync(SqliteConnection c, SqliteTransaction? tx) =>
        c.QueryAsync("SELECT data FROM fixed_assets", r => JsonSerializer.Deserialize<FixedAsset>(r.Str("data"), VortexJson.Options)!, tx);

    /// <summary>Vida útil sugerida (meses) para la categoría según el país de la empresa.</summary>
    public async Task<int> SuggestedLifeAsync(string category, CancellationToken ct = default)
    {
        await using var c = await Db.OpenAsync(ct);
        return AssetCategories.DefaultLifeMonths(await c.ScalarAsync<string?>("SELECT country_code FROM company WHERE id = 1"), category);
    }

    public Task<Guid> SaveAssetAsync(FixedAsset asset, CancellationToken ct = default)
    {
        RequirePermission(ErpPermissions.ManageAccounting);
        Require(!string.IsNullOrWhiteSpace(asset.Code) && !string.IsNullOrWhiteSpace(asset.Name), T("error.activo_datos"));
        Require(AssetCategories.All.Contains(asset.Category), T("error.activo_categoria"));
        Require(asset.Cost > 0 && asset.Residual >= 0 && asset.Residual < asset.Cost, T("error.activo_costo"));
        Require(asset.UsefulLifeMonths is > 0 and <= 1200, T("error.activo_vida"));
        return InTransactionAsync(async (c, tx, now) =>
        {
            var existing = (await AssetsAsync(c, tx)).FirstOrDefault(a => a.Id == asset.Id);
            Require(existing?.DisposedOn is null, T("error.activo_dado_de_baja"));
            var country = await c.ScalarAsync<string?>("SELECT country_code FROM company WHERE id = 1", tx);
            var maxRate = AssetCategories.AnnualRate(country, asset.Category);
            // La vida útil no puede ser más corta que la que permite la tasa máxima del país (más depreciación de la deducible).
            Require(asset.UsefulLifeMonths >= (int)Math.Floor(1200m / maxRate), T("error.activo_tasa_maxima", maxRate.ToString("0.##", CultureInfo.InvariantCulture)));
            if (existing is not null)
            {
                // Lo ya contabilizado no cambia: costo, residual, vida y fecha solo se corrigen antes del asiento de alta.
                var started = existing.Schedule(Today(now)).Any()
                    || await c.ScalarAsync<long>("SELECT COUNT(*) FROM journal_entries WHERE id = $id", tx, ("$id", DeterministicId.From("je|asset", existing.Id))) > 0;
                Require(!started || (existing.Cost == asset.Cost && existing.Residual == asset.Residual && existing.UsefulLifeMonths == asset.UsefulLifeMonths
                    && existing.AcquiredOn == asset.AcquiredOn && existing.FromPurchase == asset.FromPurchase), T("error.activo_ya_depreciado"));
            }
            await AccountingService.RequireOpenPeriodAsync(c, tx, asset.AcquiredOn);
            var clean = asset with
            {
                Id = asset.Id == Guid.Empty ? Guid.NewGuid() : asset.Id, Code = asset.Code.Trim().ToUpperInvariant(), Name = asset.Name.Trim(),
                CostCenter = Clean(asset.CostCenter)?.ToUpperInvariant(), DisposedOn = null, DisposalProceeds = null, Notes = Clean(asset.Notes),
            };
            Require(!(await AssetsAsync(c, tx)).Any(a => a.Id != clean.Id && a.Code == clean.Code), T("error.activo_codigo_repetido", clean.Code));
            await PublishAsync(c, tx, now, ErpRecordKinds.FixedAsset, clean.Id.ToString(), clean);
            await AuditAsync(c, tx, now, "asset.saved", "fixed_asset", clean.Id, clean.Code, $"{clean.Cost:0.##} / {clean.UsefulLifeMonths} m");
            return clean.Id;
        }, ct);
    }

    /// <summary>Baja (venta, robo, destrucción): deja de depreciarse desde ese mes y sale de libros con su resultado.</summary>
    public Task DisposeAssetAsync(Guid id, DateOnly on, decimal proceeds, CancellationToken ct = default)
    {
        RequirePermission(ErpPermissions.ManageAccounting);
        Require(proceeds >= 0, T("error.activo_venta"));
        return InTransactionAsync(async (c, tx, now) =>
        {
            var asset = (await AssetsAsync(c, tx)).FirstOrDefault(a => a.Id == id);
            Require(asset is not null, T("error.activo_no_existe"));
            Require(asset!.DisposedOn is null, T("error.activo_dado_de_baja"));
            Require(on >= asset.AcquiredOn, T("error.activo_baja_fecha"));
            await AccountingService.RequireOpenPeriodAsync(c, tx, on);
            var disposed = asset with { DisposedOn = on, DisposalProceeds = proceeds };
            await PublishAsync(c, tx, now, ErpRecordKinds.FixedAsset, id.ToString(), disposed);
            await AuditAsync(c, tx, now, "asset.disposed", "fixed_asset", id, asset.Code, $"{on:yyyy-MM-dd} {proceeds:0.##}");
            return 0;
        }, ct);
    }

    /// <summary>
    /// Asientos automáticos de los activos hasta <paramref name="today"/>: alta (contra compras o capital), depreciación de cada
    /// mes completo (al último día del mes) y baja (sale el costo y lo acumulado; entra lo cobrado; la diferencia es resultado).
    /// </summary>
    internal static async Task<List<(Guid Id, DateOnly Date, string Source, string Reference, string Description, List<JournalLine> Lines)>> EntriesAsync(
        SqliteConnection c, SqliteTransaction tx, DateOnly today, Func<string, string> role)
    {
        var result = new List<(Guid, DateOnly, string, string, string, List<JournalLine>)>();
        foreach (var a in await AssetsAsync(c, tx))
        {
            if (a.AcquiredOn <= today)
                result.Add((DeterministicId.From("je|asset", a.Id), a.AcquiredOn, "asset", a.Code, T("contab.alta_activo", a.Code, a.Name),
                [
                    new(role(AccountRoles.FixedAssets), a.Cost, 0),
                    new(role(a.FromPurchase ? AccountRoles.Purchases : AccountRoles.Capital), 0, a.Cost),
                ]));
            var through = a.DisposedOn is { } d && d < today ? d : today;
            foreach (var (month, amount) in a.Schedule(through))
            {
                var end = month.AddMonths(1).AddDays(-1);
                result.Add((DeterministicId.From("je|depreciation|" + month.ToString("yyyy-MM", CultureInfo.InvariantCulture), a.Id), end, "depreciation", a.Code,
                    T("contab.depreciacion", a.Code, month.ToString("MM/yyyy", CultureInfo.InvariantCulture)),
                [
                    new(role(AccountRoles.Depreciation), amount, 0, null, a.CostCenter), new(role(AccountRoles.AccumulatedDepreciation), 0, amount),
                ]));
            }
            if (a.DisposedOn is { } on && on <= today)
            {
                var accumulated = a.AccumulatedAt(on);
                var proceeds = a.DisposalProceeds ?? 0;
                var result0 = proceeds + accumulated - a.Cost;   // positivo: ganancia
                List<JournalLine> lines =
                [
                    new(role(AccountRoles.AccumulatedDepreciation), accumulated, 0), new(role(AccountRoles.Cash), proceeds, 0),
                    new(role(AccountRoles.FixedAssets), 0, a.Cost),
                    result0 >= 0 ? new(role(AccountRoles.AssetDisposal), 0, result0, null, a.CostCenter) : new(role(AccountRoles.AssetDisposal), -result0, 0, null, a.CostCenter),
                ];
                result.Add((DeterministicId.From("je|asset_disposal", a.Id), on, "asset_disposal", a.Code, T("contab.baja_activo", a.Code), lines));
            }
        }
        return result;
    }

    // ---------- Presupuestos (ERP-59) ----------

    public static Guid BudgetId(int year, string account, string? center) =>
        DeterministicId.From($"budget|{year}|{account}|{center}", Guid.Empty);

    public async Task<IReadOnlyList<Budget>> GetBudgetsAsync(int year, CancellationToken ct = default)
    {
        await using var c = await Db.OpenAsync(ct);
        return (await c.QueryAsync("SELECT data FROM budgets WHERE year = $y", r => JsonSerializer.Deserialize<Budget>(r.Str("data"), VortexJson.Options)!,
            null, ("$y", year))).OrderBy(b => b.AccountCode).ThenBy(b => b.CostCenter).ToList();
    }

    /// <summary>Guarda el presupuesto de una cuenta de ingresos o gastos (12 montos; todo en cero lo borra).</summary>
    public Task SaveBudgetAsync(int year, string accountCode, string? costCenter, IReadOnlyList<decimal> months, CancellationToken ct = default)
    {
        RequirePermission(ErpPermissions.ManageAccounting);
        Require(year is >= 2000 and <= 2100 && months.Count == 12 && months.All(m => m >= 0), T("error.presupuesto_datos"));
        return InTransactionAsync(async (c, tx, now) =>
        {
            var accounts = await AccountingService.AccountsAsync(c, tx);
            var account = accounts.FirstOrDefault(a => a.Code == accountCode.Trim());
            Require(account is { Type: AccountType.Income or AccountType.Expense }, T("error.presupuesto_cuenta"));
            var center = Clean(costCenter)?.ToUpperInvariant();
            var budget = new Budget(BudgetId(year, account!.Code, center), year, account.Code, center, months.Select(m => Math.Round(m, 2)).ToList());
            await PublishAsync(c, tx, now, ErpRecordKinds.Budget, budget.Id.ToString(), budget);
            await AuditAsync(c, tx, now, "budget.saved", "budget", budget.Id, $"{year} {account.Code}{(center is null ? "" : $" {center}")}",
                budget.Total.ToString("0.##", CultureInfo.InvariantCulture));
            return 0;
        }, ct);
    }

    /// <summary>
    /// Presupuesto contra lo real de los meses <paramref name="fromMonth"/>–<paramref name="toMonth"/> del año: cada cuenta
    /// presupuestada (por centro, si lo tiene) y las de ingresos y gastos con movimiento sin presupuesto.
    /// </summary>
    public async Task<IReadOnlyList<BudgetLine>> CompareAsync(int year, int fromMonth = 1, int toMonth = 12, string? costCenter = null, CancellationToken ct = default)
    {
        Require(fromMonth is >= 1 and <= 12 && toMonth >= fromMonth && toMonth <= 12, T("error.presupuesto_meses"));
        await new AccountingService(Db, Context, Clock).PostPendingAsync(ct);
        await using var c = await Db.OpenAsync(ct);
        var accounts = (await AccountingService.AccountsAsync(c, null)).ToDictionary(a => a.Code);
        var center = Clean(costCenter)?.ToUpperInvariant();
        var budgets = (await GetBudgetsAsync(year, ct)).Where(b => center is null || b.CostCenter == center).ToList();
        var from = new DateOnly(year, fromMonth, 1);
        var to = new DateOnly(year, toMonth, 1).AddMonths(1).AddDays(-1);
        var actual = await c.QueryAsync("""
            SELECT l.account_code, l.cost_center, SUM(l.debit) AS debit, SUM(l.credit) AS credit FROM journal_lines l JOIN journal_entries e ON e.id = l.entry_id
            WHERE e.status = 'posted' AND e.date >= $from AND e.date <= $to AND e.source <> 'year_close' AND ($center IS NULL OR l.cost_center = $center)
            GROUP BY l.account_code, l.cost_center
            """, r => (Code: r.Str("account_code"), Center: r.StrOrNull("cost_center"), Debit: r.Dec("debit"), Credit: r.Dec("credit")), null,
            ("$from", from.ToString("yyyy-MM-dd")), ("$to", to.ToString("yyyy-MM-dd")), ("$center", center));
        decimal Signed(string code, decimal debit, decimal credit) =>
            accounts.TryGetValue(code, out var a) && a.Type == AccountType.Income ? credit - debit : debit - credit;

        var lines = new List<BudgetLine>();
        foreach (var b in budgets)
        {
            if (!accounts.TryGetValue(b.AccountCode, out var a))
                continue;
            var real = actual.Where(x => x.Code == b.AccountCode && (b.CostCenter is null || x.Center == b.CostCenter)).Sum(x => Signed(x.Code, x.Debit, x.Credit));
            lines.Add(new BudgetLine(a.Code, a.Name, a.Type, b.CostCenter, b.Months.Skip(fromMonth - 1).Take(toMonth - fromMonth + 1).Sum(), real));
        }
        var budgeted = budgets.Select(b => b.AccountCode).ToHashSet();
        foreach (var g in actual.Where(x => !budgeted.Contains(x.Code)).GroupBy(x => x.Code))
            if (accounts.TryGetValue(g.Key, out var a) && a.Type is AccountType.Income or AccountType.Expense)
            {
                var real = g.Sum(x => Signed(x.Code, x.Debit, x.Credit));
                if (real != 0)
                    lines.Add(new BudgetLine(a.Code, a.Name, a.Type, null, 0, real));
            }
        return lines.OrderBy(l => l.Type).ThenBy(l => l.Code, StringComparer.Ordinal).ThenBy(l => l.CostCenter).ToList();
    }

    // ---------- Registros replicados ----------

    public static bool Handles(string kind) => kind is ErpRecordKinds.FixedAsset or ErpRecordKinds.Budget;

    private static async Task PublishAsync(SqliteConnection c, SqliteTransaction tx, DateTimeOffset now, string kind, string id, object data)
    {
        var json = JsonSerializer.SerializeToElement(data, VortexJson.Options);
        await ApplyAsync(c, tx, new ErpRecord(kind, id, now, json), force: true);
        await Outbox.EnqueueEventAsync(c, tx, now, SuiteEvents.RecordUpserted, new ErpRecord(kind, id, now, json), subjectType: kind, subjectId: id);
    }

    internal static async Task<bool> ApplyAsync(SqliteConnection c, SqliteTransaction tx, ErpRecord r, bool force = false)
    {
        var at = SqliteExtensions.Time(r.UpdatedAt);
        var table = r.Kind == ErpRecordKinds.FixedAsset ? "fixed_assets" : "budgets";
        var current = await c.ScalarAsync<string?>($"SELECT updated_at FROM {table} WHERE id = $id", tx, ("$id", r.Id));
        if (!force && current is not null && string.CompareOrdinal(current, at) >= 0)
            return false;
        if (r.Kind == ErpRecordKinds.FixedAsset)
            await c.ExecAsync("INSERT OR REPLACE INTO fixed_assets (id, data, updated_at) VALUES ($id, $data, $at)", tx,
                ("$id", r.Id), ("$data", r.Data.GetRawText()), ("$at", r.UpdatedAt));
        else
            await c.ExecAsync("INSERT OR REPLACE INTO budgets (id, year, data, updated_at) VALUES ($id, $year, $data, $at)", tx,
                ("$id", r.Id), ("$year", r.Data.GetProperty("year").GetInt32()), ("$data", r.Data.GetRawText()), ("$at", r.UpdatedAt));
        return true;
    }

    private static DateOnly Today(DateTimeOffset now) => DateOnly.FromDateTime(now.ToOffset(ErpClock.Offset).DateTime);
}
