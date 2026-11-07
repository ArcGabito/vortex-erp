# Publicar Vortex ERP en la Microsoft Store

En Windows, Vortex ERP se distribuye como paquete **MSIX** desde la **Microsoft Store**. La Store vuelve a firmar el
paquete con su propio certificado, **sin costo**: no hace falta comprar un certificado de firma de código y Windows no
muestra la advertencia de **SmartScreen** ("Windows protegió su PC"). La Store además actualiza la app sola.

El flujo `.github/workflows/store.yml` genera el paquete; esta guía cuenta lo que se configura una sola vez.

## 1. Una sola vez, en Partner Center

1. **Cuenta de desarrollador** en [Partner Center](https://partner.microsoft.com/dashboard) (programa Apps y juegos).
   Las cuentas individuales no pagan registro; las de empresa, un pago único.
2. **Reservar el nombre** "Vortex ERP" (Apps y juegos → Nueva aplicación).
3. En la app: **Administración del producto → Identidad del producto**. Copia estos tres valores como **variables**
   del repositorio (GitHub → Settings → Secrets and variables → Actions → Variables):

   | Variable | Partner Center | Ejemplo |
   |---|---|---|
   | `STORE_IDENTITY_NAME` | `Package/Identity/Name` | `12345ArcGabito.VortexERP` |
   | `STORE_PUBLISHER` | `Package/Identity/Publisher` | `CN=1A2B3C4D-…` |
   | `STORE_PUBLISHER_DISPLAY_NAME` | `Package/Properties/PublisherDisplayName` | `ArcGabito` |

4. **Primer envío, a mano:** ficha de la Store (descripción en español, inglés y portugués, capturas, categoría
   *Negocios*), URL de la política de privacidad, clasificación por edad y precio (gratis). La app es de escritorio
   con confianza total (`runFullTrust`): en *Funcionalidades restringidas* explica que es una app de gestión .NET MAUI
   que guarda su base local y se conecta al hub de la suite.

## 2. Publicar una versión

```bash
git tag v0.2.0 && git push origin v0.2.0
```

El flujo genera `VortexErp…_x64.msixupload` (artefacto `vortex-erp-msix-0.2.0`). Súbelo en Partner Center →
**Paquetes** del nuevo envío. La versión debe ser mayor que la última enviada; el paquete queda como `X.Y.Z.0`, que es
lo que exige la Store. También se puede lanzar a mano (Actions → Microsoft Store → Run workflow) indicando la versión.

## 3. Opcional: envío automático

Para que el flujo cree el envío solo, asocia una aplicación de Microsoft Entra ID en Partner Center (Configuración de la
cuenta → Administración de usuarios → Aplicaciones de Microsoft Entra, rol *Administrador*) y guarda:

- **Secretos:** `STORE_TENANT_ID`, `STORE_SELLER_ID`, `STORE_CLIENT_ID`, `STORE_CLIENT_SECRET`.
- **Variable:** `STORE_PRODUCT_ID` (el Id de la Store, p. ej. `9N…`).

Con eso, el trabajo *Enviar a la Store* usa la [CLI de la Microsoft Store](https://learn.microsoft.com/windows/apps/publish/msstore-dev-cli/overview)
(`msstore publish`) después de generar el paquete. Sin `STORE_PRODUCT_ID`, ese paso se omite.

## Notas

- **Desarrollo local:** el proyecto sigue sin empaquetar (`WindowsPackageType=None`) para que `dotnet run` funcione
  directo; el MSIX solo se genera en el flujo de la Store.
- **Datos:** instalada desde la Store, la app guarda su base local en la carpeta del paquete. Si alguien usaba una
  versión sin paquete, que **sincronice antes** de cambiar: lo que ya está en el hub vuelve a bajar solo.
- **ARM64:** el paquete es x64 y funciona en Windows ARM con emulación; un paquete ARM64 nativo puede sumarse después.
- En un PR que toca el empaquetado, el flujo genera el MSIX con una identidad de prueba solo para verificar que compila.
