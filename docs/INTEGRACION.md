# Integración del ERP con el hub y la suite

Vortex ERP sigue la guía del hub ([`vortex-api/docs/SISTEMAS.md`](https://github.com/ArcGabito/vortex-api/blob/main/docs/SISTEMAS.md)).
Este documento resume qué hace el ERP con cada dato y evento, y cómo conviven varios equipos que trabajan sin conexión.

## Identidad y sesión

- No hay tabla de usuarios: el login es contra el hub (`ProductCode = "ERP"`, clave de sistema en `appsettings.json`
  o en la pantalla de ingreso). Los tokens se guardan en `SecureStorage` y la app opera sin red con el offline token.
- Cada organización tiene su propia base SQLite (`vortex-erp-{organizationId}.db` en `AppDataDirectory`).

## Datos maestros (dueño: el hub)

| Dato | En el ERP | Altas/cambios desde el ERP |
|---|---|---|
| Artículos | Pedidos, compras, comprobantes; precio de lista e impuesto | Sí |
| Sedes | Almacén de despacho de pedidos y de recepción de compras | Alta |
| Clientes y proveedores | Pedidos, comprobantes (factura solo con RUC), compras | Alta |
| Empresa | Moneda | No |
| Listas de precios | Precio de los pedidos: la lista del cliente (`credit.priceListId`) o de la sede (`siteIds`), con escalas | Sí (se suben enteras) |

La copia local solo se actualiza desde el feed, por `id` y `version`; un alta queda **POR ENVIAR** hasta que vuelve.

## Impuestos, monedas y permisos (base común de la suite)

- El impuesto sale del país de la empresa y de su tasa general en el hub; se desglosa por tasa según la categoría de
  cada artículo (`standard`, `reduced`, `zero`, `exempt`…) y se redondea con los decimales de la moneda.
- Roles del ERP (`session.Access.Role`): `consulta` (sin rol asignado), `facturador` (pedidos, cotizaciones,
  comprobantes y cobros), `compras` (órdenes de compra), `contador` (comprobantes, cobros y auditoría) y
  `administrador` (todo, también artículos y sedes). OrgAdmin y PlatformAdmin del hub son administradores.
  Las notas de crédito y débito las emiten `facturador` y `contador`; anular un comprobante, solo `contador` y
  `administrador`.
- Sedes por usuario: con sedes asignadas en el hub (`Access.SiteIds`), el usuario solo crea pedidos y compras en ellas y
  solo ve los pedidos, compras y ventas de caja de sus sedes (los documentos sin sede se ven siempre).

## Datos propios del ERP

Pedidos, comprobantes con sus notas de crédito y débito, cobros, órdenes de compra, solicitudes de compra, facturas y pagos a
proveedores, más lo que informan los demás sistemas: ventas de caja del POS,
cotizaciones aceptadas y oportunidades ganadas del CRM, y el stock del WMS.

## Eventos (versión 1)

### Qué publica el ERP

| Evento | Cuándo | Id del evento |
|---|---|---|
| `order.created` | Al crear un pedido (manual o desde una cotización) | Id del pedido |
| `order.approved` | Al aprobar un pedido que superaba el límite de crédito del cliente | Determinista por pedido |
| `invoice.issued` | Al emitir una factura o boleta | Id del comprobante |
| `payment.received` | Al registrar un cobro (con `accountId`, la cuenta de tesorería donde entró) | Id del cobro |
| `purchase_order.created` | Al crear una orden de compra | Id de la orden |
| `purchase_order.cancelled` | Al cancelar una orden de compra (lo recibido queda; el resto ya no se espera): `{ purchaseOrderId, number, reason }` | Determinista por orden |
| `erp.record.upserted` | Solicitudes de compra, facturas y pagos a proveedores y ajustes de compras, para los demás equipos del ERP: `{ kind, id, updatedAt, data }` (kinds `purchase_request`, `supplier_invoice`, `supplier_payment`, `purchase_settings`) | Id nuevo |
| `credit_note.issued` | Al emitir una nota de crédito (anulación, devolución, descuento, corrección) | Id de la nota |
| `debit_note.issued` | Al emitir una nota de débito (intereses, aumento de valor, penalidad) | Id de la nota |
| `invoice.voided` | Al anular un comprobante sin cobros ni notas | `DeterministicId("invoice_void", invoiceId)` |

Extensiones de v1 (campos agregados, ninguno quitado):

- `invoice.issued`: `shipmentId` si factura un despacho (una parte del pedido).
- `order.created`: `quoteId` si el pedido sale de una cotización del CRM, y `approvalStatus: "pending"` si supera el
  límite de crédito del cliente: el WMS no lo prepara y no se factura hasta recibir `order.approved` (`orderId`,
  `number`, `approvedBy`).
- `invoice.issued`: `kind` (`factura` | `boleta`), `subtotal`, `tax`, `dueDate`, `paid` (lo ya cobrado en caja), `lines` y
  `exchangeRate` (cuánto vale 1 unidad de la moneda del comprobante en la de la empresa el día de emisión; sin él, es la
  misma moneda).
- `payment.received`: `number` y `reference`. `amount` va siempre en la moneda del comprobante; si se cobró en la moneda de
  la empresa, `paidAmount` y `paidCurrency` dicen lo recibido, `exchangeRate` el tipo del día y `fxDifference` la diferencia
  de cambio en moneda de la empresa (positiva si es ganancia).
- `purchase_order.created`: `number`, `currencyCode`, `total` y `expectedAt`.

Contenido de los eventos nuevos (propuestos al hub):

- `credit_note.issued` / `debit_note.issued`: `noteId`, `number`, `invoiceId`, `invoiceNumber`, `customerId`, `reason`
  (`void`, `return`, `discount`, `correction`, `interest`, `increase`, `penalty`), `issuedAt`, `currencyCode`, `subtotal`,
  `tax`, `total`, `lines` (con `invoiceLineNo` en las devoluciones), `description`, `saleId` y `orderId`.
- `invoice.voided`: `invoiceId`, `number`, `reason`, `saleId`, `orderId` y `customerId`. El pedido o la venta de caja quedan libres para
  emitir otro comprobante.

El saldo de un comprobante es total + notas de débito − notas de crédito − cobrado; si queda negativo es saldo a favor
del cliente. Las notas usan la serie del comprobante que modifican con su propia correlación (`F3F0-000001` de nota de
crédito es distinta de la factura `F3F0-000001`).

### Qué consume el ERP

| Evento | Efecto |
|---|---|
| `sale.completed` (POS) | Registra la venta de caja como ingreso; queda lista para emitir su boleta o factura, salvo que traiga `fiscal` (la caja ya emitió su comprobante electrónico) |
| `sale.voided`, `sale.returned` (POS) | Anula la venta o descuenta lo devuelto del ingreso |
| `quote.accepted` (CRM) | La cotización queda **por convertir** en pedido |
| `deal.won` (CRM) | Se registra la oportunidad ganada |
| `order.created` (CRM, WMS, otro equipo ERP) | Crea el pedido (si viene de una cotización, la vincula; si llega por aprobar, queda así) |
| `order.approved` (otro equipo ERP) | El pedido por aprobar ya se puede facturar |
| `shipment.dispatched` (WMS) | Guarda el despacho con sus líneas; el pedido queda despachado (o despachado en parte con `partial: true`) y cada despacho se puede facturar |
| `goods.received` (WMS) | Suma lo recibido a la orden de compra y la cierra cuando se completa |
| `stock.adjusted` (WMS) | Guarda el saldo absoluto (`quantityOnHand`) para valorizar el inventario |
| Contabilidad (fase 5a) | Los asientos automáticos salen de los documentos de cada equipo (no viajan); viajan como `erp.record.upserted` el plan (`ledger_account`), los ajustes contables (`accounting_settings`: roles, cuentas de tesorería, cierre) y los asientos manuales (`journal_entry`) |
| `cash.deposited` (POS) | El efectivo depositado sale de la caja y entra a la cuenta bancaria predeterminada de la moneda, para conciliarlo con el extracto (Ids deterministas por depósito) |
| `purchase.requested` (WMS) | Sugerencia de compra del almacén: solicitud de compra **por aprobar** con el Id y el número del WMS, el proveedor, las cantidades y el costo promedio del almacén; la nota dice que la sugirió el almacén y para cuándo se necesita. Solo crea: si ya existe no se toca |
| `invoice.issued`, `payment.received`, `purchase_order.created`, `credit_note.issued`, `debit_note.issued`, `invoice.voided` (otro equipo ERP) | Ver abajo |
| Cambio de `exchange_rate` (datos maestros) | Copia local de los tipos de cambio para emitir y cobrar en otras monedas sin conexión |
| Cambio de `party` (datos maestros) | Copia local del cliente con sus condiciones (`credit`: plazo, límite, descuento) |

- Cada evento se aplica una sola vez (`processed_events`), en la misma transacción que su efecto.
- `schemaVersion` distinto de 1 o `data` ilegible se registran como ignorados y no traban el feed.

### Pedidos desde cotizaciones del CRM

El pedido que nace de una cotización usa un **Id determinista** (`order_from_quote` + `quoteId`). Si el CRM genera el
pedido y además el ERP convierte la cotización, los dos calculan el mismo Id: el hub recibe un solo `order.created` y el
WMS reserva una sola vez.

## Varios equipos del ERP

El ERP se suscribe también a sus propios eventos (`IncludeOwnEvents = true`): los pedidos, comprobantes, cobros y
órdenes de compra de otros equipos se replican (los propios se reconocen por su Id). Cada equipo numera con su propio
prefijo (`PED-3F0C-000012`, `COB-…`, `OC-…`) y su propia serie de comprobantes (`F3F0-…`, `B3F0-…`).

### Limitaciones conocidas

- Las solicitudes, facturas y pagos a proveedores se replican con `erp.record.upserted`: si dos equipos cambian la
  misma factura sin conexión, gana el cambio más reciente (los pagos no chocan: cada uno es su propio registro).
- No hay envío a SUNAT (facturación electrónica): las notas y anulaciones se registran en el ERP y se informan con la
  localización fiscal (fase 7 de la hoja de ruta).
- Si un equipo anula un comprobante sin conexión mientras otro le registra un cobro, al sincronizar queda anulado con el
  cobro registrado: revisar en el estado de cuenta del cliente y devolver o aplicar el cobro.
- Las ventas de caja con devoluciones no se facturan desde aquí: se emite el comprobante por lo que quedó desde un pedido.
