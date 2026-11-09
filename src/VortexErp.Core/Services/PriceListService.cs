using System.Text.Json;
using Microsoft.Data.Sqlite;
using VortexApi.Client;
using VortexErp.Core.Data;
using VortexErp.Core.Domain;
using VortexErp.Core.Sync;

using static VortexErp.Core.Texts.CoreText;

namespace VortexErp.Core.Services;

/// <summary>
/// Listas de precios (ERP-07). Viven en el hub (<c>price_list</c>) y las comparten POS, ERP y CRM: el ERP las crea y las
/// edita (se suben enteras), y las asigna a cada cliente en sus condiciones comerciales (<c>credit.priceListId</c>).
/// </summary>
public sealed class PriceListService(ErpDatabase db, IErpContext context, TimeProvider clock) : ErpService(db, context, clock)
{
    public async Task<IReadOnlyList<PriceListRow>> GetListsAsync(bool includeInactive = true, CancellationToken ct = default)
    {
        await using var c = await Db.OpenAsync(ct);
        return await c.QueryAsync("""
            SELECT l.*, (SELECT COUNT(DISTINCT item_id) FROM price_list_lines x WHERE x.list_id = l.id) AS items,
                   (SELECT COUNT(*) FROM parties p WHERE p.price_list_id = l.id AND p.is_deleted = 0) AS customers
            FROM price_lists l WHERE l.is_deleted = 0 AND ($all = 1 OR l.is_active = 1) ORDER BY l.code
            """, ReadRow, null, ("$all", includeInactive));
    }

    public async Task<PriceListDraft?> GetAsync(Guid id, CancellationToken ct = default)
    {
        await using var c = await Db.OpenAsync(ct);
        var row = await c.FirstOrDefaultAsync("SELECT l.*, 0 AS items, 0 AS customers FROM price_lists l WHERE l.id = $id AND l.is_deleted = 0",
            ReadRow, null, ("$id", id));
        if (row is null)
            return null;
        var lines = await c.QueryAsync("SELECT item_id, price, min_qty FROM price_list_lines WHERE list_id = $id ORDER BY item_id, min_qty",
            r => new PriceListLineRow(r.Guid("item_id"), r.Dec("price"), r.Dec("min_qty")), null, ("$id", id));
        return new PriceListDraft(row.Code, row.Name, row.CurrencyCode, row.ValidFrom, row.ValidTo, row.SiteIds, row.IsActive, lines);
    }

    /// <summary>Crea o cambia una lista (entera) y la sube al hub. Mientras el hub no la confirma, ya rige en este equipo.</summary>
    public Task<Guid> SaveAsync(Guid? id, PriceListDraft d, CancellationToken ct = default)
    {
        RequirePermission(ErpPermissions.EditCatalog);
        var code = (d.Code ?? "").Trim().ToUpperInvariant();
        Require(code.Length > 0, T("error.el_codigo_es_obligatorio"));
        Require(!string.IsNullOrWhiteSpace(d.Name), T("error.el_nombre_es_obligatorio"));
        Require(d.CurrencyCode.Trim().Length == 3, T("error.la_moneda_debe_ser_un"));
        Require(d.ValidFrom is null || d.ValidTo is null || d.ValidTo >= d.ValidFrom, T("error.la_vigencia_termina_antes"));
        Require(d.Lines.All(l => l.Price >= 0 && l.MinQuantity > 0), T("error.los_precios_no_pueden_ser"));
        Require(d.Lines.GroupBy(l => (l.ItemId, l.MinQuantity)).All(g => g.Count() == 1), T("error.precio_repetido_en_la_lista"));
        return InTransactionAsync(async (c, tx, now) =>
        {
            var listId = id ?? Guid.NewGuid();
            Require(await c.ScalarAsync<long>("SELECT COUNT(*) FROM price_lists WHERE code = $code AND is_deleted = 0 AND id <> $id", tx,
                ("$code", code), ("$id", listId)) == 0, T("error.ya_existe_la_lista", code));
            var version = id is null ? 0 : await c.ScalarAsync<int>("SELECT version FROM price_lists WHERE id = $id", tx, ("$id", listId));
            var currency = d.CurrencyCode.Trim().ToUpperInvariant();
            var sites = d.SiteIds.Distinct().ToList();
            await c.ExecAsync("""
                INSERT INTO price_lists (id, code, name, currency_code, valid_from, valid_to, site_ids, is_active, version, pending, updated_at)
                VALUES ($id, $code, $name, $currency, $from, $to, $sites, $active, 0, 1, $now)
                ON CONFLICT (id) DO UPDATE SET code = $code, name = $name, currency_code = $currency, valid_from = $from, valid_to = $to,
                    site_ids = $sites, is_active = $active, pending = 1, updated_at = $now
                """, tx, ("$id", listId), ("$code", code), ("$name", d.Name.Trim()), ("$currency", currency),
                ("$from", d.ValidFrom?.ToString("yyyy-MM-dd")), ("$to", d.ValidTo?.ToString("yyyy-MM-dd")),
                ("$sites", JsonSerializer.Serialize(sites)), ("$active", d.IsActive), ("$now", now));
            await ReplaceLinesAsync(c, tx, listId, d.Lines);
            var body = new
            {
                code, name = d.Name.Trim(), currencyCode = currency, validFrom = d.ValidFrom, validTo = d.ValidTo, siteIds = sites,
                isActive = d.IsActive, lines = d.Lines.Select(l => new { itemId = l.ItemId, price = l.Price, minQuantity = l.MinQuantity }).ToList(),
            };
            await Outbox.EnqueueUpsertAsync(c, tx, now, VortexMasterResource.PriceLists, listId, body, version > 0 ? version : null);
            await AuditAsync(c, tx, now, VortexApi.Suite.Auditing.AuditActions.SettingsChanged, "price_list", listId, code,
                $"{d.Lines.Select(l => l.ItemId).Distinct().Count()} artículos · {currency}");
            return listId;
        }, ct);
    }

