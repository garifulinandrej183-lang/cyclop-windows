using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Cyclop.Core.Clipboard;
using Cyclop.Core.Data;
using Cyclop.Core.Media;
using Cyclop.Core.Panel;
using Cyclop.Core.Settings;
using Cyclop.Core.Shelf;
using Cyclop.Core.Translation;
using Cyclop.Windows.Clipboard;
using Cyclop.Windows.Media;
using Cyclop.Windows.Shelf;
using Cyclop.Windows.Shell;
using Microsoft.Win32;
using Cyclop.App.UI;

namespace Cyclop.App;

public sealed partial class MainWindow
{
    private SpotifyService? _spotify;
    private ClipboardMonitor? _clipboard;
    private ClipboardHistoryService? _history;
    private string? _historyError;
    private FileShelfService? _shelf;
    private bool _reconcilingShelf;
    private TranslationClient? _translator;
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _translateDebounce;
    private readonly SemaphoreSlim _modelInstall = new(1, 1);
    private readonly string _modelsRoot = System.IO.Path.Combine(AppSettings.DataRoot, "models");
    private SpotifySnapshot _media = SpotifySnapshot.Empty;
    private DateTimeOffset _mediaReceived;
    private long _translationGeneration;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(AppSettings.DataRoot);
        _shelf = new FileShelfService(System.IO.Path.Combine(AppSettings.DataRoot, "shelf.json"),
            System.IO.Path.Combine(AppSettings.DataRoot, "Screenshots"), new WpfImageFingerprint());
        _shelf.MaxManagedImages = _settings.ScreenshotLimit;
        _shelf.ItemsChanged += (_, _) => Dispatcher.BeginInvoke(RefreshShelf);
        try { await Task.Run(() => _shelf.LoadAsync(_lifetime.Token), _lifetime.Token); await Task.Run(() => _shelf.CleanupManagedImagesAsync(_lifetime.Token), _lifetime.Token); }
        catch (Exception) { SetStatus("Не удалось прочитать полку. Проверьте доступ к локальной папке данных."); }
        _history = new ClipboardHistoryService(new EncryptedClipboardRepository(
            System.IO.Path.Combine(AppSettings.DataRoot, "clipboard.db"), new DpapiDataProtector()));
        _history.ItemsChanged += (_, _) => Dispatcher.BeginInvoke(RefreshHistory);
        try { await Task.Run(() => _history.InitializeAsync(_lifetime.Token), _lifetime.Token); }
        catch (Exception) { _historyError = "История недоступна: файл повреждён, защищён другим пользователем Windows или превышает лимит памяти. Проверьте данные и перезапустите приложение."; _vm.ClipboardHint = _historyError; }
        _clipboard = new ClipboardMonitor(Dispatcher, () => !_disposed &&
            (_settings.CaptureClipboard && !_settings.ClipboardPaused && _historyError is null || _settings.AutoSaveImages),
            kind => kind == ClipboardPayloadKind.Text ? _settings.CaptureClipboard && !_settings.ClipboardPaused && _historyError is null
                : kind == ClipboardPayloadKind.Png && _settings.AutoSaveImages);
        _clipboard.Changed += async (_, e) =>
        {
            try
            {
                // The single Windows observer has already enforced clipboard privacy markers.
                await StoreClipboardPayloadAsync(e.Payload, _lifetime.Token);
            }
            catch (OperationCanceledException) { }
            catch (Exception) { _ = Dispatcher.BeginInvoke(() => SetStatus("Не удалось сохранить запись буфера. Проверьте свободное место.")); }
        };
        await ApplyCaptureSettingsAsync(); _clipboard.Start(); await ApplyWatchSettingsAsync();
        _spotify = new SpotifyService();
        _spotify.Changed += (_, snapshot) => Dispatcher.BeginInvoke(() => ShowMedia(snapshot));
        try { await _spotify.InitializeAsync(_lifetime.Token); ShowMedia(_spotify.Snapshot); }
        catch (Exception) { SetStatus("Windows не предоставила доступ к медиасессиям. Остальные модули доступны."); }
        await InitializeVolumeAsync();
        _translator = new TranslationClient(FindWorker(), _modelsRoot);
        RefreshShelf(); RefreshHistory();
        _vm.TranslateStatus = ModelsInstalled() ? "Модели установлены · перевод без сети" : "Установите модели в настройках · остальные модули доступны";
    }

    private async Task StoreClipboardPayloadAsync(ClipboardPayload payload, CancellationToken cancellationToken)
    {
        if (payload.Kind == ClipboardPayloadKind.Text && _history is not null && _historyError is null)
            await Task.Run(() => _history.CaptureAsync(payload, cancellationToken), cancellationToken);
        if (_settings.AutoSaveImages && payload.Kind == ClipboardPayloadKind.Png && payload.Png is { } bytes && _shelf is not null)
            await Task.Run(async () => await _shelf.AddImageAsync(bytes, "Clipboard", cancellationToken), cancellationToken);
    }

    private static string FindWorker()
    {
        var path = System.IO.Path.Combine(AppContext.BaseDirectory, "worker", "cyclop-translator.exe");
        if (File.Exists(path)) return path;
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var developmentPath = System.IO.Path.Combine(dir.FullName, "artifacts", "worker", "cyclop-translator", "cyclop-translator.exe");
            if (File.Exists(developmentPath)) return developmentPath;
        }
        return path;
    }
    private bool ModelsInstalled() => new[] { "en-ru", "ru-en" }.All(direction =>
        File.Exists(System.IO.Path.Combine(_modelsRoot, direction, "model", "model.bin")) && File.Exists(System.IO.Path.Combine(_modelsRoot, direction, "sentencepiece.model")));
    private void ShowMedia(SpotifySnapshot snapshot)
    {
        if (_disposed) return;
        var wasMusicPill = ShowsMusicPill;
        var priorArt = _media.ArtworkBytes;
        _media = snapshot; _mediaReceived = DateTimeOffset.UtcNow;
        _vm.TrackTitle = snapshot.HasSession ? (snapshot.Title.Length > 0 ? snapshot.Title : "Нет названия трека") : "Spotify не найден";
        _vm.Artist = snapshot.HasSession ? (snapshot.Artist.Length > 0 ? snapshot.Artist : "Метаданные не предоставлены Spotify") : "Запустите Spotify и включите трек";
        _vm.Playing = snapshot.IsPlaying; _vm.CanPlayPause = snapshot.HasSession && (snapshot.IsPlaying ? snapshot.CanPause : snapshot.CanPlay);
        _vm.CanPrevious = snapshot.CanSkipPrevious; _vm.CanNext = snapshot.CanSkipNext;
        _vm.CanSeek = snapshot.CanSeek && snapshot.SeekEnd > snapshot.SeekStart;
        _vm.Duration = Math.Max(1, snapshot.Duration.TotalSeconds);
        if (!priorArt.AsSpan().SequenceEqual(snapshot.ArtworkBytes.AsSpan()))
            _vm.Artwork = DecodeImage(snapshot.ArtworkBytes.IsDefaultOrEmpty ? null : snapshot.ArtworkBytes.ToArray(), 256);
        RefreshMediaProgress(); UpdateProgressTimer();
        if (wasMusicPill != ShowsMusicPill) RenderState(); else UpdateMusicPill();
    }
    private void RefreshMediaProgress()
    {
        var position = _media.Position;
        if (_media.IsPlaying) position += DateTimeOffset.UtcNow - _mediaReceived;
        var seconds = Math.Clamp(position.TotalSeconds, 0, Math.Max(0, _media.Duration.TotalSeconds));
        if (!_seekDragging) _seek.Value = seconds;
        _vm.TimeLabel = $"{FormatTime(TimeSpan.FromSeconds(seconds))} / {FormatTime(_media.Duration)}";
        _vm.ElapsedLabel = FormatTime(TimeSpan.FromSeconds(seconds));
        _vm.RemainingLabel = _media.Duration > TimeSpan.Zero ? "−" + FormatTime(TimeSpan.FromSeconds(Math.Max(0, _media.Duration.TotalSeconds - seconds))) : "—";
    }
    private static string FormatTime(TimeSpan value) => value.TotalHours >= 1 ? value.ToString(@"h\:mm\:ss") : value.ToString(@"m\:ss");
    private async Task OpenSpotifyWindowAsync()
    {
        try
        {
            var opened = await SpotifyLauncher.TryOpenAsync(_lifetime.Token);
            if (_disposed) return;
            if (opened) ChangePanelImmediately(_state.Escape);
            else SetStatus("Не удалось открыть окно Spotify. Проверьте, что приложение установлено и доступно.");
        }
        catch (OperationCanceledException) { }
        catch (Exception) { if (!_disposed) SetStatus("Не удалось открыть окно Spotify."); }
    }
    private void UpdateProgressTimer()
    { if (_state.IsExpanded && _state.Tab == PanelTab.Spotify && _media.IsPlaying && IsVisible) _progressTimer.Start(); else _progressTimer.Stop(); }
    private async Task ExecuteMediaAsync(string command)
    {
        if (_spotify is null) return;
        try
        {
            var success = command switch { "previous" => await _spotify.PreviousAsync(_lifetime.Token), "next" => await _spotify.NextAsync(_lifetime.Token), _ => await _spotify.PlayPauseAsync(_lifetime.Token) };
            _vm.Status = success ? "" : "Spotify отклонил действие или сессия уже недоступна.";
        }
        catch (OperationCanceledException) { }
        catch (Exception) { _vm.Status = "Действие недоступно. Проверьте Spotify."; }
    }
    private async Task SeekMediaAsync(double seconds)
    {
        if (_spotify is null || !_media.CanSeek) return;
            try { if (!await _spotify.SeekAsync(TimeSpan.FromSeconds(seconds), _lifetime.Token)) { SetStatus("Spotify не поддерживает перемотку этого содержимого."); RefreshMediaProgress(); } }
        catch (OperationCanceledException) { }
        catch (Exception) { SetStatus("Не удалось изменить позицию воспроизведения."); }
    }
    private void ResumeIntegrations()
    {
        _clipboard?.Stop(); _clipboard?.Start();
        _ = ApplyWatchSettingsAsync();
        if (_spotify is not null) _ = ReconnectMediaAsync();
        _ = ReconnectVolumeAsync();
    }
    private async Task ReconnectMediaAsync()
    { try { await _spotify!.InitializeAsync(_lifetime.Token); } catch (Exception) { SetStatus("Медиасессия недоступна после пробуждения."); } }

    private async Task ApplyCaptureSettingsAsync()
    {
        if (_pauseMenu is not null) _pauseMenu.IsChecked = _settings.ClipboardPaused;
        if (_pauseCheck is not null)
        {
            var priorSetup = _settingUp; _settingUp = true;
            try { _pauseCheck.IsChecked = _settings.ClipboardPaused; }
            finally { _settingUp = priorSetup; }
        }
        SaveSettings();
        if (_history is not null)
        {
            _history.Options.ExplicitlyEnabled = _settings.CaptureClipboard && _historyError is null;
            _history.Options.IsPaused = _settings.ClipboardPaused;
            _history.Options.SaveImagesToShelf = _settings.AutoSaveImages;
            _history.Options.MaxItems = _settings.HistoryLimit;
            _history.Options.MaxAge = TimeSpan.FromDays(_settings.HistoryDays);
            if (_historyError is null) try { await Task.Run(() => _history.PruneAsync(_lifetime.Token), _lifetime.Token); } catch (Exception) { _vm.ClipboardHint = "Не удалось применить лимиты истории."; }
        }
        if (_shelf is not null)
        {
            _shelf.MaxManagedImages = _settings.ScreenshotLimit;
            try { await Task.Run(() => _shelf.CleanupManagedImagesAsync(_lifetime.Token), _lifetime.Token); } catch (Exception) { SetStatus("Не удалось очистить старые управляемые PNG."); }
        }
        RefreshHistory();
    }
    private async Task ApplyWatchSettingsAsync()
    {
        if (_shelf is null) return;
        try
        {
            if (_settings.WatchScreenshots && Directory.Exists(_settings.ScreenshotFolder)) await _shelf.ConfigureWatchAsync(_settings.ScreenshotFolder, _lifetime.Token);
            else _shelf.StopWatch();
            SaveSettings();
        }
        catch (Exception) { SetStatus("Папка скриншотов недоступна. Выберите её заново в настройках."); }
    }
    private async Task AddShelfFilesAsync(IEnumerable<string> paths)
    { if (_shelf is null) return; try { await _shelf.AddFilesAsync(paths, _lifetime.Token); } catch (Exception) { SetStatus("Не удалось добавить файлы."); } }
    private async Task RemoveSelectedShelfAsync()
    {
        if (_shelf is null) return;
        foreach (var item in _shelfList.SelectedItems.Cast<ShelfCard>().ToArray()) await _shelf.RemoveAsync(item.Id, _lifetime.Token);
    }
    private void OpenManagedFolder() { if (_shelf is not null) OpenFile(_shelf.ManagedScreenshotFolder); }

    private async Task ClearShelfAsync()
    {
        if (_shelf is null) return;
        await Task.Run(() => _shelf.ClearCardsAsync(_lifetime.Token), _lifetime.Token);
        _vm.Status = ""; RefreshShelf();
    }

    private async Task ReconcileShelfAsync()
    {
        if (_shelf is null || _disposed || _reconcilingShelf) return;
        _reconcilingShelf = true;
        try { await Task.Run(() => _shelf.PruneMissingManagedImagesAsync(_lifetime.Token), _lifetime.Token); RefreshShelf(); }
        catch (OperationCanceledException) { }
        catch (Exception) { if (!_disposed) SetStatus("Не удалось обновить полку. Проверьте доступ к папке скриншотов."); }
        finally { _reconcilingShelf = false; }
    }

    private void RefreshShelf()
    {
        if (_shelf is null || _disposed) return;
        if (_clearShelf is not null) _clearShelf.IsEnabled = _shelf.Items.Count > 0;
        var selection = _shelfList.SelectedItems.Cast<ShelfCard>().Select(x => x.Id).ToHashSet();
        _shelfList.Items.Clear();
        foreach (var item in _shelf.Items)
        {
            var card = new ShelfCard(item.Id, item.Path, item.Kind == ShelfItemKind.ManagedImage);
            _shelfList.Items.Add(card); if (selection.Contains(item.Id)) _shelfList.SelectedItems.Add(card);
            card.ContextMenu = ShelfContextMenu(card);
        }
        _shelfHint.Text = _shelf.Items.Count == 0 ? "Перетащите файлы сюда · Ctrl — множественный выбор" : $"{_shelf.Items.Count} файлов · перетаскивание копирует оригиналы";
        _ = LoadShelfThumbnailsAsync(_shelfList.Items.Cast<ShelfCard>().ToArray());
    }
    private ContextMenu ShelfContextMenu(ShelfCard card)
    {
        var menu = new ContextMenu(); menu.Opened += (_, _) => _state.ContextMenuOpen = true;
        menu.Closed += (_, _) => { _state.ContextMenuOpen = false; if (!IsMouseOver) ScheduleLeave(); };
        AddMenu(menu, "Открыть", () => OpenFile(card.Path));
        AddMenu(menu, "Показать в папке", () =>
        { try { Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { "/select,", card.Path }, UseShellExecute = true }); } catch (Exception) { SetStatus("Не удалось открыть Проводник."); } });
        AddMenu(menu, "Копировать путь", async () => { if (_clipboard is not null) await _clipboard.RestoreAsync(ClipboardPayload.FromText(card.Path)); });
        AddMenu(menu, "Убрать карточку", async () => { if (_shelf is not null) await _shelf.RemoveAsync(card.Id, _lifetime.Token); });
        return menu;
    }
    private async Task LoadShelfThumbnailsAsync(ShelfCard[] cards)
    {
        foreach (var card in cards)
        {
            if (_disposed) break;
            var image = await Task.Run(() =>
            {
                try
                {
                    var extension = System.IO.Path.GetExtension(card.Path).ToLowerInvariant();
                    if (extension is not (".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif" or ".webp" or ".tif" or ".tiff")) return null;
                    var info = new FileInfo(card.Path); if (!info.Exists || info.Length > 32 * 1024 * 1024) return null;
                    return DecodeImage(File.ReadAllBytes(card.Path), 160);
                }
                catch (Exception) { return null; }
            });
            if (image is not null) card.SetThumbnail(image);
        }
    }
    private void RefreshHistory()
    {
        if (_history is null || _disposed) return;
        _clipboardList.Children.Clear();
        if (_historyError is not null) { _vm.ClipboardHint = _historyError; return; }
        foreach (var item in _history.Search(_search.Text).Where(x => x.Payload.Kind == ClipboardPayloadKind.Text))
        {
            var row = new DockPanel { LastChildFill = true };
            var delete = IconButton("close", "Удалить запись", async () => await Task.Run(() => _history.RemoveAsync(item.Id, _lifetime.Token), _lifetime.Token)); delete.Width = 24; delete.Height = 24; delete.VerticalAlignment = VerticalAlignment.Center; delete.Margin = new Thickness(0); delete.Background = Brushes.Transparent; DockPanel.SetDock(delete, Dock.Right); row.Children.Add(delete);
            var pin = IconButton("pin", item.IsPinned ? "Открепить" : "Закрепить", async () => { if (!await Task.Run(() => _history.PinAsync(item.Id, !item.IsPinned, _lifetime.Token), _lifetime.Token)) SetStatus("Достигнут лимит закреплений."); });
            pin.Width = 24; pin.Height = 24; pin.VerticalAlignment = VerticalAlignment.Center; pin.Margin = new Thickness(0); pin.Background = Brushes.Transparent; pin.Content = Icons.Create("pin", 15, Brush(item.IsPinned ? "#8CBEFF" : "#B1BDCC")); DockPanel.SetDock(pin, Dock.Right); row.Children.Add(pin);
            var restore = new Button { Style = (Style)FindResource("HistoryRowButtonStyle"), Background = Brushes.Transparent, HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(8, 4, 8, 4), ToolTip = "Восстановить в буфер обмена" };
            var content = new DockPanel();
            var text = Label(item.Preview, 11); text.TextTrimming = TextTrimming.CharacterEllipsis; text.VerticalAlignment = VerticalAlignment.Center; content.Children.Add(text); restore.Content = content;
            restore.Click += async (_, _) =>
            { try { if (_clipboard is not null) await _history.RestoreAsync(item.Id, (p, ct) => _clipboard.RestoreAsync(p, ct), _lifetime.Token); } catch (Exception) { _vm.ClipboardHint = "Буфер занят. Попробуйте восстановить запись ещё раз."; } };
            System.Windows.Automation.AutomationProperties.SetName(restore, "Восстановить запись буфера"); row.Children.Add(restore);
            _clipboardList.Children.Add(new Border { Background = (Brush)FindResource("SurfaceBrush"), CornerRadius = new CornerRadius(10), Padding = new Thickness(1), Margin = new Thickness(0, 0, 0, 4), Child = row });
        }
        var textCount = _history.Items.Count(x => x.Payload.Kind == ClipboardPayloadKind.Text);
        _vm.ClipboardHint = !_settings.CaptureClipboard ? "Запись текста выключена · включите её в настройках" : _settings.ClipboardPaused ? "Запись приостановлена" : $"Текстовых записей: {textCount} · DPAPI · поиск только в памяти";
    }
    private async Task ClearHistoryAsync()
    { if (_history is not null) { try { await Task.Run(() => _history.ClearAsync(_lifetime.Token), _lifetime.Token); } catch (Exception) { _vm.ClipboardHint = "Не удалось очистить историю."; } } }
    private static void AddMenu(ContextMenu menu, string label, Action action)
    { var item = new MenuItem { Header = label }; item.Click += (_, _) => action(); menu.Items.Add(item); }
    private void AddMenu(ContextMenu menu, string label, Func<Task> action) => AddMenu(menu, label, () => { _ = ExecuteUiTaskAsync(label, action); });

    private void ScheduleTranslation()
    {
        if (_settingUp || _disposed) return;
        _translateDebounce?.Cancel(); _translateDebounce?.Dispose(); _translateDebounce = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        var generation = Interlocked.Increment(ref _translationGeneration);
        _ = TranslateAfterDelayAsync(generation, _translateDebounce.Token);
    }
    private async Task TranslateAfterDelayAsync(long generation, CancellationToken token)
    {
        try
        {
            await Task.Delay(450, token);
            var text = _input.Text;
            if (text.Length > 100_000) { _vm.OutputText = ""; _vm.TranslateStatus = "Слишком длинный текст: больше 100000 знаков. Разбейте его на части; текст не обрезан."; return; }
            if (string.IsNullOrWhiteSpace(text)) { _vm.OutputText = ""; _vm.TranslateStatus = "Офлайн · RU ↔ EN"; return; }
            if (_translator is null || !ModelsInstalled()) { _vm.TranslateStatus = "Сначала установите обе модели в настройках"; return; }
            var direction = _direction.SelectedIndex switch { 1 => "en-ru", 2 => "ru-en", _ => text.Count(c => c is >= 'А' and <= 'я' or 'Ё' or 'ё') > text.Count(c => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z') ? "ru-en" : "en-ru" };
            _vm.Translating = true; _vm.TranslateStatus = $"Перевод… {(direction == "en-ru" ? "EN → RU" : "RU → EN")} · CPU";
            var result = await _translator.TranslateAsync(text, direction, token);
            if (generation == _translationGeneration && !token.IsCancellationRequested) { _vm.OutputText = result; _vm.TranslateStatus = "Готово · полностью офлайн"; }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (generation == _translationGeneration) _vm.TranslateStatus = ex is FileNotFoundException ? "Worker или модели отсутствуют. Проверьте установку." : "Ошибка перевода. Повторите ввод; при сбое worker перезапустится.";
        }
        finally { if (generation == _translationGeneration) _vm.Translating = false; }
    }
    private async Task CopyTranslationAsync()
    { if (_clipboard is not null && _vm.OutputText.Length > 0) { try { await _clipboard.RestoreAsync(ClipboardPayload.FromText(_vm.OutputText)); _vm.TranslateStatus = "Перевод скопирован"; } catch (Exception) { _vm.TranslateStatus = "Буфер занят. Повторите копирование."; } } }
    private async Task InstallModelsAsync()
    {
        if (!await _modelInstall.WaitAsync(0)) return;
        try
        {
            var installer = new ModelInstaller();
            var progress = new Progress<TranslationInstallProgress>(p => _vm.TranslateStatus = InstallProgressLabel(p));
            foreach (var model in ModelInstaller.Models) await Task.Run(() => installer.DownloadAsync(model.Id, _modelsRoot, progress, _lifetime.Token), _lifetime.Token);
            _vm.TranslateStatus = "Обе модели установлены · перевод готов без сети"; ScheduleTranslation();
        }
        catch (OperationCanceledException) { }
        catch (Exception) { _vm.TranslateStatus = "Не удалось установить модели. Проверьте сеть, место на диске или импортируйте пакеты."; }
        finally { _modelInstall.Release(); }
    }
    private async Task ImportModelAsync()
    {
        var picker = new OpenFileDialog { Filter = "Argos model|*.argosmodel", Title = "Импортировать закреплённую модель Argos 1.9", Multiselect = true };
        if (picker.ShowDialog(this) != true || !await _modelInstall.WaitAsync(0)) return;
        try
        {
            var installer = new ModelInstaller();
            foreach (var file in picker.FileNames)
            {
                var hash = await Task.Run(() => { using var stream = File.OpenRead(file); return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream)); });
                var model = ModelInstaller.Models.FirstOrDefault(x => string.Equals(x.Sha256, hash, StringComparison.OrdinalIgnoreCase));
                if (model is null) throw new InvalidDataException("Unsupported model checksum.");
                var progress = new Progress<TranslationInstallProgress>(p => _vm.TranslateStatus = InstallProgressLabel(p));
                await Task.Run(() => installer.ImportPackageAsync(model.Id, file, _modelsRoot, progress, _lifetime.Token), _lifetime.Token);
            }
            _vm.TranslateStatus = ModelsInstalled() ? "Обе модели установлены · перевод готов без сети" : "Модель импортирована. Установите второе направление."; ScheduleTranslation();
        }
        catch (OperationCanceledException) { }
        catch (Exception) { _vm.TranslateStatus = "Импорт не выполнен. Нужен оригинальный пакет Argos 1.9 с закреплённым SHA-256."; }
        finally { _modelInstall.Release(); }
    }
    private static BitmapSource? DecodeImage(byte[]? bytes, int width)
    {
        if (bytes is null || bytes.Length == 0 || bytes.Length > 32 * 1024 * 1024) return null;
        try
        {
            using var stream = new MemoryStream(bytes, false);
            var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.DecodePixelWidth = width; bitmap.StreamSource = stream; bitmap.EndInit(); bitmap.Freeze(); return bitmap;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or ArgumentException or FormatException or System.Runtime.InteropServices.ExternalException) { return null; }
    }
    private static string InstallProgressLabel(TranslationInstallProgress progress) =>
        $"{(progress.Stage == "download" ? "Скачивание модели" : "Проверка и установка модели")}… {(progress.Percent is double percent ? $"{percent:F0}%" : "")}";
    private void DisposeIntegrations()
    {
        _lifetime.Cancel(); _translateDebounce?.Cancel(); _volumeDebounce?.Cancel(); _volumeDebounce?.Dispose();
        _clipboard?.Dispose(); _spotify?.Dispose(); _spotifyVolume?.Dispose(); _translator?.Dispose();
        if (_shelf is not null) _shelf.DisposeAsync().AsTask().GetAwaiter().GetResult();
        if (_history is not null) _history.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
    private sealed class ShelfCard : StackPanel
    {
        public Guid Id { get; }
        public string Path { get; }
        private readonly Grid _preview;
        public ShelfCard(Guid id, string path, bool managed)
        {
            Id = id; Path = path; Width = 106; Height = 82; ToolTip = path;
            _preview = new Grid { Height = 48 }; _preview.Children.Add(Icons.Create("file", 30)); Children.Add(_preview);
            var label = Label(System.IO.Path.GetFileName(path), 11); label.TextTrimming = TextTrimming.CharacterEllipsis; label.Margin = new Thickness(5, 4, 5, 0); Children.Add(label);
            var exists = File.Exists(path) || Directory.Exists(path);
            var note = Label(exists ? managed ? "PNG полки" : "Ссылка на файл" : "Файл недоступен", 9, exists ? "#B1BDCC" : "#E5B976"); note.Margin = new Thickness(5, 2, 0, 0); Children.Add(note);
        }
        public void SetThumbnail(ImageSource image) { _preview.Children.Clear(); _preview.Children.Add(new Image { Source = image, Stretch = Stretch.Uniform, Height = 48 }); }
    }
}
