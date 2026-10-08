using Avalonia;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;

namespace DustoreLauncherV.Mac.Controls;

/// <summary>A brief Prime signature with immediate cancellation and complete visual cleanup.</summary>
internal static class IntroMotion
{
    private static CancellationTokenSource? _playback;
    public static void Skip() => _playback?.Cancel();

    public static async Task PlayAsync(MainWindow window)
    {
        var layer = window.FindControl<Grid>("IntroLayer");
        var logo = window.FindControl<Border>("IntroLogo");
        var word = window.FindControl<StackPanel>("IntroWord");
        var badge = window.FindControl<Border>("IntroBadge");
        var sidebar = window.FindControl<Border>("Sidebar");
        var main = window.FindControl<Grid>("MainColumn");
        if (layer is null || logo is null || word is null || badge is null || sidebar is null || main is null) return;
        Skip();
        using var playback = new CancellationTokenSource();
        _playback = playback;
        var token = playback.Token;
        void HandleKey(object? sender, KeyEventArgs args) { Skip(); args.Handled = true; }
        window.KeyDown += HandleKey;
        word.Children.Clear();
        var letters = new List<TextBlock>();
        foreach (char character in "DUSTORE V")
        {
            var letter = new TextBlock
            {
                Text = character.ToString(), FontSize = 36, FontWeight = FontWeight.Bold, Opacity = 0,
                LetterSpacing = -0.4, FontFamily = window.FindResource("DisplayFont") as FontFamily ?? FontFamily.Default,
                Foreground = window.FindResource(character == 'V' ? "Orchid" : "TextPrimary") as IBrush
            };
            letters.Add(letter); word.Children.Add(letter);
        }
        sidebar.Opacity = main.Opacity = 0;
        layer.Opacity = 1; layer.IsVisible = true;
        try
        {
            var mark = Tween(logo, 460, token, 0, 1, 10, 0, 0.86, 1);
            await Task.Delay(180, token);
            var arrivals = letters.Select((letter, index) => Arrive(letter, index * 22, token)).ToList();
            await Task.Delay(260, token);
            var stamp = Tween(badge, 260, token, 0, 1, 7, 0, 0.95, 1);
            await Task.WhenAll(arrivals.Append(mark).Append(stamp));
            await Task.Delay(160, token);
            await Task.WhenAll(
                Tween(layer, 300, token, 1, 0, 0, -10, 1, 1),
                Tween(sidebar, 400, token, 0, 1, 0, 0, 1, 1),
                Tween(main, 430, token, 0, 1, 16, 0, 1, 1));
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { StartupDiagnostics.RecordFailure(error); }
        finally
        {
            window.KeyDown -= HandleKey;
            if (ReferenceEquals(_playback, playback)) _playback = null;
            layer.IsVisible = false; layer.Opacity = 1; layer.RenderTransform = null;
            foreach (var part in new Control[] { sidebar, main }) { part.Opacity = 1; part.RenderTransform = null; }
        }
    }

    private static async Task Arrive(Control letter, int delay, CancellationToken token)
    {
        await Task.Delay(delay, token);
        await Tween(letter, 330, token, 0, 1, 12, 0, 1, 1);
    }

    private static async Task Tween(Visual visual, int duration, CancellationToken token,
        double opacityFrom, double opacityTo, double yFrom, double yTo, double scaleFrom, double scaleTo)
    {
        token.ThrowIfCancellationRequested();
        var scale = new ScaleTransform(scaleFrom, scaleFrom);
        var translation = new TranslateTransform(0, yFrom);
        visual.RenderTransform = new TransformGroup { Children = { scale, translation } };
        visual.Opacity = opacityFrom;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var easing = new CubicEaseOut();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16.7) };
        timer.Tick += (_, _) =>
        {
            double progress = Math.Clamp(watch.Elapsed.TotalMilliseconds / duration, 0, 1);
            double eased = easing.Ease(progress);
            visual.Opacity = opacityFrom + (opacityTo - opacityFrom) * eased;
            translation.Y = yFrom + (yTo - yFrom) * eased;
            scale.ScaleX = scale.ScaleY = scaleFrom + (scaleTo - scaleFrom) * eased;
            if (progress < 1) return;
            timer.Stop(); completion.TrySetResult();
        };
        using var cancellation = token.Register(() => Dispatcher.UIThread.Post(() =>
        {
            timer.Stop(); completion.TrySetCanceled(token);
        }));
        timer.Start();
        try { await completion.Task; }
        finally { timer.Stop(); }
    }
}
