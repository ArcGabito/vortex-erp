## Qué cambia y por qué

<!-- Resumen del cambio y el problema que resuelve. Enlaza el issue si existe: "Cierra #123". -->

## Cómo se probó

<!-- Pruebas agregadas o modificadas, y pruebas manuales en la app (plataforma y pantallas). -->

## Checklist

- [ ] `dotnet test tests/VortexErp.Core.Tests` pasa y la app compila sin advertencias nuevas.
- [ ] Pruebas para el comportamiento nuevo o corregido.
- [ ] `CHANGELOG.md` actualizado en `[Unreleased]`.
- [ ] Si toca el hub (sesión, datos maestros, eventos): sigue `docs/SISTEMAS.md` y `docs/INTEGRACION.md` está al día.
- [ ] Si cambia el esquema de la base local: nueva migración en `ErpDatabase` (sin editar las anteriores).
