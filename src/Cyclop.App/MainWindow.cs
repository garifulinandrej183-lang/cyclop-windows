using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using Cyclop.App.UI;
using Cyclop.App.ViewModels;
using Cyclop.Core.Panel;
using Cyclop.Core.Settings;
using Cyclop.Windows.Shell;
using Microsoft.Win32;

namespace Cyclop.App;

public sealed partial class MainWindow : Window, IDisposable
{
    private readonly AppSettings _settings;
    private readonly string _settingsPath = System.IO.Path.Combine(AppSettings.DataRoot, "settings.json");
    private readonly PanelState _state = new();
    private readonly MainViewModel _vm = new();
    private readonly Grid _root = new(), _expanded = new(), _expandedHost = new(), _pane = new();
    private readonly PanelOutline _silhouette = new() { IsHitTestVisible = false };
    private readonly Dictionary<PanelTab, FrameworkElement> _panes = new();
    private readonly Dictionary<PanelTab, Button> _tabs = new();
    private readonly ListBox _shelfList = new() { SelectionMode = SelectionMode.Extended };
    private Button? _clearShelf;
    private Button? _settingsButton;
    private readonly StackPanel _clipboardList = new();
    private readonly TextBox _search = new(), _input = new(), _output = new() { IsReadOnly = true };
    private readonly Slider _seek = new() { Minimum = 0, Margin = new Thickness(0, 8, 0, 0) };
    private readonly Slider _volume = new() { Minimum = 0, Maximum = 100, SmallChange = 2, LargeChange = 10, Width = 96, IsMoveToPointEnabled = true };
    private readonly Button _muteButton = new() { Width = 24, Height = 24, Padding = new Thickness(0), Background = Brushes.Transparent, Margin = new Thickness(0, 0, 6, 0) };
    private readonly TextBlock _shelfHint = Label("Перетащите сюда файлы", 13);
    private readonly ComboBox _direction = new() { Width = 112 };
    private readonly DispatcherTimer _progressTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly ScaleTransform _reveal = new(1, 1);
    private readonly MusicPill _musicPill = new();
    private TrayIcon? _tray;
    private MenuItem? _pauseMenu;
    private CheckBox? _pauseCheck;
    private ContextMenu? _trayMenu;
    private ForegroundWatcher? _foreground;
    private HwndSource? _source;
    private nint _hwnd;
    private nint _previousForeground;
    private ComboBox? _displaySelector;
    private DisplayInfo? _display;
    private CancellationTokenSource? _hover, _leave, _fullscreenDelay;
    private Point _dragStart;
    private bool _dragArmed;
    private bool _outgoingDrag, _disposed, _fullscreen, _suspended, _settingUp = true, _seekDragging;
    private bool _wasExpanded;
    private bool _instantPanelChange;
    private bool ShowsMusicPill => _state.Mode == PanelMode.Collapsed && _media.HasSession && _media.IsPlaying;
    private (double Width, double Height) ExpandedPanelSize => (520, _state.Tab == PanelTab.Settings ? 420 : 180);
    private (double Width, double Height) DesiredPanelSize => _state.IsExpanded
        ? ExpandedPanelSize
        : ShowsMusicPill ? (MusicPill.PillWidth, MusicPill.PillHeight) : (192, 12);
    private (double Width, double Height) PanelSize => _morphBounds ?? DesiredPanelSize;

