using System.ComponentModel;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using DustoreLauncherV.Mac.Services;

namespace DustoreLauncherV.Mac;

public partial class MainWindow
{
    private IClassicDesktopStyleApplicationLifetime? _gameLifetime;
    private bool _gameLifetimeClosed, _deferredGameClose, _quitInProgress;
    private CancellationTokenSource? _quitCancellation;
    private Window? _quitDialog;

    internal void AttachGameLifetime(IClassicDesktopStyleApplicationLifetime lifetime)
    {
        _gameLifetime = lifetime;
        lifetime.ShutdownRequested += OnApplicationShutdownRequested;
    }

    private void OnApplicationShutdownRequested(object? sender, ShutdownRequestedEventArgs args)
    {
        if (_gameLifetimeClosed || (!ViewModel.IsBusy && !_gameControls.HasRunningGames)) return;
        args.Cancel = true;
        _ = ConfirmQuitAsync();
    }

    private bool PreserveGamesOnClose(WindowClosingEventArgs args)
    {
        if (_quitInProgress) { args.Cancel = true; return true; }
        if (args.CloseReason is WindowCloseReason.ApplicationShutdown or WindowCloseReason.OSShutdown)
        {
            if (!ViewModel.IsBusy && !_gameControls.HasRunningGames) return false;
            args.Cancel = true;
            _ = ConfirmQuitAsync();
            return true;
        }
        // Preserve the existing busy-operation prompt before an ordinary game-window hide.
        if (ViewModel.IsBusy || !_gameControls.HasRunningGames) return false;
        args.Cancel = true;
        _deferredGameClose = true;
        Hide();
        return true;
    }

    internal void GameSessionsReconciled()
    {
        if (_gameLifetimeClosed || !_deferredGameClose || _quitInProgress || _gameControls.HasRunningGames) return;
        _deferredGameClose = false;
        // A new eX operation still gets the existing cancellation prompt, with a visible owner.
        if (ViewModel.IsBusy) RestoreLiveMain();
        Close();
    }

    internal void ReturnFromGameControls(Guid entryId)
    {
        if (_gameLifetimeClosed) return;
        _deferredGameClose = false;
        _quitCancellation?.Cancel();
        _quitDialog?.Close(false);
        RestoreLiveMain();
        ViewModel.ShowRunningGame(entryId);
    }

    private void RestoreLiveMain()
    {
        if (_gameLifetimeClosed) return;
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Show();
        Activate();
    }

