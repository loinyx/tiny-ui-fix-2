using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics;

namespace TinyUIFixApp;

public sealed partial class MainWindow : Window
{
    [DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr hwnd);

    static readonly double[] PresetValues = [1, 1.25, 1.4, 1.6, 1.75, 2, 2.5];

    // FFmpeg "scale" filter names the script accepts
    static readonly string[] Algorithms = ["", "bilinear", "bicubic", "lanczos", "spline", "area", "bicublin", "gauss", "sinc", "neighbor", "experimental"];

    readonly ObservableCollection<PatchsetItem> _patches = [];
    readonly ObservableCollection<PatchsetItem> _order = [];
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };
    readonly UIElement[] _screens;
    string? _home;
    State? _state;
    List<GameInstall> _games = [];
    GameInstall? _game;
    Screen _screen = new(1920, 1080, false);
    bool _previewLoaded;
    // a first install walks through the screens before it can generate
    bool _firstRun;
    int _step;
    UIElement[] _wizard = [];

    // controls raise change events while InitializeComponent is still creating their neighbours
    bool _ready, _loading = true, _busy, _syncing, _gameRunning;
    int _page;

    public MainWindow()
    {
        InitializeComponent();
        _ready = true;
        _screens = [ScreenScale, ScreenPatches, ScreenCursor, ScreenManage, ScreenAbout];
        _wizard = [ScreenWelcome, ScreenScale, ScreenPatches, ScreenCursor];
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(DragArea);
        void Size(double scale) => AppWindow.Resize(new SizeInt32((int)(980 * scale), (int)(700 * scale)));
        Size(GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96.0);
        ((FrameworkElement)Content).Loaded += (_, _) => Size(Content.XamlRoot.RasterizationScale);
        if (AppWindow.Presenter is OverlappedPresenter p)
        {
            p.IsMaximizable = false;
            // below this the cards would squeeze their text into single words
            var scale = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96.0;
            p.PreferredMinimumWidth = (int)(900 * scale);
            p.PreferredMinimumHeight = (int)(620 * scale);
        }
        var ico = Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
        if (File.Exists(ico)) AppWindow.SetIcon(ico);

        Title = L.T("app.title");
        // number boxes format with the app's language, not Windows'
        foreach (var box in new[] { ScaleBox, TextBox, CursorBox })
            box.NumberFormatter = new Windows.Globalization.NumberFormatting.DecimalFormatter([L.Language], L.Language.Split('-')[1]) { FractionDigits = 0, IntegerDigits = 1 };
        LanguageText.Text = L.Languages.First(l => l.Code == L.Language).Name;
        foreach (var (code, name) in L.Languages)
        {
            var item = new RadioMenuFlyoutItem { Text = name, GroupName = "language", IsChecked = code == L.Language };
            item.Click += (_, _) => SwitchLanguage(code);
            LanguageMenu.Items.Add(item);
        }

        PatchList.ItemsSource = _patches;
        OrderList.ItemsSource = _order;
        foreach (var algorithm in Algorithms) AlgorithmBox.Items.Add(L.T("algo." + (algorithm.Length == 0 ? "default" : algorithm)));
        foreach (var v in PresetValues)
        {
            var b = new ToggleButton { Content = Label(v), Tag = v, MinWidth = 64 };
            b.Click += (_, _) => SetScale(v);
            Presets.Children.Add(b);
        }

        Nav.SelectedItem = Nav.MenuItems[0];
        _timer.Tick += (_, _) => RefreshGameRunning();
        _timer.Start();
        RefreshGameRunning();
        ((FrameworkElement)Content).Loaded += async (_, _) => await LoadAsync();
    }

    static string Label(double v) => v.ToString("0.##", CultureInfo.CurrentCulture) + "×";

    async Task LoadAsync()
    {
        _loading = true;
        Show(ScreenLoading);
        UpdateFooter();
        try
        {
            _home = Tuf.FindHome();
            await Task.Run(() => { Tuf.SyncEmbeddedTool(_home); Tuf.ExtractIfNeeded(_home); });
            var folder = Tuf.ScriptFolder(_home);
            var saved = Games.LoadChoice(folder);
            _firstRun = !File.Exists(Path.Combine(folder, "LastPatchsetConfiguration.json"));
            _games = Games.Find(saved?.Kind == "folder" ? saved.Path : null);
            _game = _games.FirstOrDefault(g => g == saved) ?? _games.FirstOrDefault(g => g.Kind != "ea") ?? _games.FirstOrDefault()
                ?? throw new ScriptException(L.T("error.noGame"), "");
            _syncing = true;
            GamePicker.ItemsSource = _games;
            GamePicker.SelectedItem = _game;
            _syncing = false;
            _state = await Tuf.ReadStateAsync(_home, _game, line => DispatcherQueue.TryEnqueue(() => LoadingLine.Text = line));
            _screen = Games.Resolution(_state.UserDataPath);
            // remembered, so a folder picked once (or found once) stays in the list
            Games.SaveChoice(folder, _game);
            // lets the preview be checked at other resolutions without touching the game's Options.ini
            if (Games.ParseResolution("resolution = " + Environment.GetEnvironmentVariable("TINY_UI_FIX_PREVIEW_SCREEN")?.Replace('x', ' ')) is { } forced)
                _screen = new Screen(forced.Width, forced.Height, true);
            Fill(_state);
            await LoadPreviewAsync(_state);
            _loading = false;
            if (_firstRun) ShowStep(0); else Show(_screens[_page]);
        }
        catch (Exception e)
        {
            App.Log(e);
            ProblemInfo.Title = e.Message;
            ProblemInfo.Message = e is ScriptException ? L.T("problem.log") : e.GetType().Name;
            ProblemLog.Text = (e as ScriptException)?.Output ?? e.ToString();
            Show(ScreenProblem);
        }
        UpdateFooter();
    }

    void Fill(State s)
    {
        _syncing = true;
        VersionText.Text = $"Tiny UI Fix 2 {Tuf.AppVersion.ToString(3)}  ·  script {s.ScriptVersion}";
        PackageText.Text = Path.Combine(s.UserDataPath, "Mods", "TinyUIFix");
        UpdateText.Text = L.F("manage.youHave", Tuf.AppVersion.ToString(3));

        var ui = Tuf.Parse(s.UIScale, 1);
        // nothing chosen yet: start from the size that suits the screen
        if (_firstRun && ui <= 1) ui = Preview.SuggestedScale(_screen);
        WelcomeScale.Text = Label(ui);
        WelcomeScaleWhy.Text = L.F("welcome.forScreen", _screen.Width, _screen.Height);
        WelcomeScreen.Text = $"{_screen.Width}×{_screen.Height} ({_screen.AspectName})";
        WelcomeGame.Text = _game is null ? L.T("welcome.game") : $"{_game.Title}  ·  {_game.Path}";
        var recommended = s.Patchsets.Count(p => p.Recommendation is not null);
        WelcomeMods.Text = recommended == 0 ? L.T("welcome.noExtras") : recommended == 1 ? L.T("welcome.recommended1") : L.F("welcome.recommendedN", recommended);
        var text = Tuf.Parse(s.TextScale, 0);
        TextSameToggle.IsOn = text == 0;
        TextSlider.Value = TextBox.Value = text == 0 ? ui : text;
        CursorBox.Value = Tuf.Parse(s.CursorScale, 1);
        var algorithm = s.CursorScalingAlgorithm;
        SharpToggle.IsOn = algorithm.EndsWith("!Sharp", StringComparison.Ordinal);
        algorithm = SharpToggle.IsOn ? algorithm[..^"!Sharp".Length] : algorithm;
        AlgorithmBox.SelectedIndex = Math.Max(0, Array.IndexOf(Algorithms, algorithm));
        MismatchToggle.IsOn = !s.DisableRuntimeModMismatchCheck;

        var disabled = s.DisabledRecommendedPatchsets.Where(id => id.Length > 0).ToHashSet();
        _patches.Clear();
        foreach (var p in s.Patchsets)
        {
            var item = new PatchsetItem
            {
                Id = p.ID, Version = p.Version, Fixed = p.Fixed, Recommendation = p.Recommendation,
                // the patchsets ship English text written for a web page; the known ones have shorter, translated text
                Name = L.T($"patch.{p.ID}.name") is var name && name != $"patch.{p.ID}.name" ? name : p.Name,
                Details = L.T($"patch.{p.ID}.details") is var details && details != $"patch.{p.ID}.details" ? details : Tuf.HtmlToText(p.Description),
            };
            // as the web page did: a recommendation is switched on unless the user switched it off before
            item.Enabled = p.Active || p.Fixed || (p.Recommendation is not null && s.LoadOrderNotActivelySetByUser && !disabled.Contains(p.ID));
            item.PropertyChanged += (_, _) => { SyncOrder(); UpdateFooter(); };
            _patches.Add(item);
        }
        _order.Clear();
        foreach (var id in s.LoadOrder)
            if (_patches.FirstOrDefault(p => p.Id == id && p.Enabled) is { } item) _order.Add(item);
        SyncOrder();
        _syncing = false;
        SetScale(ui);
        TextScaleRow.Visibility = TextSameToggle.IsOn ? Visibility.Collapsed : Visibility.Visible;
    }

    // keeps the order list to the enabled patchsets, new ones at the end, the nucleus first
    void SyncOrder()
    {
        foreach (var gone in _order.Where(p => !p.Enabled).ToList()) _order.Remove(gone);
        foreach (var added in _patches.Where(p => p.Enabled && !_order.Contains(p))) _order.Add(added);
        if (_order.FirstOrDefault(p => p.Fixed) is { } nucleus && _order.IndexOf(nucleus) != 0) _order.Move(_order.IndexOf(nucleus), 0);
    }

    void OnOrderDragged(ListViewBase sender, DragItemsCompletedEventArgs args) => SyncOrder();

    double Scale => ScaleBox.Value is double v && !double.IsNaN(v) && v > 0 ? v : 1;

    void SetScale(double v)
    {
        if (_syncing || !_ready) return;
        _syncing = true;
        v = Math.Round(Math.Clamp(v, 0.5, 5), 2);
        ScaleBox.Value = v;
        ScaleSlider.Value = Math.Clamp(v, ScaleSlider.Minimum, ScaleSlider.Maximum);
        ScaleBig.Text = Label(v);
        foreach (var b in Presets.Children.OfType<ToggleButton>()) b.IsChecked = Math.Abs((double)b.Tag - v) < 0.001;
        UpdatePreview();
        if (TextSameToggle.IsOn) { TextSlider.Value = Math.Clamp(v, TextSlider.Minimum, TextSlider.Maximum); TextBox.Value = v; }
        _syncing = false;
        UpdateFooter();
    }

    async Task LoadPreviewAsync(State s)
    {
        if (!_previewLoaded)
        {
            var scene = await Embedded("preview_scene.jpg");
            var hud = await Embedded("preview_hud.png");
            PreviewScene.Source = WelcomeBeforeScene.Source = WelcomeAfterScene.Source = scene;
            PreviewHud.Source = WelcomeBeforeHud.Source = WelcomeAfterHud.Source = hud;
            _previewLoaded = true;
        }
        UpdatePreview();
    }

    // the welcome's two small screens: the HUD as it is today at 1x, and at the suggested size
    void UpdateWelcomePreviews()
    {
        foreach (var (frame, image, scale) in new[] { (WelcomeBeforeFrame, WelcomeBeforeHud, 1.0), (WelcomeAfterFrame, WelcomeAfterHud, Scale) })
        {
            if (frame.ActualWidth <= 0) continue;
            var (height, hud) = Preview.Layout(frame.ActualWidth, _screen, scale);
            if (double.IsNaN(frame.Height) || Math.Abs(frame.Height - height) > 0.5) frame.Height = height;
            image.Width = hud;
        }
        WelcomeAfterCaption.Text = L.F("welcome.suggestedCaption", Label(Scale));
    }

    static async Task<BitmapImage> Embedded(string name)
    {
        using var stream = typeof(MainWindow).Assembly.GetManifestResourceStream(name)!;
        var bitmap = new BitmapImage();
        await bitmap.SetSourceAsync(stream.AsRandomAccessStream());
        return bitmap;
    }

    // the frame is the player's whole screen at its own aspect ratio, so the HUD keeps its real share of it at any window size
    void UpdatePreview()
    {
        UpdateWelcomePreviews();
        var width = PreviewFrame.ActualWidth;
        if (width <= 0) return;
        var (height, hud) = Preview.Layout(width, _screen, Scale);
        // Height starts as NaN (auto), which never compares as different
        if (double.IsNaN(PreviewFrame.Height) || Math.Abs(PreviewFrame.Height - height) > 0.5) PreviewFrame.Height = height;
        PreviewHud.Width = hud;
        var where = L.T(_screen.FromGame ? "size.fromGame" : "size.fromMonitor");
        PreviewCaption.Text = L.F("size.previewCaption", _screen.Width, _screen.Height, _screen.AspectName, where, Label(Scale))
            + (_screen.IsUltrawide ? " " + L.T("size.ultrawide") : "");
    }

    void OnPreviewResized(object sender, SizeChangedEventArgs e) => UpdatePreview();

    void OnWelcomeFrameResized(object sender, SizeChangedEventArgs e) { if (e.NewSize.Width != e.PreviousSize.Width) UpdateWelcomePreviews(); }

    async void OnGamePicked(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || _home is null || GamePicker.SelectedItem is not GameInstall game || game == _game) return;
        Games.SaveChoice(Tuf.ScriptFolder(_home), game);
        await LoadAsync();
    }

    async void OnBrowseGame(object sender, RoutedEventArgs e)
    {
        var picker = new Microsoft.Windows.Storage.Pickers.FolderPicker(AppWindow.Id);
        var folder = await picker.PickSingleFolderAsync();
        if (folder is null || _home is null) return;
        if (!GameInstall.IsGame(folder.Path))
        {
            await new ContentDialog
            {
                XamlRoot = Content.XamlRoot, Title = L.T("dialog.notGame"),
                Content = L.T("dialog.notGameBody"), CloseButtonText = L.T("dialog.ok"),
            }.ShowAsync();
            return;
        }
        Games.SaveChoice(Tuf.ScriptFolder(_home), new GameInstall("folder", Path.GetFullPath(folder.Path.TrimEnd('\\'))));
        await LoadAsync();
    }

    void OnScaleSlider(object sender, RangeBaseValueChangedEventArgs e) => SetScale(e.NewValue);
    void OnScaleBox(NumberBox sender, NumberBoxValueChangedEventArgs e) { if (!double.IsNaN(e.NewValue)) SetScale(e.NewValue); }

    void OnTextSame(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        TextScaleRow.Visibility = TextSameToggle.IsOn ? Visibility.Collapsed : Visibility.Visible;
        if (TextSameToggle.IsOn) SetScale(Scale);
    }

    void OnTextSlider(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_syncing || !_ready) return;
        _syncing = true; TextBox.Value = Math.Round(e.NewValue, 2); _syncing = false;
        UpdatePreview();
    }

    void OnTextBox(NumberBox sender, NumberBoxValueChangedEventArgs e)
    {
        if (_syncing || !_ready || double.IsNaN(e.NewValue)) return;
        _syncing = true; TextSlider.Value = Math.Clamp(e.NewValue, TextSlider.Minimum, TextSlider.Maximum); _syncing = false;
        UpdatePreview();
    }

    void OnAnySetting(object sender, object e) { if (_ready) UpdateFooter(); }

    Settings CurrentSettings() => new()
    {
        UIScale = Tuf.Format(Scale),
        TextScale = TextSameToggle.IsOn || double.IsNaN(TextBox.Value) ? "" : Tuf.Format(TextBox.Value),
        CursorScale = Tuf.Format(double.IsNaN(CursorBox.Value) ? 1 : CursorBox.Value),
        CursorScalingAlgorithm = Algorithms[Math.Max(0, AlgorithmBox.SelectedIndex)] + (SharpToggle.IsOn ? "!Sharp" : ""),
        DisableRuntimeModMismatchCheck = !MismatchToggle.IsOn,
    };

    void OnNavSelection(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (!_ready || _syncingNav || args.SelectedItem is not NavigationViewItem { Tag: string tag }) return;
        _page = int.Parse(tag, CultureInfo.InvariantCulture);
        if (_loading || _busy || ScreenProblem.Visibility == Visibility.Visible) return;
        if (_firstRun && _page < _wizard.Length - 1) ShowStep(_page + 1);
        else Show(_screens[_page]);
    }

    bool _syncingNav;

    void ShowStep(int step)
    {
        _step = step;
        Show(_wizard[step]);
        _syncingNav = true;
        Nav.SelectedItem = step == 0 ? null : Nav.MenuItems[step - 1];
        _syncingNav = false;
        BackButton.Visibility = step > 0 ? Visibility.Visible : Visibility.Collapsed;
        var last = step == _wizard.Length - 1;
        GenerateText.Text = L.T(last ? "footer.generate" : step == 0 ? "footer.start" : "footer.next");
        GenerateIcon.Glyph = last ? "\uE896" : "\uE72A";
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(GenerateButton, GenerateText.Text);
        UpdateFooter();
    }

    // the texts are read when the window is built, so a new language opens a new window in the same place
    void SwitchLanguage(string code)
    {
        if (code == L.Language || _busy) return;
        L.Choose(code);
        var next = new MainWindow();
        next.AppWindow.MoveAndResize(new RectInt32(AppWindow.Position.X, AppWindow.Position.Y, AppWindow.Size.Width, AppWindow.Size.Height));
        next.Activate();
        Close();
    }

    void OnBack(object sender, RoutedEventArgs e) { if (_step > 0) ShowStep(_step - 1); }

    bool InWizardBeforeLastStep => _firstRun && _step < _wizard.Length - 1 && ScreenWorking.Visibility != Visibility.Visible;

    void Show(UIElement screen)
    {
        foreach (var child in Screens.Children) child.Visibility = child == screen ? Visibility.Visible : Visibility.Collapsed;
        Scroller.ChangeView(null, 0, null, true);
    }

    void RefreshGameRunning()
    {
        _gameRunning = Tuf.GameIsRunning();
        GameRunningInfo.IsOpen = _gameRunning;
        UpdateFooter();
    }

    void UpdateFooter()
    {
        GenerateButton.IsEnabled = !_loading && !_busy && _state is not null && (InWizardBeforeLastStep || !_gameRunning);
        UninstallButton.IsEnabled = !_loading && !_busy && !_gameRunning && _state is not null;
        if (_loading || _state is null) { FooterNote.Text = ""; return; }
        var on = _patches.Count(p => p.Enabled);
        var active = on == 1 ? L.F("footer.fixes1", Label(Scale)) : L.F("footer.fixesN", Label(Scale), on);
        FooterNote.Text = _firstRun ? $"{L.F("footer.step", _step + 1, _wizard.Length)}  ·  {active}" : $"{active}  ·  {L.T("footer.goesTo")}";
    }

    async void OnGenerate(object sender, RoutedEventArgs e)
    {
        if (_home is null || _state is null || _busy) return;
        if (InWizardBeforeLastStep) { ShowStep(_step + 1); return; }
        // visual checks of the app must never write the player's Mods folder
        if (Environment.GetEnvironmentVariable("TINY_UI_FIX_DRY_RUN") == "1") return;
        if (Tuf.GameIsRunning()) { RefreshGameRunning(); return; }
        _busy = true;
        UpdateFooter();
        WorkingLog.Text = "";
        WorkingStep.Text = L.T("work.preparing");
        Show(ScreenWorking);
        var disabledRecommended = _patches.Where(p => p.Recommendation is not null && !p.Enabled).Select(p => p.Id).ToList();
        try
        {
            await Tuf.GenerateAsync(_home, _game, CurrentSettings(), _order.Select(p => p.Id).ToList(), disabledRecommended,
                line => DispatcherQueue.TryEnqueue(() => { WorkingStep.Text = Step(line); WorkingLog.Text += line + "\n"; }), CancellationToken.None);
            DoneText.Text = L.F("done.body", Label(Scale));
            if (_firstRun)
            {
                _firstRun = false;
                BackButton.Visibility = Visibility.Collapsed;
                GenerateText.Text = L.T("footer.generate");
                GenerateIcon.Glyph = "\uE896";
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(GenerateButton, GenerateText.Text);
            }
            Show(ScreenDone);
        }
        catch (Exception ex)
        {
            App.Log(ex);
            ProblemInfo.Title = ex.Message;
            ProblemInfo.Message = L.T("problem.nothingChanged");
            ProblemLog.Text = (ex as ScriptException)?.Output ?? ex.ToString();
            Show(ScreenProblem);
        }
        _busy = false;
        UpdateFooter();
    }

    // the script talks in English; these are the steps worth naming for the player
    static string Step(string line) =>
        line.StartsWith("Trying to find", StringComparison.Ordinal) ? L.T("work.finding")
        : line.StartsWith("Resolving the resource priorities", StringComparison.Ordinal) ? L.T("work.reading")
        : line.StartsWith("Scaling", StringComparison.Ordinal) || line.Contains("layout", StringComparison.OrdinalIgnoreCase) ? L.T("work.layouts")
        : line.StartsWith("Resolving", StringComparison.Ordinal) && line.Contains("assemblies", StringComparison.Ordinal) ? L.T("work.code")
        : line.StartsWith("Applying", StringComparison.Ordinal) || line.Contains("was patched", StringComparison.Ordinal) ? L.T("work.patching")
        : line.StartsWith("Saving and compressing", StringComparison.Ordinal) ? L.T("work.saving")
        : line.StartsWith("A UI-scaled package file was generated", StringComparison.Ordinal) ? L.T("work.ready")
        : L.T("work.working");

    async void OnUninstall(object sender, RoutedEventArgs e)
    {
        if (_home is null) return;
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = L.T("dialog.uninstallTitle"),
            Content = L.T("dialog.uninstallBody"),
            PrimaryButtonText = L.T("manage.uninstall"),
            CloseButtonText = L.T("dialog.cancel"),
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        _busy = true;
        UpdateFooter();
        try
        {
            await Tuf.UninstallAsync(_home, _game);
            ManageInfo.Severity = InfoBarSeverity.Success;
            ManageInfo.Title = L.T("manage.uninstalled");
            ManageInfo.Message = L.T("manage.uninstalledBody");
        }
        catch (Exception ex)
        {
            App.Log(ex);
            ManageInfo.Severity = InfoBarSeverity.Error;
            ManageInfo.Title = ex.Message;
            ManageInfo.Message = "";
        }
        ManageInfo.IsOpen = true;
        _busy = false;
        UpdateFooter();
    }

    async void OnCheckUpdates(object sender, RoutedEventArgs e)
    {
        UpdateButton.IsEnabled = false;
        UpdateText.Text = L.T("manage.checking");
        try
        {
            var latest = await Tuf.LatestVersionAsync();
            var mine = Tuf.AppVersion;
            UpdateText.Text = latest is null ? L.T("manage.cantCompare")
                : latest > mine ? L.F("manage.available", latest.ToString(3))
                : L.F("manage.latest", mine.ToString(3));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            UpdateText.Text = L.T("manage.offline");
        }
        UpdateButton.IsEnabled = true;
    }

    void OnOpenPackageFolder(object sender, RoutedEventArgs e)
    {
        var dir = PackageText.Text;
        if (!Directory.Exists(dir)) dir = Path.GetDirectoryName(dir) ?? dir;
        if (Directory.Exists(dir)) Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
    }

    async void OnRetry(object sender, RoutedEventArgs e) => await LoadAsync();
    void OnBackToSettings(object sender, RoutedEventArgs e) => Show(_screens[_page]);
    void OnClose(object sender, RoutedEventArgs e) => Close();
}
