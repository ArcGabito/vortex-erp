namespace VortexErp.Services;

/// <summary>
/// Botón Atrás del sistema (Android). Los diálogos y el layout se registran; atiende el último registrado que acepte
/// (el diálogo de más arriba antes que el menú o la navegación). Si nadie lo atiende, la app hace lo de siempre.
/// </summary>
public sealed class BackButton
{
    private readonly List<Func<bool>> _handlers = [];
    private readonly Lock _gate = new();

    public IDisposable Register(Func<bool> handler)
    {
        lock (_gate)
            _handlers.Add(handler);
        return new Registration(this, handler);
    }

    public bool Handle()
    {
        Func<bool>[] handlers;
        lock (_gate)
            handlers = [.. _handlers];
        for (var i = handlers.Length - 1; i >= 0; i--)
        {
            if (handlers[i]())
                return true;
        }
        return false;
    }

    private sealed class Registration(BackButton owner, Func<bool> handler) : IDisposable
    {
        public void Dispose()
        {
            lock (owner._gate)
                owner._handlers.Remove(handler);
        }
    }
}
