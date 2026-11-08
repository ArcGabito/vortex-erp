using Microsoft.Data.Sqlite;

namespace VortexErp.Core.Data;

/// <summary>
/// Base local del ERP (SQLite, una por organización). Guarda la copia de los datos maestros del hub, los pedidos,
/// comprobantes, cobranzas y compras propios del ERP, lo que informan los otros sistemas (ventas del POS, cotizaciones
/// del CRM, stock del WMS) y la cola de sincronización, para que todo funcione sin conexión y cada documento quede en
/// la misma transacción que su evento.
/// </summary>
public sealed class ErpDatabase
{
    public ErpDatabase(string path)
    {
        Path = path;
        ConnectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = true,
            DefaultTimeout = 30,
        }.ToString();
    }

    public string Path { get; }
    public string ConnectionString { get; }

    public async Task<SqliteConnection> OpenAsync(CancellationToken ct = default)
    {
        var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(ct);
        await connection.ExecAsync("PRAGMA foreign_keys = OFF; PRAGMA synchronous = NORMAL;");
        return connection;
    }

    /// <summary>Crea o actualiza el esquema. Cada versión se aplica una sola vez (<c>PRAGMA user_version</c>).</summary>
    public async Task MigrateAsync(CancellationToken ct = default)
    {
        var directory = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path));
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        await using var connection = await OpenAsync(ct);
        await connection.ExecAsync("PRAGMA journal_mode = WAL;");
        var current = await connection.ScalarAsync<long>("PRAGMA user_version;");
        for (var version = (int)current; version < Schema.Migrations.Length; version++)
        {
            await using var tx = connection.BeginTransaction();
            await connection.ExecAsync(Schema.Migrations[version], tx);
            await connection.ExecAsync($"PRAGMA user_version = {version + 1};", tx);
            await tx.CommitAsync(ct);
        }
    }
}

