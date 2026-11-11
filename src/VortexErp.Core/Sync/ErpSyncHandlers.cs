using System.Text.Json;
using Microsoft.Data.Sqlite;
using VortexApi.Client;
using VortexApi.Client.Sync;
using VortexErp.Core.Data;
using VortexErp.Core.Integration;
using VortexErp.Core.Services;

namespace VortexErp.Core.Sync;

/// <summary>
/// Qué hace el ERP con lo que llega del hub:
/// <list type="bullet">
/// <item>Actualiza la copia de datos maestros (artículos, sedes, clientes y proveedores, empresa).</item>
/// <item>Registra lo que hacen los otros sistemas: ventas de caja (POS), cotizaciones aceptadas y oportunidades ganadas
/// (CRM), pedidos (CRM, WMS), despachos, recepciones y stock (WMS).</item>
/// <item>Replica lo que emiten <b>otros equipos del ERP</b> (pedidos, comprobantes, cobros y compras), que llegan como sus
/// propios eventos. Requiere <see cref="VortexSyncOptions.IncludeOwnEvents"/>.</item>
/// </list>
/// Cada evento se aplica una sola vez (tabla <c>processed_events</c>) y en una transacción junto con su efecto.
/// </summary>
public sealed class ErpSyncHandlers(ErpDatabase db, MasterDataService master, TimeProvider clock)
{
    public const string ProductCode = "ERP";

    /// <summary>Opciones de <see cref="VortexSync"/> que necesita el ERP (recibir lo que emiten sus otros equipos).</summary>
    public static VortexSyncOptions SyncOptions => new() { IncludeOwnEvents = true };

    /// <summary>Registra todos los handlers. Llamar antes de la primera sincronización.</summary>
    public VortexSync Register(VortexSync sync) => sync
        .OnItems((c, ct) => master.ApplyItemAsync(c, ct))
        .OnSites((c, ct) => master.ApplySiteAsync(c, ct))
        .OnParties((c, ct) => master.ApplyPartyAsync(c, ct))
        .OnCompany((c, ct) => master.ApplyCompanyAsync(c, ct))
        .OnExchangeRates((c, ct) => master.ApplyExchangeRateAsync(c, ct))
        .OnPriceLists((c, ct) => master.ApplyPriceListAsync(c, ct))
        .OnEvent(SuiteEvents.OrderCreated, HandleOrderCreatedAsync)
        .OnEvent(SuiteEvents.OrderApproved, HandleOrderApprovedAsync)
        .OnEvent(SuiteEvents.QuoteAccepted, HandleQuoteAcceptedAsync)
        .OnEvent(SuiteEvents.DealWon, HandleDealWonAsync)
        .OnEvent(SuiteEvents.ShipmentDispatched, HandleShipmentDispatchedAsync)
        .OnEvent(SuiteEvents.CashDeposited, HandleCashDepositedAsync)
        .OnEvent(SuiteEvents.SaleCompleted, HandleSaleCompletedAsync)
        .OnEvent(SuiteEvents.SaleVoided, HandleSaleVoidedAsync)
        .OnEvent(SuiteEvents.SaleReturned, HandleSaleReturnedAsync)
        .OnEvent(SuiteEvents.InvoiceIssued, HandleInvoiceIssuedAsync)
        .OnEvent(SuiteEvents.PaymentReceived, HandlePaymentReceivedAsync)
        .OnEvent(SuiteEvents.CreditNoteIssued, HandleCreditNoteIssuedAsync)
        .OnEvent(SuiteEvents.DebitNoteIssued, HandleDebitNoteIssuedAsync)
        .OnEvent(SuiteEvents.InvoiceVoided, HandleInvoiceVoidedAsync)
        .OnEvent(SuiteEvents.PurchaseOrderCreated, HandlePurchaseOrderCreatedAsync)
        .OnEvent(SuiteEvents.PurchaseOrderCancelled, HandlePurchaseOrderCancelledAsync)
        .OnEvent(SuiteEvents.RecordUpserted, HandleRecordUpsertedAsync)
        .OnEvent(SuiteEvents.GoodsReceived, HandleGoodsReceivedAsync)
        .OnEvent(SuiteEvents.PurchaseRequested, HandlePurchaseRequestedAsync)
        .OnEvent(SuiteEvents.StockAdjusted, HandleStockAdjustedAsync);

