namespace VortexErp;

public partial class App : Application
{
    public App()
    {
        InitializeComponent();
        UserAppTheme = AppTheme.Light;
    }

    protected override Window CreateWindow(IActivationState? activationState) =>
        new(new MainPage())
        {
            Title = "Vortex ERP",
            // Barra de título en la tinta de la marca, con la caja del logo junto al nombre (escritorio).
            TitleBar = new TitleBar
            {
                Title = "Vortex ERP",
                Icon = "titlebar_box.png",
                BackgroundColor = Color.FromArgb("#1C120D"),
                ForegroundColor = Colors.White,
                HeightRequest = 36,
            },
            MinimumWidth = 380,
            MinimumHeight = 600,
            Width = 1360,
            Height = 860,
        };
}