    public MainWindow()
    {
        _settings = AppSettings.Load(_settingsPath);
        Title = "Cyclop Windows";
        Width = 192; Height = 12; WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true; Background = Brushes.Transparent; Topmost = true;
        ShowInTaskbar = false; ShowActivated = false; WindowStartupLocation = WindowStartupLocation.Manual;
        DataContext = _vm;
        _expandedHost.Children.Add(_expanded);
        _root.Children.Add(_silhouette); _root.Children.Add(_expandedHost); _root.Children.Add(_musicPill); Content = _root;
        _musicPill.HorizontalAlignment = HorizontalAlignment.Center; _musicPill.VerticalAlignment = VerticalAlignment.Top;
        _musicReveal.Changed += ApplyMusicMorph;
        IsVisibleChanged += (_, _) => UpdateMusicPill();
        BuildInterface();
        _state.Changed += RenderState;
        SourceInitialized += OnSourceInitialized;
        MouseEnter += async (_, _) =>
        {
            _leave?.Cancel(); if (_state.IsExpanded) return;
            _hover?.Cancel(); _hover = new();
            try { await Task.Delay(180, _hover.Token); if (IsMouseOver) _state.Open(); }
            catch (OperationCanceledException) { }
        };
        MouseLeave += (_, _) => { _hover?.Cancel(); ScheduleLeave(); };
        PreviewMouseDown += (_, _) => { if (NativeShell.ForegroundWindow() != _hwnd) { RememberForeground(); NativeShell.SetActivationAllowed(_hwnd, true); Activate(); } };
        PreviewGotKeyboardFocus += (_, e) => { if (e.NewFocus is TextBoxBase) _state.Edit(true); };
        LostKeyboardFocus += (_, _) => Dispatcher.BeginInvoke(() =>
        { if (Keyboard.FocusedElement is not TextBoxBase) { _state.Edit(false); if (!IsMouseOver) ScheduleLeave(); } });
        Deactivated += (_, _) => { _state.Edit(false); if (!IsMouseOver) ScheduleLeave(); };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { Keyboard.ClearFocus(); ChangePanelImmediately(_state.Escape); e.Handled = true; }
            if (e.Key is >= Key.D1 and <= Key.D4 && Keyboard.Modifiers == ModifierKeys.Control)
            { ChangePanelImmediately(() => SelectTab((PanelTab)(e.Key - Key.D1), true)); e.Handled = true; }
        };
        AllowDrop = true;
        PreviewDragEnter += OnDragOver; PreviewDragOver += OnDragOver;
        DragLeave += (_, _) => { _state.EndDrop(); if (!IsMouseOver) ScheduleLeave(); };
        PreviewDrop += OnFileDrop;
        _root.SizeChanged += (_, _) => UpdateSilhouette();
        SizeChanged += (_, _) =>
        {
            if (_settingsResizeActive)
            {
                if (_musicMorphActive && _morphBounds is { } bounds) _morphBounds = (bounds.Width, Height);
                UpdateSilhouette(); return;
            }
            var (desiredWidth, desiredHeight) = PanelSize;
            if (_hwnd != 0 && _state.Mode != PanelMode.Hidden && _display is not null && _display.WorkRight - _display.WorkLeft >= desiredWidth * _display.Dpi / 96d &&
                (Math.Abs(Width - desiredWidth) > .5 || Math.Abs(Height - desiredHeight) > .5))
                Dispatcher.BeginInvoke(PositionPanel, DispatcherPriority.ContextIdle);
        };
        Loaded += (_, _) => PositionPanel();
        _progressTimer.Tick += (_, _) => RefreshMediaProgress();
        Closing += (_, e) => { if (!_disposed) { e.Cancel = true; _state.Escape(); } };
    }

    private void BuildInterface()
    {
        _expanded.Margin = new Thickness(24, 0, 24, 14);
        _expanded.RowDefinitions.Add(new() { Height = new GridLength(30) });
        _expanded.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        var header = new Grid(); header.ColumnDefinitions.Add(new()); header.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var title = Label("", 10, "#B1BDCC"); title.FontWeight = FontWeights.SemiBold; title.VerticalAlignment = VerticalAlignment.Center;
        title.SetBinding(TextBlock.TextProperty, new Binding(nameof(MainViewModel.Title))); header.Children.Add(title);
        var notice = Label("", 10, "#E5B976"); notice.HorizontalAlignment = HorizontalAlignment.Right; notice.VerticalAlignment = VerticalAlignment.Center;
        notice.MaxWidth = 410; notice.Margin = new Thickness(85, 0, 8, 0); notice.TextTrimming = TextTrimming.CharacterEllipsis;
        notice.SetBinding(TextBlock.TextProperty, new Binding(nameof(MainViewModel.Status))); notice.SetBinding(ToolTipProperty, new Binding(nameof(MainViewModel.Status))); header.Children.Add(notice);
        var settings = IconButton("settings", "Настройки", () => SelectTab(PanelTab.Settings, true)); settings.Width = 25; settings.Height = 25; _settingsButton = settings;
        Grid.SetColumn(settings, 1); header.Children.Add(settings); _expanded.Children.Add(header);
        var body = new Grid(); body.ColumnDefinitions.Add(new() { Width = new GridLength(30) });
        body.ColumnDefinitions.Add(new() { Width = new GridLength(14) }); body.ColumnDefinitions.Add(new());
        Grid.SetRow(body, 1); _expanded.Children.Add(body);
        var rail = new StackPanel { VerticalAlignment = VerticalAlignment.Top };
        var names = new[] { "Spotify", "Полка", "Буфер обмена", "Перевод" };
        var icons = new[] { "music", "shelf", "clipboard", "translate" };
        for (var i = 0; i < 4; i++)
        {
            var tab = (PanelTab)i;
            var button = IconButton(icons[i], names[i], () => SelectTab(tab, true)); button.Height = 30;
            button.Margin = new Thickness(0, 2, 0, 2); button.Background = Brushes.Transparent;
            _tabs[tab] = button; rail.Children.Add(button);
        }
        body.Children.Add(rail); Grid.SetColumn(_pane, 2); body.Children.Add(_pane);
        _panes[PanelTab.Spotify] = BuildSpotify(); _panes[PanelTab.Shelf] = BuildShelf();
        _panes[PanelTab.Clipboard] = BuildClipboard(); _panes[PanelTab.Translate] = BuildTranslate();
        _panes[PanelTab.Settings] = BuildSettings();
        foreach (var pair in _panes)
        {
            if (pair.Key != PanelTab.Settings) { pair.Value.Height = 136; pair.Value.VerticalAlignment = VerticalAlignment.Top; }
            _pane.Children.Add(pair.Value);
        }
        var revealTransforms = new TransformGroup(); revealTransforms.Children.Add(_reveal); revealTransforms.Children.Add(_revealOffset);
        _expanded.RenderTransform = revealTransforms; _expanded.RenderTransformOrigin = new Point(.5, 0);
        RenderState(); _settingUp = false;
    }

    private FrameworkElement BuildSpotify()
    {
        var grid = new Grid { VerticalAlignment = VerticalAlignment.Center };
        grid.RowDefinitions.Add(new() { Height = GridLength.Auto }); grid.RowDefinitions.Add(new() { Height = GridLength.Auto }); grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var upper = new Grid(); upper.ColumnDefinitions.Add(new() { Width = new GridLength(110) }); upper.ColumnDefinitions.Add(new()); grid.Children.Add(upper);
        var cover = new Button { Width = 96, Height = 96, Padding = new Thickness(0), Style = (Style)FindResource("CoverButtonStyle"), HorizontalAlignment = HorizontalAlignment.Left, ToolTip = "Открыть Spotify" };
        AutomationProperties.SetName(cover, "Открыть Spotify"); cover.Click += async (_, _) => await OpenSpotifyWindowAsync();
        var image = new Image { Stretch = Stretch.UniformToFill }; image.SetBinding(Image.SourceProperty, new Binding(nameof(MainViewModel.Artwork)));
        var art = new Grid { Width = 96, Height = 96, Clip = new RectangleGeometry(new Rect(0, 0, 96, 96), 8, 8) };
        var fallback = Icons.Create("music", 28); fallback.Opacity = .35; art.Children.Add(fallback); art.Children.Add(image); cover.Content = art; upper.Children.Add(cover);
        var content = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var title = Label("", 16); title.FontWeight = FontWeights.SemiBold; title.TextTrimming = TextTrimming.CharacterEllipsis;
        title.SetBinding(TextBlock.TextProperty, new Binding(nameof(MainViewModel.TrackTitle))); content.Children.Add(title);
        var artist = Label("", 12, "#B1BDCC"); artist.Margin = new Thickness(0, 3, 0, 0); artist.TextTrimming = TextTrimming.CharacterEllipsis;
        artist.SetBinding(TextBlock.TextProperty, new Binding(nameof(MainViewModel.Artist))); content.Children.Add(artist);
        var actions = new Grid { Margin = new Thickness(0, 10, 0, 0) };
        actions.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); actions.ColumnDefinitions.Add(new()); actions.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var controls = new StackPanel { Orientation = Orientation.Horizontal };
        var previous = MediaButton("previous", "Предыдущий трек", async () => await ExecuteMediaAsync("previous")); previous.SetBinding(IsEnabledProperty, new Binding(nameof(MainViewModel.CanPrevious)));
        var play = MediaButton("play", "Воспроизвести / пауза", async () => await ExecuteMediaAsync("play"));
        play.SetBinding(IsEnabledProperty, new Binding(nameof(MainViewModel.CanPlayPause)));
        _vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(MainViewModel.Playing)) play.Content = Icons.CreateMedia(_vm.Playing ? "pause" : "play"); };
        var next = MediaButton("next", "Следующий трек", async () => await ExecuteMediaAsync("next")); next.SetBinding(IsEnabledProperty, new Binding(nameof(MainViewModel.CanNext)));
        controls.Children.Add(previous); controls.Children.Add(play); controls.Children.Add(next); actions.Children.Add(controls);
        var volumeControls = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        _muteButton.Content = Icons.Create("volume", 14);
        _muteButton.SetBinding(IsEnabledProperty, new Binding(nameof(MainViewModel.CanToggleMute)));
        _muteButton.SetBinding(ToolTipProperty, new Binding(nameof(MainViewModel.MuteHint)));
        _muteButton.SetBinding(AutomationProperties.NameProperty, new Binding(nameof(MainViewModel.MuteLabel)));
        ToolTipService.SetShowOnDisabled(_muteButton, true);
        _muteButton.Click += async (_, _) => await ToggleSpotifyMuteAsync();
        _vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(MainViewModel.SoundMuted)) _muteButton.Content = Icons.Create(_vm.SoundMuted ? "volume-off" : "volume", 14); };
        volumeControls.Children.Add(_muteButton);
        _volume.SetBinding(IsEnabledProperty, new Binding(nameof(MainViewModel.CanChangeVolume)));
        _volume.SetBinding(ToolTipProperty, new Binding(nameof(MainViewModel.VolumeHint)));
        ToolTipService.SetShowOnDisabled(_volume, true);
        AutomationProperties.SetName(_volume, "Громкость Spotify");
        _volume.ValueChanged += (_, _) => QueueVolumeChange();
        _volume.PreviewMouseLeftButtonDown += (_, _) => { _volumeDragging = true; _leave?.Cancel(); RefreshMuteButton(); };
        _volume.PreviewMouseLeftButtonUp += (_, _) => EndVolumeDrag();
        _volume.LostMouseCapture += (_, _) => EndVolumeDrag();
        volumeControls.Children.Add(_volume);
        var volumeLabel = Label("", 10, "#B1BDCC"); volumeLabel.Width = 34; volumeLabel.TextAlignment = TextAlignment.Right;
        volumeLabel.VerticalAlignment = VerticalAlignment.Center; volumeLabel.SetBinding(TextBlock.TextProperty, new Binding(nameof(MainViewModel.VolumeLabel)));
        volumeLabel.SetBinding(ToolTipProperty, new Binding(nameof(MainViewModel.VolumeHint))); volumeControls.Children.Add(volumeLabel);
        Grid.SetColumn(volumeControls, 2); actions.Children.Add(volumeControls); content.Children.Add(actions);
        Grid.SetColumn(content, 1); upper.Children.Add(content);
        _seek.SetBinding(Slider.MaximumProperty, new Binding(nameof(MainViewModel.Duration))); _seek.SetBinding(IsEnabledProperty, new Binding(nameof(MainViewModel.CanSeek)));
        _seek.Margin = new Thickness(0, 6, 0, 0);
        _seek.PreviewMouseDown += (_, _) => _seekDragging = true;
        _seek.PreviewMouseUp += async (_, _) => { if (_seekDragging) { _seekDragging = false; await SeekMediaAsync(_seek.Value); } };
        _seek.PreviewKeyUp += async (_, e) => { if (e.Key is Key.Left or Key.Right or Key.Home or Key.End) await SeekMediaAsync(_seek.Value); };
        AutomationProperties.SetName(_seek, "Позиция воспроизведения Spotify"); _seek.SetBinding(ToolTipProperty, new Binding(nameof(MainViewModel.TimeLabel)));
        Grid.SetRow(_seek, 1); grid.Children.Add(_seek);
        var footer = new Grid { Margin = new Thickness(0, 2, 0, 0) }; footer.ColumnDefinitions.Add(new()); footer.ColumnDefinitions.Add(new());
        var time = Label("", 10, "#B1BDCC"); time.VerticalAlignment = VerticalAlignment.Center;
        time.SetBinding(TextBlock.TextProperty, new Binding(nameof(MainViewModel.ElapsedLabel))); footer.Children.Add(time);
        var remaining = Label("", 10, "#B1BDCC"); remaining.TextAlignment = TextAlignment.Right;
        remaining.SetBinding(TextBlock.TextProperty, new Binding(nameof(MainViewModel.RemainingLabel))); Grid.SetColumn(remaining, 1); footer.Children.Add(remaining);
        Grid.SetRow(footer, 2); grid.Children.Add(footer); return grid;
    }

    private FrameworkElement BuildShelf()
    {
        var grid = new Grid(); grid.RowDefinitions.Add(new() { Height = new GridLength(25) }); grid.RowDefinitions.Add(new());
        var top = new DockPanel(); var add = IconButton("add", "Добавить файлы", ChooseFiles); add.Width = 25; add.Height = 25; DockPanel.SetDock(add, Dock.Right); top.Children.Add(add);
        add.Style = (Style)FindResource("PrimaryButtonStyle"); add.Content = Icons.Create("add");
        _clearShelf = IconButton("trash", "Очистить полку", ClearShelfAsync); _clearShelf.Width = 25; _clearShelf.Height = 25;
        _clearShelf.Margin = new Thickness(0, 0, 4, 0); _clearShelf.IsEnabled = false;
        _clearShelf.ToolTip = "Очистить полку · файлы останутся в папках"; DockPanel.SetDock(_clearShelf, Dock.Right); top.Children.Add(_clearShelf);
        _shelfHint.TextTrimming = TextTrimming.CharacterEllipsis;
        _shelfHint.Foreground = Brush("#B1BDCC"); _shelfHint.FontSize = 11; _shelfHint.VerticalAlignment = VerticalAlignment.Center; top.Children.Add(_shelfHint); grid.Children.Add(top);
        _shelfList.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Auto);
        _shelfList.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Disabled);
        _shelfList.SetValue(ScrollViewer.CanContentScrollProperty, false);
        _shelfList.PreviewMouseWheel += (_, e) =>
        {
            var scroll = FindVisualChild<ScrollViewer>(_shelfList);
            if (scroll is null || scroll.ScrollableWidth <= 0 || e.Delta == 0) return;
            var distance = SystemParameters.WheelScrollLines < 0 ? scroll.ViewportWidth : SystemParameters.WheelScrollLines * 32d;
            scroll.ScrollToHorizontalOffset(scroll.HorizontalOffset - e.Delta / 120d * distance);
            e.Handled = true;
        };
        var template = new ItemsPanelTemplate(new FrameworkElementFactory(typeof(StackPanel)));
        template.VisualTree.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal); _shelfList.ItemsPanel = template;
        _shelfList.PreviewMouseLeftButtonDown += (_, e) =>
        {
            _dragStart = e.GetPosition(_shelfList);
            _dragArmed = e.OriginalSource is DependencyObject source && ItemsControl.ContainerFromElement(_shelfList, source) is ListBoxItem;
        };
        _shelfList.PreviewMouseLeftButtonUp += (_, _) => _dragArmed = false;
        _shelfList.MouseMove += OnShelfMouseMove;
        _shelfList.MouseDoubleClick += (_, _) => { if (_shelfList.SelectedItem is ShelfCard card) OpenFile(card.Path); };
        _shelfList.KeyDown += async (_, e) =>
        {
            if (e.Key == Key.Delete) { await RemoveSelectedShelfAsync(); e.Handled = true; }
            if (e.Key == Key.Enter && _shelfList.SelectedItem is ShelfCard card) { OpenFile(card.Path); e.Handled = true; }
        };
        Grid.SetRow(_shelfList, 1); grid.Children.Add(_shelfList); return grid;
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) return match;
            if (FindVisualChild<T>(child) is { } descendant) return descendant;
        }
        return null;
    }

    private FrameworkElement BuildClipboard()
    {
        var grid = new Grid(); grid.RowDefinitions.Add(new() { Height = new GridLength(31) }); grid.RowDefinitions.Add(new()); grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var searchRow = new DockPanel(); var clear = IconButton("trash", "Очистить историю", async () => await ClearHistoryAsync()); clear.Height = 25; clear.Width = 25; clear.Margin = new Thickness(6, 0, 0, 4); clear.VerticalAlignment = VerticalAlignment.Center;
        DockPanel.SetDock(clear, Dock.Right); searchRow.Children.Add(clear);
        _search.Style = (Style)FindResource("SearchTextBoxStyle"); _search.Tag = "Поиск по тексту и ссылкам";
        _search.Height = 27; _search.Padding = new Thickness(0); _search.Margin = new Thickness(0, 0, 0, 4);
        var searchField = new Grid(); searchField.Children.Add(_search);
        var reset = IconButton("close", "Очистить поиск", () => { _search.Clear(); _search.Focus(); });
        reset.Width = 20; reset.Height = 20; reset.Background = Brushes.Transparent; reset.HorizontalAlignment = HorizontalAlignment.Right;
        reset.VerticalAlignment = VerticalAlignment.Center; reset.Margin = new Thickness(0, 0, 4, 4); reset.Visibility = Visibility.Collapsed; reset.Content = Icons.Create("close", 13, Brush("#B1BDCC"));
        searchField.Children.Add(reset);
        _search.ToolTip = "Поиск по расшифрованной истории в памяти"; AutomationProperties.SetName(_search, "Поиск истории буфера");
        _search.TextChanged += (_, _) => { reset.Visibility = _search.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible; RefreshHistory(); }; searchRow.Children.Add(searchField); grid.Children.Add(searchRow);
        var scroll = new ScrollViewer { Content = _clipboardList, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetRow(scroll, 1); grid.Children.Add(scroll);
        var hint = Label("", 10, "#B1BDCC"); hint.TextWrapping = TextWrapping.Wrap; hint.SetBinding(TextBlock.TextProperty, new Binding(nameof(MainViewModel.ClipboardHint)));
        Grid.SetRow(hint, 2); grid.Children.Add(hint); return grid;
    }

    private FrameworkElement BuildTranslate()
    {
        var grid = new Grid(); grid.RowDefinitions.Add(new() { Height = new GridLength(38) }); grid.RowDefinitions.Add(new()); grid.RowDefinitions.Add(new() { Height = new GridLength(22) });
        var header = new Grid { Height = 26, VerticalAlignment = VerticalAlignment.Top };
        foreach (var width in new[] { new GridLength(124), new GridLength(8), new GridLength(26), new GridLength(1, GridUnitType.Star), new GridLength(78), new GridLength(8), new GridLength(26) }) header.ColumnDefinitions.Add(new() { Width = width });
        _direction.Width = 124; _direction.Height = 26;
        _direction.ItemsSource = new[] { "Авто RU/EN", "EN → RU", "RU → EN" }; _direction.SelectedIndex = 0;
        HoldForDropdown(_direction);
        _direction.SelectionChanged += (_, _) => ScheduleTranslation(); header.Children.Add(_direction);
        var swap = IconButton("swap", "Поменять языки", () =>
        {
            _direction.SelectedIndex = _direction.SelectedIndex == 1 ? 2 : 1;
            if (_output.Text.Length > 0) _input.Text = _output.Text;
        }); swap.Width = swap.Height = 26; swap.Margin = new Thickness(0); Grid.SetColumn(swap, 2); header.Children.Add(swap);
        var copy = IconButton("copy", "Копировать перевод", async () => await CopyTranslationAsync()); copy.Width = copy.Height = 26; copy.Margin = new Thickness(0); Grid.SetColumn(copy, 6); header.Children.Add(copy);
        var models = ActionButton("Модели", () => SelectTab(PanelTab.Settings, true)); models.Height = 26; models.Padding = new Thickness(10, 2, 10, 2); Grid.SetColumn(models, 4); header.Children.Add(models); grid.Children.Add(header);
        var columns = new Grid(); columns.ColumnDefinitions.Add(new()); columns.ColumnDefinitions.Add(new() { Width = new GridLength(12) }); columns.ColumnDefinitions.Add(new());
        foreach (var box in new[] { _input, _output }) { box.AcceptsReturn = true; box.TextWrapping = TextWrapping.Wrap; box.VerticalScrollBarVisibility = ScrollBarVisibility.Auto; box.Padding = new Thickness(10, 7, 10, 7); }
        _input.Tag = "Введите текст для перевода"; _output.Tag = "Перевод появится здесь";
        _input.ToolTip = "Введите текст. Автоопределение языка — эвристика, направление можно выбрать вручную. Свыше 100000 знаков — явная ошибка, текст не обрезается.";
        AutomationProperties.SetName(_input, "Исходный текст перевода"); AutomationProperties.SetName(_output, "Результат перевода");
        _input.TextChanged += (_, _) => ScheduleTranslation(); _output.SetBinding(TextBox.TextProperty, new Binding(nameof(MainViewModel.OutputText)) { Mode = BindingMode.OneWay });
        columns.Children.Add(_input); Grid.SetColumn(_output, 2); columns.Children.Add(_output); Grid.SetRow(columns, 1); grid.Children.Add(columns);
        var status = Label("", 10, "#B1BDCC"); status.TextTrimming = TextTrimming.CharacterEllipsis; status.VerticalAlignment = VerticalAlignment.Center;
        status.SetBinding(TextBlock.TextProperty, new Binding(nameof(MainViewModel.TranslateStatus))); Grid.SetRow(status, 2); grid.Children.Add(status); return grid;
    }

    private FrameworkElement BuildSettings()
    {
        var groups = new StackPanel { Margin = new Thickness(0, 0, 5, 0) };
        var stack = SettingsGroup(groups, "Приватность и хранение");
        AddCheck(stack, "Записывать текст из буфера обмена", _settings.CaptureClipboard, async v => { _settings.CaptureClipboard = v; await ApplyCaptureSettingsAsync(); });
        _pauseCheck = AddCheck(stack, "Приостановить запись", _settings.ClipboardPaused, async v => { _settings.ClipboardPaused = v; await ApplyCaptureSettingsAsync(); });
        AddCheck(stack, "Сохранять изображения из буфера на полку", _settings.AutoSaveImages, async v => { _settings.AutoSaveImages = v; await ApplyCaptureSettingsAsync(); });
        stack.Children.Add(Wrapped("История шифруется DPAPI для текущего пользователя. PNG на полке — обычные незашифрованные файлы. Очистка истории их не удаляет. Пароли автоматически не распознаются.", 11));
        AddNumber(stack, "Записей истории (10–500)", _settings.HistoryLimit, 10, 500, async n => { _settings.HistoryLimit = n; await ApplyCaptureSettingsAsync(); });
        AddNumber(stack, "Срок истории, дней (1–90)", _settings.HistoryDays, 1, 90, async n => { _settings.HistoryDays = n; await ApplyCaptureSettingsAsync(); });
        var clearHistory = ActionButton("Очистить историю буфера", async () => await ClearHistoryAsync());
        clearHistory.Style = (Style)FindResource("DestructiveButtonStyle"); clearHistory.HorizontalAlignment = HorizontalAlignment.Left; clearHistory.Margin = new Thickness(0, 6, 0, 0); stack.Children.Add(clearHistory);
        stack = SettingsGroup(groups, "Скриншоты и полка");
        AddNumber(stack, "PNG в управляемой папке (10–1000)", _settings.ScreenshotLimit, 10, 1000, async n => { _settings.ScreenshotLimit = n; await ApplyCaptureSettingsAsync(); });
        var folder = new TextBlock { Text = _settings.ScreenshotFolder.Length > 0 ? _settings.ScreenshotFolder : "Папка скриншотов не выбрана", TextWrapping = TextWrapping.Wrap, Foreground = Brush("#B1BDCC"), FontSize = 11 };
        AddCheck(stack, "Следить за выбранной папкой скриншотов", _settings.WatchScreenshots, async v => { _settings.WatchScreenshots = v; await ApplyWatchSettingsAsync(); });
        var choose = ActionButton("Выбрать папку Lightshot / скриншотов…", async () =>
        {
            var picker = new OpenFolderDialog { Title = "Папка, в которую вы сохраняете скриншоты" };
            if (picker.ShowDialog(this) == true) { _settings.ScreenshotFolder = picker.FolderName; folder.Text = picker.FolderName; await ApplyWatchSettingsAsync(); }
        }); choose.HorizontalAlignment = HorizontalAlignment.Left; stack.Children.Add(choose); stack.Children.Add(folder);
        var openManaged = ActionButton("Открыть папку PNG полки", OpenManagedFolder); openManaged.HorizontalAlignment = HorizontalAlignment.Left; openManaged.Margin = new Thickness(0, 6, 0, 8); stack.Children.Add(openManaged);
        stack = SettingsGroup(groups, "Панель");
        AddCheck(stack, "Запускать при входе в Windows", _settings.StartAtLogin, v =>
        { try { NativeShell.SetStartup(v, Environment.ProcessPath!); _settings.StartAtLogin = v; SaveSettings(); } catch (Exception) { SetStatus("Не удалось изменить автозапуск."); } });
        AddCheck(stack, "Уменьшить движение", _settings.ReducedMotion, v => { _settings.ReducedMotion = v; SaveSettings(); UpdateMotionPreferences(); });
        var displays = new ComboBox { ItemsSource = NativeShell.Displays(), Margin = new Thickness(0, 5, 0, 5) };
        _displaySelector = displays;
        HoldForDropdown(displays);
        displays.SelectedItem = displays.Items.Cast<DisplayInfo>().FirstOrDefault(x => x.Device == _settings.MonitorDevice) ?? displays.Items.Cast<DisplayInfo>().FirstOrDefault();
        displays.SelectionChanged += (_, _) => { if (!_settingUp && displays.SelectedItem is DisplayInfo d) { _settings.MonitorDevice = d.Device; SaveSettings(); PositionPanel(); } };
        AutomationProperties.SetName(displays, "Монитор панели"); stack.Children.Add(displays);
        stack.Children.Add(Wrapped("Горячая клавиша: нажмите сочетание в поле (Ctrl, Alt или Win + клавиша). Escape закрывает панель; Ctrl+1…4 выбирает модуль.", 11));
        var shortcut = new TextBox { IsReadOnly = true, Text = HotkeyLabel(), Margin = new Thickness(0, 5, 0, 10) }; AutomationProperties.SetName(shortcut, "Горячая клавиша открытия");
        shortcut.PreviewKeyDown += (_, e) =>
        {
            var key = e.Key == Key.System ? e.SystemKey : e.Key; var mods = Keyboard.Modifiers;
            if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin) return;
            if ((mods & (ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Windows)) == 0 || key == Key.Escape) return;
            var native = ((mods.HasFlag(ModifierKeys.Alt) ? 1u : 0) | (mods.HasFlag(ModifierKeys.Control) ? 2u : 0) | (mods.HasFlag(ModifierKeys.Shift) ? 4u : 0) | (mods.HasFlag(ModifierKeys.Windows) ? 8u : 0));
            var oldMods = _settings.HotkeyModifiers; var oldKey = _settings.HotkeyKey;
            NativeShell.UnregisterHotKey(_hwnd, 1);
            if (NativeShell.RegisterHotKey(_hwnd, 1, native | 0x4000, (uint)KeyInterop.VirtualKeyFromKey(key)))
            { _settings.HotkeyModifiers = native; _settings.HotkeyKey = (uint)KeyInterop.VirtualKeyFromKey(key); SaveSettings(); shortcut.Text = HotkeyLabel(); }
            else { NativeShell.RegisterHotKey(_hwnd, 1, oldMods | 0x4000, oldKey); SetStatus("Сочетание уже занято другим приложением."); }
            e.Handled = true;
        }; stack.Children.Add(shortcut);
        stack = SettingsGroup(groups, "Офлайн-перевод");
        stack.Children.Add(Wrapped("Две модели Argos (EN→RU и RU→EN) устанавливаются только по кнопке. Импорт принимает пакеты закреплённых версий и проверяет SHA-256. Для работы после установки сеть не нужна.", 11));
        var download = ActionButton("Скачать обе модели", async () => await InstallModelsAsync()); download.Style = (Style)FindResource("PrimaryButtonStyle"); download.Margin = new Thickness(0, 6, 0, 6); download.HorizontalAlignment = HorizontalAlignment.Left; stack.Children.Add(download);
        var import = ActionButton("Импортировать .argosmodel…", async () => await ImportModelAsync()); import.HorizontalAlignment = HorizontalAlignment.Left; stack.Children.Add(import);
        var installStatus = Label("", 11, "#B1BDCC"); installStatus.TextWrapping = TextWrapping.Wrap; installStatus.SetBinding(TextBlock.TextProperty, new Binding(nameof(MainViewModel.TranslateStatus))); stack.Children.Add(installStatus);
        stack = SettingsGroup(groups, "Cyclop Windows 0.1.0");
        stack.Children.Add(Wrapped("Неофициальный Windows-порт Cyclop. Оригинал: akalikbergenov/cyclop, MIT, © 2026 akalikbergenov. Upstream: 125eb02fbd28b443fe922334ae09823d4ad38df6. Сборка не подписана.", 11));
        var upstream = ActionButton("Исходный Cyclop на GitHub", () => OpenFile("https://github.com/akalikbergenov/cyclop")); upstream.HorizontalAlignment = HorizontalAlignment.Left; stack.Children.Add(upstream);
        var notices = ActionButton("Лицензии и сторонние компоненты", () => OpenFile(System.IO.Path.Combine(AppContext.BaseDirectory, "THIRD-PARTY-NOTICES.md"))); notices.HorizontalAlignment = HorizontalAlignment.Left; notices.Margin = new Thickness(0, 6, 0, 8); stack.Children.Add(notices);
        return new ScrollViewer { Content = groups, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    }

    private StackPanel SettingsGroup(Panel parent, string title)
    {
        var content = new StackPanel();
        var heading = Label(title, 12); heading.FontWeight = FontWeights.SemiBold; heading.Margin = new Thickness(0, 0, 0, 6); content.Children.Add(heading);
        parent.Children.Add(new Border { Background = (Brush)FindResource("SurfaceBrush"), CornerRadius = new CornerRadius(12), Padding = new Thickness(10, 9, 10, 9), Margin = new Thickness(0, 0, 0, 8), Child = content });
        return content;
    }

    private void RenderState()
    {
        if (_disposed) return;
        if (_state.Mode == PanelMode.Hidden) { _wasExpanded = false; FinishPaneTransition(); StopSettingsResize(); EndMusicMorph(); _progressTimer.Stop(); _musicPill.SetAnimation(false); if (IsVisible) Hide(); if (_hwnd != 0) NativeShell.SetActivationAllowed(_hwnd, false); return; }
        var open = _state.IsExpanded;
        var justOpened = open && !_wasExpanded;
        var justClosed = !open && _wasExpanded;
        var resizeExpanded = open && (_wasExpanded || _musicMorphActive);
        _wasExpanded = open;
        if (!open || !MotionEnabled || _instantPanelChange) StopSettingsResize();
        else if (resizeExpanded || justOpened && _state.Tab == PanelTab.Settings && !(_media.HasSession && _media.IsPlaying)) AnimateExpandedHeight();
        if (justOpened && _state.Tab == PanelTab.Shelf) _ = ReconcileShelfAsync();
        if (MotionEnabled && !_instantPanelChange && _media.HasSession && _media.IsPlaying && (justOpened || justClosed || _musicMorphActive)) BeginMusicMorph(open);
        else if (_musicMorphActive) EndMusicMorph();
        _vm.Title = _state.Tab switch { PanelTab.Spotify => "SPOTIFY", PanelTab.Shelf => "ПОЛКА", PanelTab.Clipboard => "БУФЕР ОБМЕНА", PanelTab.Translate => "ПЕРЕВОД", _ => "НАСТРОЙКИ" };
        RenderPaneSelection();
        var tabIcons = new[] { "music", "shelf", "clipboard", "translate" };
        foreach (var pair in _tabs)
        {
            var selected = pair.Key == _state.Tab;
            pair.Value.Background = selected ? Brush("#253B57") : Brushes.Transparent;
            pair.Value.Content = Icons.Create(tabIcons[(int)pair.Key], 18, Brush(selected ? "#8CBEFF" : "#B1BDCC"));
        }
        if (_settingsButton is not null)
        {
            var selected = _state.Tab == PanelTab.Settings;
            _settingsButton.Background = Brush(selected ? "#253B57" : "#202630");
            _settingsButton.Content = Icons.Create("settings", 18, Brush(selected ? "#8CBEFF" : "#B1BDCC"));
        }
        _expanded.Visibility = open || _musicMorphActive ? Visibility.Visible : Visibility.Collapsed;
        _musicPill.Visibility = ShowsMusicPill || _musicMorphActive ? Visibility.Visible : Visibility.Collapsed;
        _silhouette.Visibility = ShowsMusicPill && !_musicMorphActive ? Visibility.Collapsed : Visibility.Visible;
        var size = PanelSize; Width = size.Width; if (!_settingsResizeActive) Height = size.Height;
        if (_hwnd != 0) { if (!IsVisible) Show(); PositionPanel(); }
        UpdateSilhouette(); UpdateProgressTimer(); UpdateMusicPill();
        if (justClosed && NativeShell.ForegroundWindow() == _hwnd && _previousForeground != 0) NativeShell.SetForegroundWindow(_previousForeground);
        if (!open && _hwnd != 0) NativeShell.SetActivationAllowed(_hwnd, false);
        if (justOpened && !_musicMorphActive && !_instantPanelChange && NativeShell.ForegroundWindow() != _hwnd && MotionEnabled)
        {
            _expanded.BeginAnimation(OpacityProperty, new DoubleAnimation(.25, 1, TimeSpan.FromMilliseconds(180)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
            _reveal.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(.96, 1, TimeSpan.FromMilliseconds(180)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        }
        else if (!open && !_musicMorphActive) { _expanded.BeginAnimation(OpacityProperty, null); _reveal.BeginAnimation(ScaleTransform.ScaleYProperty, null); }
    }
    private void UpdateMusicPill() => _musicPill.SetAnimation(!_disposed && IsVisible && ShowsMusicPill && !_musicMorphActive && MotionEnabled);

    private void SelectTab(PanelTab tab, bool explicitAction)
    {
        var animate = _animatePaneSelection;
        _animatePaneSelection = explicitAction && _state.IsExpanded;
        try { _state.Select(tab); }
        finally { _animatePaneSelection = animate; }
        if (explicitAction) { RememberForeground(); NativeShell.SetActivationAllowed(_hwnd, true); Activate(); if (tab == PanelTab.Translate) _input.Focus(); else if (tab == PanelTab.Clipboard) _search.Focus(); }
        if (tab == PanelTab.Shelf) _ = ReconcileShelfAsync();
    }
    public void OpenExplicit()
    {
        if (_fullscreen || _suspended) return;
        RememberForeground(); NativeShell.SetActivationAllowed(_hwnd, true); _hover?.Cancel(); _leave?.Cancel(); _state.Unhide(); _state.Open();
        Show(); Activate(); NativeShell.SetForegroundWindow(_hwnd);
        if (_state.Tab == PanelTab.Translate) _input.Focus(); else if (_state.Tab == PanelTab.Clipboard) _search.Focus();
    }
    private void ChangePanelImmediately(Action action)
    { _instantPanelChange = true; try { action(); } finally { _instantPanelChange = false; } }
    private void RememberForeground() { var foreground = NativeShell.ForegroundWindow(); if (foreground != _hwnd && foreground != 0) _previousForeground = foreground; }
    private void ScheduleLeave()
    {
        _leave?.Cancel(); _leave = new(); var token = _leave.Token;
        _ = LeaveAsync(token);
    }
    private async Task LeaveAsync(CancellationToken token)
    {
        try { await Task.Delay(450, token); if (!IsMouseOver && !_outgoingDrag && !_volumeDragging) _state.PointerLeft(); }
        catch (OperationCanceledException) { }
    }
    private void UpdateSilhouette()
    {
        if (_musicMorphActive) { ApplyMusicMorph(); return; }
        var width = ActualWidth > 0 ? ActualWidth : Width; var height = ActualHeight > 0 ? ActualHeight : Height;
        var top = _state.IsExpanded ? 12d : 6d; var bottom = _state.IsExpanded ? 22d : 9d;
        bottom = Math.Min(bottom, height); var right = width - top;
        var geometry = new StreamGeometry();
        using (var p = geometry.Open())
        {
            p.BeginFigure(new Point(0, 0), true, true);
            p.QuadraticBezierTo(new Point(top, 0), new Point(top, top), true, false);
            p.LineTo(new Point(top, height - bottom), true, false);
            p.QuadraticBezierTo(new Point(top, height), new Point(top + bottom, height), true, false);
            p.LineTo(new Point(right - bottom, height), true, false);
            p.QuadraticBezierTo(new Point(right, height), new Point(right, height - bottom), true, false);
            p.LineTo(new Point(right, top), true, false);
            p.QuadraticBezierTo(new Point(right, 0), new Point(width, 0), true, false);
        }
        geometry.Freeze(); _silhouette.Data = geometry;
    }
    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle; _source = HwndSource.FromHwnd(_hwnd); _source.AddHook(WindowProc);
        NativeShell.ConfigureToolWindow(_hwnd);
        if (!NativeShell.RegisterHotKey(_hwnd, 1, _settings.HotkeyModifiers | 0x4000, _settings.HotkeyKey)) SetStatus("Горячая клавиша занята. Выберите другую в настройках.");
        _foreground = new(); _foreground.Changed += ScheduleFullscreenCheck;
        SystemEvents.DisplaySettingsChanged += OnDisplayChanged; SystemEvents.PowerModeChanged += OnPowerChanged;
        SetupTray(); PositionPanel(); ScheduleFullscreenCheck();
    }
    private nint WindowProc(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        _tray?.HandleMessage(message, lParam);
        if (message == NativeShell.WmHotkey) { ChangePanelImmediately(OpenExplicit); handled = true; }
        if (message is 0x02E0 or 0x007E or 0x001A) Dispatcher.BeginInvoke(() => { StopSettingsResize(); PositionPanel(); UpdateMotionPreferences(); });
        if (message == 0x0021)
        {
            if (!_state.IsExpanded) { Dispatcher.BeginInvoke(OpenExplicit); handled = true; return 3; }
            RememberForeground(); NativeShell.SetActivationAllowed(_hwnd, true); handled = true; return 1; // explicit click may activate, including text fields
        }
        return 0;
    }
    private void PositionPanel()
    {
        if (_hwnd == 0 || _disposed) return;
        var displays = NativeShell.Displays();
        _display = displays.FirstOrDefault(x => x.Device == _settings.MonitorDevice) ?? displays.FirstOrDefault();
        if (_display is null || _state.Mode == PanelMode.Hidden) return;
        var scale = _display.Dpi / 96d;
        // Native DPI changes can momentarily update WPF Width from the previous
        // physical bounds. Derive sizes from state, never scale that transient value again.
        var (desiredWidth, desiredHeight) = PanelSize;
        Width = Math.Min(desiredWidth, (_display.WorkRight - _display.WorkLeft) / scale);
        if (!_settingsResizeActive) Height = Math.Min(desiredHeight, (_display.WorkBottom - _display.WorkTop) / scale);
        var width = (int)Math.Round(Width * scale);
        var height = (int)Math.Round(Height * scale);
        NativeShell.SetWindowPos(_hwnd, new nint(-1), _display.WorkLeft + (_display.WorkRight - _display.WorkLeft - width) / 2,
            _display.WorkTop + (ShowsMusicPill && !_musicMorphActive ? (int)Math.Round(4 * scale) : 0), width, height, 0x0010); // NOACTIVATE; visibility belongs to WPF Show/Hide
    }
    private void OnDisplayChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(() =>
    {
        StopSettingsResize();
        if (_displaySelector is not null)
        {
            _settingUp = true; var displays = NativeShell.Displays(); _displaySelector.ItemsSource = displays;
            _displaySelector.SelectedItem = displays.FirstOrDefault(x => x.Device == _settings.MonitorDevice) ?? displays.FirstOrDefault(); _settingUp = false;
        }
        PositionPanel(); ScheduleFullscreenCheck();
    });
    private void OnPowerChanged(object sender, PowerModeChangedEventArgs e)
    {
        Dispatcher.BeginInvoke(() => { if (e.Mode == PowerModes.Suspend) { _suspended = true; _progressTimer.Stop(); _state.Hide(); } else if (e.Mode == PowerModes.Resume) { _suspended = false; _state.Unhide(); PositionPanel(); ScheduleFullscreenCheck(); ResumeIntegrations(); } });
    }
    private void ScheduleFullscreenCheck()
    {
        Dispatcher.BeginInvoke(async () =>
        {
            if (_disposed || _suspended) return; _fullscreenDelay?.Cancel(); _fullscreenDelay = new();
            try { await Task.Delay(120, _fullscreenDelay.Token); if (_display is null) return;
                var fullscreen = NativeShell.IsForegroundFullscreen(_hwnd, _display);
                _fullscreen = fullscreen;
                if (fullscreen && _state.Mode != PanelMode.Hidden) _state.Hide();
                else if (!fullscreen && _state.Mode == PanelMode.Hidden) _state.Unhide(); }
            catch (OperationCanceledException) { }
        });
    }
    private void SetupTray()
    {
        _trayMenu = new ContextMenu();
        _trayMenu.Opened += (_, _) => _state.ContextMenuOpen = true;
        _trayMenu.Closed += (_, _) =>
        {
            if (_disposed) return;
            _state.ContextMenuOpen = false;
            if (!_state.IsExpanded) { NativeShell.SetActivationAllowed(_hwnd, false); if (NativeShell.ForegroundWindow() == _hwnd && _previousForeground != 0) NativeShell.SetForegroundWindow(_previousForeground); }
            else if (!IsMouseOver) ScheduleLeave();
        };
        AddMenu(_trayMenu, "Открыть Cyclop", OpenExplicit);
        AddMenu(_trayMenu, "Настройки", () => { OpenExplicit(); SelectTab(PanelTab.Settings, true); });
        _pauseMenu = new MenuItem { Header = "Пауза записи буфера", IsChecked = _settings.ClipboardPaused, IsCheckable = true };
        _pauseMenu.Click += async (_, _) => { _settings.ClipboardPaused = _pauseMenu.IsChecked; await ApplyCaptureSettingsAsync(); };
        _trayMenu.Items.Add(_pauseMenu);
        AddMenu(_trayMenu, "Выход", () => { Dispose(); Application.Current.Shutdown(); });
        _tray = new TrayIcon(_hwnd, System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "Cyclop.ico"));
        _tray.OpenRequested += OpenExplicit;
        _tray.MenuRequested += () => { RememberForeground(); NativeShell.SetActivationAllowed(_hwnd, true); NativeShell.SetForegroundWindow(_hwnd); _trayMenu.Placement = PlacementMode.MousePoint; _trayMenu.IsOpen = true; };
    }
    private void OnDragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        _leave?.Cancel(); if (_state.Mode != PanelMode.ReceivingFiles) _state.BeginDrop(); e.Effects = DragDropEffects.Copy;
        e.Handled = true;
    }
    private async void OnFileDrop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        try { if (e.Data.GetData(DataFormats.FileDrop) is string[] files) await AddShelfFilesAsync(files); }
        catch (Exception) { SetStatus("Не удалось добавить файлы на полку."); }
        finally { _state.EndDrop(); if (!IsMouseOver) ScheduleLeave(); e.Handled = true; }
    }
    private void OnShelfMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _outgoingDrag || !_dragArmed) return;
        var now = e.GetPosition(_shelfList);
        if (Math.Abs(now.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(now.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        var paths = _shelfList.SelectedItems.Cast<ShelfCard>().Select(x => x.Path).Where(p => File.Exists(p) || Directory.Exists(p)).ToArray();
        if (paths.Length == 0) return;
        _outgoingDrag = true; _state.BeginDrop();
        try { DragDrop.DoDragDrop(_shelfList, new DataObject(DataFormats.FileDrop, paths), DragDropEffects.Copy); }
        finally { _outgoingDrag = false; _state.EndDrop(); if (!IsMouseOver) ScheduleLeave(); }
    }
    private void ChooseFiles()
    {
        var picker = new OpenFileDialog { Multiselect = true, Title = "Добавить ссылки на файлы" };
        if (picker.ShowDialog(this) == true) _ = AddShelfFilesAsync(picker.FileNames);
    }
    private void SetStatus(string message) { _vm.Status = message; if (_state.Tab == PanelTab.Shelf) _shelfHint.Text = message; }
    private void SaveSettings() { try { _settings.Save(_settingsPath); } catch (Exception) { SetStatus("Не удалось сохранить настройки."); } }
    private string HotkeyLabel() => string.Join("+", new[] { (_settings.HotkeyModifiers & 2) != 0 ? "Ctrl" : "", (_settings.HotkeyModifiers & 1) != 0 ? "Alt" : "", (_settings.HotkeyModifiers & 4) != 0 ? "Shift" : "", (_settings.HotkeyModifiers & 8) != 0 ? "Win" : "", KeyInterop.KeyFromVirtualKey((int)_settings.HotkeyKey).ToString() }.Where(x => x.Length > 0));
    internal string Diagnostics() => System.Text.Json.JsonSerializer.Serialize(new
    {
        Mode = _state.Mode.ToString(), Tab = _state.Tab.ToString(), IsVisible, IsActive,
        Width, Height, ActualWidth, ActualHeight, Dpi = _display?.Dpi, Monitor = _display?.Device,
        WpfDpi = VisualTreeHelper.GetDpi(this).PixelsPerInchX, NativeDpi = NativeShell.GetDpiForWindow(_hwnd),
        ForegroundIsPanel = NativeShell.ForegroundWindow() == _hwnd,
        FullscreenSuppressed = _fullscreen, CaptureEnabled = _settings.CaptureClipboard,
        HistoryCount = _history?.Items.Count(x => x.Payload.Kind == Cyclop.Core.Clipboard.ClipboardPayloadKind.Text), ShelfCount = _shelf?.Items.Count,
        SpotifySession = _media.HasSession, SpotifyPlaying = _media.IsPlaying,
        SpotifyVolumeAvailable = _volumeSnapshot.IsAvailable, SpotifyVolumePercent = _volumeSnapshot.IsAvailable ? (double?)Math.Round(_volumeSnapshot.Volume * 100) : null,
        SpotifyMuted = _volumeSnapshot.IsMuted,
        MusicPillVisible = _musicPill.IsVisible, MusicPillAnimating = _musicPill.IsAnimating,
        PanelTransitionRunning = _musicMorphActive,
        ModelsInstalled = ModelsInstalled(), ProgressTimerRunning = _progressTimer.IsEnabled
    });
    private void OpenFile(string path)
    { try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); } catch (Exception) { SetStatus("Файл недоступен или нет приложения для открытия."); } }
    private static SolidColorBrush Brush(string value) => (SolidColorBrush)new BrushConverter().ConvertFromString(value)!;
    private static TextBlock Label(string text, double size = 12, string color = "#F3F6FA") => new() { Text = text, FontSize = size, Foreground = Brush(color) };
    private static TextBlock Wrapped(string text, double size) => new() { Text = text, FontSize = size, Foreground = Brush("#B1BDCC"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 5, 0, 5) };
    private Button IconButton(string icon, string name, Action action)
    { var button = new Button { Style = (Style)FindResource("ToolbarButtonStyle"), Content = Icons.Create(icon, 18, Brush("#B1BDCC")), ToolTip = name, Width = 30, Height = 28, Padding = new Thickness(0), Margin = new Thickness(2, 0, 2, 0) }; AutomationProperties.SetName(button, name); button.Click += (_, _) => action(); return button; }
    private Button MediaButton(string icon, string name, Func<Task> action)
    {
        var button = IconButton(icon, name, action);
        button.ClearValue(WidthProperty); button.ClearValue(HeightProperty);
        button.Style = (Style)FindResource("MediaButtonStyle"); button.Content = Icons.CreateMedia(icon);
        button.Margin = new Thickness(2, 0, 2, 0); return button;
    }
    private static Button ActionButton(string name, Action action)
    { var button = new Button { Content = name }; AutomationProperties.SetName(button, name); button.Click += (_, _) => action(); return button; }
    private Button IconButton(string icon, string name, Func<Task> action) => IconButton(icon, name, () => { _ = ExecuteUiTaskAsync(name, action); });
    private Button ActionButton(string name, Func<Task> action) => ActionButton(name, () => { _ = ExecuteUiTaskAsync(name, action); });
    private async Task ExecuteUiTaskAsync(string name, Func<Task> action)
    {
        try { await action(); }
        catch (OperationCanceledException) { }
        catch (Exception) { SetStatus($"Не удалось выполнить действие «{name}». Проверьте доступ и повторите."); }
    }
    private CheckBox AddCheck(Panel panel, string text, bool initial, Action<bool> changed)
    { var check = new CheckBox { Content = text, IsChecked = initial }; check.Checked += (_, _) => { if (!_settingUp) changed(true); }; check.Unchecked += (_, _) => { if (!_settingUp) changed(false); }; panel.Children.Add(check); return check; }
    private void HoldForDropdown(ComboBox combo)
    {
        combo.DropDownOpened += (_, _) => _state.ContextMenuOpen = true;
        combo.DropDownClosed += (_, _) => { _state.ContextMenuOpen = false; if (!IsMouseOver) ScheduleLeave(); };
    }
    private void AddNumber(Panel panel, string label, int initial, int min, int max, Action<int> changed)
    {
        var row = new DockPanel { Margin = new Thickness(0, 5, 0, 5) }; var input = new TextBox { Text = initial.ToString(), Width = 65 }; DockPanel.SetDock(input, Dock.Right); row.Children.Add(input);
        var caption = Label(label, 11, "#B1BDCC"); caption.VerticalAlignment = VerticalAlignment.Center; row.Children.Add(caption);
        input.LostKeyboardFocus += (_, _) => { if (int.TryParse(input.Text, out var n)) { n = Math.Clamp(n, min, max); input.Text = n.ToString(); changed(n); } else input.Text = initial.ToString(); };
        AutomationProperties.SetName(input, label); panel.Children.Add(row);
    }
    public void Dispose()
    {
        if (_disposed) return; _disposed = true;
        _state.Changed -= RenderState;
        _hover?.Cancel(); _leave?.Cancel(); _fullscreenDelay?.Cancel();
        FinishPaneTransition(); StopSettingsResize(); EndMusicMorph(); _progressTimer.Stop(); _musicPill.SetAnimation(false); _foreground?.Dispose(); _source?.RemoveHook(WindowProc); NativeShell.UnregisterHotKey(_hwnd, 1);
        SystemEvents.DisplaySettingsChanged -= OnDisplayChanged; SystemEvents.PowerModeChanged -= OnPowerChanged;
        _tray?.Dispose(); DisposeIntegrations();
    }
}
