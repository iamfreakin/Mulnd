using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using SoriLab.Core;

namespace SoriLab.App;

public partial class MainWindow : Window
{
    private AudioClip? _clip;
    private string? _sourcePath;
    private EditSettings _settings = new(0, 1, 0, 0, 0);
    private EditSettings _initialSettings = new(0, 1, 0, 0, 0);
    private EditSettings? _exportedSettings;
    private readonly List<EditSettings> _history = new();
    private int _historyIndex;
    private bool _syncing;
    private bool _busy;
    private bool _closing;
    private bool _playing;
    private bool _previewReady;
    private bool _openingPreview;
    private string? _previewPath;
    private double _previewDuration;
    private int _revision;
    private int _previewRevision = -1;
    private string? _lastChangeKind;
    private DateTime _lastChangeTime;
    private readonly MediaPlayer _player = new() { Volume = 1 };
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromMilliseconds(30) };
    private readonly DispatcherTimer _peakDelay = new() { Interval = TimeSpan.FromMilliseconds(160) };
    private double _peak;
    private bool _smokeMode;

    public MainWindow()
    {
        InitializeComponent();
        Waveform.SelectionChanged += (start, end) => ApplyEdit(_settings with { StartFrame = start, EndFrame = end }, "selection");
        _clock.Tick += (_, _) => UpdatePlayhead();
        _clock.Start();
        _peakDelay.Tick += async (_, _) => { _peakDelay.Stop(); await RefreshPeakAsync(); };
        _player.MediaOpened += (_, _) =>
        {
            if (!_openingPreview || _closing) return;
            _openingPreview = false;
            _previewReady = true;
            _player.Play();
            _playing = true;
            PlayButton.Content = "Ⅱ  일시정지";
            SetStatus(BypassCheck.IsChecked == true ? "선택 구간을 효과 없이 듣고 있습니다." : "편집한 선택 구간을 듣고 있습니다.");
        };
        _player.MediaEnded += (_, _) =>
        {
            if (LoopCheck.IsChecked == true && _playing)
            {
                _player.Position = TimeSpan.Zero;
                _player.Play();
            }
            else StopPlayback(false);
        };
        _player.MediaFailed += (_, e) =>
        {
            StopPlayback(true);
            SetStatus("미리 듣기를 시작하지 못했습니다. 출력 장치와 WAV 파일을 확인해 주세요. " + e.ErrorException.Message, true);
        };
        PreviewKeyDown += OnShortcut;
        Closing += OnClosing;
        Closed += (_, _) =>
        {
            _closing = true;
            _revision++;
            _clock.Stop();
            _peakDelay.Stop();
            _player.Close();
            DeletePreview();
        };
    }

    public string SmokeTestStatus => $"파일={_clip?.Name};프레임={_clip?.FrameCount};선택={_settings.StartFrame}..{_settings.EndFrame};음량={_settings.GainDb};페이드={_settings.FadeInMs}/{_settings.FadeOutMs};이력={_history.Count};최대값={_peak:F5}";

    public void SetSmokeTestEdits()
    {
        _smokeMode = true;
        if (_clip is null) throw new InvalidOperationException("검수용 파일을 열지 못했습니다.");
        ApplyEdit(new EditSettings(_clip.SampleRate / 5, _clip.FrameCount - _clip.SampleRate / 5, -3, 70, 180), "smoke");
        OnUndo(this, new RoutedEventArgs());
        if (_settings.GainDb != 0) throw new InvalidOperationException("실행 취소 검증 실패");
        OnRedo(this, new RoutedEventArgs());
        if (_settings.GainDb != -3) throw new InvalidOperationException("다시 실행 검증 실패");
        _peak = AudioEditor.AnalyzePeak(AudioEditor.Render(_clip, _settings));
        UpdatePeakLabel();
        SetStatus("검수 준비 완료 · 구간 선택, 음량, 페이드와 실행 취소를 확인했습니다.");
    }

    public async Task RunInteractionChecksAsync(string outputBase)
    {
        _smokeMode = true;
        if (_clip is null) throw new InvalidOperationException("화면 검증용 음원이 없습니다.");
        var source = _clip;
        var baseline = _initialSettings;
        string invalidPath = outputBase + ".invalid.wav";
        File.WriteAllText(invalidPath, "잘못된 WAV 검증");
        await LoadFileAsync(invalidPath);
        if (!ReferenceEquals(source, _clip)) throw new InvalidOperationException("잘못된 파일을 열 때 기존 소리 보존 검증 실패");
        ApplyEdit(_settings with { StartFrame = 0, EndFrame = 1 }, "one-frame");
        if (!CommitTimes()) throw new InvalidOperationException("한 프레임 선택 검증 실패");
        StartInput.Text = "-1";
        if (CommitTimes()) throw new InvalidOperationException("잘못된 시작 시간 검증 실패");
        StartInput.Text = "0.25";
        EndInput.Text = "1.25";
        if (!CommitTimes() || _settings.EndFrame - _settings.StartFrame != _clip.SampleRate)
            throw new InvalidOperationException("숫자 구간 선택 검증 실패");
        for (int index = 0; index < 105; index++)
            ApplyEdit(_settings with { GainDb = index % 2 == 0 ? -2 : -4 }, "history-" + index);
        while (_historyIndex > 0) MoveHistory(-1);
        if (_initialSettings != baseline || _settings == baseline || _history.Count != 100)
            throw new InvalidOperationException("이력 한도와 원본 상태 보존 검증 실패");
        _settings = baseline;
        _history.Clear();
        _history.Add(baseline);
        _historyIndex = 0;
        _lastChangeKind = null;
        SyncControls();
        SetSmokeTestEdits();
        var audio = AudioEditor.Render(_clip, _settings);
        WavCodec.WritePcm16(outputBase + ".wav", audio);
        var reopened = WavCodec.Read(outputBase + ".wav");
        if (reopened.FrameCount != _settings.EndFrame - _settings.StartFrame || !ReferenceEquals(source, _clip))
            throw new InvalidOperationException("출력 길이와 원본 보존 검증 실패");

        // 자동 검증에서는 스피커로 소리를 내보내지 않습니다.
        _player.Volume = 0;
        OnPlay(this, new RoutedEventArgs());
        DateTime deadline = DateTime.UtcNow.AddSeconds(8);
        while (!_playing && DateTime.UtcNow < deadline) await Task.Delay(50);
        if (!_playing) throw new InvalidOperationException("Windows 미디어 재생 시작을 확인하지 못했습니다. " + StatusText.Text);
        await Task.Delay(250);
        if (_player.Position <= TimeSpan.Zero) throw new InvalidOperationException("미리 듣기 재생 위치가 진행되지 않았습니다.");
        OnPlay(this, new RoutedEventArgs());
        if (_playing) throw new InvalidOperationException("일시정지 검증 실패");
        OnPlay(this, new RoutedEventArgs());
        if (!_playing) throw new InvalidOperationException("이어 듣기 검증 실패");
        StopPlayback(false);
        if (_playing || _player.Position != TimeSpan.Zero) throw new InvalidOperationException("정지 검증 실패");
        BypassCheck.IsChecked = true;
        if (_previewReady) throw new InvalidOperationException("효과 비교용 미리 듣기 갱신 검증 실패");
        BypassCheck.IsChecked = false;
        _player.Volume = 1;
        SetStatus("검수 준비 완료 · 구간·음량·페이드·실행 취소·저장·재생 동작을 확인했습니다.");
    }

    private async void OnOpen(object sender, RoutedEventArgs e)
    {
        if (_busy || !CanReplaceEditing()) return;
        var dialog = new OpenFileDialog { Title = "편집할 WAV 파일 선택", Filter = "WAV 오디오|*.wav", Multiselect = false };
        if (dialog.ShowDialog(this) == true) await LoadFileAsync(dialog.FileName);
    }

    public async Task LoadFileAsync(string path)
    {
        if (_busy) return;
        SetBusy(true);
        StopPlayback(true);
        SetStatus("소리를 불러오고 있습니다…");
        try
        {
            var loaded = await Task.Run(() => WavCodec.Read(path));
            if (_closing) return;
            _clip = loaded;
            _sourcePath = Path.GetFullPath(path);
            _settings = new EditSettings(0, loaded.FrameCount, 0, 0, 0);
            _initialSettings = _settings;
            _exportedSettings = null;
            _history.Clear();
            _history.Add(_settings);
            _historyIndex = 0;
            _lastChangeKind = null;
            _revision++;
            FileNameText.Text = loaded.Name;
            FileInfoText.Text = $"{loaded.DurationSeconds:F3}초  ·  {loaded.SampleRate:N0} Hz  ·  {(loaded.Channels == 1 ? "모노" : "스테레오")}";
            Waveform.Clip = loaded;
            EmptyOverlay.Visibility = Visibility.Collapsed;
            SyncControls();
            await RefreshPeakAsync();
            SetStatus(loaded.DurationSeconds > 30 ? "불러왔습니다. 30초보다 긴 소리도 필요한 구간을 선택해 편집할 수 있어요." : "불러왔습니다. 파형을 드래그해 구간을 선택하세요.");
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or NotSupportedException or InvalidOperationException or OverflowException)
        {
            SetStatus("파일을 열지 못했습니다. " + ex.Message, true);
        }
        finally { if (!_closing) SetBusy(false); }
    }

    private bool CanReplaceEditing()
    {
        if (_clip is null || _settings == _initialSettings || _settings == _exportedSettings) return true;
        return MessageBox.Show(this, "내보내지 않은 편집 내용이 있습니다. 다른 파일을 열면 이 편집 내용은 사라집니다. 계속할까요?", "다른 소리 열기", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
    }

    private async void OnFileDrop(object sender, DragEventArgs e)
    {
        if (_busy || !e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        var paths = e.Data.GetData(DataFormats.FileDrop) as string[];
        if (paths is null || paths.Length != 1) { SetStatus("이번 검수본에서는 WAV 파일을 하나씩 열어 주세요.", true); return; }
        if (!CanReplaceEditing()) return;
        await LoadFileAsync(paths[0]);
    }

    private void ApplyEdit(EditSettings next, string kind)
    {
        if (_clip is null || _busy || next == _settings) return;
        StopPlayback(true);
        if (_historyIndex < _history.Count - 1) _history.RemoveRange(_historyIndex + 1, _history.Count - _historyIndex - 1);
        bool coalesce = kind.StartsWith("slider:", StringComparison.Ordinal) && kind == _lastChangeKind && (DateTime.UtcNow - _lastChangeTime).TotalMilliseconds < 700 && _historyIndex > 0;
        if (coalesce) _history[_historyIndex] = next;
        else
        {
            _history.Add(next);
            if (_history.Count > 100) _history.RemoveAt(0);
            _historyIndex = _history.Count - 1;
        }
        _lastChangeKind = kind;
        _lastChangeTime = DateTime.UtcNow;
        _settings = next;
        _revision++;
        SyncControls();
        _peakDelay.Stop();
        _peakDelay.Start();
        SetStatus("편집을 반영했습니다. 재생해서 확인하거나 WAV로 내보내세요.");
    }

    private void SyncControls()
    {
        if (_clip is null) return;
        _syncing = true;
        StartInput.Text = (_settings.StartFrame / (double)_clip.SampleRate).ToString("F3", CultureInfo.InvariantCulture);
        EndInput.Text = (_settings.EndFrame / (double)_clip.SampleRate).ToString("F3", CultureInfo.InvariantCulture);
        GainSlider.Value = _settings.GainDb;
        FadeInSlider.Value = _settings.FadeInMs;
        FadeOutSlider.Value = _settings.FadeOutMs;
        GainValue.Text = $"{_settings.GainDb:+0.0;-0.0;0.0} dB";
        FadeInValue.Text = $"{_settings.FadeInMs:0} ms";
        FadeOutValue.Text = $"{_settings.FadeOutMs:0} ms";
        SelectionLengthText.Text = $"{(_settings.EndFrame - _settings.StartFrame) / (double)_clip.SampleRate:F3}초";
        Waveform.SetSelection(_settings.StartFrame, _settings.EndFrame);
        Waveform.PlayheadSeconds = _settings.StartFrame / (double)_clip.SampleRate;
        PositionText.Text = FormatTime(0);
        UndoButton.IsEnabled = _historyIndex > 0;
        RedoButton.IsEnabled = _historyIndex < _history.Count - 1;
        _syncing = false;
    }

    private void OnSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_syncing || _clip is null || GainSlider is null || FadeInSlider is null || FadeOutSlider is null) return;
        ApplyEdit(_settings with { GainDb = GainSlider.Value, FadeInMs = FadeInSlider.Value, FadeOutMs = FadeOutSlider.Value }, "slider:" + ((Slider)sender).Name);
    }

    private bool CommitTimes()
    {
        if (_syncing || _clip is null || _busy) return true;
        // 1ms보다 짧은 선택도 그대로 재생할 수 있도록, 바뀌지 않은 표시값은 재해석하지 않습니다.
        if (StartInput.Text == (_settings.StartFrame / (double)_clip.SampleRate).ToString("F3", CultureInfo.InvariantCulture) &&
            EndInput.Text == (_settings.EndFrame / (double)_clip.SampleRate).ToString("F3", CultureInfo.InvariantCulture)) return true;
        if (!double.TryParse(StartInput.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double start) ||
            !double.TryParse(EndInput.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double end) ||
            !double.IsFinite(start) || !double.IsFinite(end) || start < 0 || end <= start || end > _clip.DurationSeconds + 0.00051)
        {
            SyncControls();
            SetStatus("시작과 끝은 초 단위 숫자로 입력해 주세요. 끝은 시작보다 크고, 파일 길이 이하여야 합니다.", true);
            return false;
        }
        int startFrame = Math.Clamp((int)Math.Round(start * _clip.SampleRate), 0, _clip.FrameCount - 1);
        int endFrame = Math.Clamp((int)Math.Round(end * _clip.SampleRate), startFrame + 1, _clip.FrameCount);
        // 표시를 위한 반올림 때문에 포커스 이동만으로 경계가 변하지 않게 합니다.
        if (StartInput.Text == (_settings.StartFrame / (double)_clip.SampleRate).ToString("F3", CultureInfo.InvariantCulture)) startFrame = _settings.StartFrame;
        if (EndInput.Text == (_settings.EndFrame / (double)_clip.SampleRate).ToString("F3", CultureInfo.InvariantCulture)) endFrame = _settings.EndFrame;
        if (endFrame <= startFrame) { SyncControls(); return false; }
        ApplyEdit(_settings with { StartFrame = startFrame, EndFrame = endFrame }, "time");
        return true;
    }

    private void OnTimeCommitted(object sender, KeyboardFocusChangedEventArgs e) => CommitTimes();
    private void OnTimeKeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) { CommitTimes(); Keyboard.ClearFocus(); e.Handled = true; } }
    private void OnSelectAll(object sender, RoutedEventArgs e) { if (_clip is not null) ApplyEdit(_settings with { StartFrame = 0, EndFrame = _clip.FrameCount }, "all"); }
    private void OnReset(object sender, RoutedEventArgs e) { if (_clip is not null) ApplyEdit(new EditSettings(0, _clip.FrameCount, 0, 0, 0), "reset"); }
    private void OnUndo(object sender, RoutedEventArgs e) => MoveHistory(-1);
    private void OnRedo(object sender, RoutedEventArgs e) => MoveHistory(1);

    private void MoveHistory(int delta)
    {
        int target = _historyIndex + delta;
        if (_busy || target < 0 || target >= _history.Count) return;
        StopPlayback(true);
        _historyIndex = target;
        _settings = _history[target];
        _revision++;
        _lastChangeKind = null;
        SyncControls();
        _peakDelay.Stop();
        _peakDelay.Start();
        SetStatus(delta < 0 ? "직전 편집을 취소했습니다." : "취소한 편집을 다시 적용했습니다.");
    }

    private async Task RefreshPeakAsync()
    {
        if (_clip is null) return;
        int revision = _revision;
        var clip = _clip;
        var settings = _settings;
        try
        {
            double peak = await Task.Run(() => AudioEditor.AnalyzePeak(AudioEditor.Render(clip, settings)));
            if (_closing || revision != _revision) return;
            _peak = peak;
            UpdatePeakLabel();
        }
        catch (ArgumentException ex)
        {
            if (_closing || revision != _revision) return;
            PeakText.Text = "처리 범위 초과";
            SetStatus(ex.Message, true);
        }
    }

    private void UpdatePeakLabel()
    {
        PeakText.Text = _peak <= 0 ? "무음" : $"{20 * Math.Log10(_peak):0.0} dBFS";
        PeakText.Foreground = Brush(_peak > 1 ? "#FFAA91" : "#BBEADB");
        PeakText.ToolTip = _peak > 1 ? "출력 한도를 넘었습니다. 음량을 낮추면 찌그러짐을 줄일 수 있어요." : "0 dBFS가 WAV 내보내기의 최대 음량입니다.";
        if (_peak > 1) SetStatus("음량이 출력 한도를 넘었습니다. 음량을 낮춘 뒤 내보내는 것을 권장합니다.", true);
    }

    private async void OnPlay(object sender, RoutedEventArgs e)
    {
        if (_clip is null || _busy || _openingPreview || !CommitTimes()) return;
        if (_playing)
        {
            _player.Pause(); _playing = false; PlayButton.Content = "▶  이어 듣기"; SetStatus("일시정지했습니다."); return;
        }
        if (_previewReady && _previewRevision == _revision)
        {
            _player.Play(); _playing = true; PlayButton.Content = "Ⅱ  일시정지"; return;
        }
        SetBusy(true);
        string? candidate = null;
        try
        {
            StopPlayback(true);
            var clip = _clip;
            var edit = BypassCheck.IsChecked == true ? _settings with { GainDb = 0, FadeInMs = 0, FadeOutMs = 0 } : _settings;
            candidate = Path.Combine(Path.GetTempPath(), "SoriLab-preview-" + Guid.NewGuid().ToString("N") + ".wav");
            string target = candidate;
            var rendered = await Task.Run(() => { var audio = AudioEditor.Render(clip, edit); WavCodec.WritePcm16(target, audio); return audio; });
            if (_closing) { TryDelete(candidate); return; }
            _previewPath = candidate;
            candidate = null;
            _previewDuration = rendered.DurationSeconds;
            _previewRevision = _revision;
            _openingPreview = true;
            _player.Open(new Uri(_previewPath));
            SetStatus("미리 듣기를 준비하고 있습니다…");
        }
        catch (Exception ex)
        {
            if (candidate is not null) TryDelete(candidate);
            StopPlayback(true);
            SetStatus("미리 듣기를 준비하지 못했습니다. " + ex.Message, true);
        }
        finally { if (!_closing) SetBusy(false); }
    }

    private void OnStop(object sender, RoutedEventArgs e) => StopPlayback(false);
    private void OnBypassChanged(object sender, RoutedEventArgs e) { if (_clip is not null) { StopPlayback(true); SetStatus("듣기 모드를 바꿨습니다. 다시 재생해 주세요. 내보내기에는 편집 효과가 적용됩니다."); } }

    private void StopPlayback(bool invalidate)
    {
        _player.Stop();
        _playing = false;
        _openingPreview = false;
        if (invalidate)
        {
            _player.Close();
            _previewReady = false;
            _previewRevision = -1;
            DeletePreview();
        }
        if (PlayButton is null) return;
        PlayButton.Content = "▶  재생";
        PositionText.Text = FormatTime(0);
        if (_clip is not null) Waveform.PlayheadSeconds = _settings.StartFrame / (double)_clip.SampleRate;
    }

    private void UpdatePlayhead()
    {
        if (!_previewReady || _clip is null || !_playing) return;
        double seconds = Math.Clamp(_player.Position.TotalSeconds, 0, _previewDuration);
        PositionText.Text = FormatTime(seconds);
        Waveform.PlayheadSeconds = _settings.StartFrame / (double)_clip.SampleRate + seconds;
    }

    private async void OnExport(object sender, RoutedEventArgs e)
    {
        if (_clip is null || _busy || !CommitTimes()) return;
        var dialog = new SaveFileDialog { Title = "편집한 소리 내보내기", Filter = "16비트 WAV 오디오|*.wav", DefaultExt = ".wav", AddExtension = true, FileName = Path.GetFileNameWithoutExtension(_clip.Name) + "_편집.wav", OverwritePrompt = true };
        if (dialog.ShowDialog(this) != true) return;
        if (string.Equals(Path.GetFullPath(dialog.FileName), _sourcePath, StringComparison.OrdinalIgnoreCase))
        {
            SetStatus("원본을 보존하기 위해 다른 파일 이름으로 저장해 주세요.", true); return;
        }
        SetBusy(true);
        StopPlayback(false);
        string? temporary = null;
        try
        {
            var clip = _clip;
            var settings = _settings;
            var audio = await Task.Run(() => AudioEditor.Render(clip, settings));
            double peak = AudioEditor.AnalyzePeak(audio);
            if (peak > 1 && MessageBox.Show(this, $"최대 음량이 출력 한도를 {20 * Math.Log10(peak):F1} dB 넘습니다. 그대로 저장하면 소리가 찌그러질 수 있어요.\n\n이 상태로 내보낼까요?", "음량 확인", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            { SetStatus("음량을 낮춘 뒤 다시 내보내 주세요."); return; }
            temporary = Path.Combine(Path.GetDirectoryName(dialog.FileName)!, ".sorilab-" + Guid.NewGuid().ToString("N") + ".tmp");
            string tempPath = temporary;
            await Task.Run(() => WavCodec.WritePcm16(tempPath, audio));
            File.Move(tempPath, dialog.FileName, true);
            temporary = null;
            _exportedSettings = settings;
            SetStatus($"저장했습니다 · {dialog.FileName}");
        }
        catch (Exception ex) { SetStatus("저장하지 못했습니다. " + ex.Message, true); }
        finally { if (temporary is not null) TryDelete(temporary); if (!_closing) SetBusy(false); }
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        OpenButton.IsEnabled = !busy;
        ExportButton.IsEnabled = !busy && _clip is not null;
        EditPanel.IsEnabled = !busy && _clip is not null;
        PlayButton.IsEnabled = !busy && _clip is not null;
        StopButton.IsEnabled = !busy && _clip is not null;
        Waveform.IsEnabled = !busy;
        BypassCheck.IsEnabled = !busy && _clip is not null;
    }

    private void SetStatus(string text, bool warning = false)
    {
        StatusText.Text = text;
        StatusText.Foreground = Brush(warning ? "#FFC3A8" : "#A8B3C9");
    }

    private void OnShortcut(object sender, KeyEventArgs e)
    {
        if (e.OriginalSource is TextBox) return;
        bool control = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        if (control && e.Key == Key.O) { OnOpen(this, new RoutedEventArgs()); e.Handled = true; }
        else if (control && e.Key == Key.Z) { MoveHistory(-1); e.Handled = true; }
        else if (control && e.Key == Key.Y) { MoveHistory(1); e.Handled = true; }
        else if (control && Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) && e.Key == Key.S) { OnExport(this, new RoutedEventArgs()); e.Handled = true; }
        else if (e.Key == Key.Space && e.OriginalSource is not Button && e.OriginalSource is not CheckBox) { OnPlay(this, new RoutedEventArgs()); e.Handled = true; }
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_smokeMode) return;
        if (_busy) { e.Cancel = true; SetStatus("현재 처리가 끝나면 창을 닫을 수 있습니다."); return; }
        if (!CommitTimes()) { e.Cancel = true; return; }
        if (_clip is null || _settings == _initialSettings || _settings == _exportedSettings) return;
        if (MessageBox.Show(this, "내보내지 않은 편집 내용이 있습니다. 이번 검수본은 작업 상태를 따로 저장하지 않습니다.\n\n내보내지 않고 닫을까요?", "편집 내용 확인", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) e.Cancel = true;
    }

    private void DeletePreview() { if (_previewPath is not null) { TryDelete(_previewPath); _previewPath = null; } }
    private static void TryDelete(string path) { try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    private static string FormatTime(double seconds) => TimeSpan.FromSeconds(seconds).ToString(@"mm\:ss\.fff");
    private static SolidColorBrush Brush(string color) => new((Color)ColorConverter.ConvertFromString(color));
}
