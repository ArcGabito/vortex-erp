using VortexErp.Services;

namespace VortexErp;

public partial class MainPage : ContentPage
{
    public MainPage()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Atrás de Android: primero cierra el diálogo o el menú abierto, después vuelve a la página anterior; solo en el
    /// panel sale de la app.
    /// </summary>
    protected override bool OnBackButtonPressed() =>
        Handler?.MauiContext?.Services.GetService<BackButton>()?.Handle() == true || base.OnBackButtonPressed();
}