    private async Task ConfirmQuitAsync()
    {
        var lifetime = _gameLifetime;
        if (lifetime is null || _gameLifetimeClosed || _quitInProgress || _closingPrompt) return;
        _quitInProgress = true;
        _deferredGameClose = false;
        bool approved = false;
        using var cancellation = new CancellationTokenSource();
        _quitCancellation = cancellation;
        try
        {
            RestoreLiveMain();
            var screen = Screens.ScreenFromWindow(this);
            double width = screen is null ? ClientSize.Width : screen.WorkingArea.Width / screen.Scaling;
            double height = screen is null ? ClientSize.Height : screen.WorkingArea.Height / screen.Scaling;
            var dialog = new Window
            {
                Title = "Выйти из DUSTORE?", Width = Math.Min(460, Math.Max(260, width - 32)),
                Height = Math.Min(300, Math.Max(180, height - 64)), CanResize = false,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Background = this.FindResource("BackgroundSoft") as IBrush,
                Foreground = this.FindResource("TextPrimary") as IBrush, FontFamily = FontFamily
            };
            _quitDialog = dialog;
            var status = new TextBlock
            {
                Text = ViewModel.IsBusy
                    ? "Для выхода нужно запросить отмену eX и дождаться завершения операции. Запущенные игры будут закрыты после подтверждения."
                    : "Закрыть запущенные игры и выйти? До их завершения кнопки управления игрой останутся доступны.",
                TextWrapping = TextWrapping.Wrap
            };
            var cancel = new Button { Content = "Остаться в DUSTORE", MinHeight = 44, HorizontalAlignment = HorizontalAlignment.Stretch };
            var stop = new Button { Content = "Остановить и выйти", MinHeight = 44, HorizontalAlignment = HorizontalAlignment.Stretch };
            var actions = new StackPanel { Spacing = 8 };
            actions.Children.Add(cancel); actions.Children.Add(stop);
            var layout = new Grid { RowDefinitions = new RowDefinitions("*,Auto"), Margin = new Thickness(20) };
            layout.Children.Add(new ScrollViewer { Content = status });
            actions.Margin = new Thickness(0, 12, 0, 0);
            Grid.SetRow(actions, 1); layout.Children.Add(actions); dialog.Content = layout;
            cancel.Click += (_, _) => dialog.Close(false);
            dialog.Closed += (_, _) => cancellation.Cancel();
            stop.Click += async (_, _) =>
            {
                if (!stop.IsEnabled) return;
                stop.IsEnabled = false;
                // One explicit attempt has a finite budget; failure never escalates to Force.
                using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token);
                attempt.CancelAfter(TimeSpan.FromSeconds(30));
                try
                {
                    var native = NativeAppSessions.Snapshot();
                    var wine = GameSessions.Snapshot();
                    if (ViewModel.IsBusy)
                    {
                        status.Text = "Запрошена отмена eX. Ожидаю завершения текущего шага…";
                        ViewModel.CancelOperation();
                        await WaitForOperationRetirementAsync(attempt.Token);
                    }
                    status.Text = "Закрываю запущенные игры…";
                    foreach (var session in native)
                    {
                        attempt.Token.ThrowIfCancellationRequested();
                        if (!await session.StopAsync(false, attempt.Token))
                            throw new IOException("Игра ещё работает. Выход отменён; её панель управления остаётся доступной.");
                    }
                    foreach (var session in wine)
                    {
                        attempt.Token.ThrowIfCancellationRequested();
                        await session.StopAsync(attempt.Token, allowProcessKill: false);
                    }
                    await WaitForGameRetirementAsync(attempt.Token);
                    attempt.Token.ThrowIfCancellationRequested();
                    if (ViewModel.IsBusy)
                        throw new IOException("Началась новая операция. Дождитесь её завершения и повторите выход.");
                    dialog.Close(true);
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
                catch (OperationCanceledException)
                { status.Text = "Операция или игра ещё не завершилась. DUSTORE остаётся открытым; можно отменить выход или повторить запрос."; }
                catch (Exception error)
                { status.Text = "Не удалось завершить выход: " + error.Message; }
                finally { if (dialog.IsVisible) stop.IsEnabled = true; }
            };
            approved = await dialog.ShowDialog<bool>(this);
        }
        catch (Exception error)
        {
            // A failed prompt never authorizes destruction of live game controllers.
            StartupDiagnostics.RecordFailure(error);
        }
        finally
        {
            cancellation.Cancel();
            _quitCancellation = null; _quitDialog = null; _quitInProgress = false;
        }
        if (approved && !_gameLifetimeClosed && !ViewModel.IsBusy && !_gameControls.HasRunningGames)
            lifetime.TryShutdown();
    }

    private async Task WaitForOperationRetirementAsync(CancellationToken cancellation)
    {
        var retired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Changed(object? sender, PropertyChangedEventArgs args)
        { if (args.PropertyName == nameof(ViewModel.IsBusy) && !ViewModel.IsBusy) retired.TrySetResult(); }
        ViewModel.PropertyChanged += Changed;
        try
        {
            using var registration = cancellation.Register(() => retired.TrySetCanceled(cancellation));
            if (!ViewModel.IsBusy) retired.TrySetResult();
            await retired.Task;
        }
        finally { ViewModel.PropertyChanged -= Changed; }
    }

    private async Task WaitForGameRetirementAsync(CancellationToken cancellation)
    {
        var retired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Check()
        { if (!_gameLifetimeClosed && !_gameControls.HasRunningGames) retired.TrySetResult(); }
        void Changed(object? sender, GameSessionEventArgs args) => Dispatcher.UIThread.Post(Check);
        GameSessions.Changed += Changed; NativeAppSessions.Changed += Changed;
        try
        {
            using var registration = cancellation.Register(() => retired.TrySetCanceled(cancellation));
            Check();
            await retired.Task;
        }
        finally { GameSessions.Changed -= Changed; NativeAppSessions.Changed -= Changed; }
    }

    private void DisposeGameLifetime()
    {
        _gameLifetimeClosed = true;
        _quitCancellation?.Cancel();
        _quitDialog?.Close(false);
        if (_gameLifetime is not null) _gameLifetime.ShutdownRequested -= OnApplicationShutdownRequested;
        _gameLifetime = null;
    }
}
