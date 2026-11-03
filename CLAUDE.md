# CLAUDE.md

Vortex ERP: gestión administrativa de la suite Vortex (ERP, CRM, WMS, POS). App .NET MAUI Blazor Hybrid, AGPL-3.0,
textos de la interfaz y documentación en español (es-PE). Sigue la misma arquitectura que Vortex WMS (la referencia).

## Comandos

```bash
dotnet test tests/VortexErp.Core.Tests                                   # pruebas de documentos y sincronización
VORTEX_HUB_URL=http://localhost:5299/ dotnet test tests/VortexErp.Core.Tests   # + prueba contra un hub real
dotnet build src/VortexErp -f net10.0-windows10.0.19041.0                # app en Windows (también -f net10.0-android)
dotnet run --project src/VortexErp -f net10.0-windows10.0.19041.0        # compila y abre (en Windows, -t:Run no compila)
scripts/pack-sdk.sh   |   ./scripts/pack-sdk.ps1                         # SDK del hub -> packages/vortex-sdk
```

## Estructura

- `src/VortexErp.Core`: todo lo que no es interfaz. `Data/` (SQLite con Microsoft.Data.Sqlite sin ORM, migraciones
  por `PRAGMA user_version` en `ErpDatabase`), `Services/` (`OrderService`, `InvoiceService` con cobranzas y ventas de
  caja, `PurchaseService`, `ReportService`, `MasterDataService`), `Sync/` (store y handlers), `Integration/` (eventos v1).
- `src/VortexErp`: app. `Services/AppSession.cs` (sesión, heartbeat, sincronización), `Components/Pages` (pantallas;
  heredan `ErpPage`), `Components/Shared`, `wwwroot/css/app.css` (sistema de diseño de la suite).
- `tests/VortexErp.Core.Tests`: xUnit sobre bases SQLite temporales; `SyncTests` simula otros sistemas y equipos.

## Reglas

- Cada documento se guarda y encola su evento en la misma transacción (`Outbox`), con el Id del documento como Id del evento.
- Pedido desde una cotización del CRM: Id `OrderService.OrderIdForQuote(quoteId)` (el CRM calcula el mismo).
- Comprobantes con serie por equipo (`F` / `B` + 3 caracteres del equipo). Factura solo a clientes con RUC.
- El stock es del WMS: el ERP guarda el saldo absoluto de `stock.adjusted` y solo lo valoriza.
- Los handlers de eventos son idempotentes (`processed_events`) y nunca lanzan por datos inválidos.
- Errores para el usuario: `ErpValidationException` con el mensaje final en español.
- En Razor, dentro de atributos usa `@(...)` cuando haya comillas y no nombres variables `code` ni `page`.
- UI: reutiliza las clases de `app.css` y los componentes compartidos (`Modal`, `KebabMenu`, `StatTile`,
  `StatusPill`, `Pager`, `ItemPicker`, `LineEditor`).

## Hub Vortex (identidad, datos maestros e integración)

Este sistema es **Vortex ERP** (`ProductCode = "ERP"`) y depende del hub `vortex-api`, clonado en `~/vortex-api`.
Antes de tocar login, sesiones, usuarios, clientes, artículos, sedes, datos de la empresa o comunicación con otro
sistema de la suite, lee `~/vortex-api/docs/SISTEMAS.md` y síguelo (`docs/INTEGRACION.md` resume cómo lo aplica el ERP).

- Levantar el hub: `cd ~/vortex-api && docker compose up -d && dotnet run --project src/VortexApi --launch-profile http`
  (http://localhost:5299, referencia en /docs). Usuario demo: `luis@vortex.local` / `Suite-Local-2026!`,
  clave de sistema `vxp_erp_dev_key_change_me`.
- SDK: paquete `VortexApi.Client` desde el feed local `packages/vortex-sdk` (`scripts/pack-sdk.*`). Si cambias el
  SDK, hazlo en `~/vortex-api` con sus pruebas y vuelve a empaquetar.
- No crear tablas de usuarios ni contraseñas; no llamar directamente a otros sistemas; usar Ids del hub.
- Si falta algo en el hub, proponer el cambio en `~/vortex-api` en vez de un parche local.
