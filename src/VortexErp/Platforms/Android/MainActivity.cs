using Android.App;
using Android.Content.PM;
using Android.OS;
using AndroidX.Core.View;

namespace VortexErp
{
    [Activity(Theme = "@style/Maui.SplashTheme", MainLauncher = true, LaunchMode = LaunchMode.SingleTop, ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
    public class MainActivity : MauiAppCompatActivity
    {
        protected override void OnCreate(Bundle? savedInstanceState)
        {
            base.OnCreate(savedInstanceState);
            // La app es clara: íconos oscuros en las barras de estado y de navegación.
            if (Window is { } window)
            {
                var bars = WindowCompat.GetInsetsController(window, window.DecorView);
                bars.AppearanceLightStatusBars = true;
                bars.AppearanceLightNavigationBars = true;
            }
        }
    }
}