    // ---------- Ventas: CRM, WMS, POS ----------

    /// <summary>Pedido del CRM, del WMS (funcionando solo) o de otro equipo del ERP. Los propios ya existen.</summary>
    public Task HandleOrderCreatedAsync(VortexEvent e, CancellationToken ct = default) =>
        ApplyOnceAsync<OrderCreated>(e, async (order, c, tx) =>
        {
            if (order.OrderId == Guid.Empty)
                return "sin orderId";
            await OrderService.ApplyOrderCreatedAsync(c, tx, e.OccurredAt, e.Source, order);
            return null;
        }, ct);

    /// <summary>Otro equipo del ERP aprobó un pedido que superaba el límite de crédito.</summary>
    public Task HandleOrderApprovedAsync(VortexEvent e, CancellationToken ct = default) =>
        ApplyOnceAsync<OrderApproved>(e, async (approved, c, tx) =>
        {
            if (approved.OrderId == Guid.Empty)
                return "sin orderId";
            await OrderService.ApplyApprovedAsync(c, tx, e.OccurredAt, approved.OrderId, approved.ApprovedBy);
            return null;
        }, ct);

    public Task HandleQuoteAcceptedAsync(VortexEvent e, CancellationToken ct = default) =>
        ApplyOnceAsync<QuoteAccepted>(e, async (quote, c, tx) =>
        {
            if (quote.QuoteId == Guid.Empty)
                return "sin quoteId";
            await OrderService.ApplyQuoteAcceptedAsync(c, tx, e.OccurredAt, quote);
            return null;
        }, ct);

    public Task HandleDealWonAsync(VortexEvent e, CancellationToken ct = default) =>
        ApplyOnceAsync<DealWon>(e, async (deal, c, tx) =>
        {
            await OrderService.ApplyDealWonAsync(c, tx, e.OccurredAt, deal);
            return null;
        }, ct);

    public Task HandleShipmentDispatchedAsync(VortexEvent e, CancellationToken ct = default) =>
        ApplyOnceAsync<ShipmentDispatched>(e, async (d, c, tx) =>
        {
            await OrderService.ApplyDispatchedAsync(c, tx, e.OccurredAt, d);
            return null;
        }, ct);

    public Task HandleSaleCompletedAsync(VortexEvent e, CancellationToken ct = default) =>
        ApplyOnceAsync<SaleCompleted>(e, async (sale, c, tx) =>
        {
            if (sale.SaleId == Guid.Empty)
                return "sin saleId";
            await InvoiceService.ApplySaleCompletedAsync(c, tx, e.OccurredAt, sale);
            return null;
        }, ct);

    public Task HandleSaleVoidedAsync(VortexEvent e, CancellationToken ct = default) =>
        ApplyOnceAsync<SaleVoided>(e, async (voided, c, tx) =>
        {
            await InvoiceService.ApplySaleVoidedAsync(c, tx, voided);
            return null;
        }, ct);

    public Task HandleSaleReturnedAsync(VortexEvent e, CancellationToken ct = default) =>
        ApplyOnceAsync<SaleReturned>(e, async (ret, c, tx) =>
        {
            await InvoiceService.ApplySaleReturnedAsync(c, tx, ret);
            return null;
        }, ct);

    // ---------- Otros equipos del ERP ----------

    public Task HandleInvoiceIssuedAsync(VortexEvent e, CancellationToken ct = default) =>
        ApplyOnceAsync<InvoiceIssued>(e, async (invoice, c, tx) =>
        {
            if (e.Source != ProductCode)
                return "ignorado: los comprobantes solo los emite el ERP";
            if (invoice.InvoiceId == Guid.Empty || string.IsNullOrWhiteSpace(invoice.Number))
                return "sin invoiceId o número";
            await InvoiceService.ApplyInvoiceIssuedAsync(c, tx, invoice);
            return null;
        }, ct);

