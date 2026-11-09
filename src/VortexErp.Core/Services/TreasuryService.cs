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

public static class TreasuryAccountKinds
{
    public const string Bank = "bank";
    public const string Cash = "cash";
}

public static class TreasuryMovementKinds
{
    public const string Collection = "collection";
    public const string SupplierPayment = "supplier_payment";
    public const string Transfer = "transfer";
    public const string PosDeposit = "pos_deposit";
    public const string Fee = "fee";
    public const string Interest = "interest";
    public const string Adjustment = "adjustment";

    /// <summary>Los que se pueden registrar a mano (o desde una línea del extracto sin pareja).</summary>
    public static readonly string[] Manual = [Fee, Interest, Adjustment];
}

/// <summary>Cuenta bancaria o caja (ERP-35). <c>Methods</c>: medios de pago que entran aquí por defecto (en su moneda).</summary>
public sealed record TreasuryAccount(Guid Id, string Name, string Kind, string? Bank, string? Number, string CurrencyCode,
    decimal OpeningBalance, DateOnly OpeningOn, IReadOnlyList<string> Methods, bool IsActive = true);

public sealed record TreasuryAccountRow(TreasuryAccount Account, decimal Balance, int Unreconciled, int UnmatchedLines);

/// <summary>
/// Un movimiento de una cuenta: los cobros y los pagos a proveedores (salen de sus tablas) y los propios de tesorería.
/// <c>Ref</c> lo identifica para conciliarlo: <c>payment:{id}</c>, <c>supplier_payment:{id}</c> o <c>movement:{id}</c>.
/// </summary>
public sealed record TreasuryMovementRow(string Ref, Guid? AccountId, DateOnly On, decimal Amount, string CurrencyCode, string Kind,
    string Description, string? Reference, bool Reconciled);

public sealed record StatementLineRow(Guid Id, Guid AccountId, DateOnly On, decimal Amount, string Description, string? Reference,
    string? MatchedRef, string? MatchedBy);

public sealed record StatementImport(int Imported, int Duplicates, int Matched, int Unmatched);

public sealed record CashFlowWeek(DateOnly Start, decimal In, decimal Out, decimal Balance);

/// <summary>Flujo de caja proyectado (ERP-38) en una moneda: saldo de hoy, lo vencido y semana a semana.</summary>
public sealed record CashFlowProjection(string CurrencyCode, decimal Opening, decimal OverdueIn, decimal OverdueOut, IReadOnlyList<CashFlowWeek> Weeks);

/// <summary>
/// Tesorería (fase 4b): cuentas bancarias y cajas, sus movimientos, transferencias, depósitos de caja del POS,
/// importación de extractos con conciliación automática y flujo de caja proyectado.
/// <para>
/// Cuentas, movimientos propios y líneas de extracto son registros del ERP (<c>erp.record.upserted</c>, gana el más
/// nuevo). Los depósitos del POS llegan por <c>cash.deposited</c> a cada equipo y se guardan con Ids deterministas.
/// </para>
/// </summary>
public sealed class TreasuryService(ErpDatabase db, IErpContext context, TimeProvider clock) : ErpService(db, context, clock)
{
    /// <summary>Días de diferencia que se toleran entre el extracto y el movimiento al conciliar solo.</summary>
    public const int MatchWindowDays = 3;

    // ---------- Cuentas ----------

    public async Task<IReadOnlyList<TreasuryAccountRow>> GetAccountsAsync(bool activeOnly = false, CancellationToken ct = default)
    {
        await using var c = await Db.OpenAsync(ct);
        var accounts = await AccountsAsync(c, null);
        var movements = await MovementsAsync(c, null, accounts);
        var unmatched = (await c.QueryAsync("SELECT account_id, COUNT(*) AS n FROM statement_lines WHERE matched_ref IS NULL GROUP BY account_id",
            r => (Id: r.Guid("account_id"), N: r.Int("n")), null)).ToDictionary(x => x.Id, x => x.N);
        return accounts.Where(a => !activeOnly || a.IsActive).Select(a =>
        {
            var own = movements.Where(m => m.AccountId == a.Id && m.On >= a.OpeningOn).ToList();
            return new TreasuryAccountRow(a, a.OpeningBalance + own.Sum(m => m.Amount), own.Count(m => !m.Reconciled),
                unmatched.GetValueOrDefault(a.Id));
        }).ToList();
    }

