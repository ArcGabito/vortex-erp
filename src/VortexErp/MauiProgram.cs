using System.Globalization;
using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Maui.LifecycleEvents;
using VortexApi.Client.Sessions;
using VortexErp.Services;

namespace VortexErp;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        CultureInfo.DefaultThreadCurrentCulture = Fmt.Culture;
        CultureInfo.DefaultThreadCurrentUICulture = Fmt.Culture;

        var builder = MauiApp.CreateBuilder();
        builder.UseMauiApp<App>();

        using (var settings = Assembly.GetExecutingAssembly().GetManifestResourceStream("VortexErp.appsettings.json"))
        {
            if (settings is not null)
                builder.Configuration.AddJsonStream(settings);
        }

        builder.Services.AddMauiBlazorWebView();
        builder.Services.AddSingleton<HubSettings>();
        builder.Services.AddSingleton<ISessionStore, SecureStorageSessionStore>();
        builder.Services.AddSingleton<AppSession>();
        builder.Services.AddSingleton<Toasts>();
        builder.Services.AddSingleton<FileExporter>();

#if WINDOWS
        // Botones de la barra de título (minimizar, maximizar, cerrar) en blanco sobre la barra oscura de
        // App.CreateWindow; si no, con el tema claro de Windows quedan oscuros sobre oscuro.
        builder.ConfigureLifecycleEvents(events => events.AddWindows(windows => windows.OnWindowCreated(window =>
        {
            var bar = window.AppWindow.TitleBar;
            var white = Microsoft.UI.Colors.White;
            bar.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
            bar.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
            bar.ButtonForegroundColor = white;
            bar.ButtonHoverForegroundColor = white;
            bar.ButtonPressedForegroundColor = white;
            bar.ButtonHoverBackgroundColor = Windows.UI.Color.FromArgb(255, 0x33, 0x26, 0x1F);
            bar.ButtonPressedBackgroundColor = Windows.UI.Color.FromArgb(255, 0x4A, 0x36, 0x2C);
            bar.ButtonInactiveForegroundColor = Windows.UI.Color.FromArgb(255, 0x9A, 0x90, 0x8A);
        })));
#endif

#if DEBUG
        builder.Services.AddBlazorWebViewDeveloperTools();
        builder.Logging.AddDebug();
#endif

        return builder.Build();
    }
}
