# Changelog

Todos los cambios relevantes de este proyecto se documentan en este archivo.

El formato se basa en [Keep a Changelog](https://keepachangelog.com/es-ES/1.1.0/)
y el proyecto sigue [Versionado Semántico](https://semver.org/lang/es/).

## [Unreleased]

### Added
- **Sugerencias de compra del WMS:** `purchase.requested` llega como solicitud de compra por aprobar con el número del
  almacén, el proveedor y el costo promedio (ERP-26).
- **Contabilidad (fase 5b):** centros de costo (por línea y por sede) con resultado por centro; cierre anual contra
  utilidades acumuladas con asiento de apertura; diferencia de cambio no realizada con reversión al día siguiente. Las
  ventas de caja con comprobante electrónico del POS no se vuelven a facturar.
- **Contabilidad (fase 5a):** plan de cuentas por país, asientos automáticos de ventas, notas, cobros (con diferencia de
  cambio), ventas de caja, compras, pagos y tesorería; asientos manuales con aprobación y reversión; libro diario, mayor,
  balance de comprobación, estado de resultados y balance general; cierre de periodo que bloquea documentos con fecha
  anterior. Rol asistente contable.
- **Tesorería (fase 4b):** cuentas bancarias y cajas con su saldo (los cobros y pagos entran solos a la cuenta de su
  medio de pago), transferencias entre cuentas y monedas, comisiones e intereses, importación de extractos CSV, OFX y
  CAMT.053 con conciliación automática y manual, flujo de caja proyectado por semana y depósitos de las cajas del POS
  (`cash.deposited`).
- **Compras y cuentas por pagar (fase 4a):** solicitudes de compra con aprobación por monto; facturas de proveedor
  con conciliación de tres vías (orden, recepción y factura) que retiene las que no cuadran; cuentas por pagar por
  vencimiento, pagos parciales y programados; cancelación de órdenes publicada (`purchase_order.cancelled`). Se
  replican entre equipos del ERP con `erp.record.upserted`.
- **Listas de precios (ERP-07):** pantalla para crear y editar las listas del hub (`price_list`: moneda, vigencia, sedes y
  escalas por cantidad) y lista de cada cliente en sus condiciones comerciales. Los pedidos toman el precio de la lista
  del cliente o de la sede (con la escala de la cantidad); lo que no está en la lista lleva el descuento habitual.
- **Despachos parciales y cobranza (fase 3b):** cada despacho del WMS queda en el pedido con lo que salió y se factura
  por separado (`invoice.issued` con `shipmentId`); recordatorio de cobranza con los comprobantes vencidos, listo para
  WhatsApp o correo, que queda registrado.
- **Crédito del cliente (fase 3a):** plazo de pago, límite de crédito y descuento habitual por cliente, compartidos por
  el hub (`party.credit`). Un pedido que supera el crédito disponible queda **por aprobar** (no se factura ni se
  prepara) hasta que alguien con permiso lo apruebe (`order.approved`); el pedido toma el descuento del cliente y la
  factura su plazo. Pestaña "Por aprobar" en Pedidos.
- **Multimoneda (fase 2b):** pedidos y comprobantes en otra moneda con el tipo de cambio del hub (copia local, funciona
  sin conexión): el comprobante guarda el del día de emisión y los precios de lista se convierten. Un comprobante en otra
  moneda se cobra en ella o en la de la empresa al tipo del día, y la diferencia de cambio queda registrada en el cobro
  (`payment.received` con `paidAmount`, `paidCurrency`, `exchangeRate` y `fxDifference`). El panel, los ingresos y la
  antigüedad de saldos suman en moneda de la empresa; tipos de cambio en Configuración y diferencia de cambio del mes en
  Cobranzas.
- **Celular (Android):** la app respeta las barras del sistema (estado y gestos) con íconos oscuros; el botón Atrás
  cierra el diálogo o el menú abiertos y vuelve a la página anterior en vez de cerrar la app; las tablas se ven como
  tarjetas y las pestañas que no caben se deslizan; en pantallas táctiles no se abre el teclado solo. El negro de la
  pantalla de inicio, el ícono, la barra de título y el logo deja el café `#1C120D` que se veía amarillento; los montos
  menores a 10 mil del panel se muestran con decimales.
- **Notas de crédito y débito (fase 2a):** vinculadas al comprobante, con motivo (anulación, devolución por líneas,
  descuento, corrección; intereses, aumento de valor, penalidad) y el impuesto desglosado. El saldo del comprobante
  suma los débitos y resta los créditos; un crédito sobre una factura cobrada deja saldo a favor. Eventos
  `credit_note.issued` y `debit_note.issued`.
- **Anulación de comprobantes** emitidos por error (sin cobros ni notas): el pedido o la venta de caja quedan libres
  para emitir otro. Evento `invoice.voided` con Id determinista. Solo `contador` y `administrador`.
- **Antigüedad de saldos** por cliente y moneda (por vencer, 1-30, 31-60, 61-90 y más de 90 días) y **estado de
  cuenta** con saldo anterior, cargos, abonos y saldo acumulado, descargable en PDF.
- **Sedes por usuario:** con sedes asignadas en el hub, pedidos, compras y ventas de caja solo de esas sedes.

### Changed
- Los ingresos del panel y de los reportes no cuentan los comprobantes anulados y restan las notas de crédito (suman
  las de débito) en la fecha en que se emiten.
- **Microsoft Store:** paquete MSIX generado por `.github/workflows/store.yml` (etiqueta `vX.Y.Z`), firmado por la Store
  para instalar sin la advertencia de SmartScreen; envío automático opcional. Guía en `docs/MICROSOFT-STORE.md`.
- **El idioma sigue al usuario:** el que elige en Configuración se guarda en el hub y se usa al entrar en cualquier
  sistema o equipo de la suite. Sin conexión queda en este equipo.
- **Idiomas**: la interfaz en español, inglés y portugués. Se elige en Configuración → Este equipo; por defecto sale
  del país de la empresa (portugués en Brasil y Portugal, inglés en EE. UU. y el Reino Unido, español en el resto).
  Una prueba verifica que cada texto exista en los tres idiomas con los mismos datos variables.
- Los **mensajes de validación y de permisos** salen en el idioma elegido (`CoreText` en Core,
  con el mismo catálogo que la interfaz).
- **Base común de la suite** (`VortexApi.Suite`): impuestos por país (Perú, LATAM, EE. UU., Reino Unido y UE) con
  desglose por tasa y redondeo según la moneda en pedidos y comprobantes; formatos y hora según el país de la empresa.
- **Roles del ERP** asignados en el hub: `consulta` (por defecto, solo ve), `facturador`, `compras`, `contador` y
  `administrador`. Cada acción (pedidos, comprobantes, cobros, compras, catálogo) valida su permiso en Core.
- **Auditoría** de emisión de comprobantes, cobros y cancelaciones (pantalla Auditoría).
- `docs/ROADMAP.md`: hoja de ruta para competir con Odoo, SAP Business One, Business Central y NetSuite.

### Changed
- `docs/ROADMAP.md` pasa a ser la guía de requisitos empresariales: cada requisito (`ERP-xx`) con su estado y
  la fase que lo cubre, y cada fase con su criterio de terminado, propuestas al hub y decisiones pendientes.
- **Morado .NET como color de la suite:** el naranja de la marca (`#FF5B14`) pasa a `#512BD4`, el morado de .NET,
  en el acento de `app.css`, el logo, el ícono de la app, la pantalla de inicio, la barra de título y Android. La portada
  del inicio de sesión se muestra en tonos morados con `filter: hue-rotate`. Los colores de estado no cambian.
- `invoice.issued` lleva la categoría de impuesto de cada línea (`taxCode`).

### Added
- App **.NET MAUI Blazor Hybrid** (Windows, Android, iOS, macOS) con la arquitectura de Vortex WMS: base SQLite por
  organización, sesión con el hub `vortex-api` y sincronización sin conexión.
- **Pedidos de venta** creados en el ERP, recibidos del CRM o del WMS, o convertidos desde cotizaciones aceptadas.
- **Comprobantes** (factura y boleta) desde pedidos o ventas de caja del POS, con desglose de IGV y serie por equipo.
- **Cobranzas**: cobros parciales y totales, cuentas por cobrar por cliente con lo vencido.
- **Compras** que el WMS recibe y que se cierran con sus recepciones.
- **Inventario valorizado** con el stock del WMS y el último costo de compra.
- **Panel** con ingresos del mes y de 6 meses, por cobrar, vencido y pendientes.
- Eventos `order.created`, `invoice.issued`, `payment.received` y `purchase_order.created`; consumo de las ventas del
  POS, cotizaciones y oportunidades del CRM, y despachos, recepciones y stock del WMS.
- CI (pruebas en Linux y app en Windows), Dependabot, plantilla de pull request, CONTRIBUTING y SECURITY.

### Changed
- Licencia: de SSPL a **AGPL-3.0**, como el resto de la suite.

### Removed
- Plantilla inicial de .NET MAUI (XAML).

### Fixed
- El buscador de artículos perdía dígitos cuando un lector de códigos escribía rápido.
- Celular: el editor de líneas repetía "Artículo" en la cabecera; ahora solo nombra precio y cantidad sobre sus campos.
- Celular: los diálogos (detalle del comprobante, nuevo pedido) y los indicadores ya no se salen de la pantalla; las líneas
  del pedido muestran el nombre del artículo con precio y cantidad debajo.