    public Task<Guid> SaveAccountAsync(TreasuryAccount account, CancellationToken ct = default)
    {
        RequirePermission(ErpPermissions.ManageTreasury);
        Require(!string.IsNullOrWhiteSpace(account.Name), T("error.cuenta_sin_nombre"));
        Require(account.Kind is TreasuryAccountKinds.Bank or TreasuryAccountKinds.Cash, T("error.cuenta_tipo"));
        Require(VortexApi.Suite.Money.Currencies.IsKnown(account.CurrencyCode?.ToUpperInvariant()), T("error.moneda_desconocida", account.CurrencyCode));
        var methods = account.Methods.Where(PaymentMethods.All.Contains).Distinct().ToList();
        return InTransactionAsync(async (c, tx, now) =>
        {
            var data = account with
            {
                Id = account.Id == Guid.Empty ? Guid.NewGuid() : account.Id, Name = account.Name.Trim(), Bank = Clean(account.Bank),
                Number = Clean(account.Number), CurrencyCode = account.CurrencyCode.ToUpperInvariant(), Methods = methods,
            };
            // Un medio de pago entra por defecto a una sola cuenta de cada moneda: se quita de las otras.
            foreach (var other in (await AccountsAsync(c, tx)).Where(o => o.Id != data.Id && o.CurrencyCode == data.CurrencyCode
                         && o.Methods.Intersect(methods).Any()))
                await PublishAsync(c, tx, now, ErpRecordKinds.TreasuryAccount, other.Id, other with { Methods = other.Methods.Except(methods).ToList() });
            await PublishAsync(c, tx, now, ErpRecordKinds.TreasuryAccount, data.Id, data);
            await AuditAsync(c, tx, now, "treasury.account_saved", "treasury_account", data.Id, data.Name,
                $"{data.Kind} {data.CurrencyCode} {data.OpeningBalance:0.00}");
            return data.Id;
        }, ct);
    }

    internal static async Task<List<TreasuryAccount>> AccountsAsync(SqliteConnection c, SqliteTransaction? tx) =>
        await c.QueryAsync("SELECT * FROM treasury_accounts ORDER BY is_active DESC, kind, name", r => new TreasuryAccount(
            r.Guid("id"), r.Str("name"), r.Str("kind"), r.StrOrNull("bank"), r.StrOrNull("number"), r.Str("currency_code"),
            r.Dec("opening_balance"), DateOnly.Parse(r.Str("opening_on"), CultureInfo.InvariantCulture),
            JsonSerializer.Deserialize<List<string>>(r.Str("methods")) ?? [], r.Bool("is_active")), tx);

    /// <summary>La cuenta donde entra un medio de pago en una moneda: la que lo tiene marcado, si no la primera del tipo.</summary>
    internal static TreasuryAccount? DefaultAccount(IReadOnlyList<TreasuryAccount> accounts, string method, string currency)
    {
        var usable = accounts.Where(a => a.IsActive && a.CurrencyCode == currency).ToList();
        return usable.FirstOrDefault(a => a.Methods.Contains(method))
               ?? usable.FirstOrDefault(a => a.Kind == (method == PaymentMethods.Cash ? TreasuryAccountKinds.Cash : TreasuryAccountKinds.Bank));
    }

    // ---------- Movimientos ----------

    public async Task<IReadOnlyList<TreasuryMovementRow>> GetMovementsAsync(Guid? accountId, DateOnly? from = null, DateOnly? to = null,
        CancellationToken ct = default)
    {
        await using var c = await Db.OpenAsync(ct);
        var movements = await MovementsAsync(c, null, await AccountsAsync(c, null));
        return movements.Where(m => m.AccountId == accountId && (from is null || m.On >= from) && (to is null || m.On <= to))
            .OrderByDescending(m => m.On).ThenBy(m => m.Ref).ToList();
    }

