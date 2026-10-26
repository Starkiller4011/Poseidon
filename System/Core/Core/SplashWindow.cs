using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Poseidon;

public sealed class SplashWindow : Window
{
    public SplashWindow()
    {
        Title = "Poseidon - starting";
        Width = 640;
        Height = 360;
        Background = Brushes.Black;
        SystemDecorations = SystemDecorations.None;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Content = new TextBlock
        {
            Text = "Poseidon",
            Foreground = Brushes.White,
            FontWeight = FontWeight.Bold,
            FontSize = 48,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
    }

    public async Task ShowWhileLoadingAsync(Func<Task> load)
    {
        Show();
        try { await load(); }
        finally { Close(); }
    }
}