    /// <summary>Cobro registrado en otro equipo del ERP (o, a futuro, en el POS).</summary>
    public Task HandlePaymentReceivedAsync(VortexEvent e, CancellationToken ct = default) =>
        ApplyOnceAsync<PaymentReceived>(e, async (payment, c, tx) =>
        {
            if (payment.PaymentId == Guid.Empty || payment.Amount <= 0)
                return "sin paymentId o monto";
            await InvoiceService.ApplyPaymentReceivedAsync(c, tx, e.OccurredAt, payment);
            return null;
        }, ct);

    public Task HandleCreditNoteIssuedAsync(VortexEvent e, CancellationToken ct = default) => HandleNoteAsync(e, Domain.NoteKinds.Credit, ct);

    public Task HandleDebitNoteIssuedAsync(VortexEvent e, CancellationToken ct = default) => HandleNoteAsync(e, Domain.NoteKinds.Debit, ct);

    /// <summary>Nota de crédito o débito de otro equipo del ERP (solo el ERP las emite).</summary>
    private Task HandleNoteAsync(VortexEvent e, string kind, CancellationToken ct) =>
        ApplyOnceAsync<InvoiceNoteIssued>(e, async (note, c, tx) =>
        {
            if (e.Source != ProductCode)
                return "ignorado: las notas solo las emite el ERP";
            if (note.NoteId == Guid.Empty || note.InvoiceId == Guid.Empty || note.Total <= 0)
                return "sin noteId, invoiceId o total";
            await InvoiceService.ApplyNoteIssuedAsync(c, tx, kind, note);
            return null;
        }, ct);

    /// <summary>Comprobante anulado en otro equipo del ERP.</summary>
    public Task HandleInvoiceVoidedAsync(VortexEvent e, CancellationToken ct = default) =>
        ApplyOnceAsync<InvoiceVoided>(e, async (voided, c, tx) =>
        {
            if (e.Source != ProductCode)
                return "ignorado: los comprobantes solo los anula el ERP";
            if (voided.InvoiceId == Guid.Empty)
                return "sin invoiceId";
            await InvoiceService.ApplyInvoiceVoidedAsync(c, tx, e.OccurredAt, voided);
            return null;
        }, ct);

    public Task HandlePurchaseOrderCreatedAsync(VortexEvent e, CancellationToken ct = default) =>
        ApplyOnceAsync<PurchaseOrderCreated>(e, async (po, c, tx) =>
        {
            if (e.Source != ProductCode)
                return "ignorado: las órdenes de compra solo las emite el ERP";
            await PurchaseService.ApplyCreatedAsync(c, tx, e.OccurredAt, po);
            return null;
        }, ct);

    /// <summary>Orden de compra cancelada en otro equipo del ERP.</summary>
    public Task HandlePurchaseOrderCancelledAsync(VortexEvent e, CancellationToken ct = default) =>
        ApplyOnceAsync<PurchaseOrderCancelled>(e, async (po, c, tx) =>
        {
            if (e.Source != ProductCode)
                return "ignorado: las órdenes de compra solo las cancela el ERP";
            await PurchaseService.ApplyCancelledAsync(c, tx, po);
            return null;
        }, ct);

    /// <summary>Sugerencia de compra del WMS: queda como solicitud por aprobar con el mismo Id (ERP-26).</summary>
    public Task HandlePurchaseRequestedAsync(VortexEvent e, CancellationToken ct = default) =>
        ApplyOnceAsync<PurchaseRequested>(e, (d, c, tx) =>
            e.Source != "WMS" ? Task.FromResult<string?>("ignorado: las sugerencias de compra las envía el WMS")
                : PayablesService.ApplyWarehouseRequestAsync(c, tx, e.OccurredAt, d), ct);