    /// <summary>Todos los movimientos de todas las cuentas (los cobros y pagos sin cuenta van a la predeterminada).</summary>
    private static async Task<List<TreasuryMovementRow>> MovementsAsync(SqliteConnection c, SqliteTransaction? tx, IReadOnlyList<TreasuryAccount> accounts)
    {
        var reconciled = (await c.QueryAsync("SELECT matched_ref FROM statement_lines WHERE matched_ref IS NOT NULL", r => r.Str("matched_ref"), tx))
            .ToHashSet();
        Guid? Account(string? stored, string method, string currency) =>
            Guid.TryParse(stored, out var id) ? id : DefaultAccount(accounts, method, currency)?.Id;
        var list = new List<TreasuryMovementRow>();

        list.AddRange(await c.QueryAsync("""
            SELECT p.*, COALESCE(pt.name, '') AS customer, i.number AS invoice FROM payments p
            LEFT JOIN parties pt ON pt.id = p.customer_id LEFT JOIN invoices i ON i.id = p.invoice_id
            """, r =>
        {
            var currency = r.StrOrNull("paid_currency") ?? r.Str("currency_code");
            var reference = $"payment:{r.Str("id")}";
            var description = string.Join(" · ", new[] { T("tesoreria.cobro", r.Str("number")), r.StrOrNull("invoice"), r.StrOrNull("customer") }
                .Where(x => !string.IsNullOrWhiteSpace(x)));
            return new TreasuryMovementRow(reference, Account(r.StrOrNull("account_id"), r.Str("method"), currency),
                DateOnly.FromDateTime(r.Time("received_at").ToOffset(ErpClock.Offset).DateTime), r.DecOrNull("paid_amount") ?? r.Dec("amount"),
                currency, TreasuryMovementKinds.Collection, description, r.StrOrNull("reference"), reconciled.Contains(reference));
        }, tx));

        list.AddRange(await c.QueryAsync("""
            SELECT sp.*, si.currency_code, si.document_number, COALESCE(pt.name, '') AS supplier FROM supplier_payments sp
            JOIN supplier_invoices si ON si.id = sp.invoice_id LEFT JOIN parties pt ON pt.id = si.supplier_id
            """, r =>
        {
            var reference = $"supplier_payment:{r.Str("id")}";
            var description = string.Join(" · ", new[] { T("tesoreria.pago", r.Str("document_number")), r.StrOrNull("supplier") }
                .Where(x => !string.IsNullOrWhiteSpace(x)));
            return new TreasuryMovementRow(reference, Account(r.StrOrNull("account_id"), r.Str("method"), r.Str("currency_code")),
                DateOnly.Parse(r.Str("paid_on"), CultureInfo.InvariantCulture), -r.Dec("amount"), r.Str("currency_code"),
                TreasuryMovementKinds.SupplierPayment, description, r.StrOrNull("reference"), reconciled.Contains(reference));
        }, tx));

        var currencies = accounts.ToDictionary(a => a.Id, a => a.CurrencyCode);
        list.AddRange(await c.QueryAsync("SELECT * FROM treasury_movements", r =>
        {
            var reference = $"movement:{r.Str("id")}";
            var account = r.Guid("account_id");
            return new TreasuryMovementRow(reference, account, DateOnly.Parse(r.Str("on_date"), CultureInfo.InvariantCulture), r.Dec("amount"),
                currencies.GetValueOrDefault(account, ""), r.Str("kind"), r.Str("description"), r.StrOrNull("reference"), reconciled.Contains(reference));
        }, tx));
        return list;
    }

    /// <summary>Comisión, interés o ajuste a mano (con signo: negativo sale de la cuenta).</summary>
    public Task<Guid> AddMovementAsync(Guid accountId, DateOnly on, decimal amount, string kind, string description, string? reference = null,
        CancellationToken ct = default)
    {
        RequirePermission(ErpPermissions.ManageTreasury);
        Require(amount != 0, T("error.el_monto_no_puede_ser_cero"));
        Require(TreasuryMovementKinds.Manual.Contains(kind), T("error.movimiento_tipo"));
        Require(!string.IsNullOrWhiteSpace(description), T("error.movimiento_sin_descripcion"));
        return InTransactionAsync(async (c, tx, now) =>
        {
            Require((await AccountsAsync(c, tx)).Any(a => a.Id == accountId), T("error.la_cuenta_no_existe"));
            var id = await InsertMovementAsync(c, tx, now, Guid.NewGuid(), accountId, on, amount, kind, description.Trim(), Clean(reference), null);
            await AuditAsync(c, tx, now, "treasury.movement", "treasury_account", accountId, kind, $"{amount:0.00} {description}");
            return id;
        }, ct);
    }

