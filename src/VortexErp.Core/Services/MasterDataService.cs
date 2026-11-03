using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using VortexApi.Client;
using VortexErp.Core.Data;
using VortexErp.Core.Domain;
using VortexErp.Core.Sync;

namespace VortexErp.Core.Services;

/// <summary>
/// Artículos, sedes, clientes, proveedores y empresa: datos del hub. El ERP guarda una copia que se actualiza <b>solo desde el
/// feed</b> (por Id y versión). Las altas y cambios se guardan localmente como pendientes y se encolan para el hub;
/// nunca existen solo en la base local.
/// </summary>
public sealed partial class MasterDataService(ErpDatabase db, IErpContext context, TimeProvider clock)
    : ErpService(db, context, clock)
{
    // ---------- Feed del hub ----------

    public async Task ApplyItemAsync(VortexMasterChange<VortexItem> change, CancellationToken ct = default)
    {
        var d = change.Data;
        await using var c = await Db.OpenAsync(ct);
        await c.ExecAsync("""
            INSERT INTO items (id, sku, name, kind, description, unit, barcode, category, price, currency_code, tax_code,
                               track_inventory, is_active, version, is_deleted, pending, updated_at)
            VALUES ($id, $sku, $name, $kind, $desc, $unit, $barcode, $category, $price, $currency, $tax, $track, $active,
                    $version, $deleted, 0, $updated)
            ON CONFLICT (id) DO UPDATE SET
                sku = excluded.sku, name = excluded.name, kind = excluded.kind, description = excluded.description,
                unit = excluded.unit, barcode = excluded.barcode, category = excluded.category, price = excluded.price,
                currency_code = excluded.currency_code, tax_code = excluded.tax_code,
                track_inventory = excluded.track_inventory, is_active = excluded.is_active, version = excluded.version,
                is_deleted = excluded.is_deleted, pending = 0, updated_at = excluded.updated_at
            WHERE excluded.version > items.version
            """, null, ("$id", change.EntityId), ("$sku", d.Sku), ("$name", d.Name), ("$kind", d.Kind), ("$desc", d.Description),
            ("$unit", d.Unit), ("$barcode", d.Barcode), ("$category", d.Category), ("$price", d.Price),
            ("$currency", d.CurrencyCode), ("$tax", d.TaxCode), ("$track", d.TrackInventory), ("$active", d.IsActive),
            ("$version", change.Version), ("$deleted", Deleted(change.Operation, d.Meta)), ("$updated", d.Meta.UpdatedAt));
    }

    public async Task ApplySiteAsync(VortexMasterChange<VortexSite> change, CancellationToken ct = default)
    {
        var d = change.Data;
        await using var c = await Db.OpenAsync(ct);
        await c.ExecAsync("""
            INSERT INTO sites (id, code, name, kind, phone, address_line, city, country_code, is_active, version, is_deleted, pending, updated_at)
            VALUES ($id, $code, $name, $kind, $phone, $line, $city, $country, $active, $version, $deleted, 0, $updated)
            ON CONFLICT (id) DO UPDATE SET
                code = excluded.code, name = excluded.name, kind = excluded.kind, phone = excluded.phone,
                address_line = excluded.address_line, city = excluded.city, country_code = excluded.country_code,
                is_active = excluded.is_active, version = excluded.version, is_deleted = excluded.is_deleted, pending = 0,
                updated_at = excluded.updated_at
            WHERE excluded.version > sites.version
            """, null, ("$id", change.EntityId), ("$code", d.Code), ("$name", d.Name), ("$kind", d.Kind), ("$phone", d.Phone),
            ("$line", d.Address?.Line), ("$city", d.Address?.City), ("$country", d.Address?.CountryCode),
            ("$active", d.IsActive), ("$version", change.Version), ("$deleted", Deleted(change.Operation, d.Meta)),
            ("$updated", d.Meta.UpdatedAt));
    }

    public async Task ApplyPartyAsync(VortexMasterChange<VortexParty> change, CancellationToken ct = default)
    {
        var d = change.Data;
        await using var c = await Db.OpenAsync(ct);
        await c.ExecAsync("""
            INSERT INTO parties (id, name, trade_name, kind, document_type, document_number, is_customer, is_supplier, email,
                                 phone, city, is_active, version, is_deleted, pending, updated_at)
            VALUES ($id, $name, $trade, $kind, $doctype, $docnum, $customer, $supplier, $email, $phone, $city, $active,
                    $version, $deleted, 0, $updated)
            ON CONFLICT (id) DO UPDATE SET
                name = excluded.name, trade_name = excluded.trade_name, kind = excluded.kind,
                document_type = excluded.document_type, document_number = excluded.document_number,
                is_customer = excluded.is_customer, is_supplier = excluded.is_supplier, email = excluded.email,
                phone = excluded.phone, city = excluded.city, is_active = excluded.is_active, version = excluded.version,
                is_deleted = excluded.is_deleted, pending = 0, updated_at = excluded.updated_at
            WHERE excluded.version > parties.version
            """, null, ("$id", change.EntityId), ("$name", d.Name), ("$trade", d.TradeName), ("$kind", d.Kind),
            ("$doctype", d.DocumentType), ("$docnum", d.DocumentNumber), ("$customer", d.IsCustomer),
            ("$supplier", d.IsSupplier), ("$email", d.Email), ("$phone", d.Phone), ("$city", d.Address?.City),
            ("$active", d.IsActive), ("$version", change.Version), ("$deleted", Deleted(change.Operation, d.Meta)),
            ("$updated", d.Meta.UpdatedAt));
    }

    public async Task ApplyCompanyAsync(VortexMasterChange<VortexCompany> change, CancellationToken ct = default)
    {
        var d = change.Data;
        await using var c = await Db.OpenAsync(ct);
        await c.ExecAsync("""
            INSERT INTO company (id, legal_name, trade_name, tax_id, country_code, currency_code, version)
            VALUES (1, $legal, $trade, $tax, $country, $currency, $version)
            ON CONFLICT (id) DO UPDATE SET legal_name = excluded.legal_name, trade_name = excluded.trade_name,
                tax_id = excluded.tax_id, country_code = excluded.country_code, currency_code = excluded.currency_code,
                version = excluded.version
            WHERE excluded.version > company.version
            """, null, ("$legal", d.LegalName), ("$trade", d.TradeName), ("$tax", d.TaxId), ("$country", d.CountryCode),
            ("$currency", d.CurrencyCode), ("$version", change.Version));
    }

    private static bool Deleted(VortexChangeOperation operation, VortexMasterMeta meta) =>
        operation == VortexChangeOperation.Deleted || meta.IsDeleted;

    // ---------- Altas y cambios (se encolan para el hub) ----------

    public Task<Guid> CreateItemAsync(ItemDraft draft, CancellationToken ct = default) => SaveItemAsync(null, draft, ct);

    public Task<Guid> UpdateItemAsync(Guid id, ItemDraft draft, CancellationToken ct = default) => SaveItemAsync(id, draft, ct);

    private Task<Guid> SaveItemAsync(Guid? existingId, ItemDraft draft, CancellationToken ct)
    {
        var sku = draft.Sku.Trim().ToUpperInvariant();
        Require(sku.Length > 0, "El SKU es obligatorio.");
        Require(!string.IsNullOrWhiteSpace(draft.Name), "El nombre es obligatorio.");
        Require(!string.IsNullOrWhiteSpace(draft.Unit), "La unidad es obligatoria.");
        Require(draft.Price >= 0, "El precio no puede ser negativo.");
        Require(draft.CurrencyCode.Trim().Length == 3, "La moneda debe ser un código ISO de 3 letras (PEN, USD…).");

        return InTransactionAsync(async (c, tx, now) =>
        {
            var duplicate = await c.ScalarAsync<long>(
                "SELECT COUNT(*) FROM items WHERE sku = $sku AND is_deleted = 0 AND id <> $id", tx,
                ("$sku", sku), ("$id", existingId ?? Guid.Empty));
            Require(duplicate == 0, $"Ya existe un artículo con el SKU {sku}.");

            var id = existingId ?? Guid.NewGuid();
            var version = existingId is null ? 0 : await c.ScalarAsync<int>("SELECT version FROM items WHERE id = $id", tx, ("$id", id));
            var body = new
            {
                sku, name = draft.Name.Trim(), kind = draft.Kind, description = Clean(draft.Description), unit = draft.Unit.Trim().ToLowerInvariant(),
                barcode = Clean(draft.Barcode), category = Clean(draft.Category), price = draft.Price,
                currencyCode = draft.CurrencyCode.Trim().ToUpperInvariant(), taxCode = Clean(draft.TaxCode),
                trackInventory = draft.Kind == "Product" && draft.TrackInventory, isActive = draft.IsActive,
            };

            await c.ExecAsync("""
                INSERT INTO items (id, sku, name, kind, description, unit, barcode, category, price, currency_code, tax_code,
                                   track_inventory, is_active, version, is_deleted, pending, updated_at)
                VALUES ($id, $sku, $name, $kind, $desc, $unit, $barcode, $category, $price, $currency, $tax, $track, $active,
                        0, 0, 1, $now)
                ON CONFLICT (id) DO UPDATE SET sku = $sku, name = $name, kind = $kind, description = $desc, unit = $unit,
                    barcode = $barcode, category = $category, price = $price, currency_code = $currency, tax_code = $tax,
                    track_inventory = $track, is_active = $active, pending = 1, updated_at = $now
                """, tx, ("$id", id), ("$sku", body.sku), ("$name", body.name), ("$kind", body.kind), ("$desc", body.description),
                ("$unit", body.unit), ("$barcode", body.barcode), ("$category", body.category), ("$price", body.price),
                ("$currency", body.currencyCode), ("$tax", body.taxCode), ("$track", body.trackInventory),
                ("$active", body.isActive), ("$now", now));

            await Outbox.EnqueueUpsertAsync(c, tx, now, VortexMasterResource.Items, id, body, version > 0 ? version : null);
            return id;
        }, ct);
    }

    public Task<Guid> CreateSiteAsync(SiteDraft draft, CancellationToken ct = default)
    {
        var code = draft.Code.Trim().ToUpperInvariant();
        Require(code.Length > 0, "El código es obligatorio.");
        Require(!string.IsNullOrWhiteSpace(draft.Name), "El nombre es obligatorio.");

        return InTransactionAsync(async (c, tx, now) =>
        {
            var duplicate = await c.ScalarAsync<long>("SELECT COUNT(*) FROM sites WHERE code = $code AND is_deleted = 0", tx, ("$code", code));
            Require(duplicate == 0, $"Ya existe una sede con el código {code}.");

            var id = Guid.NewGuid();
            var body = new
            {
                code, name = draft.Name.Trim(), kind = draft.Kind, phone = Clean(draft.Phone),
                address = new { line = Clean(draft.AddressLine), city = Clean(draft.City), countryCode = draft.CountryCode },
                isActive = draft.IsActive,
            };
            await c.ExecAsync("""
                INSERT INTO sites (id, code, name, kind, phone, address_line, city, country_code, is_active, version, is_deleted, pending, updated_at)
                VALUES ($id, $code, $name, $kind, $phone, $line, $city, $country, $active, 0, 0, 1, $now)
                """, tx, ("$id", id), ("$code", code), ("$name", body.name), ("$kind", body.kind), ("$phone", body.phone),
                ("$line", body.address.line), ("$city", body.address.city), ("$country", body.address.countryCode),
                ("$active", body.isActive), ("$now", now));
            await Outbox.EnqueueUpsertAsync(c, tx, now, VortexMasterResource.Sites, id, body);
            return id;
        }, ct);
    }

    public Task<Guid> CreatePartyAsync(PartyDraft draft, CancellationToken ct = default)
    {
        Require(!string.IsNullOrWhiteSpace(draft.Name), "El nombre es obligatorio.");
        Require(draft.IsCustomer || draft.IsSupplier, "Indica si es cliente, proveedor o ambos.");
        var docType = Clean(draft.DocumentType)?.ToUpperInvariant();
        var docNumber = Clean(draft.DocumentNumber);
        Require((docType is null) == (docNumber is null), "El tipo y el número de documento van juntos.");
        if (docType == "RUC")
            Require(Ruc().IsMatch(docNumber!), "El RUC debe tener 11 dígitos.");
        if (docType == "DNI")
            Require(Dni().IsMatch(docNumber!), "El DNI debe tener 8 dígitos.");

        return InTransactionAsync(async (c, tx, now) =>
        {
            if (docNumber is not null)
            {
                var existing = await c.ScalarAsync<string?>(
                    "SELECT name FROM parties WHERE document_number = $doc AND is_deleted = 0 LIMIT 1", tx, ("$doc", docNumber));
                Require(existing is null, $"El documento {docNumber} ya está registrado ({existing}).");
            }

            var id = Guid.NewGuid();
            var body = new
            {
                name = draft.Name.Trim(), isCustomer = draft.IsCustomer, isSupplier = draft.IsSupplier, kind = draft.Kind,
                documentType = docType, documentNumber = docNumber, email = Clean(draft.Email), phone = Clean(draft.Phone),
                address = draft.City is null ? null : new { city = Clean(draft.City), countryCode = draft.CountryCode },
                isActive = true,
            };
            await c.ExecAsync("""
                INSERT INTO parties (id, name, trade_name, kind, document_type, document_number, is_customer, is_supplier,
                                     email, phone, city, is_active, version, is_deleted, pending, updated_at)
                VALUES ($id, $name, NULL, $kind, $doctype, $docnum, $customer, $supplier, $email, $phone, $city, 1, 0, 0, 1, $now)
                """, tx, ("$id", id), ("$name", body.name), ("$kind", body.kind), ("$doctype", docType), ("$docnum", docNumber),
                ("$customer", body.isCustomer), ("$supplier", body.isSupplier), ("$email", body.email), ("$phone", body.phone),
                ("$city", Clean(draft.City)), ("$now", now));
            await Outbox.EnqueueUpsertAsync(c, tx, now, VortexMasterResource.Parties, id, body);
            return id;
        }, ct);
    }

    // ---------- Consultas ----------

    /// <summary>Catálogo con el stock que informa el WMS en <paramref name="siteId"/> (o en todas las sedes).</summary>
    public async Task<PagedResult<ItemRow>> GetItemsAsync(string? search, string? category, int page, int pageSize,
        bool includeInactive = true, Guid? siteId = null, CancellationToken ct = default)
    {
        await using var c = await Db.OpenAsync(ct);
        const string where = """
            WHERE i.is_deleted = 0 AND ($all = 1 OR i.is_active = 1)
              AND ($search IS NULL OR i.sku LIKE $like OR i.name LIKE $like OR i.barcode = $search)
              AND ($category IS NULL OR i.category = $category)
            """;
        var args = new (string, object?)[]
        {
            ("$all", includeInactive), ("$search", Clean(search)), ("$like", Like(search)), ("$category", Clean(category)),
            ("$site", siteId), ("$limit", pageSize), ("$offset", (page - 1) * pageSize),
        };
        var total = await c.ScalarAsync<int>($"SELECT COUNT(*) FROM items i {where}", null, args);
        var items = await c.QueryAsync($"""
            SELECT i.*, (SELECT SUM(s.on_hand) FROM stock s WHERE s.item_id = i.id AND ($site IS NULL OR s.site_id = $site)) AS on_hand
            FROM items i {where}
            ORDER BY i.name COLLATE NOCASE LIMIT $limit OFFSET $offset
            """, ReadItem, null, args);
        return new PagedResult<ItemRow>(items, total, page, pageSize);
    }

    public async Task<IReadOnlyList<ItemRow>> SearchItemsAsync(string? term, int limit = 8, Guid? siteId = null, CancellationToken ct = default) =>
        (await GetItemsAsync(term, null, 1, limit, includeInactive: false, siteId, ct)).Items;

    public async Task<ItemRow?> GetItemAsync(Guid id, Guid? siteId = null, CancellationToken ct = default)
    {
        await using var c = await Db.OpenAsync(ct);
        return await c.FirstOrDefaultAsync("""
            SELECT i.*, (SELECT SUM(s.on_hand) FROM stock s WHERE s.item_id = i.id AND ($site IS NULL OR s.site_id = $site)) AS on_hand
            FROM items i WHERE i.id = $id
            """, ReadItem, null, ("$id", id), ("$site", siteId));
    }

    public async Task<IReadOnlyList<string>> GetCategoriesAsync(CancellationToken ct = default)
    {
        await using var c = await Db.OpenAsync(ct);
        return await c.QueryAsync("""
            SELECT DISTINCT category FROM items WHERE is_deleted = 0 AND is_active = 1 AND category IS NOT NULL AND category <> ''
            ORDER BY category COLLATE NOCASE
            """, r => r.Str("category"));
    }

    public async Task<IReadOnlyList<SiteRow>> GetSitesAsync(bool activeOnly = false, CancellationToken ct = default)
    {
        await using var c = await Db.OpenAsync(ct);
        return await c.QueryAsync("""
            SELECT t.*,
                   (SELECT COUNT(*) FROM sales_orders o WHERE o.site_id = t.id AND o.status = 'open') AS open_orders,
                   (SELECT COUNT(*) FROM purchase_orders p WHERE p.site_id = t.id AND p.status IN ('open', 'partial')) AS open_purchases
            FROM sites t
            WHERE t.is_deleted = 0 AND ($active = 0 OR t.is_active = 1)
            ORDER BY CASE t.kind WHEN 'Warehouse' THEN 0 ELSE 1 END, t.code
            """, r => new SiteRow(r.Guid("id"), r.Str("code"), r.Str("name"), r.Str("kind"), r.StrOrNull("phone"),
                r.StrOrNull("address_line"), r.StrOrNull("city"), r.StrOrNull("country_code"), r.Bool("is_active"),
                r.Int("version"), r.Bool("pending"), r.Int("open_orders"), r.Int("open_purchases")), null, ("$active", activeOnly));
    }

    public async Task<PagedResult<PartyRow>> GetPartiesAsync(string? search, bool? customers, bool? suppliers, int page,
        int pageSize, CancellationToken ct = default)
    {
        await using var c = await Db.OpenAsync(ct);
        const string where = """
            WHERE is_deleted = 0
              AND ($search IS NULL OR name LIKE $like OR trade_name LIKE $like OR document_number LIKE $like OR email LIKE $like)
              AND ($customers IS NULL OR is_customer = $customers)
              AND ($suppliers IS NULL OR is_supplier = $suppliers)
            """;
        var args = new (string, object?)[]
        {
            ("$search", Clean(search)), ("$like", Like(search)), ("$customers", customers), ("$suppliers", suppliers),
            ("$limit", pageSize), ("$offset", (page - 1) * pageSize),
        };
        var total = await c.ScalarAsync<int>($"SELECT COUNT(*) FROM parties {where}", null, args);
        var rows = await c.QueryAsync($"SELECT * FROM parties {where} ORDER BY name COLLATE NOCASE LIMIT $limit OFFSET $offset",
            ReadParty, null, args);
        return new PagedResult<PartyRow>(rows, total, page, pageSize);
    }

    public async Task<IReadOnlyList<Option>> GetPartyOptionsAsync(bool suppliers, CancellationToken ct = default)
    {
        await using var c = await Db.OpenAsync(ct);
        return await c.QueryAsync($"""
            SELECT id, name, document_number FROM parties
            WHERE is_deleted = 0 AND is_active = 1 AND {(suppliers ? "is_supplier" : "is_customer")} = 1
            ORDER BY name COLLATE NOCASE
            """, r => new Option(r.Guid("id"), r.Str("name"), r.StrOrNull("document_number")));
    }

    public async Task<CompanyInfo?> GetCompanyAsync(CancellationToken ct = default)
    {
        await using var c = await Db.OpenAsync(ct);
        return await c.FirstOrDefaultAsync("SELECT * FROM company WHERE id = 1", r => new CompanyInfo(
            r.Str("legal_name"), r.StrOrNull("trade_name"), r.StrOrNull("tax_id"), r.Str("country_code"), r.Str("currency_code")));
    }

    internal static ItemRow ReadItem(SqliteDataReader r) => new(
        r.Guid("id"), r.Str("sku"), r.Str("name"), r.Str("kind"), r.StrOrNull("description"), r.Str("unit"),
        r.StrOrNull("barcode"), r.StrOrNull("category"), r.Dec("price"), r.Str("currency_code"), r.StrOrNull("tax_code"),
        r.Bool("track_inventory"), r.Bool("is_active"), r.Int("version"), r.Bool("pending"), r.DecOrNull("on_hand"));

    private static PartyRow ReadParty(SqliteDataReader r) => new(
        r.Guid("id"), r.Str("name"), r.StrOrNull("trade_name"), r.Str("kind"), r.StrOrNull("document_type"),
        r.StrOrNull("document_number"), r.Bool("is_customer"), r.Bool("is_supplier"), r.StrOrNull("email"),
        r.StrOrNull("phone"), r.StrOrNull("city"), r.Bool("is_active"), r.Int("version"), r.Bool("pending"));

    [GeneratedRegex(@"^\d{11}$")]
    private static partial Regex Ruc();

    [GeneratedRegex(@"^\d{8}$")]
    private static partial Regex Dni();
}
