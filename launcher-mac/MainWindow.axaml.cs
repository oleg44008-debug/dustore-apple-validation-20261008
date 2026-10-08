using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DustoreLauncherV.Mac.Controls;
using DustoreLauncherV.Mac.Services;
using DustoreLauncherV.Mac.ViewModels;

namespace DustoreLauncherV.Mac;

public partial class MainWindow : Window
{
    private Task? _initializeTask;
    private bool _closingPrompt;
    private readonly SurfaceMotion _surfaceMotion = new();
    private readonly GameControlHub _gameControls;

    private readonly NativeWebView? _web;
    private string? _webSectionShown;

    public MainWindow() : this(new MainViewModel()) { }

    public MainWindow(MainViewModel viewModel)
    {
        AvaloniaXamlLoader.Load(this);
        FreeAppearancePreview = ArrangeFreeSettings();
        Title = "DUSTORE LAUNCHER V" + (Edition.IsPrime ? " Prime" : "");
        ViewModel = viewModel;
        DataContext = ViewModel;
        _gameControls = new GameControlHub(this);
        InstallNativeMenu();
        if (OperatingSystem.IsMacOS())
        {
            // Content runs under the title bar; the rail leaves room for the window buttons.
            ExtendClientAreaToDecorationsHint = true;
            ExtendClientAreaChromeHints = Avalonia.Platform.ExtendClientAreaChromeHints.PreferSystemChrome;
            ExtendClientAreaTitleBarHeightHint = 34;
            // Sidebar material like Finder: blurred desktop behind a translucent tint when macOS grants it.
            TransparencyLevelHint = new[] { WindowTransparencyLevel.AcrylicBlur, WindowTransparencyLevel.Blur, WindowTransparencyLevel.None };
            Opened += (_, _) =>
            {
                if (ActualTransparencyLevel != WindowTransparencyLevel.AcrylicBlur && ActualTransparencyLevel != WindowTransparencyLevel.Blur) return;
                Background = Avalonia.Media.Brushes.Transparent;
                if (this.FindControl<Border>("Sidebar") is { } sidebar && Application.Current?.FindResource("NavTranslucent") is Avalonia.Media.IBrush tint)
                    sidebar.Background = tint;
            };
        }
        if (NativeWebView.IsSupported && this.FindControl<Panel>("SiteHost") is { } siteHost)
        {
            // WKWebView exists only on macOS; other systems never create a native host.
            if (!string.IsNullOrWhiteSpace(ViewModel.DataDirectory))
                Services.WebKitBridge.DownloadDirectory = System.IO.Path.Combine(ViewModel.DataDirectory, "Downloads");
            _web = new NativeWebView();
            _web.StateChanged += (_, _) => PushWebState();
            _web.DownloadChanged += (_, download) => ViewModel.UpdateDownload(download);
            _web.FallbackDownload += (_, url) => ViewModel.OpenDownloadInBrowser(url);
            siteHost.Children.Add(_web);
        }
        ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.Section))
            {
                ShowWebSection();
                _web?.SetPresentationActive(IsActive && WindowState != WindowState.Minimized && ViewModel.IsWeb);
                Dispatcher.UIThread.Post(() => _surfaceMotion.Reveal(this.FindControl<Control>(ViewModel.Section switch
                {
                    "library" => "LibraryPage", "ex" => "ExPage", "settings" => "SettingsPage", _ => ""
                }), ViewModel.EffectiveReduceMotion || WindowState == WindowState.Minimized));
            }
            if (e.PropertyName == nameof(MainViewModel.SelectedGame) && ViewModel.IsLibrary)
                Dispatcher.UIThread.Post(() => _surfaceMotion.Reveal(this.FindControl<Border>("GameHero"), ViewModel.EffectiveReduceMotion || WindowState == WindowState.Minimized, 180, 6));
            if (e.PropertyName == nameof(MainViewModel.ShelfPageIndex) && this.FindControl<ScrollViewer>("LibraryScroll") is { } scroll) scroll.Offset = default;
            if (e.PropertyName == nameof(MainViewModel.EffectiveReduceMotion))
            {
                Classes.Set("reducedMotion", ViewModel.EffectiveReduceMotion);
                if (ViewModel.EffectiveReduceMotion) { _surfaceMotion.Reset(); IntroMotion.Skip(); }
            }
        };
        if (Program.StartSection is "ex" or "settings" || MainViewModel.WebStartUrlFor(Program.StartSection ?? "") is not null)
        {
            ViewModel.Section = Program.StartSection!;
            ShowWebSection();
        }
        // ULTRA: the launcher goes to the Dock so the game gets the GPU it was drawing with.
        ViewModel.YieldToGameRequested += (_, _) => Dispatcher.UIThread.Post(() => WindowState = WindowState.Minimized);
        ViewModel.CoverPickRequested += async (_, _) =>
        {
            if (!StorageProvider.CanOpen) return;
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Обложка игры", AllowMultiple = false,
                FileTypeFilter = new[] { new FilePickerFileType("Изображения") { Patterns = new[] { "*.png", "*.jpg", "*.jpeg" } } }
            });
            if (files.FirstOrDefault()?.TryGetLocalPath() is { } path) await ViewModel.SetCustomCoverAsync(path);
        };
        Opened += OnOpened;
        Closing += OnClosing;
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        DragDrop.AddDragOverHandler(this, OnDragOver);
        DragDrop.AddDragLeaveHandler(this, (_, _) => { if (this.FindControl<Border>("DropHint") is { } hint) hint.IsVisible = false; });
        DragDrop.AddDropHandler(this, OnDrop);
        SizeChanged += (_, _) => UpdatePresentation();
        PositionChanged += (_, _) => RefreshCurrentDisplay();
        Closed += (_, _) => { _gameControls.Dispose(); _surfaceMotion.Dispose(); IntroMotion.Skip(); ViewModel.Dispose(); };
        PropertyChanged += (_, args) =>
        {
            if (args.Property != WindowStateProperty && args.Property != IsActiveProperty) return;
            bool minimized = WindowState == WindowState.Minimized;
            ViewModel.SetBackgroundWorkPaused(minimized);
            if (IsActive) ViewModel.RefreshSystemMotionPreference();
            if (minimized) { _surfaceMotion.Reset(); IntroMotion.Skip(); }
            _web?.SetPresentationActive(IsActive && !minimized && ViewModel.IsWeb);
        };
    }

    public MainViewModel ViewModel { get; }
    public NativeWebView? WebView => _web;
    internal GameControlHub GameControls => _gameControls;
    internal Expander? FreeAppearancePreview { get; }

    private Expander? ArrangeFreeSettings()
    {
        if (Edition.IsPrime) return null;
        // Build the Free hierarchy before assigning DataContext, so reparenting cannot reset
        // a two-way preference binding. The original disabled Prime controls remain a preview.
        var cards = this.FindControl<StackPanel>("SettingsCards")!;
        var appearance = this.FindControl<Border>("AppearanceCard")!;
        var contents = this.FindControl<StackPanel>("AppearanceContents")!;
        var note = this.FindControl<TextBlock>("MotionPreferenceHint")!;
        var motion = this.FindControl<CheckBox>("MotionToggle")!;
        contents.Children.Remove(note); contents.Children.Remove(motion);
        var comfort = new StackPanel { Spacing = 10 };
        comfort.Children.Add(new TextBlock { Text = "Движение", FontSize = 16, FontWeight = Avalonia.Media.FontWeight.SemiBold });
        comfort.Children.Add(note); comfort.Children.Add(motion);
        var motionCard = new Border { Child = comfort }; motionCard.Classes.Add("card");
        cards.Children.Remove(appearance);
        var library = this.FindControl<Border>("LibrarySettingsCard")!;
        cards.Children.Remove(library);
        cards.Children.Insert(0, motionCard); cards.Children.Insert(1, library);
        var preview = new Expander { Header = "Оформление Prime · посмотреть возможности", Content = appearance,
            IsExpanded = false, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch };
        cards.Children.Add(preview);
        return preview;
    }

    private void InstallNativeMenu()
    {
        var file = new NativeMenu();
        var add = new NativeMenuItem("Добавить игру…") { Gesture = new KeyGesture(Key.O, KeyModifiers.Meta) };
        var addFolder = new NativeMenuItem("Добавить папку игры…");
        add.Click += (_, _) => AddGameFile_Click(this, new RoutedEventArgs());
        addFolder.Click += (_, _) => AddGameFolder_Click(this, new RoutedEventArgs());
        file.Items.Add(add); file.Items.Add(addFolder); file.Items.Add(new NativeMenuItemSeparator());
        file.Items.Add(new NativeMenuItem("Открыть папку библиотеки") { Command = ViewModel.RevealDataCommand });
        file.Items.Add(new NativeMenuItem("Обновить библиотеку") { Command = ViewModel.RefreshCommand, Gesture = new KeyGesture(Key.R, KeyModifiers.Meta) });
        file.NeedsUpdate += (_, _) => { add.IsEnabled = addFolder.IsEnabled = !ViewModel.IsBusy && StorageProvider.CanOpen; };
        var view = new NativeMenu();
        view.Items.Add(new NativeMenuItem("Библиотека") { Command = ViewModel.LibraryCommand, Gesture = new KeyGesture(Key.D1, KeyModifiers.Meta) });
        var ex = new NativeMenuItem("eX · перенос игр") { Command = ViewModel.ExCommand, Gesture = new KeyGesture(Key.D2, KeyModifiers.Meta) };
        var store = new NativeMenuItem("Магазин") { Command = ViewModel.StoreCommand, Gesture = new KeyGesture(Key.D3, KeyModifiers.Meta) };
        view.Items.Add(ex); view.Items.Add(store); view.Items.Add(new NativeMenuItemSeparator());
        view.Items.Add(new NativeMenuItem("Настройки…") { Command = ViewModel.SettingsCommand, Gesture = new KeyGesture(Key.OemComma, KeyModifiers.Meta) });
        var fullscreen = new NativeMenuItem("На весь экран / вернуться") { Gesture = new KeyGesture(Key.F, KeyModifiers.Meta | KeyModifiers.Control) };
        fullscreen.Click += (_, _) => ToggleFullscreen(); view.Items.Add(fullscreen);
        view.NeedsUpdate += (_, _) => { ex.IsVisible = ViewModel.ShowExSection; store.IsVisible = ViewModel.ShowStoreSection; };
        var game = new NativeMenu();
        game.Items.Add(new NativeMenuItem("Играть") { Command = ViewModel.LaunchCommand });
        game.Items.Add(new NativeMenuItem("Закрыть запущенную игру") { Command = ViewModel.StopGameCommand });
        game.Items.Add(new NativeMenuItem("Показать игру в Finder") { Command = ViewModel.RevealGameCommand });
        NativeMenu.SetMenu(this, new NativeMenu
        {
            new NativeMenuItem("Файл") { Menu = file }, new NativeMenuItem("Вид") { Menu = view }, new NativeMenuItem("Игра") { Menu = game }
        });
    }

    private void ShowWebSection()
    {
        if (_web is null || !ViewModel.IsWeb) return;
        // Each site section opens its start page once; returning keeps the page the user left.
        if (_webSectionShown == ViewModel.Section) return;
        _webSectionShown = ViewModel.Section;
        _web.Navigate(ViewModel.WebStartUrl);
    }

    private void PushWebState()
    {
        if (_web is null) return;
        var state = _web.State;
        ViewModel.UpdateWebState(state.Url, state.Title, state.IsLoading, state.Progress, state.CanGoBack, state.CanGoForward, state.Error);
    }

    private void WebBack_Click(object? sender, RoutedEventArgs e) => _web?.GoBack();
    private void WebForward_Click(object? sender, RoutedEventArgs e) => _web?.GoForward();
    private void WebReload_Click(object? sender, RoutedEventArgs e)
    {
        if (ViewModel.HasWebError) WebRetry_Click(sender, e);
        else _web?.Reload();
    }

    private void WebRetry_Click(object? sender, RoutedEventArgs e)
    {
        string url = ViewModel.WebRetryUrl;
        ViewModel.ClearWebError();
        _web?.Navigate(url);
    }
    private void DismissError_Click(object? sender, RoutedEventArgs e) => ViewModel.DismissError();
    private void Intro_PointerPressed(object? sender, PointerPressedEventArgs e) => IntroMotion.Skip();
    private void CancelDownload_Click(object? sender, RoutedEventArgs e) => _web?.CancelDownload();
    private void RetryDownload_Click(object? sender, RoutedEventArgs e)
    {
        string page = ViewModel.DownloadRetryUrl;
        ViewModel.Section = "store";
        _web?.Navigate(page);
    }
    private async void RetryDownloadImport_Click(object? sender, RoutedEventArgs e) => await ViewModel.RetryDownloadImportAsync();

    private void Fullscreen_Click(object? sender, RoutedEventArgs e) => ToggleFullscreen();
    private WindowState _beforeFullscreen;
    private void ToggleFullscreen()
    {
        if (WindowState == WindowState.FullScreen) WindowState = _beforeFullscreen;
        else { _beforeFullscreen = WindowState is WindowState.Normal or WindowState.Maximized ? WindowState : WindowState.Normal; WindowState = WindowState.FullScreen; }
    }
    private void UpdatePresentation()
    {
        ViewModel.SetPresentationSize(ClientSize.Width, ClientSize.Height);
        if (this.FindControl<Grid>("Shell") is { } shell) shell.ColumnDefinitions[0].Width = new GridLength(ViewModel.ExpandedNavigation ? 224 : 84);
        Classes.Set("compact", !ViewModel.ExpandedNavigation);
        RefreshCurrentDisplay();
    }
    private void RefreshCurrentDisplay()
    {
        if (Screens.ScreenFromWindow(this) is { } screen)
        {
            UltraMode.Display = ((int)(screen.Bounds.Width / screen.Scaling), (int)(screen.Bounds.Height / screen.Scaling));
            UltraMode.WorkingArea = (Math.Max(1, (int)(screen.WorkingArea.Width / screen.Scaling)),
                Math.Max(1, (int)(screen.WorkingArea.Height / screen.Scaling)));
        }
    }
    private void OnDragOver(object? sender, DragEventArgs e)
    {
        bool allowed = !ViewModel.IsBusy && e.DataTransfer.Contains(DataFormat.File);
        e.DragEffects = allowed ? DragDropEffects.Copy : DragDropEffects.None;
        if (this.FindControl<Border>("DropHint") is { } hint) hint.IsVisible = allowed;
        e.Handled = true;
    }
    private async void OnDrop(object? sender, DragEventArgs e)
    {
        if (this.FindControl<Border>("DropHint") is { } hint) hint.IsVisible = false;
        if (ViewModel.IsBusy) return;
        var files = e.DataTransfer.TryGetFiles()?.Select(f => f.TryGetLocalPath()).Where(p => !string.IsNullOrWhiteSpace(p)).ToArray();
        if (files is null || files.Length == 0) return;
        e.Handled = true;
        if (ViewModel.IsEx) await ViewModel.SetSourceAsync(files[0]!);
        else foreach (string? file in files) await ViewModel.ImportGameAsync(file!);
    }

    private void DragArea_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is Control source && source.FindAncestorOfType<Button>(includeSelf: true) is null
            && source.FindAncestorOfType<TextBox>(includeSelf: true) is null
            && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            BeginMoveDrag(e);
    }

    private void Tile_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is Control { DataContext: GameItemViewModel game })
        {
            ViewModel.SelectedGame = game;
            if (ViewModel.LaunchCommand.CanExecute(null)) ViewModel.LaunchCommand.Execute(null);
        }
    }
    private void Tile_GotFocus(object? sender, GotFocusEventArgs e)
    {
        if (sender is Control { DataContext: GameItemViewModel game }) ViewModel.SelectedGame = game;
    }

    public Task InitializeAsync() => _initializeTask ??= ViewModel.InitializeAsync();

    private async void OnOpened(object? sender, EventArgs args)
    {
        ViewModel.RefreshSystemMotionPreference();
        if (Screens.ScreenFromWindow(this) is { } screen)
        {
            UltraMode.Display = ((int)(screen.Bounds.Width / screen.Scaling), (int)(screen.Bounds.Height / screen.Scaling));
            if (!Program.UiSmoke)
            {
                double availableWidth = Math.Max(320, screen.WorkingArea.Width / screen.Scaling - 24);
                double availableHeight = Math.Max(320, screen.WorkingArea.Height / screen.Scaling - 24);
                MinWidth = Math.Min(MinWidth, availableWidth); MinHeight = Math.Min(MinHeight, availableHeight);
                Width = Math.Min(Width, availableWidth); Height = Math.Min(Height, availableHeight);
            }
        }
        UpdatePresentation();
        if (!Program.UiSmoke && !ViewModel.EffectiveReduceMotion && MainViewModel.PrimeIntroWanted(ViewModel.DataDirectory)) _ = IntroMotion.PlayAsync(this);
        try
        {
            await InitializeAsync();
            if (IsVisible) StartupDiagnostics.RecordReady(this);
        }
        catch (Exception error)
        {
            ViewModel.ReportError(error);
            StartupDiagnostics.RecordFailure(error);
        }
    }

    private async Task<string?> PickFileAsync(string title, bool gamesOnly = true)
    {
        if (ViewModel.IsBusy || !StorageProvider.CanOpen) return null;
        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = title,
                AllowMultiple = false,
                FileTypeFilter = gamesOnly ? new[]
                {
                    new FilePickerFileType("Игры и игровые данные") { Patterns = new[] { "*.exe", "*.zip", "*.pck", "*.love", "*.app" } },
                    FilePickerFileTypes.All
                } : new[] { FilePickerFileTypes.All }
            });
            return files.FirstOrDefault()?.TryGetLocalPath();
        }
        catch (Exception error) { ViewModel.ReportError(error); return null; }
    }

    private async Task<string?> PickFolderAsync(string title)
    {
        if (ViewModel.IsBusy || !StorageProvider.CanPickFolder) return null;
        try
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = title, AllowMultiple = false });
            return folders.FirstOrDefault()?.TryGetLocalPath();
        }
        catch (Exception error) { ViewModel.ReportError(error); return null; }
    }

    private async void AddGameFile_Click(object? sender, RoutedEventArgs e)
    {
        if (await PickFileAsync("Добавить игру в библиотеку") is { } path) await ViewModel.ImportGameAsync(path);
    }

    private async void AddGameFolder_Click(object? sender, RoutedEventArgs e)
    {
        if (await PickFolderAsync("Добавить Mac-приложение .app или папку игры") is { } path) await ViewModel.ImportGameAsync(path);
    }

    private async void SelectSourceFile_Click(object? sender, RoutedEventArgs e)
    {
        if (await PickFileAsync("Выбрать исходную сборку для eX") is { } path) await ViewModel.SetSourceAsync(path);
    }

    private async void SelectSourceFolder_Click(object? sender, RoutedEventArgs e)
    {
        if (await PickFolderAsync("Выбрать .app или папку исходной игры") is { } path) await ViewModel.SetSourceAsync(path);
    }

    private async void SelectRuntime_Click(object? sender, RoutedEventArgs e)
    {
        if (await PickFileAsync("Выбрать официальный пакет движка", false) is { } path) ViewModel.RuntimePath = path;
    }

    private async void SelectOutput_Click(object? sender, RoutedEventArgs e)
    {
        if (ViewModel.IsBusy || !StorageProvider.CanSave) return;
        try
        {
            var result = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Создать новый ZIP игры",
                SuggestedFileName = string.IsNullOrWhiteSpace(ViewModel.OutputPath) ? "game-converted.zip" : Path.GetFileName(ViewModel.OutputPath),
                DefaultExtension = "zip",
                FileTypeChoices = new[] { new FilePickerFileType("ZIP") { Patterns = new[] { "*.zip" } } }
            });
            if (result?.TryGetLocalPath() is { } path) ViewModel.OutputPath = path;
        }
        catch (Exception error) { ViewModel.ReportError(error); }
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (this.FindControl<Grid>("IntroLayer")?.IsVisible == true) { IntroMotion.Skip(); e.Handled = true; return; }
        bool command = e.KeyModifiers.HasFlag(KeyModifiers.Meta) || e.KeyModifiers.HasFlag(KeyModifiers.Control);
        if (e.Key == Key.F11 || e.Key == Key.F && e.KeyModifiers.HasFlag(KeyModifiers.Meta) && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        { ToggleFullscreen(); e.Handled = true; }
        else if (command && e.Key == Key.D1) { ViewModel.Section = "library"; e.Handled = true; }
        else if (command && e.Key == Key.D2 && ViewModel.ShowExSection) { ViewModel.Section = "ex"; this.FindControl<TextBox>("SourcePathBox")?.Focus(); e.Handled = true; }
        else if (command && e.Key == Key.D3 && ViewModel.ShowStoreSection) { ViewModel.Section = "store"; e.Handled = true; }
        else if (command && e.Key == Key.OemComma) { ViewModel.Section = "settings"; e.Handled = true; }
        else if (command && e.Key == Key.O && !ViewModel.IsBusy) { AddGameFile_Click(this, new RoutedEventArgs()); e.Handled = true; }
        else if (command && e.Key == Key.F && ViewModel.IsLibrary && ViewModel.HasGames)
        {
            this.FindControl<TextBox>("SearchBox")?.Focus(); e.Handled = true;
        }
        else if (e.Source is Control source && source.FindAncestorOfType<Button>(includeSelf: true) is { DataContext: GameItemViewModel game } && ViewModel.IsLibrary)
        {
            ViewModel.SelectedGame = game;
            if (e.Key == Key.Enter)
            {
                if (ViewModel.LaunchCommand.CanExecute(null)) ViewModel.LaunchCommand.Execute(null);
                else if (game.CanLaunch == false && ViewModel.ConvertGameCommand.CanExecute(null)) ViewModel.ConvertGameCommand.Execute(null);
                e.Handled = true;
            }
            else if (e.Key is Key.Left or Key.Right or Key.Up or Key.Down or Key.Home or Key.End or Key.PageUp or Key.PageDown)
            {
                var shelf = this.FindControl<ItemsControl>("Shelf");
                int columns = Math.Max(1, (int)Math.Floor((shelf?.Bounds.Width ?? 300) / (ViewModel.TileSize + 18)));
                int delta = e.Key switch { Key.Left => -1, Key.Right => 1, Key.Up => -columns, Key.Down => columns, Key.PageUp => -60, Key.PageDown => 60, _ => 0 };
                if (e.Key == Key.Home && ViewModel.Games.Count > 0) ViewModel.SelectVisibleGame(ViewModel.Games[0].Entry.Id);
                else if (e.Key == Key.End && ViewModel.Games.Count > 0) ViewModel.SelectVisibleGame(ViewModel.Games[^1].Entry.Id);
                else ViewModel.MoveShelfSelection(delta);
                Dispatcher.UIThread.Post(FocusSelectedTile, DispatcherPriority.Loaded); e.Handled = true;
            }
        }
        else if (e.Key == Key.Escape)
        {
            if (ViewModel.IsBusy) { ViewModel.CancelOperation(); e.Handled = true; }
            else if (WindowState == WindowState.FullScreen) { WindowState = _beforeFullscreen; e.Handled = true; }
            else if (ViewModel.IsLibrary && ViewModel.Search.Length > 0) { ViewModel.Search = ""; e.Handled = true; }
        }
    }
    private void FocusSelectedTile()
    {
        var button = this.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.DataContext is GameItemViewModel game && ReferenceEquals(game, ViewModel.SelectedGame));
        button?.Focus(NavigationMethod.Directional); button?.BringIntoView();
    }

    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (!ViewModel.IsBusy) return;
        e.Cancel = true;
        if (_closingPrompt) return;
        _closingPrompt = true;
        try
        {
            var dialog = new Window
            {
                Title = "Операция выполняется", Width = 460, Height = 210, CanResize = false,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Background = this.FindResource("BackgroundSoft") as Avalonia.Media.IBrush,
                Foreground = this.FindResource("TextPrimary") as Avalonia.Media.IBrush,
                FontFamily = FontFamily
            };
            var content = new StackPanel { Margin = new Thickness(24), Spacing = 18 };
            content.Children.Add(new TextBlock
            {
                Text = "eX ещё работает. Можно запросить отмену и дождаться завершения текущего шага. Исходные файлы сохраняются.",
                Foreground = this.FindResource("TextPrimary") as Avalonia.Media.IBrush, TextWrapping = Avalonia.Media.TextWrapping.Wrap
            });
            var actions = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 10 };
            var stay = new Button { Content = "Продолжить", Padding = new Thickness(15, 9) };
            var cancel = new Button { Content = "Отменить операцию", Padding = new Thickness(15, 9) };
            stay.Click += (_, _) => dialog.Close();
            cancel.Click += (_, _) => { ViewModel.CancelOperation(); dialog.Close(); };
            actions.Children.Add(stay); actions.Children.Add(cancel);
            content.Children.Add(actions); dialog.Content = content;
            await dialog.ShowDialog(this);
        }
        finally { _closingPrompt = false; }
    }
}