    /// <summary>
    /// Transferencia entre dos cuentas (ERP-36): sale de una y entra a la otra el mismo día. Entre monedas distintas, indique
    /// cuánto entra (<paramref name="receivedAmount"/>).
    /// </summary>
    public Task<Guid> TransferAsync(Guid fromId, Guid toId, decimal amount, DateOnly on, decimal? receivedAmount = null, string? reference = null,
        CancellationToken ct = default)
    {
        RequirePermission(ErpPermissions.ManageTreasury);
        Require(amount > 0, T("error.el_monto_debe_ser_mayor"));
        Require(fromId != toId, T("error.transferencia_misma_cuenta"));
        return InTransactionAsync(async (c, tx, now) =>
        {
            var accounts = await AccountsAsync(c, tx);
            var from = accounts.FirstOrDefault(a => a.Id == fromId);
            var to = accounts.FirstOrDefault(a => a.Id == toId);
            Require(from is not null && to is not null, T("error.la_cuenta_no_existe"));
            var received = receivedAmount ?? amount;
            Require(from!.CurrencyCode == to!.CurrencyCode || receivedAmount is > 0, T("error.transferencia_otra_moneda", to.CurrencyCode));
            var transfer = Guid.NewGuid();
            var description = T("tesoreria.transferencia", from.Name, to.Name);
            await InsertMovementAsync(c, tx, now, Guid.NewGuid(), from.Id, on, -amount, TreasuryMovementKinds.Transfer, description, Clean(reference), transfer);
            await InsertMovementAsync(c, tx, now, Guid.NewGuid(), to.Id, on, received, TreasuryMovementKinds.Transfer, description, Clean(reference), transfer);
            await AuditAsync(c, tx, now, "treasury.transfer", "treasury_account", from.Id, to.Name,
                $"{amount:0.00} {from.CurrencyCode} → {received:0.00} {to.CurrencyCode}");
            return transfer;
        }, ct);
    }

    private async Task<Guid> InsertMovementAsync(SqliteConnection c, SqliteTransaction tx, DateTimeOffset now, Guid id, Guid accountId, DateOnly on,
        decimal amount, string kind, string description, string? reference, Guid? transferId)
    {
        await PublishAsync(c, tx, now, ErpRecordKinds.TreasuryMovement, id,
            new MovementData(id, accountId, on, amount, kind, description, reference, transferId, now, Context.UserName));
        return id;
    }

    /// <summary>
    /// Depósito de caja del POS (ERP-39, <c>cash.deposited</c>): sale de la caja y entra al banco predeterminado de la
    /// moneda, para conciliarlo con el extracto. Ids deterministas: cada equipo del ERP lo recibe y lo guarda igual.
    /// </summary>
    internal static async Task<string?> ApplyPosDepositAsync(SqliteConnection c, SqliteTransaction tx, DateTimeOffset at, CashDeposited d)
    {
        if (d.Amount <= 0)
            return "depósito sin monto";
        var accounts = await AccountsAsync(c, tx);
        var bank = DefaultAccount(accounts, PaymentMethods.Transfer, d.CurrencyCode);
        if (bank is null)
            return $"sin cuenta bancaria en {d.CurrencyCode}";
        var cash = DefaultAccount(accounts, PaymentMethods.Cash, d.CurrencyCode);
        var on = d.DepositedOn ?? DateOnly.FromDateTime(at.ToOffset(ErpClock.Offset).DateTime);
        var site = await c.ScalarAsync<string?>("SELECT name FROM sites WHERE id = $id", tx, ("$id", d.SiteId)) ?? "POS";
        var description = T("tesoreria.deposito_pos", site, d.UserName ?? "");
        async Task Insert(Guid id, Guid account, decimal amount) =>
            await c.ExecAsync("""
                INSERT OR IGNORE INTO treasury_movements (id, account_id, on_date, amount, kind, description, reference, transfer_id, created_at,
                                                          user_name, updated_at)
                VALUES ($id, $account, $on, $amount, $kind, $description, $ref, $transfer, $at, $user, $at)
                """, tx, ("$id", id), ("$account", account), ("$on", on.ToString("yyyy-MM-dd")), ("$amount", amount),
                ("$kind", TreasuryMovementKinds.PosDeposit), ("$description", description.Trim().TrimEnd('·', ' ')), ("$ref", d.Reference),
                ("$transfer", d.DepositId), ("$at", at), ("$user", d.UserName));
        await Insert(DeterministicId.From("pos_deposit_in", d.DepositId), bank.Id, d.Amount);
        if (cash is not null)
            await Insert(DeterministicId.From("pos_deposit_out", d.DepositId), cash.Id, -d.Amount);
        return null;
    }

    // ---------- Extractos y conciliación (ERP-37) ----------

