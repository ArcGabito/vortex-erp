using VortexApi.Client;

namespace VortexErp.Services;

/// <summary>Mensajes para el usuario según el <c>code</c> estable de los errores del hub (SISTEMAS.md, sección 8).</summary>
public static class HubErrors
{
    public static string For(VortexApiException ex) => ex.Code switch
    {
        "invalid_credentials" => "Correo o contraseña incorrectos.",
        "account_locked" => "La cuenta está bloqueada temporalmente por varios intentos fallidos. Intenta más tarde.",
        "invalid_product" => "La clave de sistema del ERP no es válida para este hub. Revisa la configuración del servidor.",
        "product_not_enabled" or "product_disabled" => "Vortex ERP no está habilitado para tu organización.",
        "access_expired" => "Tu acceso a Vortex ERP venció. Pide a tu administrador que lo renueve.",
        "no_access" => "Tu usuario no tiene acceso a Vortex ERP.",
        "organization_disabled" => "La organización está desactivada.",
        "account_disabled" => "Tu cuenta está desactivada.",
        "user_limit_reached" => "Tu organización alcanzó el límite de usuarios del ERP. Contacta a tu administrador.",
        "device_limit_reached" => "Tu organización alcanzó el límite de equipos. Contacta a tu administrador.",
        "device_revoked" => "Este equipo fue dado de baja. Contacta a tu administrador.",
        "device_mismatch" => "La sesión pertenece a otro equipo. Inicia sesión de nuevo.",
        "refresh_token_reused" or "invalid_refresh_token" => "Por seguridad se cerró la sesión. Inicia sesión de nuevo.",
        "not_signed_in" => "Inicia sesión para continuar.",
        "duplicate_document" => "Ya existe un cliente o proveedor con ese documento.",
        "duplicate_sku" => "Ya existe un artículo con ese SKU.",
        "duplicate_code" => "Ya existe una sede con ese código.",
        "invalid_document" => "El documento no es válido (RUC de 11 dígitos, DNI de 8).",
        "validation_error" => "Hay datos inválidos: " + ex.Message,
        _ => $"{ex.Message} ({ex.Code})",
    };

    public static string ForFailed(string code, string error) => code switch
    {
        "duplicate_document" or "duplicate_sku" or "duplicate_code" or "invalid_document" =>
            For(new VortexApiException(400, code, error)),
        _ => error,
    };
}
