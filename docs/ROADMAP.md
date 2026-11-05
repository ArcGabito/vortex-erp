# Hoja de ruta

Plan para que Vortex ERP compita con Odoo, SAP Business One, Dynamics 365 Business Central y NetSuite en pymes y
medianas empresas de **Perú, LATAM, EE. UU. y Europa**. Las fases van en el orden en que conviene construirlas; la
fase 1 es común a la suite ([ROADMAP-SUITE](https://github.com/ArcGabito/vortex-api/blob/main/docs/ROADMAP-SUITE.md)).
Tamaños (S, M, L, XL) relativos entre fases.

## Dónde estamos (0.1.0)

| Área | Hoy | Falta para competir |
|---|---|---|
| Ventas | Pedidos (manuales, CRM, WMS, cotizaciones), despacho del WMS | Listas de precios, descuentos por cliente, aprobaciones, entregas parciales |
| Facturación | Factura y boleta con IGV, serie por equipo | Notas de crédito y débito, anulación, impuestos por país, multimoneda, facturación electrónica |
| Cobranzas | Cobros parciales, cuentas por cobrar con vencido | Antigüedad de saldos, recordatorios, anticipos, retenciones y detracciones |
| Compras | Órdenes de compra recibidas por el WMS | Solicitudes y aprobaciones, facturas de proveedor, cuentas por pagar, pagos |
| Contabilidad | Nada | Plan de cuentas, asientos automáticos, libro diario y mayor, balances, cierres |
| Tesorería | Nada | Bancos, cajas, conciliación bancaria, flujo de caja |
| Inventario | Valorizado al último costo con el stock del WMS | Costo promedio ponderado, kardex valorizado, ajustes contables |
| Reportes | Panel e ingresos por mes | Estado de resultados, balance, ventas por producto/cliente, exportación |
| Usuarios | Login del hub; todos pueden hacer todo | Roles (contador, facturador, compras, gerente), auditoría |
| Internacional | Perú, soles, español | Impuestos por país, multimoneda, idiomas, localización fiscal |

## Reglas que el plan respeta

- El hub es dueño de usuarios, roles, artículos, sedes y terceros; lo que falte se propone en `vortex-api`.
- Cada documento se guarda y publica su evento en la misma transacción; los eventos v1 solo crecen.
- Sin conexión primero y varios equipos: toda función nueva define cómo se replica.
- Nada fijo para un país: impuestos, monedas y formatos vienen de `VortexApi.Suite` y del país de la empresa.
- Módulos opcionales por `features` del hub (`contabilidad`, `tesoreria`, `facturacion_electronica`…).

## Fase 1: base común de la suite · M

- **Impuestos por país** en pedidos y comprobantes (reemplaza el IGV fijo): desglose por tasa, precios con o sin impuesto.
- **Moneda del documento** con redondeo según la moneda.
- **Roles del ERP**: `administrador`, `contador`, `facturador`, `compras`, `consulta`; se validan en los servicios.
- **Auditoría** de emisión, cobros, cancelaciones y cambios de precio.
- **Idiomas** español, inglés y portugués (1b, hecho): la interfaz completa, con selector en Configuración → Este
  equipo (por defecto, el idioma del país de la empresa). Falta traducir los mensajes de validación de la lógica.

**Terminado cuando:** una empresa de México factura con IVA 16 %, un `compras` no puede emitir comprobantes y cada
cobro queda en la auditoría.

## Fase 2: notas, anulaciones y multimoneda · M

- Notas de crédito (devolución, descuento, anulación) y de débito, vinculadas al comprobante; saldo del cliente al día.
- Anulación de comprobantes con motivo; propuesta `invoice.voided` al hub.
- Multimoneda: tipo de cambio por fecha (propuesta de dato maestro en el hub), comprobantes en cualquier moneda,
  saldos y reportes en la moneda de la empresa, diferencia de cambio.
- Antigüedad de saldos (0-30, 31-60, 61-90, +90) y estado de cuenta por cliente en PDF.

## Fase 3: cuentas por pagar y tesorería · L

- Factura de proveedor (desde la orden de compra y la recepción del WMS: conciliación de tres vías).
- Cuentas por pagar con vencimientos y programación de pagos.
- Bancos y cajas, transferencias, conciliación bancaria con importación de extractos (CSV/OFX).
- Flujo de caja proyectado.

## Fase 4: contabilidad · XL

- Plan de cuentas por país (PCGE en Perú, y plantillas para México, Colombia, EE. UU. GAAP y PGC de España).
- Asientos automáticos desde ventas, cobros, compras, pagos, inventario y diferencia de cambio; asientos manuales.
- Libro diario, mayor, balance de comprobación, estado de resultados y balance general; centros de costo.
- Cierres de periodo (bloqueo de fechas) y apertura.

## Fase 5: costos e inventario contable · M

- Costo promedio ponderado con los movimientos del WMS; costo de ventas en cada comprobante.
- Kardex valorizado y ajustes contables de inventario.

## Fase 6: localización fiscal · XL

- **Perú:** factura, boleta y notas electrónicas UBL 2.1 vía OSE/SUNAT, resumen diario, comunicación de baja,
  detracciones, retenciones y percepciones, PLE (registro de ventas y compras, libro diario).
- **México:** CFDI 4.0 con complemento de pago, vía PAC. **Colombia:** factura electrónica DIAN.
  **Chile:** DTE del SII. **EE. UU.:** sales tax por estado. **UE:** IVA intracomunitario y factura electrónica.

## Fase 7: escala · M

Presupuestos, aprobaciones por monto, multiempresa (consolidado), reportes configurables, importación masiva y API.

## Propuestas al hub

Tipos de cambio como dato maestro; `invoice.voided`, `credit_note.issued`, `order.cancelled`,
`purchase_order.cancelled`; cuentas bancarias de la empresa en `company`.
