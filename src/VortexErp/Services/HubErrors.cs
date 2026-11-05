using VortexApi.Client;

using static VortexErp.Services.Txt;

namespace VortexErp.Services;

/// <summary>Mensajes para el usuario según el <c>code</c> estable de los errores del hub (SISTEMAS.md, sección 8).</summary>
public static class HubErrors
{
    public static string For(VortexApiException ex) => ex.Code switch
    {
        "invalid_credentials" => T("huberrors.correo_contrasena_incorrectos"),
        "account_locked" => T("huberrors.la_cuenta_esta_bloqueada_temporalmente"),
        "invalid_product" => T("huberrors.la_clave_de_sistema_del"),
        "product_not_enabled" or "product_disabled" => T("huberrors.vortex_erp_no_esta_habilitado"),
        "access_expired" => T("huberrors.tu_acceso_vortex_erp_vencio"),
        "no_access" => T("huberrors.tu_usuario_no_tiene_acceso"),
        "organization_disabled" => T("huberrors.la_organizacion_esta_desactivada"),
        "account_disabled" => T("huberrors.tu_cuenta_esta_desactivada"),
        "user_limit_reached" => T("huberrors.tu_organizacion_alcanzo_el_limite"),
        "device_limit_reached" => T("huberrors.tu_organizacion_alcanzo_el_limite_2"),
        "device_revoked" => T("huberrors.este_equipo_fue_dado_de"),
        "device_mismatch" => T("huberrors.la_sesion_pertenece_otro_equipo"),
        "refresh_token_reused" or "invalid_refresh_token" => T("huberrors.por_seguridad_se_cerro_la"),
        "not_signed_in" => T("huberrors.inicia_sesion_para_continuar"),
        "duplicate_document" => T("huberrors.ya_existe_un_cliente_proveedor"),
        "duplicate_sku" => T("huberrors.ya_existe_un_articulo_con"),
        "duplicate_code" => T("huberrors.ya_existe_una_sede_con"),
        "invalid_document" => T("huberrors.el_documento_no_es_valido"),
        "validation_error" => T("huberrors.hay_datos_invalidos") + ex.Message,
        _ => $"{ex.Message} ({ex.Code})",
    };

    public static string ForFailed(string code, string error) => code switch
    {
        "duplicate_document" or "duplicate_sku" or "duplicate_code" or "invalid_document" =>
            For(new VortexApiException(400, code, error)),
        _ => error,
    };
}
