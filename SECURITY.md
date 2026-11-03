# Seguridad

Vortex ERP guarda en cada equipo los comprobantes, cobranzas y compras de la empresa, la sesión del usuario y la clave de sistema con la
que se conecta al hub, así que nos tomamos en serio los reportes de seguridad.

## Versiones con soporte

Se corrigen vulnerabilidades en la última versión publicada (ver [CHANGELOG.md](CHANGELOG.md)). Los equipos deben
actualizarse a esa versión para recibir las correcciones.

## Reportar una vulnerabilidad

**No abras un issue público.** Usa el reporte privado de GitHub:
[Security → Report a vulnerability](https://github.com/ArcGabito/vortex-erp/security/advisories/new).

Incluye, si puedes:

- Qué parte afecta (app, `VortexErp.Core`, scripts o CI), en qué plataforma (Windows, Android, iOS, macOS) y en
  qué versión.
- Pasos para reproducirlo o una prueba de concepto.
- El impacto que ves (p. ej. leer la sesión o la clave de sistema guardadas en el equipo, ver o modificar datos
  de otra organización, enviar al hub eventos que el usuario no generó).

Qué esperar:

- Confirmación de recepción en un máximo de 5 días hábiles.
- Una evaluación inicial y, si se confirma, un plan de corrección.
- Crédito en el aviso de seguridad, si lo deseas.

Pedimos no divulgar la vulnerabilidad hasta que exista una versión corregida.

## Alcance

Sí: la app (`src/VortexErp`), `src/VortexErp.Core`, los scripts de `scripts/` y los workflows de `.github/`.

No:

- El hub y su SDK `VortexApi.Client` (login, sesiones, claves de sistema en el servidor): repórtalo según el
  [SECURITY.md de vortex-api](https://github.com/ArcGabito/vortex-api/blob/main/SECURITY.md).
- Instalaciones concretas de terceros (repórtalo a quien las opera) y equipos ya comprometidos (con acceso de
  administrador o el dispositivo desbloqueado en manos de otra persona).
- Las credenciales de desarrollo publicadas a propósito: la clave `vxp_erp_dev_key_change_me` de
  `src/VortexErp/appsettings.json` y el usuario demo de las compilaciones Debug.