internal static class Schema
{
    /// <summary>Migraciones en orden. Nunca modificar una ya publicada: agregar una nueva al final.</summary>
    public static readonly string[] Migrations =
    [
        """
        -- Copia local de datos maestros del hub (dueño: hub). pending = cambio local aún no confirmado por el hub.
        CREATE TABLE items (
            id TEXT PRIMARY KEY, sku TEXT NOT NULL, name TEXT NOT NULL, kind TEXT NOT NULL, description TEXT,
            unit TEXT NOT NULL, barcode TEXT, category TEXT, price REAL NOT NULL, currency_code TEXT NOT NULL,
            tax_code TEXT, track_inventory INTEGER NOT NULL, is_active INTEGER NOT NULL, version INTEGER NOT NULL,
            is_deleted INTEGER NOT NULL DEFAULT 0, pending INTEGER NOT NULL DEFAULT 0, updated_at TEXT NOT NULL);
        CREATE INDEX ix_items_sku ON items (sku);
        CREATE INDEX ix_items_barcode ON items (barcode);
        CREATE INDEX ix_items_category ON items (category);

        CREATE TABLE sites (
            id TEXT PRIMARY KEY, code TEXT NOT NULL, name TEXT NOT NULL, kind TEXT NOT NULL, phone TEXT,
            address_line TEXT, city TEXT, country_code TEXT, is_active INTEGER NOT NULL, version INTEGER NOT NULL,
            is_deleted INTEGER NOT NULL DEFAULT 0, pending INTEGER NOT NULL DEFAULT 0, updated_at TEXT NOT NULL);

        CREATE TABLE parties (
            id TEXT PRIMARY KEY, name TEXT NOT NULL, trade_name TEXT, kind TEXT NOT NULL, document_type TEXT,
            document_number TEXT, is_customer INTEGER NOT NULL, is_supplier INTEGER NOT NULL, email TEXT, phone TEXT,
            city TEXT, is_active INTEGER NOT NULL, version INTEGER NOT NULL, is_deleted INTEGER NOT NULL DEFAULT 0,
            pending INTEGER NOT NULL DEFAULT 0, updated_at TEXT NOT NULL);
        CREATE INDEX ix_parties_document ON parties (document_number);

        CREATE TABLE company (
            id INTEGER PRIMARY KEY CHECK (id = 1), legal_name TEXT NOT NULL, trade_name TEXT, tax_id TEXT,
            country_code TEXT NOT NULL, currency_code TEXT NOT NULL, version INTEGER NOT NULL);

        -- Espejo del stock que publica el WMS (stock.adjusted, saldo absoluto). El ERP lo valoriza, no lo modifica.
        CREATE TABLE stock (
            site_id TEXT NOT NULL, item_id TEXT NOT NULL, on_hand REAL NOT NULL, updated_at TEXT NOT NULL,
            PRIMARY KEY (site_id, item_id));

        -- Pedidos de venta: creados en el ERP o recibidos del CRM/WMS (order.created). source = sistema de origen.
        CREATE TABLE sales_orders (
            id TEXT PRIMARY KEY, number TEXT NOT NULL, source TEXT NOT NULL, customer_id TEXT, site_id TEXT, quote_id TEXT,
            status TEXT NOT NULL, currency_code TEXT NOT NULL, total REAL NOT NULL, created_at TEXT NOT NULL, user_name TEXT,
            dispatched_at TEXT, shipment_number TEXT, carrier TEXT, tracking_number TEXT, invoice_id TEXT);
        CREATE INDEX ix_sales_orders_status ON sales_orders (status, created_at);
        CREATE TABLE sales_order_lines (
            order_id TEXT NOT NULL, line_no INTEGER NOT NULL, item_id TEXT NOT NULL, quantity REAL NOT NULL,
            unit_price REAL NOT NULL, PRIMARY KEY (order_id, line_no));

        -- Cotizaciones aceptadas y oportunidades ganadas en el CRM (quote.accepted, deal.won).
        CREATE TABLE accepted_quotes (
            id TEXT PRIMARY KEY, number TEXT, customer_id TEXT, currency_code TEXT NOT NULL, total REAL NOT NULL,
            accepted_at TEXT NOT NULL, order_id TEXT);
        CREATE TABLE accepted_quote_lines (
            quote_id TEXT NOT NULL, line_no INTEGER NOT NULL, item_id TEXT NOT NULL, quantity REAL NOT NULL,
            unit_price REAL NOT NULL, PRIMARY KEY (quote_id, line_no));
        CREATE TABLE won_deals (
            id TEXT PRIMARY KEY, customer_id TEXT, amount REAL NOT NULL, currency_code TEXT NOT NULL, quote_id TEXT,
            won_at TEXT NOT NULL);

        -- Ventas de caja del POS (sale.*): ingresos del día y base para emitir su comprobante.
        CREATE TABLE pos_sales (
            id TEXT PRIMARY KEY, number TEXT NOT NULL, site_id TEXT, customer_id TEXT, status TEXT NOT NULL,
            currency_code TEXT NOT NULL, subtotal REAL NOT NULL, tax REAL NOT NULL, total REAL NOT NULL,
            credit REAL NOT NULL DEFAULT 0, returned REAL NOT NULL DEFAULT 0, created_at TEXT NOT NULL, invoice_id TEXT);
        CREATE INDEX ix_pos_sales_time ON pos_sales (created_at);
        CREATE TABLE pos_sale_lines (
            sale_id TEXT NOT NULL, line_no INTEGER NOT NULL, item_id TEXT NOT NULL, name TEXT, quantity REAL NOT NULL,
            unit_price REAL NOT NULL, total REAL NOT NULL, taxable INTEGER NOT NULL, PRIMARY KEY (sale_id, line_no));

        -- Comprobantes (factura/boleta). origin: 'local' o 'remote' (emitido en otro equipo del ERP).
        CREATE TABLE invoices (
            id TEXT PRIMARY KEY, number TEXT NOT NULL, kind TEXT NOT NULL, origin TEXT NOT NULL, customer_id TEXT,
            order_id TEXT, sale_id TEXT, issued_at TEXT NOT NULL, due_date TEXT, currency_code TEXT NOT NULL,
            subtotal REAL NOT NULL, tax REAL NOT NULL, total REAL NOT NULL, paid REAL NOT NULL DEFAULT 0, user_name TEXT);
        CREATE INDEX ix_invoices_time ON invoices (issued_at);
        CREATE INDEX ix_invoices_customer ON invoices (customer_id);
        CREATE TABLE invoice_lines (
            invoice_id TEXT NOT NULL, line_no INTEGER NOT NULL, item_id TEXT, description TEXT NOT NULL,
            quantity REAL NOT NULL, unit_price REAL NOT NULL, total REAL NOT NULL, PRIMARY KEY (invoice_id, line_no));

        CREATE TABLE payments (
            id TEXT PRIMARY KEY, number TEXT NOT NULL, customer_id TEXT, invoice_id TEXT, sale_id TEXT, amount REAL NOT NULL,
            currency_code TEXT NOT NULL, method TEXT NOT NULL, reference TEXT, received_at TEXT NOT NULL, user_name TEXT);
        CREATE INDEX ix_payments_invoice ON payments (invoice_id);

        -- Compras a proveedores.
        CREATE TABLE purchase_orders (
            id TEXT PRIMARY KEY, number TEXT NOT NULL, supplier_id TEXT NOT NULL, site_id TEXT NOT NULL, status TEXT NOT NULL,
            currency_code TEXT NOT NULL, total REAL NOT NULL, created_at TEXT NOT NULL, expected_at TEXT, received_at TEXT,
            user_name TEXT);
        CREATE INDEX ix_purchase_orders_status ON purchase_orders (status, created_at);
        CREATE TABLE purchase_order_lines (
            po_id TEXT NOT NULL, line_no INTEGER NOT NULL, item_id TEXT NOT NULL, quantity REAL NOT NULL,
            unit_cost REAL NOT NULL, received_qty REAL NOT NULL DEFAULT 0, PRIMARY KEY (po_id, line_no));

        CREATE TABLE document_sequences (prefix TEXT PRIMARY KEY, value INTEGER NOT NULL);

        -- Integración: eventos ya aplicados (idempotencia) y estado de VortexSync (cursores, cola, rechazos).
        CREATE TABLE processed_events (
            event_id TEXT PRIMARY KEY, type TEXT NOT NULL, processed_at TEXT NOT NULL, note TEXT);
        CREATE TABLE sync_cursors (stream TEXT PRIMARY KEY, cursor INTEGER NOT NULL);
        CREATE TABLE sync_outbox (seq INTEGER PRIMARY KEY AUTOINCREMENT, id TEXT NOT NULL UNIQUE, entry TEXT NOT NULL);
        CREATE TABLE sync_failed (
            id TEXT PRIMARY KEY, entry TEXT NOT NULL, error_code TEXT NOT NULL, error TEXT NOT NULL, failed_at TEXT NOT NULL);
        """,
        // 2: base común de la suite. Impuesto y zona horaria de la empresa (del hub), categoría de impuesto por línea y
        // bitácora de auditoría.
        """
        ALTER TABLE company ADD COLUMN default_tax_rate REAL;
        ALTER TABLE company ADD COLUMN time_zone TEXT;
        ALTER TABLE pos_sale_lines ADD COLUMN tax_code TEXT;
        UPDATE pos_sale_lines SET tax_code = CASE taxable WHEN 1 THEN 'standard' ELSE 'exempt' END;
        ALTER TABLE invoice_lines ADD COLUMN tax_code TEXT;
        """ + VortexApi.Suite.Auditing.AuditSchema.Sql,
        // 3: notas de crédito y débito vinculadas al comprobante y anulación. El saldo del comprobante suma lo debitado y
        // resta lo acreditado; credited_qty lleva lo devuelto de cada línea para no devolverlo dos veces.
        """
        ALTER TABLE invoices ADD COLUMN status TEXT NOT NULL DEFAULT 'issued';
        ALTER TABLE invoices ADD COLUMN voided_at TEXT;
        ALTER TABLE invoices ADD COLUMN void_reason TEXT;
        ALTER TABLE invoices ADD COLUMN credited REAL NOT NULL DEFAULT 0;
        ALTER TABLE invoices ADD COLUMN debited REAL NOT NULL DEFAULT 0;
        ALTER TABLE invoice_lines ADD COLUMN credited_qty REAL NOT NULL DEFAULT 0;

        CREATE TABLE invoice_notes (
            id TEXT PRIMARY KEY, number TEXT NOT NULL, kind TEXT NOT NULL, origin TEXT NOT NULL, invoice_id TEXT NOT NULL,
            customer_id TEXT, reason TEXT NOT NULL, description TEXT, issued_at TEXT NOT NULL, currency_code TEXT NOT NULL,
            subtotal REAL NOT NULL, tax REAL NOT NULL, total REAL NOT NULL, user_name TEXT);
        CREATE INDEX ix_invoice_notes_invoice ON invoice_notes (invoice_id);
        CREATE INDEX ix_invoice_notes_customer ON invoice_notes (customer_id, issued_at);
        CREATE TABLE invoice_note_lines (
            note_id TEXT NOT NULL, line_no INTEGER NOT NULL, item_id TEXT, description TEXT NOT NULL, quantity REAL NOT NULL,
            unit_price REAL NOT NULL, total REAL NOT NULL, tax_code TEXT, invoice_line_no INTEGER, PRIMARY KEY (note_id, line_no));
        CREATE INDEX ix_payments_customer ON payments (customer_id, received_at);
        """,
        // 4: multimoneda. Tipos de cambio del hub (copia local para emitir y cobrar sin conexión); cada comprobante guarda
        // el del día de emisión a la moneda de la empresa y cada cobro en otra moneda, el del día y la diferencia de cambio
        // (en moneda de la empresa). Se borra el cursor de datos maestros para volver a bajarlos con sus tipos de cambio.
        """
        CREATE TABLE exchange_rates (
            id TEXT PRIMARY KEY, date TEXT NOT NULL, from_currency TEXT NOT NULL, to_currency TEXT NOT NULL, rate REAL NOT NULL,
            buy_rate REAL, sell_rate REAL, source TEXT, version INTEGER NOT NULL, is_deleted INTEGER NOT NULL DEFAULT 0);
        CREATE INDEX ix_exchange_rates_pair ON exchange_rates (from_currency, to_currency, date);
        ALTER TABLE invoices ADD COLUMN exchange_rate REAL;
        ALTER TABLE payments ADD COLUMN exchange_rate REAL;
        ALTER TABLE payments ADD COLUMN paid_amount REAL;
        ALTER TABLE payments ADD COLUMN paid_currency TEXT;
        ALTER TABLE payments ADD COLUMN fx_difference REAL;
        DELETE FROM sync_cursors WHERE stream = 'master';
        """,
        // 5: crédito del cliente (fase 3a). Condiciones comerciales del party del hub (plazo, límite, descuento) y una copia
        // del cliente tal como llega del hub, para devolverlo completo al cambiar las condiciones. Un pedido que supera el
        // límite queda por aprobar. Se vuelven a bajar los clientes para traer sus condiciones.
        """
        ALTER TABLE parties ADD COLUMN payment_term_days INTEGER;
        ALTER TABLE parties ADD COLUMN credit_limit REAL;
        ALTER TABLE parties ADD COLUMN discount_percent REAL;
        ALTER TABLE parties ADD COLUMN hub_data TEXT;
        ALTER TABLE sales_orders ADD COLUMN approval TEXT;
        ALTER TABLE sales_orders ADD COLUMN approved_by TEXT;
        ALTER TABLE sales_orders ADD COLUMN approved_at TEXT;
        DELETE FROM sync_cursors WHERE stream = 'master';
        """,
    ];
}
