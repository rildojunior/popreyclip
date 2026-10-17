using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using PoPreyClip.Config;
using PoPreyClip.Editing;
using PoPreyClip.Playback;
using PoPreyClip.Recording;

namespace PoPreyClip.App.ViewModels;

public enum CropMode
{
    None,
    Free,
    Wide,
    Vertical,
    Square,
    Portrait,
}

/// <summary>One audio track in the editor, with its volume slider.</summary>
public sealed class TrackVolumeViewModel(int index, string name, Action<int, int> changed) : ObservableObject
{
    private int _volume = 100;

    public string Name { get; } = name;

    public int Volume
    {
        get => _volume;
        set
        {
            if (SetProperty(ref _volume, Math.Clamp(value, 0, 200)))
            {
                OnPropertyChanged(nameof(VolumeText));
                changed(index, _volume);
            }
        }
    }

    public string VolumeText => $"{Volume}%";
}

/// <summary>State of the clip editor window: the part to keep, the crop, the volumes and how to save.</summary>
public sealed class EditorViewModel : ObservableObject, IDisposable
{
    /// <summary>Largest preview frame. The player scales the video down to this, so a 1440p clip stays cheap to show.</summary>
    public const int MaxPreviewWidth = 1280;
    public const int MaxPreviewHeight = 720;

    private readonly AppConfig _config;
    private CancellationTokenSource? _saveCancel;
    private ClipMedia? _media;
    private ClipPlayer? _player;
    private double _start;
    private double _end;
    private double _position;
    private CropMode _cropMode;
    private Rect _crop = new(0, 0, 1, 1);
    private bool _mergeAudio;
    private bool _overwrite;
    private bool _isLoading = true;
    private bool _isSaving;
    private double _saveProgress;
    private string _status = "";
    private BitmapSource? _thumbnails;

    public EditorViewModel(AppConfig config, string path)
    {
        _config = config;
        Path = path;
        SaveCommand = new AsyncRelayCommand(SaveAsync, () => !IsLoading && !IsSaving && _media is not null);
        CancelSaveCommand = new RelayCommand(() => _saveCancel?.Cancel(), () => IsSaving);
    }

    /// <summary>Raised after a successful save with the saved file and whether it replaced the original.</summary>
    public event Action<string, bool>? Saved;

    public string Path { get; }

    public string FileName => System.IO.Path.GetFileName(Path);

    public ClipMedia? Media => _media;

    public ClipPlayer? Player => _player;

    public ObservableCollection<TrackVolumeViewModel> Tracks { get; } = [];

    public ICommand SaveCommand { get; }

    public ICommand CancelSaveCommand { get; }

    public bool IsLoading
    {
        get => _isLoading;
        private set => SetProperty(ref _isLoading, value);
    }

