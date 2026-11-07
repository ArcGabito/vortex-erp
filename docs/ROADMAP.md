# Hoja de ruta

Guía para que Vortex ERP cumpla lo que una pyme o mediana empresa exige a un ERP y compita con Odoo, SAP Business
One, Dynamics 365 Business Central y NetSuite en **Perú, LATAM, EE. UU. y Europa**. Lo común a la suite está en
[ROADMAP-SUITE](https://github.com/ArcGabito/vortex-api/blob/main/docs/ROADMAP-SUITE.md).

Cómo usarla:

1. **Requisitos empresariales** es la lista de lo que debe tener el ERP. Cada requisito tiene un código (`ERP-xx`),
   su estado y la fase que lo cubre.
2. **Fases** dice en qué orden se construyen, qué datos y eventos necesitan y cuándo se dan por terminadas.
3. Al cerrar una fase se actualiza su **Estado** y el de sus requisitos, y se anota en el `CHANGELOG`.

Los tamaños (S, M, L, XL) son relativos entre fases y se vuelven a estimar al empezar cada una.

## Requisitos empresariales

Estados: **Hecho**, **Parcial** (existe una parte; la fase indica qué falta) y **Pendiente**.

### Ventas y facturación (pedido a cobro)

| # | Requisito | Estado | Fase |
|---|---|---|---|
| ERP-01 | Pedidos manuales, del CRM, del WMS y desde cotizaciones aceptadas | Hecho | — |
| ERP-02 | Factura y boleta con impuestos por país y serie por equipo | Hecho | 1 |
| ERP-03 | Comprobantes desde las ventas del POS | Hecho | — |
| ERP-04 | Notas de crédito (devolución, descuento, anulación) y de débito | Hecho | 2 |
| ERP-05 | Anulación de comprobantes con motivo, replicada | Hecho | 2 |
| ERP-06 | Comprobantes en cualquier moneda con el tipo de cambio del hub | Pendiente | 2 |
| ERP-07 | Listas de precios y descuentos por cliente | Pendiente | 3 |
| ERP-08 | Condiciones de pago y límite de crédito con bloqueo y aprobación | Pendiente | 3 |
| ERP-09 | Facturación de despachos parciales | Pendiente | 3 |
| ERP-10 | Facturación recurrente (suscripciones, alquileres) | Pendiente | 3 |

### Cobranzas

| # | Requisito | Estado | Fase |
|---|---|---|---|
| ERP-15 | Cobros parciales y cuentas por cobrar con lo vencido | Hecho | — |
| ERP-16 | Antigüedad de saldos (0-30, 31-60, 61-90, +90) | Hecho | 2 |
| ERP-17 | Estado de cuenta del cliente en PDF | Hecho | 2 |
| ERP-18 | Recordatorios de cobranza por correo o WhatsApp | Pendiente | 3 |
| ERP-19 | Anticipos y su aplicación a comprobantes | Pendiente | 3 |
| ERP-20 | Retenciones, percepciones y detracciones | Pendiente | 7 |

### Compras y cuentas por pagar (solicitud a pago)

| # | Requisito | Estado | Fase |
|---|---|---|---|
| ERP-25 | Órdenes de compra recibidas por el WMS | Hecho | — |
| ERP-26 | Solicitudes de compra (también las sugeridas por el WMS) | Pendiente | 4 |
| ERP-27 | Aprobación de compras por monto | Pendiente | 4 |
| ERP-28 | Factura de proveedor con conciliación de tres vías (OC, recepción, factura) | Pendiente | 4 |
| ERP-29 | Cuentas por pagar con vencimientos | Pendiente | 4 |
| ERP-30 | Programación y registro de pagos a proveedores | Pendiente | 4 |
| ERP-31 | Cancelación de órdenes de compra, replicada | Pendiente | 4 |

### Tesorería

| # | Requisito | Estado | Fase |
|---|---|---|---|
| ERP-35 | Cuentas bancarias y cajas de la empresa | Pendiente | 4 |
| ERP-36 | Transferencias entre cuentas | Pendiente | 4 |
| ERP-37 | Conciliación bancaria con extractos (CSV, OFX, CAMT.053) | Pendiente | 4 |
| ERP-38 | Flujo de caja proyectado | Pendiente | 4 |
| ERP-39 | Depósitos de las cajas del POS | Pendiente | 4 |

### Contabilidad

| # | Requisito | Estado | Fase |
|---|---|---|---|
| ERP-45 | Plan de cuentas por país (PCGE, México, Colombia, US GAAP, PGC) | Pendiente | 5 |
| ERP-46 | Asientos automáticos desde ventas, cobros, compras, pagos, inventario y diferencia de cambio | Pendiente | 5 |
| ERP-47 | Asientos manuales con aprobación | Pendiente | 5 |
| ERP-48 | Libro diario, mayor y balance de comprobación | Pendiente | 5 |
| ERP-49 | Estado de resultados y balance general | Pendiente | 5 |
| ERP-50 | Centros de costo | Pendiente | 5 |
| ERP-51 | Cierre de periodo (bloqueo de fechas), cierre anual y apertura | Pendiente | 5 |
| ERP-52 | Diferencia de cambio realizada y no realizada | Pendiente | 5 |

### Costos, activos y presupuestos

| # | Requisito | Estado | Fase |
|---|---|---|---|
| ERP-55 | Inventario valorizado con el stock del WMS | Parcial: al último costo de compra | 6 |
| ERP-56 | Costo de ventas con el costo promedio del WMS | Pendiente | 6 |
| ERP-57 | Asientos de inventario (entradas, salidas, ajustes) | Pendiente | 6 |
| ERP-58 | Activos fijos con depreciación por país | Pendiente | 6 |
| ERP-59 | Presupuestos y comparación con lo real | Pendiente | 6 |

### Control y seguridad

| # | Requisito | Estado | Fase |
|---|---|---|---|
| ERP-65 | Roles (`consulta`, `facturador`, `compras`, `contador`, `administrador`) validados en Core | Hecho | 1 |
| ERP-66 | Auditoría de emisión, cobros y cancelaciones | Hecho | 1 |
| ERP-67 | Auditoría de cambios de precio, notas, pagos y asientos | Pendiente | 2-5 |
| ERP-68 | Aprobaciones configurables por tipo de documento y monto | Pendiente | 8 |
| ERP-69 | Segregación de funciones (quien registra no aprueba) | Pendiente | 8 |
| ERP-70 | Sedes por usuario | Hecho | 2 |

### Localización fiscal

| # | Requisito | Estado | Fase |
|---|---|---|---|
| ERP-75 | Perú: factura, boleta y notas UBL 2.1 vía OSE, resumen diario, baja, PLE | Pendiente | 7 |
| ERP-76 | México: CFDI 4.0 con complemento de pago vía PAC | Pendiente | 7 |
| ERP-77 | Colombia (DIAN), Chile (SII), Ecuador (SRI), Brasil (NF-e) | Pendiente | 7 |
| ERP-78 | EE. UU.: sales tax por estado; UE: IVA intracomunitario y factura electrónica | Pendiente | 7 |

### Escala

| # | Requisito | Estado | Fase |
|---|---|---|---|
| ERP-85 | Multiempresa con consolidación y operaciones entre empresas | Pendiente | 8 |
| ERP-86 | Reportes configurables y exportación a Excel y PDF | Parcial: panel e ingresos por mes | 8 |
| ERP-87 | Importación masiva (saldos iniciales, clientes, artículos) | Pendiente | 8 |
| ERP-88 | API y webhooks propios del ERP sobre los del hub | Pendiente | 8 |
| ERP-89 | Nómina mediante integración con proveedores locales | Pendiente | 8 |

## Reglas que el plan respeta

- El hub es dueño de usuarios, roles, artículos, sedes y terceros; lo que falte se propone en `vortex-api`.
- Cada documento se guarda y publica su evento en la misma transacción; los eventos v1 solo crecen.
- Un comprobante emitido no se edita: se anula o se corrige con una nota.
- Sin conexión primero y varios equipos: toda función nueva define cómo se replica.
- Nada fijo para un país: impuestos, monedas y formatos vienen de `VortexApi.Suite` y del país de la empresa.
- El stock y su costo son del WMS; el ERP los valoriza y contabiliza, nunca los mueve.
- Módulos opcionales por `features` del hub (`contabilidad`, `tesoreria`, `activos`, `facturacion_electronica`…).

## Fase 1: base común de la suite · M

Impuestos por país, moneda del documento, roles del ERP, auditoría e idiomas (español, inglés y portugués).

**Terminado cuando:** una empresa de México factura con IVA 16 %, un `compras` no puede emitir comprobantes y cada
cobro queda en la auditoría.

**Estado (2026-11):** hecha.

## Fase 2: notas, anulaciones y multimoneda · M

**Objetivo:** corregir comprobantes como lo exige la ley y facturar en otras monedas.

- ERP-04, ERP-05: notas de crédito y débito vinculadas al comprobante; anulación con motivo.
- ERP-06: comprobantes en cualquier moneda con el tipo de cambio del hub (`exchange_rate`, ya disponible); saldos y
  reportes en la moneda de la empresa.
- ERP-16, ERP-17: antigüedad de saldos y estado de cuenta en PDF.
- ERP-70: sedes por usuario (`Access.SiteIds`, ya en el hub).

**Datos y eventos:** `credit_note.issued`, `debit_note.issued` e `invoice.voided` (propuestas al hub); los
comprobantes guardan moneda y tipo de cambio del día.

**Terminado cuando:** una nota de crédito parcial deja el saldo del cliente al día en dos equipos; una factura en
dólares se cobra en soles y la diferencia queda registrada; el CRM y el POS ven la anulación.

**Estado (2026-11):** 2a hecha: notas de crédito (anulación, devolución por líneas, descuento, corrección) y de débito
vinculadas al comprobante, anulación de comprobantes sin cobros ni notas, antigüedad de saldos por cliente y moneda,
estado de cuenta con PDF y sedes por usuario. Eventos `credit_note.issued`, `debit_note.issued` e `invoice.voided`
(propuestos al hub). Pruebas en `NotesTests`. **Pendiente (2b):** multimoneda con el tipo de cambio del hub y
diferencia de cambio (ERP-06); que el CRM y el POS muestren la anulación.

## Fase 3: ciclo de venta completo · M

- ERP-07, ERP-08: listas de precios y descuentos por cliente; condiciones de pago y límite de crédito (un pedido que lo
  supera queda pendiente de aprobación).
- ERP-09: facturación de despachos parciales con las líneas de `shipment.dispatched` (fase 4b del WMS).
- ERP-10: facturación recurrente.
- ERP-18, ERP-19: recordatorios de cobranza y anticipos.

**Hub:** listas de precios compartidas con el POS y el CRM; condiciones de pago y límite de crédito en `party`.

**Terminado cuando:** un cliente que supera su límite no puede recibir otro pedido sin aprobación; un pedido despachado
en dos partes se factura en dos comprobantes.

## Fase 4: compras, cuentas por pagar y tesorería · L

- ERP-26 a ERP-31: solicitudes (incluidas las `purchase.requested` del WMS), aprobación por monto, factura de proveedor
  con conciliación de tres vías, cuentas por pagar, pagos y cancelación de órdenes.
- ERP-35 a ERP-39: bancos y cajas, transferencias, conciliación con extractos, flujo de caja proyectado y depósitos
  del POS.

**Hub:** `purchase_order.cancelled`; cuentas bancarias de la empresa en `company`.

**Terminado cuando:** una factura de proveedor que no cuadra con lo recibido queda retenida; un extracto importado
concilia cobros y pagos automáticamente y deja visibles los que no encuentran pareja.

## Fase 5: contabilidad · XL

- ERP-45 a ERP-52: plan de cuentas por país, asientos automáticos y manuales, libros, estados financieros, centros de
  costo, cierres y diferencia de cambio.
- ERP-67: auditoría de asientos manuales y reaperturas.

**Datos:** asientos inmutables (una corrección es otro asiento); cada asiento automático guarda su documento de
origen. Los montos en decimal exacto.

**Multiequipo:** los asientos automáticos usan Id determinista por documento, para que dos equipos no contabilicen
dos veces lo mismo.

**Terminado cuando:** un mes con ventas, cobros, compras y pagos cuadra en el balance de comprobación; un periodo
cerrado no acepta documentos con fecha anterior.

## Fase 6: costos, activos y presupuestos · M

- ERP-55 a ERP-57: costo de ventas y asientos de inventario con el costo promedio del WMS (`unitCost` y `totalCost`
  en `stock.adjusted` y `goods.received`, ya publicados por el WMS).
- ERP-58: activos fijos con depreciación (línea recta y la que exija cada país).
- ERP-59: presupuestos por cuenta y centro de costo, comparados con lo real.

**Terminado cuando:** el costo de ventas del ERP coincide con el del kardex valorizado del WMS en el mismo periodo.

## Fase 7: localización fiscal · XL

- ERP-75: Perú primero: comprobantes electrónicos UBL 2.1 vía OSE/SUNAT, resumen diario, comunicación de baja, PLE
  (ventas, compras, diario) y ERP-20 (detracciones, retenciones, percepciones).
- ERP-76 a ERP-78: México, Colombia, Chile, Ecuador, Brasil, EE. UU. y UE, cada uno como paquete por `features`.

El paquete de localización vive en `VortexApi.Suite` y lo comparte el POS.

**Terminado cuando:** una factura, una boleta y una nota de crédito quedan aceptadas por la OSE y el PLE del mes pasa
el validador de SUNAT.

## Fase 8: escala · L

ERP-68, ERP-69 (aprobaciones configurables y segregación de funciones), ERP-85 (multiempresa y consolidación),
ERP-86 a ERP-88 (reportes configurables, importación masiva, API) y ERP-89 (nómina por integración).

## Propuestas al hub

| Propuesta | Tipo | Fase |
|---|---|---|
| `invoice.voided`, `credit_note.issued`, `debit_note.issued` | Eventos | 2 |
| Listas de precios | Dato maestro compartido con POS y CRM | 3 |
| Condiciones de pago y límite de crédito | Campos de `party` | 3 |
| `purchase_order.cancelled` | Evento | 4 |
| Cuentas bancarias de la empresa | Campos de `company` | 4 |
| Paquete de localización fiscal | Librería en `VortexApi.Suite` | 7 |
| Grupo de empresas para consolidar | Recurso de organización | 8 |

## Decisiones pendientes

| # | Decisión | Recomendación | Antes de |
|---|---|---|---|
| 1 | Tipo de cambio de un comprobante | El del día de emisión (venta o compra según el documento), guardado en el comprobante | Fase 2 |
| 2 | Contabilidad en el ERP o exportada a un sistema contable | En el ERP, con exportación para el contador externo | Fase 5 |
| 3 | Planes de cuentas | Plantillas por país editables por la empresa | Fase 5 |
| 4 | Proveedor de facturación electrónica | OSE certificado en Perú y PAC en México, detrás de una interfaz común | Fase 7 |
| 5 | Nómina | Integración con proveedores locales, no un módulo propio | Fase 8 |

## Versiones

| Versión | Contenido |
|---|---|
| 0.2.0 | Fase 1 |
| 0.3.0 | Fase 2 |
| 0.4.0 | Fase 3 |
| 0.5.0 | Fase 4 |
| 0.6.0 | Fase 5 |
| 0.7.0 | Fase 6 |
| 0.8.0 | Fase 7 (Perú) |
| 1.0.0 | Estabilización y piloto con una empresa real |
| 1.x | Fase 8 y localización de otros países |

## Cómo se trabaja cada fase

1. **Hub primero:** las propuestas de la fase se envían a `vortex-api` con pruebas y se vuelve a empaquetar el SDK
   (`scripts/pack-sdk.*`).
2. **Base de datos:** una migración nueva en `ErpDatabase`; nunca se edita una publicada.
3. **Documentos:** cada documento y su evento se guardan en la misma transacción; los que pueden procesar varios
   equipos usan Id determinista.
4. **Permisos y auditoría:** cada acción sensible valida su permiso en Core y deja su auditoría.
5. **Pruebas:** unitarias y un escenario en `SyncTests` por cada documento o dato replicado.
6. **Documentación:** los eventos nuevos o ampliados van en [INTEGRACION.md](INTEGRACION.md); los cambios, en el
   `CHANGELOG`; los requisitos cumplidos se marcan en esta guía.
7. **Pantallas:** con los componentes compartidos, en los tres idiomas, probadas en Windows y en la vista móvil.
