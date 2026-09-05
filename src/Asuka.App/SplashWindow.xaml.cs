using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Animation;

namespace Asuka.App;

internal sealed partial class SplashWindow : Window
{
    private bool _closed;
    private TaskCompletionSource? _closeCompletion;

    public SplashWindow()
    {
        InitializeComponent();
        Title = "Asuka";
        WindowChrome.ConfigureSplash(this, 420, 300);
        Closed += (_, _) =>
        {
            _closed = true;
            _closeCompletion?.TrySetResult();
        };
        Activated += SplashWindow_Activated;
    }

    private void SplashWindow_Activated(object sender, WindowActivatedEventArgs args)
    {
        Activated -= SplashWindow_Activated;
        if (FluentMotion.AreAnimationsEnabled) EntranceStoryboard.Begin();
        else RootGrid.Opacity = 1;
    }

    public async Task CloseWithAnimationAsync()
    {
        if (_closed)
        {
            return;
        }

        if (!FluentMotion.AreAnimationsEnabled)
        {
            Close();
            return;
        }

        if (_closeCompletion is not null)
        {
            await _closeCompletion.Task;
            return;
        }

        _closeCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnCompleted(object? sender, object e) => _closeCompletion.TrySetResult();

        ExitStoryboard.Completed += OnCompleted;
        try
        {
            ExitStoryboard.Begin();
            await _closeCompletion.Task;
        }
        finally
        {
            ExitStoryboard.Completed -= OnCompleted;
        }

        if (!_closed)
        {
            Close();
        }
    }
}
