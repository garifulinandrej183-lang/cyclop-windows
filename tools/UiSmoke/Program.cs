using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Cyclop.App;
using Cyclop.App.UI;
using Cyclop.Core.Media;
using Cyclop.Core.Panel;
using Cyclop.Core.Settings;
using Cyclop.Core.Clipboard;
using Cyclop.Core.Data;
using Cyclop.Core.Shelf;
using Cyclop.Core.Translation;
using Cyclop.Windows.Shell;
using System.Windows.Interop;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var output = Path.GetFullPath(args.FirstOrDefault() ?? "artifacts/ui-smoke");
        Directory.CreateDirectory(output);
        Environment.SetEnvironmentVariable("CYCLOP_DATA_ROOT", Path.Combine(output, "isolated-data"));
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        application.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/Cyclop;component/UI/Theme.xaml") });
        var panel = new MainWindow();
        var exit = 0;
        application.Dispatcher.BeginInvoke(async () =>
        {
            try
            {
                panel.Show();
                if (args.Contains("--readme-previews-only")) { SaveReadmePreviews(panel, output); Console.WriteLine("README previews rendered from synthetic data (no live integrations)."); return; }
                if (args.Contains("--translate-layout-only")) { await CheckTranslationLayoutAsync(panel, output); Console.WriteLine("WPF translation-spacing/field-focus smoke passed (no live clipboard/audio access)."); return; }
                if (args.Contains("--pane-motion-only")) { await CheckUniformPanesAsync(panel, output); await CheckPaneTransitionsAsync(panel, output); Console.WriteLine("WPF pane-transition smoke passed (isolated data; no live clipboard/audio access)."); return; }
                if (args.Contains("--focus-only")) { await CheckButtonFocusAsync(panel, output); Console.WriteLine("WPF button-focus smoke passed (no live clipboard/audio access)."); return; }
                if (args.Contains("--screen-review-only")) { await CheckUniformPanesAsync(panel, output); await CheckScreenAlignmentAsync(panel, output); await CheckClickNavigationAsync(panel); await CheckSettingsDrawerAsync(panel, output); Console.WriteLine("WPF screen/alignment/settings-motion smoke passed (isolated data; no live clipboard/audio access)."); return; }
                if (args.Contains("--apple-style-only")) { await CheckAppleStyleAsync(panel, output); Console.WriteLine("WPF Apple-style smoke passed (isolated data; no live clipboard/audio access)."); return; }
                if (args.Contains("--shelf-only")) { await CheckShelfAsync(panel, output); Console.WriteLine("WPF shelf smoke passed (isolated files; no live clipboard/audio access)."); return; }
                if (args.Contains("--uniform-panes-only")) { await CheckUniformPanesAsync(panel, output); Console.WriteLine("WPF uniform-pane smoke passed (no live clipboard/audio access)."); return; }
                if (args.Contains("--volume-history-only")) { await CheckVolumeHistoryAsync(panel, output); Console.WriteLine("WPF volume/history smoke passed (synthetic payloads; no live clipboard/audio writes)."); return; }
                if (args.Contains("--menus-only")) { await CheckMenusAsync(panel, output); Console.WriteLine("WPF menu smoke passed."); return; }
                if (args.Contains("--controls-only")) { CheckControls(panel, output); Console.WriteLine("WPF controls visual smoke passed."); return; }
                await CheckMusicPillAsync(panel, output);
                if (args.Contains("--motion-only")) { Console.WriteLine("WPF music motion smoke passed."); return; }
                await panel.InitializeAsync();
                await Task.Delay(150);
                CheckCollapsed(panel, "collapsed native bounds");
                Check(NativeShell.ForegroundWindow() != new WindowInteropHelper(panel).Handle, "passive panel preserves foreground window");
                Snapshot(panel, output, "collapsed");
                panel.OpenExplicit(); await Task.Delay(450);
                Check(panel.Width == 520 && panel.Height == 180, $"compact Spotify bounds ({panel.Width} × {panel.Height})");
                var activated = NativeShell.ForegroundWindow() == new WindowInteropHelper(panel).Handle;
                Console.WriteLine(activated ? "PASS explicit open activates native window" : "NOT EXECUTED: native foreground activation requires a real user gesture under current Windows foreground policy.");
                Snapshot(panel, output, "spotify");
                foreach (var tab in new[] { "Полка", "Буфер обмена", "Перевод", "Настройки" })
                {
                    Click(panel, tab); await Task.Delay(120); Snapshot(panel, output, tab switch { "Полка" => "shelf", "Буфер обмена" => "clipboard", "Перевод" => "translate", _ => "settings" });
                }
                var paused = Descendants(panel).OfType<CheckBox>().Single(x => Equals(x.Content, "Приостановить запись"));
                paused.IsChecked = true;
                var trayPause = (MenuItem)typeof(MainWindow).GetField("_pauseMenu", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(panel)!;
                trayPause.IsChecked = false; trayPause.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                Check(paused.IsChecked == false, "tray pause state stays consistent with settings");
                Click(panel, "Перевод");
                var source = Descendants(panel).OfType<TextBox>().Single(x => AutomationProperties.GetName(x) == "Исходный текст перевода");
                Check(ReferenceEquals(Keyboard.FocusedElement, source) || ReferenceEquals(FocusManager.GetFocusedElement(panel), source), "explicit translate tab assigns field focus");
                var result = Descendants(panel).OfType<TextBox>().Single(x => AutomationProperties.GetName(x) == "Результат перевода");
                var root = FindRepo(output);
                var installer = new ModelInstaller();
                foreach (var model in ModelInstaller.Models)
                    await installer.ImportPackageAsync(model.Id, Path.Combine(root, "artifacts", "model-cache", model.Id + ".argosmodel"), Path.Combine(AppSettings.DataRoot, "models"));
                source.Text = "Hello, how are you?\n\nHave a nice day.";
                await WaitForAsync(() => result.Text.Length > 0, TimeSpan.FromSeconds(60));
                Check(result.Text.Any(c => c is >= 'А' and <= 'я'), "C# UI -> frozen worker EN to RU");
                Check(result.Text.Contains("\n\n"), "paragraph preserved");
                Snapshot(panel, output, "translation-real");
                var previous = result.Text; source.Text = "Привет, как дела?";
                await WaitForAsync(() => result.Text.Length > 0 && result.Text != previous, TimeSpan.FromSeconds(60));
                Check(result.Text.Any(c => c is >= 'a' and <= 'z'), "C# UI -> frozen worker RU to EN");
                // Render at target DPIs. These are visual fixtures, not physical monitor transition tests.
                foreach (var dpi in new[] { 96, 144, 192 }) Snapshot(panel, output, "translate-dpi-" + dpi, dpi);
                source.Text = new string('x', 100_001);
                await Task.Delay(600);
                Check(source.Text.Length == 100_001 && result.Text.Length == 0 && Descendants(panel).OfType<TextBlock>().Any(x => x.Text.Contains("Слишком длинный текст")), "oversized input is preserved with an explicit error");
                panel.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(panel), 0, Key.Escape) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
                await Task.Delay(450);
                CheckCollapsed(panel, "Escape collapses actual window");
                if (activated) Check(NativeShell.ForegroundWindow() != new WindowInteropHelper(panel).Handle, "Escape returns native foreground focus");
                Console.WriteLine("WPF smoke passed: bounds, tabs, explicit field focus, real model imports and translation both ways.");
            }
            catch (Exception error) { Console.Error.WriteLine(error); exit = 1; }
            finally { panel.Dispose(); panel.Close(); application.Shutdown(); }
        });
        application.Run(); return exit;
    }
    private static void SaveReadmePreviews(MainWindow panel, string output)
    {
        Field<AppSettings>(panel, "_settings").ReducedMotion = true;
        var media = SpotifySnapshot.Empty with { SourceAppUserModelId = "Spotify.exe", Title = "Cyclop Windows", Artist = "Spotify · demo preview", Position = TimeSpan.FromSeconds(65), Duration = TimeSpan.FromSeconds(181), SeekEnd = TimeSpan.FromSeconds(181), CanPlay = true, CanPause = true, CanSkipPrevious = true, CanSkipNext = true, CanSeek = true };
        typeof(MainWindow).GetMethod("ShowMedia", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(panel, [media]);
        typeof(MainWindow).GetMethod("ShowVolume", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(panel, [new SpotifyVolumeSnapshot(true, .5, false)]);
        ((Cyclop.App.ViewModels.MainViewModel)panel.DataContext).Status = "";
        Field<PanelState>(panel, "_state").Select(PanelTab.Spotify); Snapshot(panel, output, "panel", 144);
        SetField(panel, "_settingUp", true); Field<TextBox>(panel, "_input").Text = "Hello, world!\nA little more space."; SetField(panel, "_settingUp", false);
        ((Cyclop.App.ViewModels.MainViewModel)panel.DataContext).OutputText = "Привет, мир!\nНемного больше воздуха.";
        Field<PanelState>(panel, "_state").Select(PanelTab.Translate); Snapshot(panel, output, "translate", 144);
    }

    private static async Task CheckTranslationLayoutAsync(MainWindow panel, string output)
    {
        Field<AppSettings>(panel, "_settings").ReducedMotion = true;
        var state = Field<PanelState>(panel, "_state"); state.Select(PanelTab.Translate); state.ContextMenuOpen = true;
        ((Cyclop.App.ViewModels.MainViewModel)panel.DataContext).Status = "";
        var combo = Field<ComboBox>(panel, "_direction"); var input = Field<TextBox>(panel, "_input"); var result = Field<TextBox>(panel, "_output");
        Button Button(string name) => Descendants(panel).OfType<Button>().Single(x => AutomationProperties.GetName(x) == name);
        var models = Button("Модели"); var swap = Button("Поменять языки"); var copy = Button("Копировать перевод"); panel.UpdateLayout();
        Rect Bounds(FrameworkElement element) => new(element.TranslatePoint(new Point(), panel), element.RenderSize);
        var header = new[] { Bounds(combo), Bounds(swap), Bounds(models), Bounds(copy) };
        Check(header.All(x => Math.Abs(x.Height - 26) < .01 && Math.Abs(x.Top - header[0].Top) < .5), "all translation toolbar controls share their height and top edge");
        Check(Bounds(swap).Left - Bounds(combo).Right >= 8 && Bounds(copy).Left - Bounds(models).Right >= 8, "toolbar groups have clear 8 DIP gaps");
        Check(Bounds(input).Top - header.Max(x => x.Bottom) >= 12 && Bounds(result).Left - Bounds(input).Right >= 12, "editors have 12 DIP spacing above and between them");
        var caption = Descendants(models).OfType<TextBlock>().First(); var captionBounds = new Rect(caption.TranslatePoint(new Point(), models), caption.RenderSize);
        Check(captionBounds.Top >= 3 && captionBounds.Bottom <= models.ActualHeight - 3 && Math.Abs(captionBounds.Top + captionBounds.Height / 2 - models.ActualHeight / 2) < .5, "Models caption is centered with enough unclipped line height");
        foreach (var dpi in new[] { 96, 144, 192 }) Snapshot(panel, output, "translate-spaced-empty-" + dpi, dpi);
        Click(panel, "Перевод"); input.Focus(); await Task.Delay(30); panel.UpdateLayout();
        var accent = ((SolidColorBrush)panel.FindResource("AccentBrush")).Color;
        var inputField = (Border)input.Template.FindName("Field", input);
        Check(input.IsKeyboardFocused && inputField.BorderBrush is SolidColorBrush stroke && stroke.Color != accent, "mouse-focused editor avoids the sharp blue outline");
        Check(inputField.Background is SolidColorBrush fill && fill.Color == ((SolidColorBrush)panel.FindResource("FocusedFieldBrush")).Color, "editor focus uses a soft surface change");
        Snapshot(panel, output, "translate-soft-focus-144", 144);
        combo.Focus(); combo.IsDropDownOpen = true; await Task.Delay(80); panel.UpdateLayout();
        var toggle = (System.Windows.Controls.Primitives.ToggleButton)combo.Template.FindName("Toggle", combo);
        var comboField = (Border)toggle.Template.FindName("Field", toggle);
        Check(comboField.BorderBrush is SolidColorBrush comboStroke && comboStroke.Color != accent, "open language selector retains a quiet border");
        combo.IsDropDownOpen = false; await Task.Delay(80); state.ContextMenuOpen = true;
        SetField(panel, "_settingUp", true); input.Text = "Hello, world!\nA little more space."; SetField(panel, "_settingUp", false);
        ((Cyclop.App.ViewModels.MainViewModel)panel.DataContext).OutputText = "Привет, мир!\nНемного больше воздуха.";
        panel.UpdateLayout(); var first = input.GetRectFromCharacterIndex(0);
        Check(first.Top >= 7 && first.Bottom < input.ActualHeight && result.IsReadOnly, "padded editor text fits and output remains read-only");
        Snapshot(panel, output, "translate-spaced-filled-144", 144);
        Click(panel, "Модели");
        Check(state.Tab == PanelTab.Settings && panel.Height == 420, "Models action still opens settings without installing anything");
    }

    private static async Task CheckPaneTransitionsAsync(MainWindow panel, string output)
    {
        var settings = Field<AppSettings>(panel, "_settings"); var state = Field<PanelState>(panel, "_state");
        var panes = Field<Dictionary<PanelTab, FrameworkElement>>(panel, "_panes");
        settings.ReducedMotion = true; state.Select(PanelTab.Spotify); state.ContextMenuOpen = true;
        ((Cyclop.App.ViewModels.MainViewModel)panel.DataContext).Status = "";
        var input = Field<TextBox>(panel, "_input"); var originalText = input.Text;
        settings.ReducedMotion = false;
        Click(panel, "Полка"); panel.UpdateLayout();
        Check(state.Tab == PanelTab.Shelf && panes[PanelTab.Shelf].IsHitTestVisible, "click selects an interactive incoming pane immediately");
        if (SystemParameters.ClientAreaAnimation)
        {
            Check(Field<bool>(panel, "_paneTransitionActive") && panes[PanelTab.Spotify].Visibility == Visibility.Visible && !panes[PanelTab.Spotify].IsHitTestVisible, "outgoing pane fades without accepting pointer input");
            Check(KeyboardNavigation.GetTabNavigation(panes[PanelTab.Spotify]) == KeyboardNavigationMode.None, "outgoing controls leave keyboard traversal during the fade");
            await Task.Delay(50); panel.UpdateLayout();
            Check(panes[PanelTab.Shelf].Opacity > 0 && panes[PanelTab.Shelf].Opacity < 1 && panes[PanelTab.Spotify].Opacity < 1, "panes pass through intermediate opacity values");
            Snapshot(panel, output, "pane-transition-mid-144", 144);
            var displayed = panes.Values.Where(x => x.Visibility == Visibility.Visible && x.Opacity > .01).ToDictionary(x => x, x => x.Opacity);
            Click(panel, "Перевод");
            Check(displayed.All(x => Math.Abs(x.Key.Opacity - x.Value) < .03), "interrupted fades retain the displayed composite opacities");
        }
        Click(panel, "Буфер обмена"); Click(panel, "Полка");
        await Task.Delay(220); panel.UpdateLayout();
        Check(state.Tab == PanelTab.Shelf && panes.Count(x => x.Value.Visibility == Visibility.Visible) == 1, "rapid clicks settle on the latest pane without a queue");
        Check(!Field<bool>(panel, "_paneTransitionActive") && panes.Values.All(x => !DependencyPropertyHelper.GetValueSource(x, UIElement.OpacityProperty).IsAnimated && x.Opacity == 1), "completion releases every pane opacity clock");
        Click(panel, "Полка"); Check(!Field<bool>(panel, "_paneTransitionActive"), "clicking the selected section does not replay the fade");
        foreach (var dpi in new[] { 96, 144, 192 }) Snapshot(panel, output, "pane-transition-settled-" + dpi, dpi);

        Click(panel, "Настройки"); await Task.Delay(55);
        Check(!SystemParameters.ClientAreaAnimation || Field<bool>(panel, "_paneTransitionActive") && Field<bool>(panel, "_settingsResizeActive"), "content fade coordinates with settings height growth");
        Snapshot(panel, output, "pane-settings-mid-144", 144);
        await Task.Delay(300);
        Check(panel.Height == 420 && !Field<bool>(panel, "_paneTransitionActive"), "settings content and height finish at the correct bounds");
        Click(panel, "Spotify"); await Task.Delay(300);
        Check(panel.Height == 180 && panes[PanelTab.Spotify].Opacity == 1, "leaving settings restores the compact player");

        typeof(MainWindow).GetMethod("ChangePanelImmediately", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(panel, [(Action)(() => Click(panel, "Перевод"))]);
        Check(!Field<bool>(panel, "_paneTransitionActive") && panes[PanelTab.Translate].Opacity == 1, "keyboard-initiated selection remains immediate");
        Click(panel, "Полка"); await Task.Delay(35); settings.ReducedMotion = true;
        typeof(MainWindow).GetMethod("UpdateMotionPreferences", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(panel, null);
        Check(!Field<bool>(panel, "_paneTransitionActive") && panes[PanelTab.Shelf].Opacity == 1 && panes[PanelTab.Translate].Visibility == Visibility.Collapsed, "reduced motion finishes a running fade immediately");
        Click(panel, "Буфер обмена"); Check(!Field<bool>(panel, "_paneTransitionActive"), "reduced-motion clicks do not create pane clocks");
        settings.ReducedMotion = false; Click(panel, "Перевод"); await Task.Delay(35); state.Hide();
        Check(!panel.IsVisible && !Field<bool>(panel, "_paneTransitionActive") && panes.Values.All(x => !DependencyPropertyHelper.GetValueSource(x, UIElement.OpacityProperty).IsAnimated), "hide clears in-flight pane clocks");
        await Task.Delay(220); state.Unhide();
        CheckCollapsed(panel, "hidden fade callbacks cannot reopen the panel");
        Check(input.Text == originalText, "transitions preserve existing editor content");
    }

    private static async Task CheckButtonFocusAsync(MainWindow panel, string output)
    {
        Field<AppSettings>(panel, "_settings").ReducedMotion = true;
        var state = Field<PanelState>(panel, "_state"); state.Select(PanelTab.Shelf); state.ContextMenuOpen = true;
        ((Cyclop.App.ViewModels.MainViewModel)panel.DataContext).Status = "";
        var button = Field<Dictionary<PanelTab, Button>>(panel, "_tabs")[PanelTab.Shelf]; panel.UpdateLayout();
        var surface = (Border)button.Template.FindName("Surface", button);
        var originalRadius = surface.CornerRadius; var originalBounds = surface.RenderSize;
        Click(panel, "Полка"); button.Focus(); await Task.Delay(50); panel.UpdateLayout();
        Check(button.IsKeyboardFocused, "selected icon can retain native WPF keyboard focus");
        Check(surface.BorderBrush is SolidColorBrush brush && brush.Color.A == 0, "focused selected icon keeps its soft fill without a painted border");
        Check(surface.CornerRadius == originalRadius && surface.RenderSize == originalBounds, "focus preserves the selected icon radius and bounds");
        foreach (var dpi in new[] { 96, 144, 192 }) Snapshot(panel, output, "button-mouse-focus-" + dpi, dpi);
        // Routed input does not update WPF's native-input modality. Set it only in
        // this isolated process so the framework's keyboard-only adorners can render.
        var modality = typeof(InputManager).GetProperty(nameof(InputManager.MostRecentInputDevice))!;
        modality.SetValue(InputManager.Current, Keyboard.PrimaryDevice);
        InputManager.Current.ProcessInput(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(button), Environment.TickCount, Key.Tab) { RoutedEvent = Keyboard.KeyDownEvent, Source = button });
        await Task.Delay(50); panel.UpdateLayout();
        var next = Keyboard.FocusedElement as UIElement;
        Check(next is not null && next != button, "Tab still moves focus to the next control");
        var layer = AdornerLayer.GetAdornerLayer(next!);
        Check(layer is not null && Descendants(layer).OfType<Border>().Any(x => x.Name == "KeyboardFocusRing" && x.IsVisible), "keyboard-modality fixture shows the rounded focus adorner");
        Snapshot(panel, output, "button-keyboard-focus-144", 144);
        modality.SetValue(InputManager.Current, Mouse.PrimaryDevice);
        InputManager.Current.ProcessInput(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = Mouse.PreviewMouseDownEvent, Source = button });
        button.Focus(); button.ReleaseMouseCapture(); await Task.Delay(50); panel.UpdateLayout();
        Check(!Descendants(layer!).OfType<Border>().Any(x => x.Name == "KeyboardFocusRing" && x.IsVisible), "mouse input clears the keyboard-only focus adorner");
        Check(state.Tab == PanelTab.Shelf, "focus styling leaves section selection unchanged");
        Snapshot(panel, output, "button-mouse-again-144", 144);
    }

    private static async Task CheckScreenAlignmentAsync(MainWindow panel, string output)
    {
        var state = Field<PanelState>(panel, "_state");
        ((Cyclop.App.ViewModels.MainViewModel)panel.DataContext).Status = "";
        SetField(panel, "_settingUp", true);
        try
        {
            foreach (var (name, tab, sample) in new[] {
                ("_search", PanelTab.Clipboard, "Список покупок"),
                ("_input", PanelTab.Translate, "Hello, world!\nПривет, мир!"),
                ("_output", PanelTab.Translate, "Привет, мир!\nHello, world!") })
            {
                state.Select(tab);
                var box = Field<TextBox>(panel, name); box.SetCurrentValue(TextBox.TextProperty, ""); panel.UpdateLayout();
                var placeholder = (TextBlock)box.Template.FindName("Placeholder", box);
                var origin = placeholder.TranslatePoint(new Point(), box);
                Check(placeholder.IsVisible, name + " empty prompt is visible");
                box.SetCurrentValue(TextBox.TextProperty, sample); box.Select(0, 0); box.ScrollToHome(); panel.UpdateLayout();
                var first = box.GetRectFromCharacterIndex(0);
                Check(Math.Abs(first.X - origin.X) <= .75 && Math.Abs(first.Y - origin.Y) <= .75, name + " typed text matches placeholder X/Y insets");
                Check(first.Top >= 0 && first.Bottom <= box.ActualHeight, name + " first text line fits inside the field");
            }
            state.Select(PanelTab.Translate); panel.UpdateLayout();
            foreach (var dpi in new[] { 96, 144, 192 }) Snapshot(panel, output, "review-translation-filled-" + dpi, dpi);
            state.Select(PanelTab.Settings); panel.UpdateLayout();
            foreach (var box in Descendants(panel).OfType<TextBox>().Where(x => !string.IsNullOrEmpty(AutomationProperties.GetName(x)) && x.Tag is null))
            {
                var first = box.GetRectFromCharacterIndex(0);
                Check(first.Top >= 0 && first.Bottom <= box.ActualHeight, AutomationProperties.GetName(box) + " numeric/shortcut text is not clipped");
            }
        }
        finally { SetField(panel, "_settingUp", false); }
    }

    private static async Task CheckSettingsDrawerAsync(MainWindow panel, string output)
    {
        var settings = Field<AppSettings>(panel, "_settings"); var state = Field<PanelState>(panel, "_state");
        settings.ReducedMotion = true; state.Select(PanelTab.Spotify); panel.UpdateLayout();
        // Routed activation can leave the physical pointer outside this fixture window.
        state.ContextMenuOpen = true;
        var top = panel.Top; var left = panel.Left;
        var panes = Field<Dictionary<PanelTab, FrameworkElement>>(panel, "_panes");
        var spotifyTop = panes[PanelTab.Spotify].TranslatePoint(new Point(), panel).Y;
        settings.ReducedMotion = false;
        state.Select(PanelTab.Settings);
        if (SystemParameters.ClientAreaAnimation)
        {
            Check(Field<bool>(panel, "_settingsResizeActive"), "settings begin a native height transition");
            await Task.Delay(65); panel.UpdateLayout();
            Check(panel.Height > 180 && panel.Height < 420 && panel.Width == 520, "settings grow downward through intermediate heights");
            Check(Math.Abs(panel.Top - top) < .75 && Math.Abs(panel.Left - left) < .75, "drawer keeps its top edge and horizontal position fixed");
            Check(Field<ScaleTransform>(panel, "_reveal").ScaleY == 1, "drawer resize does not scale its text");
            Snapshot(panel, output, "settings-drawer-mid-144", 144);
            var current = panel.Height; state.Select(PanelTab.Spotify);
            Check(Math.Abs(panel.Height - current) < .75, "drawer reversal starts at its displayed height");
            await Task.Delay(45); current = panel.Height; state.Select(PanelTab.Settings);
            Check(Math.Abs(panel.Height - current) < .75, "interrupted drawer can reopen without a jump");
        }
        await Task.Delay(350); panel.UpdateLayout();
        Check(panel.Height == 420 && panel.ActualHeight == 420 && !Field<bool>(panel, "_settingsResizeActive"), "settings settle at 520 × 420 and release the height clock");
        var scroll = (ScrollViewer)panes[PanelTab.Settings]; scroll.ScrollToHome(); await Task.Delay(60);
        foreach (var dpi in new[] { 96, 144, 192 }) Snapshot(panel, output, "settings-drawer-open-" + dpi, dpi);
        state.Select(PanelTab.Spotify); state.Select(PanelTab.Settings); await Task.Delay(320);
        Check(panel.Height == 420 && !Field<bool>(panel, "_settingsResizeActive"), $"same-frame reverse cancels the previous height target ({panel.Height}, {state.Mode})");
        state.Select(PanelTab.Spotify); await Task.Delay(320); panel.UpdateLayout();
        Check(panel.Height == 180 && Math.Abs(panes[PanelTab.Spotify].TranslatePoint(new Point(), panel).Y - spotifyTop) < .75, "leaving settings restores compact bounds without moving player content");
        state.Escape(); state.ContextMenuOpen = true; state.Select(PanelTab.Settings); await Task.Delay(320);
        Check(panel.Height == 420 && !Field<bool>(panel, "_settingsResizeActive"), "settings also open from the collapsed strip");
        state.Select(PanelTab.Spotify); await Task.Delay(40); settings.ReducedMotion = true;
        typeof(MainWindow).GetMethod("UpdateMotionPreferences", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(panel, null);
        Check(panel.Height == 180 && !Field<bool>(panel, "_settingsResizeActive"), "reduced motion settles drawer height immediately");
        settings.ReducedMotion = false; state.Select(PanelTab.Settings); await Task.Delay(45); state.Hide();
        Check(!panel.IsVisible && !Field<bool>(panel, "_settingsResizeActive"), "hide cancels the drawer clock");
        state.Unhide(); CheckCollapsed(panel, "unhide restores collapsed bounds after drawer interruption");
    }

    private static async Task CheckClickNavigationAsync(MainWindow panel)
    {
        var state = Field<PanelState>(panel, "_state"); state.Select(PanelTab.Spotify);
        var tabs = Field<Dictionary<PanelTab, Button>>(panel, "_tabs");
        foreach (var tab in new[] { PanelTab.Shelf, PanelTab.Clipboard, PanelTab.Translate })
        {
            var previous = state.Tab;
            tabs[tab].RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = Mouse.MouseEnterEvent });
            await Task.Delay(280);
            Check(state.Tab == previous, tab + " hover event leaves the current section unchanged");
            tabs[tab].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(state.Tab == tab, tab + " switches on activation");
        }
    }

    private static async Task CheckAppleStyleAsync(MainWindow panel, string output)
    {
        var settings = Field<AppSettings>(panel, "_settings"); settings.ReducedMotion = true; settings.CaptureClipboard = true;
        var state = Field<PanelState>(panel, "_state");
        var vm = (Cyclop.App.ViewModels.MainViewModel)panel.DataContext;
        vm.Status = "";
        var history = new ClipboardHistoryService(new EncryptedClipboardRepository(Path.Combine(output, "style-history.db"), new DpapiDataProtector()), new() { ExplicitlyEnabled = true });
        await history.InitializeAsync(); await history.ClearAsync();
        SetField(panel, "_history", history);
        await history.CaptureAsync(ClipboardPayload.FromText("Список покупок на выходные"));
        await history.CaptureAsync(ClipboardPayload.FromText("https://example.com/design"));
        await history.CaptureAsync(ClipboardPayload.FromText("Идеи для следующего проекта"));
        void Refresh() => typeof(MainWindow).GetMethod("RefreshHistory", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(panel, null);
        Refresh(); state.Select(PanelTab.Clipboard); panel.UpdateLayout();
        var search = Field<TextBox>(panel, "_search"); var rows = Field<StackPanel>(panel, "_clipboardList");
        var field = (Border)search.Template.FindName("Field", search); var placeholder = (TextBlock)search.Template.FindName("Placeholder", search);
        Check(field.CornerRadius.TopLeft == 9 && placeholder.IsVisible && placeholder.Text.Contains("Поиск"), "rounded search shows a useful empty placeholder");
        Check(rows.Children.Count == 3 && rows.Children.OfType<Border>().All(x => x.CornerRadius.TopLeft == 10), "history uses consistent grouped rows");
        foreach (var dpi in new[] { 96, 144, 192 }) Snapshot(panel, output, "apple-history-" + dpi, dpi);
        search.Text = "покупок"; panel.UpdateLayout();
        Check(rows.Children.Count == 1 && !placeholder.IsVisible, "search filtering still works and hides its placeholder");
        Check(Math.Abs(search.GetRectFromCharacterIndex(0).X - placeholder.TranslatePoint(new Point(), search).X) <= 4, "typed search text aligns with its placeholder");
        var reset = Descendants(panel).OfType<Button>().Single(x => AutomationProperties.GetName(x) == "Очистить поиск");
        Check(reset.IsVisible, "search clear action appears only for a nonempty query");
        var host = (FrameworkElement)search.Template.FindName("PART_ContentHost", search);
        Check(host.TransformToAncestor(search).Transform(new Point(host.ActualWidth, 0)).X <= search.ActualWidth - 20, "search content reserves space for its clear action");
        Snapshot(panel, output, "apple-search-filtered-144", 144);
        Click(panel, "Очистить поиск"); panel.UpdateLayout();
        Check(search.Text.Length == 0 && rows.Children.Count == 3 && reset.Visibility == Visibility.Collapsed, "clear query restores all rows without clearing history");
        Descendants(rows).OfType<Button>().First(x => AutomationProperties.GetName(x) == "Закрепить").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await WaitForAsync(() => history.Items.Any(x => x.IsPinned), TimeSpan.FromSeconds(3)); Refresh();
        Check(Descendants(rows).OfType<Button>().Any(x => Equals(x.ToolTip, "Открепить")), "pin action retains its state with the new row template");

        state.Select(PanelTab.Translate); panel.UpdateLayout();
        var input = Field<TextBox>(panel, "_input"); var result = Field<TextBox>(panel, "_output");
        Check(((TextBlock)input.Template.FindName("Placeholder", input)).IsVisible && ((TextBlock)result.Template.FindName("Placeholder", result)).IsVisible && result.IsReadOnly, "both translation placeholders retain editor semantics");
        Snapshot(panel, output, "apple-translate-empty-144", 144);
        SetField(panel, "_settingUp", true); input.Text = "Пример текста"; SetField(panel, "_settingUp", false); panel.UpdateLayout();
        var inputPlaceholder = (TextBlock)input.Template.FindName("Placeholder", input);
        Check(Math.Abs(input.GetRectFromCharacterIndex(0).X - inputPlaceholder.TranslatePoint(new Point(), input).X) <= 4, "editor text and placeholder share the same inset");
        var combo = Field<ComboBox>(panel, "_direction"); combo.IsDropDownOpen = true; await Task.Delay(100);
        var popup = (System.Windows.Controls.Primitives.Popup)combo.Template.FindName("PART_Popup", combo);
        Check(popup.IsOpen && state.ContextMenuOpen && ((Border)popup.Child).CornerRadius.TopLeft == 10, "rounded language menu opens and holds the panel");
        Snapshot((FrameworkElement)popup.Child, output, "apple-languages-144", 144);
        SetField(panel, "_settingUp", true); combo.SelectedIndex = 1; SetField(panel, "_settingUp", false);
        combo.IsDropDownOpen = false; await Task.Delay(100);
        Check(combo.SelectedIndex == 1 && !state.ContextMenuOpen, "language selection survives closing its popup");
        SetField(panel, "_settingUp", true);
        combo.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(combo), Environment.TickCount, Key.Down) { RoutedEvent = Keyboard.KeyDownEvent });
        SetField(panel, "_settingUp", false);
        Check(combo.SelectedIndex == 2, "language menu retains keyboard Down navigation");

        state.Select(PanelTab.Settings); panel.UpdateLayout();
        var pause = Descendants(panel).OfType<CheckBox>().Single(x => Equals(x.Content, "Приостановить запись"));
        pause.IsChecked = true; panel.UpdateLayout();
        var knob = (FrameworkElement)pause.Template.FindName("Knob", pause);
        Check(settings.ClipboardPaused && knob.HorizontalAlignment == HorizontalAlignment.Right, "switch checked state updates its setting and thumb");
        pause.IsChecked = false; panel.UpdateLayout();
        Check(!settings.ClipboardPaused && knob.HorizontalAlignment == HorizontalAlignment.Left, "switch unchecked state remains consistent");
        pause.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(pause), Environment.TickCount, Key.Space) { RoutedEvent = Keyboard.KeyDownEvent });
        pause.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(pause), Environment.TickCount, Key.Space) { RoutedEvent = Keyboard.KeyUpEvent });
        Check(pause.IsChecked == true && settings.ClipboardPaused, "switch retains keyboard Space activation");
        pause.IsChecked = false;
        Snapshot(panel, output, "apple-settings-144", 144);
        var settingsScroll = (ScrollViewer)Field<Dictionary<PanelTab, FrameworkElement>>(panel, "_panes")[PanelTab.Settings];
        var verticalBar = (System.Windows.Controls.Primitives.ScrollBar)settingsScroll.Template.FindName("PART_VerticalScrollBar", settingsScroll);
        System.Windows.Controls.Primitives.ScrollBar.PageDownCommand.Execute(null, verticalBar); await Task.Delay(80);
        Check(settingsScroll.VerticalOffset > 0, "styled scrollbar retains its page navigation command");
        settingsScroll.ScrollToEnd(); await Task.Delay(80); Snapshot(panel, output, "apple-settings-end-144", 144);
        Check(panel.Width == 520 && panel.Height == 420, "settings use the taller drawer bounds");
    }

    private static async Task CheckShelfAsync(MainWindow panel, string output)
    {
        Field<AppSettings>(panel, "_settings").ReducedMotion = true;
        var fixtureRoot = Path.Combine(output, "shelf-data-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixtureRoot);
        var shelf = new FileShelfService(Path.Combine(fixtureRoot, "shelf.json"), Path.Combine(fixtureRoot, "screenshots"));
        SetField(panel, "_shelf", shelf);
        var refresh = typeof(MainWindow).GetMethod("RefreshShelf", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        shelf.ItemsChanged += (_, _) => panel.Dispatcher.BeginInvoke(() => refresh.Invoke(panel, null));
        await shelf.LoadAsync();
        var originals = Enumerable.Range(1, 12).Select(n => Path.Combine(fixtureRoot, $"Документ {n:00}.txt")).ToArray();
        foreach (var path in originals) await File.WriteAllTextAsync(path, "Synthetic shelf fixture");
        await shelf.AddFilesAsync(originals);
        var image = await shelf.AddImageAsync(Png(60), "Screenshot");
        Field<PanelState>(panel, "_state").Select(PanelTab.Shelf);
        var list = Field<ListBox>(panel, "_shelfList");
        await WaitForAsync(() => list.Items.Count == 13, TimeSpan.FromSeconds(5)); panel.UpdateLayout();
        var clear = Descendants(panel).OfType<Button>().Single(x => AutomationProperties.GetName(x) == "Очистить полку");
        var add = Descendants(panel).OfType<Button>().Single(x => AutomationProperties.GetName(x) == "Добавить файлы");
        Check(clear.IsEnabled && clear.ActualWidth == 25 && clear.ActualHeight == 25 && clear.TranslatePoint(new Point(), panel).X < add.TranslatePoint(new Point(), panel).X, "clear shelf is available beside add with matching dimensions");
        var scroll = Descendants(list).OfType<ScrollViewer>().First();
        list.SelectedIndex = 1;
        async Task Wheel(int delta)
        {
            var wheel = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, delta) { RoutedEvent = Mouse.PreviewMouseWheelEvent };
            ((UIElement)list.Items[0]).RaiseEvent(wheel);
            Check(wheel.Handled, "wheel over a card is handled horizontally");
            await Task.Delay(60); panel.UpdateLayout();
        }
        await Wheel(-120); Check(scroll.HorizontalOffset > 0 && list.SelectedIndex == 1, "wheel down moves right without changing selection");
        var afterDown = scroll.HorizontalOffset;
        await Wheel(120); Check(scroll.HorizontalOffset < afterDown, "wheel up moves left");
        await Wheel(-30); Check(scroll.HorizontalOffset > 0 && scroll.HorizontalOffset < afterDown, "partial wheel delta supports fine scrolling");
        scroll.ScrollToRightEnd(); await Task.Delay(60); await Wheel(-120);
        Check(Math.Abs(scroll.HorizontalOffset - scroll.ScrollableWidth) < .75, "wheel clamps at the shelf edge");
        foreach (var dpi in new[] { 96, 144, 192 }) Snapshot(panel, output, "shelf-populated-" + dpi, dpi);
        File.Delete(image!.Path);
        await WaitForAsync(() => shelf.Items.Count == 12 && list.Items.Count == 12, TimeSpan.FromSeconds(5));
        Check(!Descendants(list).OfType<TextBlock>().Any(x => x.Text == "Файл недоступен"), "external screenshot deletion removes its card from the real UI");
        var retained = await shelf.AddImageAsync(Png(90), "Retained");
        await WaitForAsync(() => list.Items.Count == 13, TimeSpan.FromSeconds(5));
        Click(panel, "Очистить полку");
        await WaitForAsync(() => list.Items.Count == 0 && !clear.IsEnabled, TimeSpan.FromSeconds(5));
        Check(shelf.Items.Count == 0 && originals.All(File.Exists) && File.Exists(retained!.Path), "clear shelf removes cards while preserving originals and saved screenshots");
        Check(panel.Width == 520 && panel.Height == 180, "empty shelf preserves common panel dimensions");
        Snapshot(panel, output, "shelf-empty-144", 144);
    }

    private static async Task CheckUniformPanesAsync(MainWindow panel, string output)
    {
        var settings = Field<AppSettings>(panel, "_settings"); settings.ReducedMotion = true; settings.CaptureClipboard = true;
        var state = Field<PanelState>(panel, "_state");
        var list = Field<ListBox>(panel, "_shelfList");
        var cardType = typeof(MainWindow).GetNestedType("ShelfCard", System.Reflection.BindingFlags.NonPublic)!;
        for (var index = 0; index < 16; index++)
        {
            var path = Path.Combine(output, $"Документ {index + 1:00}.txt"); await File.WriteAllTextAsync(path, "Synthetic shelf fixture");
            list.Items.Add(Activator.CreateInstance(cardType, Guid.NewGuid(), path, false));
        }
        var history = new ClipboardHistoryService(new EncryptedClipboardRepository(Path.Combine(output, "uniform-history.db"), new DpapiDataProtector()), new() { ExplicitlyEnabled = true });
        await history.InitializeAsync(); await history.ClearAsync(); SetField(panel, "_history", history);
        for (var index = 0; index < 18; index++) await history.CaptureAsync(ClipboardPayload.FromText($"Текстовая запись {index + 1:00}"));
        typeof(MainWindow).GetMethod("RefreshHistory", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(panel, null);
        var text = string.Join("\n", Enumerable.Range(1, 25).Select(n => $"Строка {n}: длинный текст для проверки прокрутки."));
        SetField(panel, "_settingUp", true); Field<TextBox>(panel, "_input").Text = text; SetField(panel, "_settingUp", false);
        ((Cyclop.App.ViewModels.MainViewModel)panel.DataContext).OutputText = text;
        foreach (var tab in Enum.GetValues<PanelTab>())
        {
            state.Select(tab); panel.UpdateLayout();
            var height = tab == PanelTab.Settings ? 420 : 180;
            Check(panel.Width == 520 && panel.Height == height && panel.ActualWidth == 520 && panel.ActualHeight == height, tab + " uses expected bounds");
            foreach (var dpi in new[] { 96, 144, 192 }) Snapshot(panel, output, "uniform-" + tab.ToString().ToLowerInvariant() + "-" + dpi, dpi);
        }
        state.Select(PanelTab.Shelf); panel.UpdateLayout();
        var shelfScroll = Descendants(list).OfType<ScrollViewer>().First();
        var shelfViewport = Descendants(shelfScroll).OfType<ScrollContentPresenter>().First();
        var first = (FrameworkElement)list.Items[0]; var cardOrigin = first.TransformToAncestor(shelfViewport).Transform(new Point());
        Check(cardOrigin.Y + first.ActualHeight <= shelfViewport.ActualHeight + .75, "shelf card preview/name/status fit above the horizontal scrollbar");
        Check(shelfScroll.ScrollableWidth > 0, "shelf retains horizontal scrolling for many cards");
        shelfScroll.ScrollToRightEnd(); await Task.Delay(80); panel.UpdateLayout();
        var last = (FrameworkElement)list.Items[list.Items.Count - 1]; var lastOrigin = last.TransformToAncestor(shelfViewport).Transform(new Point());
        Check(lastOrigin.X >= -.75 && lastOrigin.X + last.ActualWidth <= shelfViewport.ActualWidth + .75, "last shelf card is reachable by scrolling");
        Snapshot(panel, output, "uniform-shelf-end-144", 144);
        state.Select(PanelTab.Clipboard); panel.UpdateLayout();
        var rows = Field<StackPanel>(panel, "_clipboardList");
        DependencyObject ancestor = rows;
        while (ancestor is not ScrollViewer) ancestor = VisualTreeHelper.GetParent(ancestor) ?? throw new InvalidOperationException("History scroll host is missing.");
        var historyScroll = (ScrollViewer)ancestor;
        Check(historyScroll.ScrollableHeight > 0, "text history retains vertical scrolling");
        historyScroll.ScrollToEnd(); await Task.Delay(80); Snapshot(panel, output, "uniform-history-end-144", 144);
        state.Select(PanelTab.Translate); panel.UpdateLayout();
        var input = Field<TextBox>(panel, "_input"); var result = Field<TextBox>(panel, "_output");
        Check(input.ActualHeight >= 60 && result.ActualHeight >= 60 && input.ExtentHeight > input.ViewportHeight && result.ExtentHeight > result.ViewportHeight, "both translation editors stay usable and scroll long text");
        input.ScrollToEnd(); result.ScrollToEnd(); await Task.Delay(80); Snapshot(panel, output, "uniform-translate-end-144", 144);
        state.Select(PanelTab.Settings); panel.UpdateLayout();
        var panes = Field<Dictionary<PanelTab, FrameworkElement>>(panel, "_panes"); var settingsScroll = (ScrollViewer)panes[PanelTab.Settings];
        Check(settingsScroll.ScrollableHeight > 0, "settings retain scrolling at the common height");
        settingsScroll.ScrollToEnd(); await Task.Delay(80); panel.UpdateLayout();
        var notices = Descendants(panel).OfType<Button>().Single(x => Equals(x.Content, "Лицензии и сторонние компоненты"));
        var noticesOrigin = notices.TransformToAncestor(settingsScroll).Transform(new Point());
        Check(noticesOrigin.Y >= -.75 && noticesOrigin.Y + notices.ActualHeight <= settingsScroll.ActualHeight + .75, "last settings action remains reachable");
        Snapshot(panel, output, "uniform-settings-end-144", 144);
    }
    private static async Task CheckVolumeHistoryAsync(MainWindow panel, string output)
    {
        var settings = Field<AppSettings>(panel, "_settings"); settings.ReducedMotion = true;
        settings.CaptureClipboard = true; settings.AutoSaveImages = true;
        var state = Field<PanelState>(panel, "_state");
        var media = SpotifySnapshot.Empty with { SourceAppUserModelId = "Spotify.exe", Title = "Название трека", Artist = "Исполнитель", CanPlay = true, CanPause = true, CanSkipPrevious = true, CanSkipNext = true, CanSeek = true, Duration = TimeSpan.FromMinutes(3), SeekEnd = TimeSpan.FromMinutes(3), Position = TimeSpan.FromSeconds(65) };
        typeof(MainWindow).GetMethod("ShowMedia", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(panel, [media]);
        var showVolume = typeof(MainWindow).GetMethod("ShowVolume", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        showVolume.Invoke(panel, [new SpotifyVolumeSnapshot(true, .63, false)]); state.Open(); panel.UpdateLayout();
        var volume = Field<Slider>(panel, "_volume");
        Check(panel.Width == 520 && panel.Height == 180, "Spotify uses compact native bounds");
        Check(volume.IsEnabled && Math.Abs(volume.Value - 63) < .01, "volume snapshot maps to the enabled 0–100 slider");
        Check(Descendants(panel).OfType<TextBlock>().Any(x => x.Text == "63%"), "volume percent is visible");
        var cover = Descendants(panel).OfType<Button>().Single(x => AutomationProperties.GetName(x) == "Открыть Spotify");
        Check(cover.ActualWidth == 96 && cover.ActualHeight == 96 && cover.Focusable && cover.IsEnabled, "96px cover is an accessible Spotify launch button");
        var mute = Field<Button>(panel, "_muteButton");
        Check(mute.IsEnabled && AutomationProperties.GetName(mute) == "Отключить звук Spotify", "speaker button exposes mute action");
        var previous = Descendants(panel).OfType<Button>().Single(x => AutomationProperties.GetName(x) == "Предыдущий трек");
        Check(Math.Abs(previous.TransformToAncestor(panel).Transform(new Point()).Y - mute.TransformToAncestor(panel).Transform(new Point()).Y) < .75, "transport and volume controls share one row");
        Check(Descendants(panel).OfType<TextBlock>().Any(x => x.Text == "1:05") && Descendants(panel).OfType<TextBlock>().Any(x => x.Text == "−1:55"), "elapsed and remaining time are shown separately");
        foreach (var dpi in new[] { 96, 144, 192 }) Snapshot(panel, output, "spotify-volume-" + dpi, dpi);
        var longMedia = media with { Title = new string('Ж', 120), Artist = new string('W', 120) };
        typeof(MainWindow).GetMethod("ShowMedia", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(panel, [longMedia]);
        Snapshot(panel, output, "spotify-long-title-144", 144);
        Check(panel.Width == 520 && Descendants(panel).OfType<TextBlock>().Single(x => x.Text == longMedia.Title).ActualWidth < 330, "long metadata remains constrained inside the compact player");
        typeof(MainWindow).GetMethod("ShowMedia", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(panel, [media]);
        showVolume.Invoke(panel, [new SpotifyVolumeSnapshot(true, .63, true)]);
        Check(volume.Value == 0 && volume.IsEnabled, "muted session shows zero and can be unmuted by adjustment");
        Check(mute.IsEnabled && AutomationProperties.GetName(mute) == "Включить звук Spotify" && mute.ToolTip.ToString()!.Contains("63%"), "mute remembers the native level and offers restore");
        Snapshot(panel, output, "spotify-muted-144", 144);
        showVolume.Invoke(panel, [new SpotifyVolumeSnapshot(true, 0, false)]);
        Check(mute.IsEnabled && Field<SpotifyMuteState>(panel, "_muteState").GetToggle(new(true, 0, false))?.RestoreVolume == .63, "zero slider can restore the prior observed level");
        showVolume.Invoke(panel, [SpotifyVolumeSnapshot.Unavailable]);
        Check(!volume.IsEnabled && Descendants(panel).OfType<TextBlock>().Any(x => x.Text == "—"), "no audio session disables volume without a fake percentage");
        Snapshot(panel, output, "spotify-volume-unavailable-144", 144);

        var history = new ClipboardHistoryService(new EncryptedClipboardRepository(Path.Combine(output, "synthetic-history.db"), new DpapiDataProtector()), new() { ExplicitlyEnabled = true });
        var shelf = new FileShelfService(Path.Combine(output, "synthetic-shelf.json"), Path.Combine(output, "managed"));
        await history.InitializeAsync(); await history.ClearAsync(); await shelf.LoadAsync();
        SetField(panel, "_history", history); SetField(panel, "_shelf", shelf);
        var png = Png(80); var file = ClipboardPayload.FromFiles([Path.Combine(output, "legacy-file.txt")]);
        await history.CaptureAsync(ClipboardPayload.FromPng(png)); await history.CaptureAsync(file);
        await history.CaptureAsync(ClipboardPayload.FromText("Сохранённый текст"));
        var store = typeof(MainWindow).GetMethod("StoreClipboardPayloadAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        Task Store(ClipboardPayload payload) => (Task)store.Invoke(panel, [payload, CancellationToken.None])!;
        await Store(ClipboardPayload.FromPng(png)); await Store(file);
        Check(history.Items.Count == 3, "new PNG/file payloads never enter text history");
        Check(shelf.Items.Count == 1 && File.Exists(shelf.Items[0].Path), "opted-in PNG is saved on shelf");
        await Store(ClipboardPayload.FromPng(png)); Check(shelf.Items.Count == 1, "repeated PNG remains one shelf image");
        await Store(ClipboardPayload.FromText("https://example.com"));
        settings.AutoSaveImages = false; await Store(ClipboardPayload.FromPng(Png(160)));
        Check(history.Items.Count == 4 && shelf.Items.Count == 1, "image-save opt-out leaves both stores unchanged");
        history.Options.IsPaused = true; await Store(ClipboardPayload.FromText("Не записывать")); history.Options.IsPaused = false;
        Check(history.Items.Count == 4, "text history pause is still respected");
        typeof(MainWindow).GetMethod("RefreshHistory", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(panel, null);
        state.Select(PanelTab.Clipboard); panel.UpdateLayout();
        var rows = Field<StackPanel>(panel, "_clipboardList");
        Check(rows.Children.Count == 2 && !Descendants(rows).OfType<Image>().Any(x => x.Source is BitmapSource), "legacy image/file rows are hidden, text and links remain");
        Check(history.Items.Count == 4, "legacy nontext records are preserved rather than silently deleted");
        Check(Descendants(panel).OfType<TextBlock>().Any(x => x.Text.StartsWith("Текстовых записей: 2")), "history counter reports visible text records");
        foreach (var dpi in new[] { 96, 144, 192 }) Snapshot(panel, output, "history-text-only-" + dpi, dpi);
        Field<TextBox>(panel, "_search").Text = "legacy-file"; panel.UpdateLayout();
        Check(rows.Children.Count == 0, "search never brings hidden legacy files back");

    }
    private static byte[] Png(byte value)
    {
        var pixels = Enumerable.Range(0, 4).SelectMany(_ => new byte[] { value, value, value, 255 }).ToArray();
        var bitmap = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32, null, pixels, 8);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream(); encoder.Save(stream); return stream.ToArray();
    }
    private static void SetField(MainWindow panel, string name, object value) => typeof(MainWindow).GetField(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(panel, value);
    private static async Task CheckMenusAsync(MainWindow panel, string output)
    {
        Field<AppSettings>(panel, "_settings").ReducedMotion = true;
        var state = Field<PanelState>(panel, "_state"); state.Open();
        var cardType = typeof(MainWindow).GetNestedType("ShelfCard", System.Reflection.BindingFlags.NonPublic)!;
        var card = Activator.CreateInstance(cardType, Guid.NewGuid(), Path.Combine(output, "menu-fixture.txt"), false)!;
        var shelf = (ContextMenu)typeof(MainWindow).GetMethod("ShelfContextMenu", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(panel, [card])!;
        var tray = Field<ContextMenu>(panel, "_trayMenu");
        foreach (var (name, menu) in new[] { ("shelf", shelf), ("tray", tray) })
        {
            menu.PlacementTarget = panel; menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            menu.IsOpen = true; await Task.Delay(100); menu.UpdateLayout();
            Check(menu.IsOpen && menu.ActualWidth >= 160, name + " menu uses the explicit dark template");
            state.PointerLeft(); Check(state.IsExpanded && state.ContextMenuOpen, name + " menu holds the panel open");
            foreach (var dpi in new[] { 96, 144, 192 }) Snapshot(menu, output, "menu-" + name + "-" + dpi, dpi);
            var bitmap = Render(menu, 96); var stride = bitmap.PixelWidth * 4; var pixels = new byte[stride * bitmap.PixelHeight];
            bitmap.CopyPixels(pixels, stride, 0);
            var noLightGutter = true;
            for (var x = 6; x < Math.Min(24, bitmap.PixelWidth); x++)
            {
                var light = 0;
                for (var y = 5; y < bitmap.PixelHeight - 5; y++)
                { var i = y * stride + x * 4; if (pixels[i] > 180 && pixels[i + 1] > 180 && pixels[i + 2] > 180 && pixels[i + 3] > 240) light++; }
                noLightGutter &= light < (bitmap.PixelHeight - 10) * .6;
            }
            Check(noLightGutter, name + " has no light gutter");
            if (name == "tray")
            {
                var pause = Field<MenuItem>(panel, "_pauseMenu"); pause.IsChecked = true; menu.UpdateLayout();
                var glyph = (FrameworkElement)pause.Template.FindName("CheckGlyph", pause);
                Check(glyph.IsVisible, "tray checkmark remains visible when checked");
                Snapshot(menu, output, "menu-tray-checked-144", 144);
                pause.IsChecked = false; Check(!glyph.IsVisible, "tray checkmark hides when unchecked");
            }
            menu.IsOpen = false;
            await WaitForAsync(() => !state.ContextMenuOpen, TimeSpan.FromSeconds(2));
            Check(!state.ContextMenuOpen, name + " closing releases the panel hold");
        }
    }
    private static void CheckControls(MainWindow panel, string output)
    {
        Field<AppSettings>(panel, "_settings").ReducedMotion = true;
        var state = Field<PanelState>(panel, "_state");
        var show = typeof(MainWindow).GetMethod("ShowMedia", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var media = SpotifySnapshot.Empty with { SourceAppUserModelId = "Spotify.exe", Title = "Название трека", Artist = "Исполнитель", CanPlay = true, CanPause = true, CanSkipPrevious = true, CanSkipNext = true };
        show.Invoke(panel, [media]); state.Open(); panel.UpdateLayout();
        Button Button(string name) => Descendants(panel).OfType<Button>().Single(x => AutomationProperties.GetName(x) == name);
        foreach (var name in new[] { "Предыдущий трек", "Воспроизвести / пауза", "Следующий трек" })
        { var button = Button(name); Check(button.ActualWidth == 24 && button.ActualHeight == 24 && button.IsEnabled, "equal compact media bounds: " + name); Centered(button); }
        Centered(Button("Настройки"));
        foreach (var dpi in new[] { 96, 144, 192 }) Snapshot(panel, output, "controls-play-" + dpi, dpi);
        show.Invoke(panel, [media with { IsPlaying = true }]); Snapshot(panel, output, "controls-pause-144", 144);
        show.Invoke(panel, [SpotifySnapshot.Empty]);
        Check(new[] { "Предыдущий трек", "Воспроизвести / пауза", "Следующий трек" }.All(x => !Button(x).IsEnabled), "media capabilities still disable unavailable controls");
        Snapshot(panel, output, "controls-disabled-144", 144);
        state.Select(PanelTab.Shelf); panel.UpdateLayout(); Centered(Button("Добавить файлы"));
        var settings = Button("Настройки"); var add = Button("Добавить файлы");
        Check(settings.ActualWidth == 25 && settings.ActualHeight == 25 && add.ActualWidth == 25 && add.ActualHeight == 25, "settings and add use equal square bounds");
        var settingsX = settings.TransformToAncestor(panel).Transform(new Point(settings.ActualWidth / 2, 0)).X;
        var addX = add.TransformToAncestor(panel).Transform(new Point(add.ActualWidth / 2, 0)).X;
        Check(Math.Abs(settingsX - addX) < .75, "settings and add align on the same vertical axis");
        foreach (var dpi in new[] { 96, 144, 192 }) Snapshot(panel, output, "controls-shelf-" + dpi, dpi);
        static void Centered(Button button)
        {
            var icon = (FrameworkElement)button.Content;
            var origin = icon.TransformToAncestor(button).Transform(new Point());
            Check(Math.Abs(origin.X + icon.ActualWidth / 2 - button.ActualWidth / 2) < .75 && Math.Abs(origin.Y + icon.ActualHeight / 2 - button.ActualHeight / 2) < .75,
                "icon centered without clipping: " + AutomationProperties.GetName(button));
        }
    }
    private static T Field<T>(MainWindow panel, string name) => (T)typeof(MainWindow).GetField(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(panel)!;
    private static void CheckCollapsed(MainWindow panel, string message)
    {
        var media = Field<SpotifySnapshot>(panel, "_media");
        var playing = media.HasSession && media.IsPlaying;
        Check(panel.Width == (playing ? MusicPill.PillWidth : 192) && panel.Height == (playing ? MusicPill.PillHeight : 12), message);
    }
    private static async Task CheckMusicPillAsync(MainWindow panel, string output)
    {
        // Deterministic snapshots before connecting GSMTC; no playback commands or system clipboard access.
        var state = Field<PanelState>(panel, "_state");
        var settings = Field<AppSettings>(panel, "_settings");
        var pill = Field<MusicPill>(panel, "_musicPill");
        var show = typeof(MainWindow).GetMethod("ShowMedia", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var playing = SpotifySnapshot.Empty with { SourceAppUserModelId = "Spotify.exe", Title = "Playback fixture", IsPlaying = true };
        void Media(SpotifySnapshot value) => show.Invoke(panel, [value]);
        settings.ReducedMotion = false; Media(playing); await Task.Delay(150);
        CheckCollapsed(panel, "playing music pill bounds");
        Check(pill.IsVisible && pill.IsAnimating == SystemParameters.ClientAreaAnimation, "playing indicator respects Windows animation setting");
        var transforms = Descendants(pill).OfType<Border>().Select(x => x.RenderTransform).OfType<ScaleTransform>().ToArray();
        var before = transforms.Select(x => x.ScaleY).ToArray(); await Task.Delay(275);
        Check(!SystemParameters.ClientAreaAnimation || transforms.Where((x, i) => Math.Abs(x.ScaleY - before[i]) > .01).Any(), "equalizer bars move during playback");
        foreach (var dpi in new[] { 96, 144, 192 }) Snapshot(panel, output, "music-playing-dpi-" + dpi, dpi);
        settings.ReducedMotion = true; Media(playing);
        Check(!pill.IsAnimating && transforms.All(x => !x.HasAnimatedProperties), "reduced motion removes animation clocks");
        settings.ReducedMotion = false; Media(playing);
        state.Open(); Check(!pill.IsAnimating, "unfolding panel stops equalizer clocks");
        if (SystemParameters.ClientAreaAnimation)
        {
            Check(Field<bool>(panel, "_musicMorphActive"), "music panel starts geometry transition");
            Snapshot(panel, output, "unfold-00"); await Task.Delay(180);
            var intermediate = Field<Grid>(panel, "_expanded").Opacity;
            Check(intermediate > 0 && intermediate < 1, "unfold reveals content progressively");
            Snapshot(panel, output, "unfold-01"); await Task.Delay(55); Snapshot(panel, output, "unfold-02");
            var beforeReverse = Field<PanelOutline>(panel, "_silhouette").Data.Bounds;
            state.Escape();
            Check(Field<bool>(panel, "_musicMorphActive"), "mid-flight collapse reverses current transition");
            Check(Math.Abs(Field<PanelOutline>(panel, "_silhouette").Data.Bounds.Width - beforeReverse.Width) < 1, "reversing retains the currently displayed geometry");
            await Task.Delay(450); CheckCollapsed(panel, "interrupted unfold returns to compact pill");
            Check(!Field<bool>(panel, "_musicMorphActive"), "interrupted transition releases its clock and expanded bounds");
            state.Open(); await Task.Delay(100); Snapshot(panel, output, "unfold-03");
        }
        await Task.Delay(450);
        Check(!pill.IsAnimating && !pill.IsVisible && !Field<bool>(panel, "_musicMorphActive"), "expanded panel settles and releases transition clocks");
        Snapshot(panel, output, "unfold-04");
        var outline = Field<PanelOutline>(panel, "_silhouette"); panel.UpdateLayout();
        outline.Data = outline.Data.CloneCurrentValue();
        Check(outline.IsMeasureValid, "updating outline geometry invalidates rendering without invalidating measure");
        panel.Width = 800; // Simulate the transient native/WPF bounds seen during DPI changes.
        state.Escape();
        Check(panel.Width == (SystemParameters.ClientAreaAnimation ? 520 : MusicPill.PillWidth), "closing ignores transient native bounds");
        await Task.Delay(450);
        CheckCollapsed(panel, "animated collapse restores compact native bounds");
        Check(pill.IsAnimating == SystemParameters.ClientAreaAnimation, "collapse resumes playback indicator");
        var instant = typeof(MainWindow).GetMethod("ChangePanelImmediately", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        instant.Invoke(panel, [(Action)state.Open]);
        Check(!Field<bool>(panel, "_musicMorphActive") && panel.Width == 520, "keyboard opening remains immediate");
        instant.Invoke(panel, [(Action)state.Escape]); CheckCollapsed(panel, "keyboard closing remains immediate");
        if (SystemParameters.ClientAreaAnimation)
        {
            state.Open(); await Task.Delay(50); settings.ReducedMotion = true;
            typeof(MainWindow).GetMethod("UpdateMotionPreferences", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(panel, null);
            Check(!Field<bool>(panel, "_musicMorphActive"), "enabling reduced motion finishes the current transition immediately");
            state.Escape(); CheckCollapsed(panel, "reduced motion changes native bounds immediately");
            settings.ReducedMotion = false; Media(playing);
        }
        state.Open(); await Task.Delay(50); state.Select(PanelTab.Shelf); panel.UpdateLayout();
        Check(panel.Width == 520 && panel.Height == 180, "switching panes during reveal keeps the same bounds");
        Check(Field<bool>(panel, "_musicMorphActive") == SystemParameters.ClientAreaAnimation, "switching panes does not interrupt the common-size reveal");
        await Task.Delay(450);
        state.Select(PanelTab.Spotify); panel.UpdateLayout();
        Check(panel.Width == 520 && panel.Height == 180, "switching back restores compact Spotify bounds");
        state.Escape(); await Task.Delay(450); state.Open(); await Task.Delay(70);
        state.ContextMenuOpen = true; state.Select(PanelTab.Settings); await Task.Delay(65);
        if (SystemParameters.ClientAreaAnimation)
        {
            Check(panel.Height > 180 && panel.Height < 420 && Field<bool>(panel, "_musicMorphActive"), "settings height and music outline can animate together");
            Snapshot(panel, output, "music-settings-intermediate-144", 144);
        }
        state.Escape(); await Task.Delay(70); state.ContextMenuOpen = true; state.Open(); await Task.Delay(500); panel.UpdateLayout();
        Check(panel.Height == 420 && !Field<bool>(panel, "_musicMorphActive") && !Field<bool>(panel, "_settingsResizeActive"), "reopening interrupted music/settings transitions reaches the taller bounds");
        Check(Math.Abs(Field<PanelOutline>(panel, "_silhouette").Data.Bounds.Height - 420) < 1, "settings outline matches its final native height");
        state.Select(PanelTab.Spotify); await Task.Delay(320);
        state.Escape(); await Task.Delay(450); state.Open(); await Task.Delay(65); Click(panel, "Полка");
        Check(!SystemParameters.ClientAreaAnimation || Field<bool>(panel, "_musicMorphActive") && Field<bool>(panel, "_paneTransitionActive"), "pane fade can run alongside the music reveal");
        await Task.Delay(220); Check(!Field<bool>(panel, "_paneTransitionActive"), "music reveal does not restart a completed pane fade");
        state.Escape(); await Task.Delay(450);
        state.Open(); await Task.Delay(40); state.Hide();
        Check(!panel.IsVisible && !pill.IsAnimating && !Field<bool>(panel, "_musicMorphActive"), "hidden panel stops music and transition clocks");
        state.Unhide(); CheckCollapsed(panel, "unhide returns directly to compact bounds");
        Check(!Field<bool>(panel, "_musicMorphActive"), "unhide never replays an old expanded transition");
        Media(playing with { IsPlaying = false });
        CheckCollapsed(panel, "pause restores collapsed strip");
        Check(!pill.IsAnimating && transforms.All(x => !x.HasAnimatedProperties), "pause removes animation clocks");
        Media(playing); Media(SpotifySnapshot.Empty);
        CheckCollapsed(panel, "missing Spotify restores collapsed strip");
        Check(!pill.IsAnimating, "session disappearance stops music animation");
    }
    private static void Check(bool value, string what) { if (!value) throw new InvalidOperationException("Failed: " + what); Console.WriteLine("PASS " + what); }
    private static string FindRepo(string start)
    { for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent) if (File.Exists(Path.Combine(dir.FullName, "Cyclop.sln"))) return dir.FullName; throw new DirectoryNotFoundException(); }
    private static async Task WaitForAsync(Func<bool> predicate, TimeSpan timeout)
    { var until = DateTime.UtcNow + timeout; while (!predicate()) { if (DateTime.UtcNow > until) throw new TimeoutException("UI condition did not settle before the timeout."); await Task.Delay(100); } }
    private static void Click(DependencyObject root, string name)
    { Descendants(root).OfType<Button>().Single(x => AutomationProperties.GetName(x) == name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    { for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) { var child = VisualTreeHelper.GetChild(root, i); yield return child; foreach (var next in Descendants(child)) yield return next; } }
    private static RenderTargetBitmap Render(FrameworkElement window, int dpi)
    {
        window.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth * dpi / 96d), (int)Math.Ceiling(window.ActualHeight * dpi / 96d), dpi, dpi, PixelFormats.Pbgra32);
        bitmap.Render(window);
        return bitmap;
    }
    private static void Snapshot(FrameworkElement window, string folder, string name, int dpi = 96)
    {
        var bitmap = Render(window, dpi);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(folder, name + ".png")); encoder.Save(file);
    }
}
