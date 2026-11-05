using Microsoft.AspNetCore.Components;
using VortexErp.Core;
using VortexErp.Core.Domain;
using VortexErp.Services;

using static VortexErp.Services.Txt;

namespace VortexErp.Components;

/// <summary>
/// Base de las pantallas del ERP: acceso a la base de la organización, recarga automática cuando la sincronización
/// trae datos nuevos y ejecución de operaciones con mensajes para el usuario.
/// </summary>
public abstract class ErpPage : ComponentBase, IDisposable
{
    [Inject] protected AppSession Session { get; set; } = null!;
    [Inject] protected Toasts Toasts { get; set; } = null!;
    [Inject] protected NavigationManager Nav { get; set; } = null!;

    protected ErpWorkspace Erp => Session.Workspace!;
    protected bool Loading { get; private set; } = true;

    protected override async Task OnInitializedAsync()
    {
        Session.DataChanged += OnDataChanged;
        await ReloadAsync();
    }

    /// <summary>Carga los datos de la pantalla.</summary>
    protected abstract Task LoadAsync();

    protected async Task ReloadAsync()
    {
        if (Session.Workspace is null)
            return;
        try
        {
            await LoadAsync();
        }
        catch (Exception ex)
        {
            Toasts.Error(T("page.no_se_pudieron_cargar_los", ex.Message));
        }
        finally
        {
            Loading = false;
        }
    }

    private void OnDataChanged() => _ = InvokeAsync(async () =>
    {
        await ReloadAsync();
        StateHasChanged();
    });

    /// <summary>
    /// Ejecuta una operación del ERP. Devuelve el mensaje de error para mostrar (o null si salió bien); al terminar
    /// avisa a las demás pantallas y pide sincronizar.
    /// </summary>
    protected async Task<string?> TryAsync(Func<Task> action, string? success = null)
    {
        try
        {
            await action();
        }
        catch (ErpValidationException ex)
        {
            return ex.Message;
        }
        catch (Exception ex)
        {
            return T("page.no_se_pudo_completar_la", ex.Message);
        }
        if (success is not null)
            Toasts.Success(success);
        Session.NotifyLocalChange();
        return null;
    }

    /// <summary>Como <see cref="TryAsync"/>, pero el error se muestra como aviso.</summary>
    protected async Task RunAsync(Func<Task> action, string? success = null)
    {
        if (await TryAsync(action, success) is { } error)
            Toasts.Error(error);
    }

    public virtual void Dispose()
    {
        Session.DataChanged -= OnDataChanged;
        GC.SuppressFinalize(this);
    }
}