    /// <summary>
    /// Importa el extracto (CSV, OFX o CAMT.053) de la cuenta y concilia solo. Volver a importar el mismo extracto no duplica
    /// líneas (Id determinista por fecha, importe, referencia y descripción).
    /// </summary>
    public Task<StatementImport> ImportStatementAsync(Guid accountId, string content, CancellationToken ct = default)
    {
        RequirePermission(ErpPermissions.ManageTreasury);
        IReadOnlyList<BankEntry> entries;
        try
        {
            entries = BankStatements.Parse(content);
        }
        catch (Exception ex) when (ex is System.Xml.XmlException or FormatException)
        {
            throw new ErpValidationException(T("error.extracto_ilegible", ex.Message));
        }
        Require(entries.Count > 0, T("error.extracto_vacio"));
        return InTransactionAsync(async (c, tx, now) =>
        {
            Require((await AccountsAsync(c, tx)).Any(a => a.Id == accountId), T("error.la_cuenta_no_existe"));
            int imported = 0, duplicates = 0;
            var seen = new Dictionary<string, int>();
            foreach (var e in entries)
            {
                var key = $"{e.On:yyyy-MM-dd}|{e.Amount.ToString("0.00", CultureInfo.InvariantCulture)}|{e.Reference}|{e.Description}";
                var occurrence = seen[key] = seen.GetValueOrDefault(key) + 1;
                var id = DeterministicId.From($"statement_line|{key}|{occurrence}", accountId);
                if (await c.ScalarAsync<long>("SELECT COUNT(*) FROM statement_lines WHERE id = $id", tx, ("$id", id)) > 0)
                {
                    duplicates++;
                    continue;
                }
                await PublishAsync(c, tx, now, ErpRecordKinds.StatementLine, id,
                    new StatementLineData(id, accountId, e.On, e.Amount, e.Description, e.Reference, now, null, null, null));
                imported++;
            }
            var matched = await AutoMatchAsync(c, tx, now, accountId);
            var unmatched = await c.ScalarAsync<int>("SELECT COUNT(*) FROM statement_lines WHERE account_id = $id AND matched_ref IS NULL", tx,
                ("$id", accountId));
            await AuditAsync(c, tx, now, "treasury.statement_imported", "treasury_account", accountId, null,
                $"{imported} líneas, {duplicates} repetidas, {matched} conciliadas");
            return new StatementImport(imported, duplicates, matched, unmatched);
        }, ct);
    }

    /// <summary>Concilia lo que falta de la cuenta: mismo importe y fecha cercana; si hay varias parejas, la de la misma referencia o la más cercana.</summary>
    public Task<int> AutoMatchAsync(Guid accountId, CancellationToken ct = default)
    {
        RequirePermission(ErpPermissions.ManageTreasury);
        return InTransactionAsync((c, tx, now) => AutoMatchAsync(c, tx, now, accountId), ct);
    }

    private async Task<int> AutoMatchAsync(SqliteConnection c, SqliteTransaction tx, DateTimeOffset now, Guid accountId)
    {
        var lines = await LinesAsync(c, tx, accountId, unmatchedOnly: true);
        var free = (await MovementsAsync(c, tx, await AccountsAsync(c, tx))).Where(m => m.AccountId == accountId && !m.Reconciled).ToList();
        var matched = 0;
        foreach (var line in lines.OrderBy(l => l.On))
        {
            var candidate = free
                .Where(m => Math.Abs(m.Amount - line.Amount) < 0.005m && Math.Abs(m.On.DayNumber - line.On.DayNumber) <= MatchWindowDays)
                .OrderByDescending(m => SameReference(m, line))
                .ThenBy(m => Math.Abs(m.On.DayNumber - line.On.DayNumber))
                .FirstOrDefault();
            if (candidate is null)
                continue;
            free.Remove(candidate);
            await SetMatchAsync(c, tx, now, line, candidate.Ref, "auto");
            matched++;
        }
        return matched;
    }

    private static bool SameReference(TreasuryMovementRow m, StatementLineRow line) =>
        m.Reference is { Length: >= 3 } r && (line.Reference?.Contains(r, StringComparison.OrdinalIgnoreCase) == true
                                              || line.Description.Contains(r, StringComparison.OrdinalIgnoreCase));

