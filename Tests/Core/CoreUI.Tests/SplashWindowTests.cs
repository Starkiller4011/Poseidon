using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.Media;
using Poseidon;
using Xunit;

namespace CoreUI.Tests;

public sealed class SplashWindowTests
{
    [AvaloniaFact]
    public void Appearance_IsBlackWithCenteredBoldWhiteTitle()
    {
        var splash = new SplashWindow();
        Assert.Equal(Colors.Black, Assert.IsAssignableFrom<ISolidColorBrush>(splash.Background).Color);
        Assert.Equal(SystemDecorations.None, splash.SystemDecorations);
        Assert.False(splash.CanResize);
        Assert.Equal(WindowStartupLocation.CenterScreen, splash.WindowStartupLocation);
        var title = Assert.IsType<TextBlock>(splash.Content);
        Assert.Equal("Poseidon", title.Text);
        Assert.Equal(Colors.White, Assert.IsAssignableFrom<ISolidColorBrush>(title.Foreground).Color);
        Assert.Equal(FontWeight.Bold, title.FontWeight);
        Assert.Equal(HorizontalAlignment.Center, title.HorizontalAlignment);
        Assert.Equal(VerticalAlignment.Center, title.VerticalAlignment);
    }

    [AvaloniaFact]
    public async Task Loading_KeepsSplashVisibleUntilCompletionThenCloses()
    {
        var splash = new SplashWindow();
        var loading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var task = splash.ShowWhileLoadingAsync(() =>
        {
            Assert.True(splash.IsVisible);
            return loading.Task;
        });
        try
        {
            Assert.True(splash.IsVisible);
            Assert.False(task.IsCompleted);
        }
        finally
        {
            loading.TrySetResult();
            await task;
        }
        Assert.False(splash.IsVisible);
    }

    [AvaloniaFact]
    public async Task LoadingFailure_ClosesSplashAndPropagatesErrorForRecovery()
    {
        var splash = new SplashWindow();
        var error = new InvalidOperationException("Module discovery failed");
        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            splash.ShowWhileLoadingAsync(() => Task.FromException(error)));
        Assert.Same(error, thrown);
        Assert.False(splash.IsVisible);
    }
}