    /// <summary>
    /// Cómo se pone precio a un pedido de este cliente (o de la sede) en esa moneda: la lista que rige y el descuento
    /// habitual. La usan el pedido (Core) y la pantalla, así lo que se ve es lo que se guarda.
    /// </summary>
    public async Task<PriceBook> PriceBookAsync(Guid? customerId, Guid? siteId, string currency, CancellationToken ct = default)
    {
        await using var c = await Db.OpenAsync(ct);
        // Sin sede elegida, la misma que usará el pedido (la por defecto del usuario).
        return await LoadAsync(c, null, Clock.GetUtcNow(), customerId, siteId ?? await DefaultSiteAsync(c, null), currency);
    }

    internal static async Task<PriceBook> LoadAsync(SqliteConnection c, SqliteTransaction? tx, DateTimeOffset now, Guid? customerId, Guid? siteId,
        string currency)
    {
        var today = DateOnly.FromDateTime(now.ToOffset(ErpClock.Offset).DateTime);
        var day = today.ToString("yyyy-MM-dd");
        var party = customerId is { } id
            ? (await c.QueryAsync("SELECT discount_percent, price_list_id FROM parties WHERE id = $id",
                r => (Discount: r.DecOrNull("discount_percent") ?? 0, List: r.GuidOrNull("price_list_id")), tx, ("$id", id))).FirstOrDefault()
            : default;
        const string vigente = """
            l.is_active = 1 AND l.is_deleted = 0 AND (l.valid_from IS NULL OR l.valid_from <= $today) AND (l.valid_to IS NULL OR l.valid_to >= $today)
            """;
        var list = party.List is { } own
            ? (await c.QueryAsync($"SELECT l.id, l.name, l.currency_code FROM price_lists l WHERE l.id = $id AND {vigente}",
                r => (Id: r.Guid("id"), Name: r.Str("name"), Currency: r.Str("currency_code")), tx, ("$id", own), ("$today", day))).FirstOrDefault()
            : default;
        if (list.Id == Guid.Empty && siteId is { } site)
            list = (await c.QueryAsync($"""
                SELECT l.id, l.name, l.currency_code FROM price_lists l
                WHERE {vigente} AND EXISTS (SELECT 1 FROM json_each(l.site_ids) WHERE value = $site) ORDER BY l.code LIMIT 1
                """, r => (Id: r.Guid("id"), Name: r.Str("name"), Currency: r.Str("currency_code")), tx, ("$site", site.ToString()), ("$today", day)))
                .FirstOrDefault();
        if (list.Id == Guid.Empty)
            return new PriceBook(null, null, currency, party.Discount, 1m, new Dictionary<Guid, IReadOnlyList<(decimal, decimal)>>());
        // La lista en otra moneda se convierte con el tipo de cambio del día; sin tipo de cambio no rige.
        var rate = await Fx.RateAsync(c, tx, list.Currency, currency, today);
        if (rate is null)
            return new PriceBook(null, null, currency, party.Discount, 1m, new Dictionary<Guid, IReadOnlyList<(decimal, decimal)>>());
        var lines = (await c.QueryAsync("SELECT item_id, min_qty, price FROM price_list_lines WHERE list_id = $id",
                r => (Item: r.Guid("item_id"), Min: r.Dec("min_qty"), Price: r.Dec("price")), tx, ("$id", list.Id)))
            .GroupBy(l => l.Item)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<(decimal, decimal)>)g.Select(l => (l.Min, l.Price)).OrderBy(x => x.Min).ToList());
        return new PriceBook(list.Id, list.Name, currency, party.Discount, rate.Value, lines);
    }

    internal static async Task ReplaceLinesAsync(SqliteConnection c, SqliteTransaction tx, Guid listId, IEnumerable<PriceListLineRow> lines)
    {
        await c.ExecAsync("DELETE FROM price_list_lines WHERE list_id = $id", tx, ("$id", listId));
        foreach (var line in lines.GroupBy(l => (l.ItemId, l.MinQuantity)).Select(g => g.Last()))
            await c.ExecAsync("INSERT INTO price_list_lines (list_id, item_id, min_qty, price) VALUES ($id, $item, $min, $price)", tx,
                ("$id", listId), ("$item", line.ItemId), ("$min", line.MinQuantity), ("$price", line.Price));
    }

    private static PriceListRow ReadRow(SqliteDataReader r) => new(
        r.Guid("id"), r.Str("code"), r.Str("name"), r.Str("currency_code"),
        r.StrOrNull("valid_from") is { } f ? DateOnly.Parse(f, System.Globalization.CultureInfo.InvariantCulture) : null,
        r.StrOrNull("valid_to") is { } t ? DateOnly.Parse(t, System.Globalization.CultureInfo.InvariantCulture) : null,
        JsonSerializer.Deserialize<List<Guid>>(r.Str("site_ids")) ?? [], r.Bool("is_active"), r.Bool("pending"), r.Int("items"), r.Int("customers"));
}
