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

La copia local solo se actualiza desde el feed, por `id` y `version`; un alta queda **POR ENVIAR** hasta que vuelve.

## Impuestos, monedas y permisos (base común de la suite)

- El impuesto sale del país de la empresa y de su tasa general en el hub; se desglosa por tasa según la categoría de
  cada artículo (`standard`, `reduced`, `zero`, `exempt`…) y se redondea con los decimales de la moneda.
- Roles del ERP (`session.Access.Role`): `consulta` (sin rol asignado), `facturador` (pedidos, cotizaciones,
  comprobantes y cobros), `compras` (órdenes de compra), `contador` (comprobantes, cobros y auditoría) y
  `administrador` (todo, también artículos y sedes). OrgAdmin y PlatformAdmin del hub son administradores.

## Datos propios del ERP

Pedidos, comprobantes, cobros y órdenes de compra, más lo que informan los demás sistemas: ventas de caja del POS,
cotizaciones aceptadas y oportunidades ganadas del CRM, y el stock del WMS.

## Eventos (versión 1)

### Qué publica el ERP

| Evento | Cuándo | Id del evento |
|---|---|---|
| `order.created` | Al crear un pedido (manual o desde una cotización) | Id del pedido |
| `invoice.issued` | Al emitir una factura o boleta | Id del comprobante |
| `payment.received` | Al registrar un cobro | Id del cobro |
| `purchase_order.created` | Al crear una orden de compra | Id de la orden |

Extensiones de v1 (campos agregados, ninguno quitado):

- `order.created`: `quoteId` si el pedido sale de una cotización del CRM.
- `invoice.issued`: `kind` (`factura` | `boleta`), `subtotal`, `tax`, `dueDate`, `paid` (lo ya cobrado en caja) y `lines`.
- `payment.received`: `number` y `reference`.
- `purchase_order.created`: `number`, `currencyCode`, `total` y `expectedAt`.

### Qué consume el ERP

| Evento | Efecto |
|---|---|
| `sale.completed` (POS) | Registra la venta de caja como ingreso; queda lista para emitir su boleta o factura |
| `sale.voided`, `sale.returned` (POS) | Anula la venta o descuenta lo devuelto del ingreso |
| `quote.accepted` (CRM) | La cotización queda **por convertir** en pedido |
| `deal.won` (CRM) | Se registra la oportunidad ganada |
| `order.created` (CRM, WMS) | Crea el pedido (si viene de una cotización, la vincula) |
| `shipment.dispatched` (WMS) | Marca el pedido como despachado con el transportista y la guía |
| `goods.received` (WMS) | Suma lo recibido a la orden de compra y la cierra cuando se completa |
| `stock.adjusted` (WMS) | Guarda el saldo absoluto (`quantityOnHand`) para valorizar el inventario |
| `invoice.issued`, `payment.received`, `purchase_order.created` (otro equipo ERP) | Ver abajo |

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

- Cancelar un pedido o una orden de compra no se replica a otros equipos: el catálogo v1 no tiene `order.cancelled`
  ni `purchase_order.cancelled`. Según la sección 10 de la guía, se propondrá en el hub.
- No hay notas de crédito ni anulación de comprobantes, ni envío a SUNAT (facturación electrónica).
- Las ventas de caja con devoluciones no se facturan desde aquí: se emite el comprobante por lo que quedó desde un pedido.