    /// <summary>Efectivo de una caja del POS depositado en el banco: entra a la cuenta bancaria para conciliarlo (ERP-39).</summary>
    public Task HandleCashDepositedAsync(VortexEvent e, CancellationToken ct = default) =>
        ApplyOnceAsync<CashDeposited>(e, (d, c, tx) => TreasuryService.ApplyPosDepositAsync(c, tx, e.OccurredAt, d), ct);

    /// <summary>Solicitudes, facturas y pagos a proveedores, ajustes de compras y tesorería de otro equipo del ERP (gana el más nuevo).</summary>
    public Task HandleRecordUpsertedAsync(VortexEvent e, CancellationToken ct = default) =>
        ApplyOnceAsync<ErpRecord>(e, async (r, c, tx) =>
        {
            if (e.Source != ProductCode)
                return "ignorado: solo lo publica el ERP";
            var applied = TreasuryService.Handles(r.Kind) ? await TreasuryService.ApplyAsync(c, tx, r)
                : AccountingService.Handles(r.Kind) ? await AccountingService.ApplyAsync(c, tx, r)
                : await PayablesService.ApplyAsync(c, tx, r);
            return applied ? null : $"sin cambios ({r.Kind}: ya tenía uno más nuevo o es desconocido)";
        }, ct);

    // ---------- WMS ----------

    public Task HandleGoodsReceivedAsync(VortexEvent e, CancellationToken ct = default) =>
        ApplyOnceAsync<GoodsReceived>(e, (g, c, tx) => PurchaseService.ApplyGoodsReceivedAsync(c, tx, e.OccurredAt, g), ct);

    /// <summary>Stock del WMS: se guarda el saldo absoluto (<c>quantityOnHand</c>); el feed llega en orden y el último gana.</summary>
    public Task HandleStockAdjustedAsync(VortexEvent e, CancellationToken ct = default) =>
        ApplyOnceAsync<StockAdjusted>(e, async (s, c, tx) =>
        {
            if (s.SiteId == Guid.Empty || s.ItemId == Guid.Empty)
                return "sin siteId o itemId";
            await c.ExecAsync("""
                INSERT INTO stock (site_id, item_id, on_hand, updated_at) VALUES ($site, $item, $qty, $at)
                ON CONFLICT (site_id, item_id) DO UPDATE SET on_hand = $qty, updated_at = $at
                """, tx, ("$site", s.SiteId), ("$item", s.ItemId), ("$qty", s.QuantityOnHand), ("$at", e.OccurredAt));
            return null;
        }, ct);

    // ---------- Internos ----------

    /// <summary>
    /// Aplica un evento una sola vez. Un esquema futuro o un <c>data</c> ilegible se registran como ignorados (nunca
    /// se van a poder aplicar y no deben trabar el feed); un error de la base sí se propaga para reintentar.
    /// </summary>
    private async Task ApplyOnceAsync<T>(VortexEvent e, Func<T, SqliteConnection, SqliteTransaction, Task<string?>> apply,
        CancellationToken ct) where T : class
    {
        await using var c = await db.OpenAsync(ct);
        await using var tx = c.BeginTransaction();
        if (await c.ScalarAsync<long>("SELECT COUNT(*) FROM processed_events WHERE event_id = $id", tx, ("$id", e.Id)) > 0)
            return;

        string? note;
        if (e.SchemaVersion != 1)
        {
            note = $"ignorado: schemaVersion {e.SchemaVersion} no soportada";
        }
        else
        {
            T? data = null;
            try
            {
                data = e.DataAs<T>();
                note = data is null ? "ignorado: data vacía" : null;
            }
            catch (JsonException ex)
            {
                note = $"ignorado: data inválida ({ex.Message})";
            }
            if (data is not null)
                note = await apply(data, c, tx);
        }

        await c.ExecAsync("INSERT INTO processed_events (event_id, type, processed_at, note) VALUES ($id, $type, $now, $note)", tx,
            ("$id", e.Id), ("$type", e.Type), ("$now", clock.GetUtcNow()), ("$note", note));
        await tx.CommitAsync(ct);
    }
}