    public bool IsSaving
    {
        get => _isSaving;
        private set
        {
            if (SetProperty(ref _isSaving, value))
            {
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    public double SaveProgress
    {
        get => _saveProgress;
        private set => SetProperty(ref _saveProgress, value);
    }

    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    public BitmapSource? Thumbnails
    {
        get => _thumbnails;
        private set => SetProperty(ref _thumbnails, value);
    }

    public double Duration => _media?.Duration ?? 0;

    public double SourceWidth => Math.Max(1, _media?.Width ?? 16);

    public double SourceHeight => Math.Max(1, _media?.Height ?? 9);

    public double Start
    {
        get => _start;
        set
        {
            if (SetProperty(ref _start, Math.Clamp(value, 0, Duration)))
            {
                OnRangeChanged();
            }
        }
    }

    public double End
    {
        get => _end;
        set
        {
            if (SetProperty(ref _end, Math.Clamp(value, 0, Duration)))
            {
                OnRangeChanged();
            }
        }
    }

    public double Position
    {
        get => _position;
        set
        {
            if (SetProperty(ref _position, value))
            {
                OnPropertyChanged(nameof(PositionText));
            }
        }
    }

    public string PositionText => $"{FormatTime(Position)} / {FormatTime(Duration)}";

    public string SelectionText => $"Trecho: {FormatTime(Start)} a {FormatTime(End)} · {FormatTime(End - Start)}";

    /// <summary>Where the saved clip really starts. Without a crop the video is copied from the keyframe before the start.</summary>
    public double RealStart => _media is null || IsCropping ? Start : _media.KeyframeAtOrBefore(Start);

    public bool ShowsRealStartHint => RealStart < Start - 0.05;

    public string RealStartHint =>
        $"Sem recorte da imagem, o vídeo é copiado sem perder qualidade e começa em {FormatTime(RealStart)}, " +
        "o ponto de corte mais próximo (faixa amarela).";

    public CropMode CropMode
    {
        get => _cropMode;
        set
        {
            if (!SetProperty(ref _cropMode, value))
            {
                return;
            }

            Crop = value == CropMode.None || value == CropMode.Free ? new Rect(0, 0, 1, 1) : CenteredCrop(AspectOf(value));
            OnPropertyChanged(nameof(IsCropping));
            OnPropertyChanged(nameof(CropAspect));
            OnRangeChanged();
        }
    }

    public bool IsCropping => CropMode != CropMode.None;

    public double CropAspect => AspectOf(CropMode);

    /// <summary>Crop normalized to the video (0 to 1).</summary>
    public Rect Crop
    {
        get => _crop;
        set
        {
            if (SetProperty(ref _crop, value))
            {
                OnPropertyChanged(nameof(CropText));
            }
        }
    }

    public string CropText => ToSourceCrop() is { } crop ? $"{crop.Width} × {crop.Height} pixels" : $"{SourceWidth:0} × {SourceHeight:0} pixels (imagem inteira)";

    public bool HasAudio => Tracks.Count > 0;

    /// <summary>Merging only makes sense when the clip has more than one track.</summary>
    public bool CanMergeAudio => (_media?.AudioTracks.Count ?? 0) > 1;

    public bool MergeAudio
    {
        get => _mergeAudio;
        set => SetProperty(ref _mergeAudio, value);
    }

    public bool Overwrite
    {
        get => _overwrite;
        set
        {
            if (SetProperty(ref _overwrite, value))
            {
                OnPropertyChanged(nameof(SaveAsNew));
            }
        }
    }

    public bool SaveAsNew
    {
        get => !_overwrite;
        set => Overwrite = !value;
    }

    /// <param name="previewWidth">Width of the video area on screen, in physical pixels; frames are decoded at this size.</param>
    public async Task LoadAsync(int previewWidth, int previewHeight)
    {
        try
        {
            _media = await ClipMedia.ProbeAsync(_config, Path);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or System.Text.Json.JsonException)
        {
            Log.Error($"Editor: não foi possível ler o clipe: {ex.Message}");
            Status = "Não foi possível abrir este clipe.";
            IsLoading = false;
            return;
        }

        _start = 0;
        _end = _media.Duration;
        foreach (var (track, i) in _media.SourceTracks.Select((t, i) => (t, i)))
        {
            Tracks.Add(new TrackVolumeViewModel(i, TrackName(track.Title), (index, volume) => _player?.SetVolume(index, volume)));
        }

        _player = new ClipPlayer(_config, _media,
            Math.Clamp(previewWidth, 480, MaxPreviewWidth), Math.Clamp(previewHeight, 270, MaxPreviewHeight));
        foreach (var name in new[]
                 {
                     nameof(Media), nameof(Player), nameof(Duration), nameof(SourceWidth), nameof(SourceHeight), nameof(Start), nameof(End),
                     nameof(HasAudio), nameof(CanMergeAudio), nameof(PositionText), nameof(CropText),
                 })
        {
            OnPropertyChanged(name);
        }

        OnRangeChanged();
        IsLoading = false;
        CommandManager.InvalidateRequerySuggested();
        Thumbnails = await LoadThumbnailsAsync(_config, _media);
    }

    private static string TrackName(string title) => title switch
    {
        _ when title.Equals("Mix", StringComparison.OrdinalIgnoreCase) => "Todo o áudio",
        _ => title,
    };

    /// <summary>Ten frames spread over the clip, side by side in one image (like Medal's film strip).</summary>
    private static async Task<BitmapSource?> LoadThumbnailsAsync(AppConfig config, ClipMedia media)
    {
        const int count = 10;
        const int height = 72;
        var width = Math.Max(2, (int)(height * media.Width / (double)Math.Max(1, media.Height)) & ~1);
        var args = new List<string> { "-hide_banner", "-loglevel", "error", "-nostdin" };
        for (var i = 0; i < count; i++)
        {
            args.AddRange(["-ss", Ffmpeg.Format(media.Duration * (i + 0.5) / count), "-i", media.Path]);
        }

        var graph = string.Concat(Enumerable.Range(0, count).Select(i => $"[{i}:v:0]scale={width}:{height},setsar=1[t{i}];")) +
                    string.Concat(Enumerable.Range(0, count).Select(i => $"[t{i}]")) + $"hstack=inputs={count}";
        args.AddRange(["-filter_complex", graph, "-frames:v", "1", "-f", "image2pipe", "-c:v", "bmp", "pipe:1"]);
        try
        {
            var bytes = await Task.Run(async () =>
            {
                var info = new System.Diagnostics.ProcessStartInfo(config.FfmpegExe)
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                foreach (var argument in args)
                {
                    info.ArgumentList.Add(argument);
                }

                using var process = System.Diagnostics.Process.Start(info)!;
                PoPreyClip.Native.ChildProcessJob.Attach(process);
                _ = process.StandardError.ReadToEndAsync();
                using var memory = new MemoryStream();
                await process.StandardOutput.BaseStream.CopyToAsync(memory);
                await process.WaitForExitAsync();
                return process.ExitCode == 0 ? memory.ToArray() : null;
            });
            if (bytes is null || bytes.Length == 0)
            {
                return null;
            }

            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = new MemoryStream(bytes);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception)
        {
            Log.Debug($"Editor: miniaturas não geradas: {ex.Message}");
            return null;
        }
    }

    /// <summary>The crop in source pixels (even numbers, as the encoders need), or null for the whole picture.</summary>
    public CropRect? ToSourceCrop()
    {
        if (_media is null || !IsCropping)
        {
            return null;
        }

        int Even(double value) => (int)Math.Round(value / 2) * 2;
        var x = Math.Clamp(Even(Crop.X * _media.Width), 0, _media.Width - 2);
        var y = Math.Clamp(Even(Crop.Y * _media.Height), 0, _media.Height - 2);
        var width = Math.Clamp(Even(Crop.Width * _media.Width), 2, _media.Width - x);
        var height = Math.Clamp(Even(Crop.Height * _media.Height), 2, _media.Height - y);
        return x == 0 && y == 0 && width >= _media.Width - 1 && height >= _media.Height - 1 ? null : new CropRect(x, y, width, height);
    }

    private static double AspectOf(CropMode mode) => mode switch
    {
        CropMode.Wide => 16 / 9.0,
        CropMode.Vertical => 9 / 16.0,
        CropMode.Square => 1,
        CropMode.Portrait => 4 / 5.0,
        _ => 0,
    };

    /// <summary>The largest centered crop with the given shape.</summary>
    private Rect CenteredCrop(double aspect)
    {
        var width = SourceWidth;
        var height = width / aspect;
        if (height > SourceHeight)
        {
            height = SourceHeight;
            width = height * aspect;
        }

        return new Rect((SourceWidth - width) / 2 / SourceWidth, (SourceHeight - height) / 2 / SourceHeight, width / SourceWidth, height / SourceHeight);
    }

    private void OnRangeChanged()
    {
        OnPropertyChanged(nameof(SelectionText));
        OnPropertyChanged(nameof(RealStart));
        OnPropertyChanged(nameof(ShowsRealStartHint));
        OnPropertyChanged(nameof(RealStartHint));
    }

    private async Task SaveAsync()
    {
        if (_media is null)
        {
            return;
        }

        _player?.Pause();
        var plan = new ClipEditPlan(Start, End, ToSourceCrop(), Tracks.Select(t => t.Volume).ToList(), MergeAudio && CanMergeAudio);
        if (Overwrite)
        {
            var warning = "O clipe original será substituído pela versão editada. Isso não pode ser desfeito.";
            if (plan.MergeAudio)
            {
                warning += "\n\nAs faixas de áudio separadas também serão juntadas, e não será mais possível ajustar o volume de cada uma.";
            }

            if (MessageBox.Show(warning + "\n\nContinuar?", "Substituir o clipe original", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            {
                return;
            }
        }

        IsSaving = true;
        SaveProgress = 0;
        Status = plan.Crop is null ? "Salvando..." : "Salvando (a imagem recortada precisa ser recodificada)...";
        _saveCancel = new CancellationTokenSource();
        try
        {
            var progress = new Progress<double>(p => SaveProgress = p);
            var saved = await ClipEditor.SaveAsync(_config, _media, plan, Overwrite, progress, _saveCancel.Token);
            Log.Info($"Clipe editado salvo: {saved}");
            Status = "";
            Saved?.Invoke(saved, Overwrite);
        }
        catch (OperationCanceledException)
        {
            Status = "Salvamento cancelado. O clipe original não foi alterado.";
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            Log.Error($"Editor: falha ao salvar: {ex.Message}");
            Status = "Não foi possível salvar. O clipe original não foi alterado. Detalhes no log.";
        }
        finally
        {
            _saveCancel.Dispose();
            _saveCancel = null;
            IsSaving = false;
        }
    }

    public static string FormatTime(double seconds)
    {
        seconds = Math.Max(0, seconds);
        var minutes = (int)(seconds / 60);
        var rest = seconds - minutes * 60;
        return $"{minutes}:{rest.ToString("00.0", CultureInfo.GetCultureInfo("pt-BR"))}";
    }

    public void Dispose()
    {
        _saveCancel?.Cancel();
        _player?.Dispose();
        _player = null;
    }
}