    public async Task<IReadOnlyList<StatementLineRow>> GetStatementLinesAsync(Guid accountId, bool unmatchedOnly = false, CancellationToken ct = default)
    {
        await using var c = await Db.OpenAsync(ct);
        return (await LinesAsync(c, null, accountId, unmatchedOnly)).OrderByDescending(l => l.On).ToList();
    }

    private static Task<List<StatementLineRow>> LinesAsync(SqliteConnection c, SqliteTransaction? tx, Guid accountId, bool unmatchedOnly) =>
        c.QueryAsync("SELECT * FROM statement_lines WHERE account_id = $id AND ($all = 1 OR matched_ref IS NULL) ORDER BY on_date, id",
            r => new StatementLineRow(r.Guid("id"), r.Guid("account_id"), DateOnly.Parse(r.Str("on_date"), CultureInfo.InvariantCulture),
                r.Dec("amount"), r.Str("description"), r.StrOrNull("reference"), r.StrOrNull("matched_ref"), r.StrOrNull("matched_by")),
            tx, ("$id", accountId), ("$all", !unmatchedOnly));

    /// <summary>Movimientos sin conciliar de la cuenta que podrían ser la pareja de la línea (mismo signo, los más parecidos primero).</summary>
    public async Task<IReadOnlyList<TreasuryMovementRow>> SuggestAsync(Guid lineId, CancellationToken ct = default)
    {
        await using var c = await Db.OpenAsync(ct);
        var line = await LineAsync(c, null, lineId);
        var movements = await MovementsAsync(c, null, await AccountsAsync(c, null));
        return movements.Where(m => m.AccountId == line.AccountId && !m.Reconciled && Math.Sign(m.Amount) == Math.Sign(line.Amount))
            .OrderBy(m => Math.Abs(m.Amount - line.Amount)).ThenBy(m => Math.Abs(m.On.DayNumber - line.On.DayNumber)).Take(20).ToList();
    }

    /// <summary>Concilia a mano una línea con un movimiento (importes distintos se aceptan: p. ej. el banco descontó una comisión aparte).</summary>
    public Task MatchAsync(Guid lineId, string movementRef, CancellationToken ct = default)
    {
        RequirePermission(ErpPermissions.ManageTreasury);
        return InTransactionAsync(async (c, tx, now) =>
        {
            var line = await LineAsync(c, tx, lineId);
            var movement = (await MovementsAsync(c, tx, await AccountsAsync(c, tx))).FirstOrDefault(m => m.Ref == movementRef);
            Require(movement is not null && movement.AccountId == line.AccountId, T("error.movimiento_de_otra_cuenta"));
            Require(!movement!.Reconciled, T("error.movimiento_ya_conciliado"));
            await SetMatchAsync(c, tx, now, line, movementRef, Context.UserName);
            return 0;
        }, ct);
    }

    public Task UnmatchAsync(Guid lineId, CancellationToken ct = default)
    {
        RequirePermission(ErpPermissions.ManageTreasury);
        return InTransactionAsync(async (c, tx, now) =>
        {
            await SetMatchAsync(c, tx, now, await LineAsync(c, tx, lineId), null, null);
            return 0;
        }, ct);
    }

    /// <summary>Una línea sin pareja que es una comisión, un interés o un ajuste: crea el movimiento y lo concilia.</summary>
    public Task<Guid> CreateFromLineAsync(Guid lineId, string kind, string? description = null, CancellationToken ct = default)
    {
        RequirePermission(ErpPermissions.ManageTreasury);
        Require(TreasuryMovementKinds.Manual.Contains(kind), T("error.movimiento_tipo"));
        return InTransactionAsync(async (c, tx, now) =>
        {
            var line = await LineAsync(c, tx, lineId);
            Require(line.MatchedRef is null, T("error.linea_ya_conciliada"));
            var id = await InsertMovementAsync(c, tx, now, Guid.NewGuid(), line.AccountId, line.On, line.Amount, kind,
                Clean(description) ?? line.Description, line.Reference, null);
            await SetMatchAsync(c, tx, now, line, $"movement:{id}", Context.UserName);
            return id;
        }, ct);
    }

    private static async Task<StatementLineRow> LineAsync(SqliteConnection c, SqliteTransaction? tx, Guid id) =>
        await c.FirstOrDefaultAsync("SELECT * FROM statement_lines WHERE id = $id",
            r => new StatementLineRow(r.Guid("id"), r.Guid("account_id"), DateOnly.Parse(r.Str("on_date"), CultureInfo.InvariantCulture),
                r.Dec("amount"), r.Str("description"), r.StrOrNull("reference"), r.StrOrNull("matched_ref"), r.StrOrNull("matched_by")),
            tx, ("$id", id))
        ?? throw new ErpValidationException(T("error.linea_no_existe"));

