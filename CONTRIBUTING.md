# Contribuir a Vortex ERP

Gracias por tu interés. Vortex ERP es la gestión administrativa de la suite Vortex (ERP, CRM, WMS, POS): pedidos,
facturas y boletas, cobranzas, compras e inventario valorizado, con trabajo sin conexión. Es software libre
(AGPL-3.0) y las contribuciones son bienvenidas: código, documentación, pruebas, reportes de errores e ideas.

## Antes de empezar

- **Errores y propuestas**: abre un [issue](https://github.com/ArcGabito/vortex-erp/issues) con los pasos para
  reproducirlo, la plataforma (Windows, Android, iOS, macOS) y la versión de la app (**Configuración → Acerca de**).
  Para cambios grandes (una pantalla nueva, un evento, un cambio en cómo se factura), discútelo en un issue
  antes de programar.
- **Vulnerabilidades**: **no** abras un issue público; sigue [SECURITY.md](SECURITY.md).
- **Si lo que falta es del hub** (un dato maestro, un evento, un permiso), propónlo en
  [vortex-api](https://github.com/ArcGabito/vortex-api/issues) siguiendo [docs/SISTEMAS.md del hub](https://github.com/ArcGabito/vortex-api/blob/main/docs/SISTEMAS.md)
  (sección 10), en vez de resolverlo con un parche local en el ERP.

## Entorno de desarrollo

Requisitos: [.NET SDK 10](https://dotnet.microsoft.com/download) con el workload de MAUI
(`dotnet workload install maui`) y el hub `vortex-api` corriendo en local, en Linux, macOS o WSL con Docker. Para
Android hacen falta además un JDK y el SDK de Android.

```bash
git clone https://github.com/ArcGabito/vortex-api ~/vortex-api
cd ~/vortex-api && docker compose up -d && dotnet run --project src/VortexApi --launch-profile http   # http://localhost:5299

git clone https://github.com/ArcGabito/vortex-erp && cd vortex-erp
scripts/pack-sdk.sh                                                # SDK del hub -> packages/vortex-sdk
dotnet test tests/VortexErp.Core.Tests                             # pruebas (no necesitan el hub)
dotnet run --project src/VortexErp -f net10.0-windows10.0.19041.0   # compila y abre la app en Windows
```

En Windows con el hub dentro de WSL, el SDK se empaqueta con `./scripts/pack-sdk.ps1` (`-Distro Ubuntu` si tu
distro no es `archlinux`). El usuario demo y el resto de plataformas están en
[README.md](README.md#empezar).

| Carpeta | Contenido |
|---|---|
| `src/VortexErp.Core` | Todo lo que no es interfaz, sin dependencias de MAUI: `Data/` (SQLite con `Microsoft.Data.Sqlite`, sin ORM), `Services/` (pedidos, comprobantes y cobranzas, compras, reportes, datos maestros), `Sync/` (store de la sincronización y handlers de eventos), `Integration/` (contratos de los eventos v1). |
| `src/VortexErp` | App MAUI Blazor Hybrid: `Services/` (sesión, sincronización, formatos), `Components/Pages` (pantallas), `Components/Shared` (componentes), `wwwroot/css/app.css` (sistema de diseño), `Platforms/`. |
| `tests/VortexErp.Core.Tests` | Pruebas xUnit sobre bases SQLite temporales. `SyncTests` simula otros equipos del ERP y otros sistemas, y `HubIntegrationTests` corre contra un hub real. |
| `scripts/` | `pack-sdk.sh` y `pack-sdk.ps1`: empaquetan el SDK del hub en `packages/vortex-sdk`. |
| `docs/` | Documentación: integración con el hub y avisos de terceros. |

## Flujo de trabajo

1. Haz un fork (o una rama, si tienes acceso) desde `main`: `feat/…`, `fix/…`, `docs/…`, `chore/…`.
2. Haz los cambios **con sus pruebas** y actualiza la documentación afectada (`docs/INTEGRACION.md` si cambia lo
   que el ERP envía al hub o consume de él).
3. Agrega una línea en [CHANGELOG.md](CHANGELOG.md) bajo `## [Unreleased]` (`Added`, `Changed`, `Fixed`,
   `Removed`, `Security`) si el cambio es visible para quien usa la app.
4. Abre un pull request hacia `main` y completa la plantilla.

`main` está protegida:

- El CI debe pasar: **Pruebas** (Linux: empaqueta el SDK del hub y corre `dotnet test`) y **App (Windows)**
  (compila la app en Release con las advertencias como errores). El CI usa el `main` de `vortex-api`: si tu cambio
  necesita algo nuevo del hub, ese cambio debe fusionarse allá primero.
- La rama debe estar al día con `main` y el historial es lineal: los PR se fusionan con *squash*. Actualiza tu
  rama con `git rebase origin/main` (no con merge).
- Todas las conversaciones de la revisión deben quedar resueltas.

## Convenciones

**Idioma.** Identificadores de código en inglés (`InvoiceService`, `PurchaseService`); textos de la interfaz,
mensajes de error, comentarios, docs y mensajes de commit en español (es-PE). Los nombres de las pruebas son
frases en español (`Factura_exige_RUC_desglosa_el_igv_y_publica_invoice_issued`).

**Código.** Sigue el estilo del código que rodea al cambio: C# moderno, *nullable* habilitado y SQL escrito a
mano. Sin dependencias nuevas salvo que sean necesarias (coméntalo en el PR). Comentarios solo donde expliquen el
*porqué*.

**Documentos.**

- Cada documento (pedido, comprobante, cobro, orden de compra) se guarda y encola su evento en una sola
  transacción (`order.created`, `invoice.issued`, `payment.received`, `purchase_order.created`) con el Id del documento.
- Un comprobante emitido no se edita. Lo cobrado en caja ya viene pagado; solo el crédito queda por cobrar.
- Un pedido que sale de una cotización del CRM usa `OrderService.OrderIdForQuote`, el mismo Id que calcula el CRM,
  para que el pedido no se duplique si los dos lo crean.
- El stock es del WMS: el ERP guarda el saldo que publica (`stock.adjusted`) y solo lo valoriza.
- Los errores para el usuario se lanzan con `ErpValidationException` y el mensaje final en español.

**Integración con el hub.**

- Usuarios, artículos, sedes, clientes y proveedores son del hub: no se crean tablas de usuarios ni contraseñas,
  se usan los Ids del hub y nunca se llama directamente a otro sistema de la suite. Sigue
  [docs/SISTEMAS.md del hub](https://github.com/ArcGabito/vortex-api/blob/main/docs/SISTEMAS.md).
- Los handlers de eventos son idempotentes (`processed_events`) y nunca lanzan por datos inválidos: los registran
  como ignorados para no trabar el feed.
- `VortexApi.Client` viene del feed local `packages/vortex-sdk`. Si hay que cambiar el SDK, se cambia en
  `vortex-api` con sus pruebas y se vuelve a empaquetar; no se copia ni se parchea aquí.

**Base de datos local.** El esquema se migra con `PRAGMA user_version`: un cambio es una entrada nueva al final de
`Schema.Migrations` en `src/VortexErp.Core/Data/PosDatabase.cs`. Las migraciones ya publicadas no se modifican,
porque los equipos de los usuarios ya las aplicaron.

**Interfaz.**

- Las pantallas viven en `Components/Pages` y heredan de `ErpPage`. Antes de crear estilos o componentes nuevos,
  reutiliza las clases de `app.css` (`filters`, `table.data`, `two-line`, `pill`, `modal`…) y los componentes de
  `Components/Shared` (`Modal`, `KebabMenu`, `StatTile`, `StatusPill`, `Pager`, `ItemPicker`, `LineEditor`).
- Cantidades, montos y fechas se muestran con `Fmt` (cultura es-PE).
- En Razor, dentro de atributos usa `@(...)` cuando la expresión lleve comillas, y no nombres variables `code` ni
  `page`.
- Revisa la pantalla en ancho de escritorio y de móvil.

**Pruebas.** Cada cambio de comportamiento en `VortexErp.Core` lleva una prueba en `tests/VortexErp.Core.Tests`;
si afecta a la replicación entre equipos, agrégala en `SyncTests`. Antes de abrir el PR:

```bash
dotnet test tests/VortexErp.Core.Tests                                         # sin fallos
dotnet build src/VortexErp -f net10.0-windows10.0.19041.0                      # sin advertencias nuevas
VORTEX_HUB_URL=http://localhost:5299/ dotnet test tests/VortexErp.Core.Tests   # con el hub levantado
```

**Commits.** Mensajes en español, en imperativo o descriptivos, con un resumen corto en la primera línea y el
detalle debajo si hace falta (`Agregar notas de crédito`).

## Versiones

El proyecto sigue [Versionado Semántico](https://semver.org/lang/es/). Las versiones las publica quien mantiene
el repo: actualiza `Version` en `Directory.Build.props` (la app la toma como `ApplicationDisplayVersion`), sube
`ApplicationVersion` en `src/VortexErp/VortexErp.csproj`, mueve `[Unreleased]` del CHANGELOG a la nueva versión y
crea el tag `vX.Y.Z`.

## Licencia

Al contribuir aceptas que tu contribución se publique bajo la licencia del proyecto,
[AGPL-3.0-only](LICENSE.md). Las tipografías, íconos o bibliotecas de terceros que agregues deben tener una
licencia compatible y registrarse en [docs/THIRD-PARTY-NOTICES.md](docs/THIRD-PARTY-NOTICES.md).

## Uso de agentes de IA

Puedes usar asistentes de IA para contribuir. Revisa y entiende todo lo que generen: eres responsable del cambio
como si lo hubieras escrito tú, y las pruebas y convenciones aplican igual. Para el contexto del proyecto, apunta
al agente a este archivo, a [CLAUDE.md](CLAUDE.md) y a [docs/SISTEMAS.md del hub](https://github.com/ArcGabito/vortex-api/blob/main/docs/SISTEMAS.md).
