using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Cyclop.Core.Media;
using System.Windows.Media;

namespace Cyclop.App.ViewModels;

public partial class MainViewModel : ObservableObject
{
    [ObservableProperty] private string title = "SPOTIFY";
    [ObservableProperty] private string status = "";
    [ObservableProperty] private string trackTitle = "Spotify не найден";
    [ObservableProperty] private string artist = "Запустите Spotify и включите трек";
    [ObservableProperty] private ImageSource? artwork;
    [ObservableProperty] private bool canPlayPause;
    [ObservableProperty] private bool canPrevious;
    [ObservableProperty] private bool canNext;
    [ObservableProperty] private bool canSeek;
    [ObservableProperty] private bool playing;
    [ObservableProperty] private double progress;
    [ObservableProperty] private double duration = 1;
    [ObservableProperty] private string timeLabel = "0:00 / 0:00";
    [ObservableProperty] private string elapsedLabel = "0:00";
    [ObservableProperty] private string remainingLabel = "—";
    [ObservableProperty] private bool canChangeVolume;
    [ObservableProperty] private string volumeLabel = "—";
    [ObservableProperty] private string volumeHint = "Включите музыку в Spotify, чтобы Windows создала аудиосессию.";
    [ObservableProperty] private bool soundMuted;
    [ObservableProperty] private bool canToggleMute;
    [ObservableProperty] private string muteLabel = "Звук Spotify недоступен";
    [ObservableProperty] private string muteHint = "Включите музыку в Spotify.";
    [ObservableProperty] private string translateStatus = "Офлайн · RU ↔ EN";
    [ObservableProperty] private string outputText = "";
    [ObservableProperty] private bool translating;
    [ObservableProperty] private string clipboardHint = "История текста выключена. Включите запись в настройках.";
}