    private static async Task SetMatchAsync(SqliteConnection c, SqliteTransaction tx, DateTimeOffset now, StatementLineRow line, string? movementRef,
        string? by)
    {
        var importedAt = await c.ScalarAsync<string>("SELECT imported_at FROM statement_lines WHERE id = $id", tx, ("$id", line.Id));
        await PublishAsync(c, tx, now, ErpRecordKinds.StatementLine, line.Id,
            new StatementLineData(line.Id, line.AccountId, line.On, line.Amount, line.Description, line.Reference,
                DateTimeOffset.Parse(importedAt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal), movementRef,
                movementRef is null ? null : now, by));
    }

    // ---------- Flujo de caja proyectado (ERP-38) ----------

    /// <summary>
    /// Saldo de hoy de las cuentas en la moneda, más lo que se cobrará (saldo de comprobantes por vencimiento) y menos lo que
    /// se pagará (facturas de proveedor aprobadas o retenidas, por fecha programada o vencimiento), semana a semana desde el
    /// lunes de esta semana. Lo vencido se muestra aparte y entra en la primera semana.
    /// </summary>
    public async Task<CashFlowProjection> GetCashFlowAsync(string currency, int weeks = 12, CancellationToken ct = default)
    {
        await using var c = await Db.OpenAsync(ct);
        var today = DateOnly.FromDateTime(Clock.GetUtcNow().ToOffset(ErpClock.Offset).DateTime);
        var monday = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));
        var accounts = (await GetAccountsAsync(true, ct)).Where(a => a.Account.CurrencyCode == currency).ToList();
        var opening = accounts.Sum(a => a.Balance);

        var inflows = await c.QueryAsync("""
            SELECT COALESCE(due_date, substr(issued_at, 1, 10)) AS due, total + debited - credited - paid AS balance FROM invoices
            WHERE status = 'issued' AND currency_code = $cur AND total + debited - credited - paid > 0.005
            """, r => (Due: ParseDay(r.Str("due")), Amount: r.Dec("balance")), null, ("$cur", currency));
        var outflows = await c.QueryAsync("""
            SELECT COALESCE(si.planned_on, si.due_on) AS due,
                   si.total - COALESCE((SELECT SUM(sp.amount) FROM supplier_payments sp WHERE sp.invoice_id = si.id), 0) AS balance
            FROM supplier_invoices si WHERE si.status IN ('approved', 'held') AND si.currency_code = $cur
            """, r => (Due: ParseDay(r.Str("due")), Amount: r.Dec("balance")), null, ("$cur", currency));

        var overdueIn = inflows.Where(x => x.Due < today).Sum(x => x.Amount);
        var overdueOut = outflows.Where(x => x.Due < today).Sum(x => x.Amount > 0 ? x.Amount : 0);
        var balance = opening;
        var result = new List<CashFlowWeek>();
        for (var w = 0; w < weeks; w++)
        {
            var start = monday.AddDays(7 * w);
            var end = start.AddDays(7);
            bool InWeek(DateOnly d) => (w == 0 && d < today) || (d >= start && d < end && d >= today);
            var @in = inflows.Where(x => InWeek(x.Due)).Sum(x => x.Amount);
            var @out = outflows.Where(x => InWeek(x.Due) && x.Amount > 0).Sum(x => x.Amount);
            balance += @in - @out;
            result.Add(new CashFlowWeek(start, @in, @out, balance));
        }
        return new CashFlowProjection(currency, opening, overdueIn, overdueOut, result);
    }

    private static DateOnly ParseDay(string value) => DateOnly.Parse(value[..10], CultureInfo.InvariantCulture);

    // ---------- Registros replicados ----------

    public sealed record MovementData(Guid Id, Guid AccountId, DateOnly On, decimal Amount, string Kind, string Description, string? Reference,
        Guid? TransferId, DateTimeOffset CreatedAt, string? UserName);

    public sealed record StatementLineData(Guid Id, Guid AccountId, DateOnly On, decimal Amount, string Description, string? Reference,
        DateTimeOffset ImportedAt, string? MatchedRef, DateTimeOffset? MatchedAt, string? MatchedBy);

    private static async Task PublishAsync(SqliteConnection c, SqliteTransaction tx, DateTimeOffset now, string kind, Guid id, object data)
    {
        var json = JsonSerializer.SerializeToElement(data, VortexJson.Options);
        await ApplyAsync(c, tx, new ErpRecord(kind, id.ToString(), now, json), force: true);
        await Outbox.EnqueueEventAsync(c, tx, now, SuiteEvents.RecordUpserted, new ErpRecord(kind, id.ToString(), now, json),
            subjectType: kind, subjectId: id.ToString());
    }

    public static bool Handles(string kind) =>
        kind is ErpRecordKinds.TreasuryAccount or ErpRecordKinds.TreasuryMovement or ErpRecordKinds.StatementLine;

    /// <summary>Aplica un registro de tesorería. El de otro equipo solo si es más nuevo que el que hay.</summary>
    internal static async Task<bool> ApplyAsync(SqliteConnection c, SqliteTransaction tx, ErpRecord r, bool force = false)
    {
        var at = SqliteExtensions.Time(r.UpdatedAt);
        async Task<bool> NewerAsync(string table, Guid id) =>
            force || await c.ScalarAsync<string?>($"SELECT updated_at FROM {table} WHERE id = $id", tx, ("$id", id)) is not { } current
                  || string.CompareOrdinal(current, at) < 0;
        switch (r.Kind)
        {
            case ErpRecordKinds.TreasuryAccount:
            {
                var d = r.Data.Deserialize<TreasuryAccount>(VortexJson.Options)!;
                if (!await NewerAsync("treasury_accounts", d.Id))
                    return false;
                await c.ExecAsync("""
                    INSERT OR REPLACE INTO treasury_accounts (id, name, kind, bank, number, currency_code, opening_balance, opening_on, methods,
                                                              is_active, updated_at)
                    VALUES ($id, $name, $kind, $bank, $number, $currency, $opening, $on, $methods, $active, $upd)
                    """, tx, ("$id", d.Id), ("$name", d.Name), ("$kind", d.Kind), ("$bank", d.Bank), ("$number", d.Number),
                    ("$currency", d.CurrencyCode), ("$opening", d.OpeningBalance), ("$on", d.OpeningOn.ToString("yyyy-MM-dd")),
                    ("$methods", JsonSerializer.Serialize(d.Methods)), ("$active", d.IsActive), ("$upd", r.UpdatedAt));
                return true;
            }
            case ErpRecordKinds.TreasuryMovement:
            {
                var d = r.Data.Deserialize<MovementData>(VortexJson.Options)!;
                if (!await NewerAsync("treasury_movements", d.Id))
                    return false;
                await c.ExecAsync("""
                    INSERT OR REPLACE INTO treasury_movements (id, account_id, on_date, amount, kind, description, reference, transfer_id,
                                                               created_at, user_name, updated_at)
                    VALUES ($id, $account, $on, $amount, $kind, $description, $ref, $transfer, $created, $user, $upd)
                    """, tx, ("$id", d.Id), ("$account", d.AccountId), ("$on", d.On.ToString("yyyy-MM-dd")), ("$amount", d.Amount),
                    ("$kind", d.Kind), ("$description", d.Description), ("$ref", d.Reference), ("$transfer", d.TransferId),
                    ("$created", d.CreatedAt), ("$user", d.UserName), ("$upd", r.UpdatedAt));
                return true;
            }
            case ErpRecordKinds.StatementLine:
            {
                var d = r.Data.Deserialize<StatementLineData>(VortexJson.Options)!;
                if (!await NewerAsync("statement_lines", d.Id))
                    return false;
                await c.ExecAsync("""
                    INSERT OR REPLACE INTO statement_lines (id, account_id, on_date, amount, description, reference, imported_at, matched_ref,
                                                            matched_at, matched_by, updated_at)
                    VALUES ($id, $account, $on, $amount, $description, $ref, $imported, $matched, $mat, $mby, $upd)
                    """, tx, ("$id", d.Id), ("$account", d.AccountId), ("$on", d.On.ToString("yyyy-MM-dd")), ("$amount", d.Amount),
                    ("$description", d.Description), ("$ref", d.Reference), ("$imported", d.ImportedAt), ("$matched", d.MatchedRef),
                    ("$mat", d.MatchedAt), ("$mby", d.MatchedBy), ("$upd", r.UpdatedAt));
                return true;
            }
            default:
                return false;
        }
    }
}
