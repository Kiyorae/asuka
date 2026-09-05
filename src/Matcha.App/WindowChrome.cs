using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Composition.SystemBackdrops;
using Windows.Graphics;

namespace Matcha.App;

internal static class WindowChrome
{
    public static void Configure(Window window, int width, int height)
    {
        window.AppWindow.Resize(new SizeInt32(width, height));
        ApplyIcon(window);
        if (window.AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(true, true);
        }
    }

    public static void ConfigureSplash(Window window, int width, int height)
    {
        window.SystemBackdrop = new MicaBackdrop { Kind = MicaKind.BaseAlt };
        window.AppWindow.Resize(new SizeInt32(width, height));
        ApplyIcon(window);

        if (window.AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(false, false);
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
        }

        var workArea = DisplayArea.GetFromWindowId(
            window.AppWindow.Id,
            DisplayAreaFallback.Primary).WorkArea;
        window.AppWindow.Move(new PointInt32(
            workArea.X + ((workArea.Width - width) / 2),
            workArea.Y + ((workArea.Height - height) / 2)));
    }

    private static void ApplyIcon(Window window)
    {
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "Matcha.ico");
        if (File.Exists(iconPath))
        {
            window.AppWindow.SetIcon(iconPath);
        }
    }

    public static ElementTheme ToElementTheme(AppThemePreference theme) => theme switch
    {
        AppThemePreference.Light => ElementTheme.Light,
        AppThemePreference.Dark => ElementTheme.Dark,
        _ => ElementTheme.Default,
    };
}
