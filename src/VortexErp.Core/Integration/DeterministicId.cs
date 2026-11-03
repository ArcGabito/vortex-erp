using System.Security.Cryptography;
using System.Text;

namespace VortexErp.Core.Integration;

/// <summary>
/// Ids derivados de un dato de otro sistema (p. ej. el movimiento que genera una venta del POS). Todos los equipos del
/// ERP calculan el mismo Id para lo mismo, así el hub deduplica lo que publican y nadie aplica dos veces.
/// </summary>
public static class DeterministicId
{
    private static readonly Guid Namespace = Guid.Parse("6b1f3c1e-6f0e-4a8c-9d7e-5f2a1c0b9e47");

    public static Guid From(string kind, params Guid[] parts)
    {
        var key = new StringBuilder(Namespace.ToString()).Append('|').Append(kind);
        foreach (var part in parts)
            key.Append('|').Append(part.ToString("N"));

        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(key.ToString()), hash);
        var bytes = hash[..16];
        bytes[7] = (byte)((bytes[7] & 0x0F) | 0x50);   // versión 5 (basado en nombre)
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);   // variante RFC 4122
        return new Guid(bytes);
    }
}
