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

public partial class StudioWindow : Window
{
    private sealed record Snapshot(Guid Id, AudioTrack[] Tracks, double MasterDb);
    private Snapshot _state = new(Guid.NewGuid(), [], 0);
    private Guid _savedId;
    private Guid? _selectedId;
    private string? _projectPath;
    private readonly List<Snapshot> _history = [];
    private int _historyIndex;
    private bool _syncing, _busy, _closing, _allowClose, _handlingClose, _smokeMode;
    private bool _playing, _ready, _opening;
    private string? _previewPath;
    private double _previewLength;
    private int _playRequest;
    private readonly HashSet<string> _protectedSources = new(StringComparer.OrdinalIgnoreCase);
    private string? _coalesceKind;
    private DateTime _lastEdit;
    private CancellationTokenSource? _peakCancellation;
    private readonly MediaPlayer _player = new() { Volume = 1 };
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromMilliseconds(35) };
    private readonly DispatcherTimer _peakDelay = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private AudioTrack? Selected => _state.Tracks.FirstOrDefault(t => t.Id == _selectedId);
    private bool IsDirty => _state.Id != _savedId;
    public string SmokeTestStatus => $"트랙={_state.Tracks.Select(t => t.EffectiveLaneId).Distinct().Count()};클립={_state.Tracks.Length};선택={_selectedId};프로젝트수정={IsDirty};이력={_history.Count};전체음량={_state.MasterDb}";

    public StudioWindow()
    {
        InitializeComponent();
        InitializeWorkspaceShell();
        _savedId = _state.Id;
        _history.Add(_state);
        Timeline.TrackSelected += id => SelectTrack(id);
        Timeline.ClipMoved += MoveClip;
        Timeline.CursorChanged += SetEditCursor;
        TimelineScroll.SizeChanged += (_, _) => UpdateTimelineViewport();
        TimelineScroll.ScrollChanged += (_, _) => UpdateTimelineViewport();
        SourceWaveform.SelectionChanged += (start, end) => UpdateSelected(t => t with { Edit = t.Edit with { StartFrame = start, EndFrame = end } }, "trim");
        SourceWaveform.Focusable = true;
        SourceWaveform.PreviewMouseDown += (_, e) => { if (!CommitNumbers()) e.Handled = true; else SourceWaveform.Focus(); };
        _clock.Tick += (_, _) => UpdatePlayhead();
        _clock.Start();
        _peakDelay.Tick += async (_, _) => { _peakDelay.Stop(); await UpdatePeakAsync(); };
        _player.MediaOpened += (_, _) =>
        {
            if (!_opening || _closing) return;
            _opening = false; _ready = true; _playing = true;
            _player.Play(); PlayButton.Content = "Ⅱ 일시정지";
            Status("미리 듣는 중입니다. 편집을 바꾸면 재생이 멈춥니다.");
        };
        _player.MediaEnded += (_, _) =>
        {
            if (_playing && LoopCheck.IsChecked == true) { _player.Position = TimeSpan.Zero; _player.Play(); }
            else StopPlayback(false);
        };
        _player.MediaFailed += (_, e) => { StopPlayback(true); Status("재생하지 못했습니다. " + e.ErrorException.Message, true); };
        PreviewKeyDown += OnShortcut;
        Closing += OnClosing;
        Closed += (_, _) => { _closing = true; _clock.Stop(); _peakDelay.Stop(); _peakCancellation?.Cancel(); StopPlayback(true); };
        RefreshControls();
    }

    private void SelectTrack(Guid id)
    {
        if (_busy || !_state.Tracks.Any(t => t.Id == id)) return;
        if (!CommitNumbers()) return;
        if (_selectedId != id) StopPlayback(true);
        _selectedId = id;
        RefreshControls();
    }

    private void CommitState(AudioTrack[] tracks, double masterDb, string kind, Guid? select = null)
    {
        if (_busy) return;
        try { AudioMixer.Validate(tracks, 48000, masterDb); }
        catch (ArgumentException ex) { Status(ex.Message, true); RefreshControls(); return; }
        if (_state.MasterDb == masterDb && _state.Tracks.SequenceEqual(tracks)) return;
        StopPlayback(true);
        _peakCancellation?.Cancel();
        if (_historyIndex < _history.Count - 1) _history.RemoveRange(_historyIndex + 1, _history.Count - _historyIndex - 1);
        var next = new Snapshot(Guid.NewGuid(), tracks, masterDb);
        bool combine = kind.StartsWith("slider:", StringComparison.Ordinal) && _coalesceKind == kind && _state.Id != _savedId && (DateTime.UtcNow - _lastEdit).TotalMilliseconds < 700 && _historyIndex > 0;
        if (combine) _history[_historyIndex] = next;
        else { _history.Add(next); if (_history.Count > 100) _history.RemoveAt(0); _historyIndex = _history.Count - 1; }
        _state = next;
        _coalesceKind = kind;
        _lastEdit = DateTime.UtcNow;
        if (select.HasValue) _selectedId = select;
        if (!_state.Tracks.Any(t => t.Id == _selectedId)) _selectedId = _state.Tracks.LastOrDefault()?.Id;
        RefreshControls();
        SchedulePeak();
        Status("편집을 반영했습니다. 전체 재생으로 조합을 확인하세요.");
    }

    private void UpdateTrack(Guid id, Func<AudioTrack, AudioTrack> update, string kind)
    {
        var next = _state.Tracks.Select(t => t.Id == id ? update(t) : t).ToArray();
        CommitState(next, _state.MasterDb, kind);
    }
    private void UpdateSelected(Func<AudioTrack, AudioTrack> update, string kind) { if (Selected is { } selected) UpdateTrack(selected.Id, update, kind); }

    private void RefreshControls()
    {
        _syncing = true;
        Timeline.Tracks = _state.Tracks;
        Timeline.SelectedTrackId = _selectedId;
        TimelineEmpty.Visibility = _state.Tracks.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        TrackSummary.Text = $"{_state.Tracks.Select(t => t.EffectiveLaneId).Distinct().Count()}개 트랙 · {_state.Tracks.Length}개 클립";
        ProjectName.Text = (_projectPath is null ? "새 프로젝트" : Path.GetFileNameWithoutExtension(_projectPath)) + (IsDirty ? "  • 저장 전" : "  • 저장됨");
        Title = "소리공방 · " + ProjectName.Text;
        var selected = Selected;
        Inspector.IsEnabled = !_busy;
        TrackCommands.IsEnabled = selected is not null && !_busy;
        AddToTrackButton.IsEnabled = selected is not null && !_busy;
        ArrangementCommands.IsEnabled = !_busy;
        foreach (Control control in new Control[] { OffsetInput, StartInput, EndInput, GainSlider, FadeInSlider, FadeOutSlider, RateSlider, ReverseCheck }) control.IsEnabled = selected is not null;
        if (selected is not null)
        {
            SelectedName.Text = selected.Source.Name;
            SelectedInfo.Text = $"{selected.Source.SampleRate:N0} Hz · {(selected.Source.Channels == 1 ? "모노" : "스테레오")} · 원본 보존";
            SourceLabel.Text = "원본 구간 · " + selected.Source.Name;
            SourceWaveform.Clip = selected.Source;
            SourceWaveform.SetSelection(selected.Edit.StartFrame, selected.Edit.EndFrame);
            StartInput.Text = FormatSeconds(selected.Edit.StartFrame / (double)selected.Source.SampleRate);
            EndInput.Text = FormatSeconds(selected.Edit.EndFrame / (double)selected.Source.SampleRate);
            OffsetInput.Text = FormatSeconds(selected.OffsetSeconds);
            GainSlider.Value = selected.Edit.GainDb;
            FadeInSlider.Value = selected.Edit.FadeInMs;
            FadeOutSlider.Value = selected.Edit.FadeOutMs;
            RateSlider.Value = selected.PlaybackRate;
            GainValue.Text = $"{selected.Edit.GainDb:+0.0;-0.0;0.0} dB";
            FadeInValue.Text = $"{selected.Edit.FadeInMs:0} ms";
            FadeOutValue.Text = $"{selected.Edit.FadeOutMs:0} ms";
            RateValue.Text = $"{selected.PlaybackRate:0.00}×";
            PitchInfo.Text = $"음높이 {12 * Math.Log2(selected.PlaybackRate):+0.0;-0.0;0.0}반음 · 길이 {AudioMixer.GetDurationSeconds(selected):0.###}초";
            MuteCheck.IsChecked = selected.Muted;
            SoloCheck.IsChecked = selected.Solo;
            ReverseCheck.IsChecked = selected.Reverse;
        }
        else
        {
            SourceWaveform.Clip = null; SelectedName.Text = "클립을 선택하세요"; SourceLabel.Text = "선택한 클립의 원본 구간";
            StartInput.Text = EndInput.Text = OffsetInput.Text = "0";
        }
        UndoButton.IsEnabled = _historyIndex > 0;
        RedoButton.IsEnabled = _historyIndex < _history.Count - 1;
        MasterSlider.Value = _state.MasterDb;
        MasterValue.Text = $"{_state.MasterDb:0.0} dB";
        DurationText.Text = $"전체 {GetAudibleDuration():0.###}초";
        ExportButton.IsEnabled = _state.Tracks.Length > 0;
        PlayButton.IsEnabled = _state.Tracks.Length > 0 && !_busy;
        RefreshCursorControls();
        _syncing = false;
        RefreshWorkspaceShell();
    }

    private double GetAudibleDuration()
    {
        bool solo = _state.Tracks.Any(t => t.Solo);
        return _state.Tracks.Where(t => !t.Muted && (!solo || t.Solo)).Select(t => AudioMixer.GetEndSeconds(t)).DefaultIfEmpty(0).Max();
    }

    private bool CommitNumbers()
    {
        var track = Selected;
        if (_syncing || _busy || track is null) return true;
        string currentStart = FormatSeconds(track.Edit.StartFrame / (double)track.Source.SampleRate);
        string currentEnd = FormatSeconds(track.Edit.EndFrame / (double)track.Source.SampleRate);
        string currentOffset = FormatSeconds(track.OffsetSeconds);
        if (StartInput.Text == currentStart && EndInput.Text == currentEnd && OffsetInput.Text == currentOffset) return true;
        if (!ParseNumber(StartInput.Text, out double start) || !ParseNumber(EndInput.Text, out double end) || !ParseNumber(OffsetInput.Text, out double offset) ||
            start < 0 || end <= start || end > track.Source.DurationSeconds + 0.000001 || offset < 0 || offset > 120)
        { RefreshControls(); Status("시간은 0 이상의 초 단위 숫자로 입력하세요. 원본 끝은 시작보다 크고 파일 길이 이하여야 합니다.", true); return false; }
        int first = StartInput.Text == currentStart ? track.Edit.StartFrame : (int)Math.Round(start * track.Source.SampleRate);
        int last = EndInput.Text == currentEnd ? track.Edit.EndFrame : Math.Min(track.Source.FrameCount, (int)Math.Round(end * track.Source.SampleRate));
        if (last <= first) { RefreshControls(); Status("최소 한 샘플 이상을 선택해 주세요.", true); return false; }
        var next = track with { OffsetSeconds = OffsetInput.Text == currentOffset ? track.OffsetSeconds : offset, Edit = track.Edit with { StartFrame = first, EndFrame = last } };
        try { AudioMixer.Validate(_state.Tracks.Select(t => t.Id == track.Id ? next : t).ToArray(), 48000, _state.MasterDb); }
        catch (ArgumentException ex) { RefreshControls(); Status(ex.Message, true); return false; }
        UpdateSelected(_ => next, "numbers");
        return true;
    }

    private void OnNumbersCommitted(object sender, KeyboardFocusChangedEventArgs e) => CommitNumbers();
    private void OnNumberKeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) { CommitNumbers(); e.Handled = true; } }
    private void OnSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_syncing || Selected is null || RateSlider is null) return;
        UpdateSelected(t => t with { PlaybackRate = RateSlider.Value, Edit = t.Edit with { GainDb = GainSlider.Value, FadeInMs = FadeInSlider.Value, FadeOutMs = FadeOutSlider.Value } }, "slider:" + ((Slider)sender).Name + _selectedId);
    }
    private void OnMasterChanged(object sender, RoutedPropertyChangedEventArgs<double> e) { if (!_syncing) CommitState(_state.Tracks, MasterSlider.Value, "slider:master"); }
    private void OnTrackSwitch(object sender, RoutedEventArgs e)
    {
        if (_syncing || Selected is null || ReverseCheck is null) return;
        if (sender == ReverseCheck) UpdateSelected(t => t with { Reverse = ReverseCheck.IsChecked == true }, "reverse");
        else
        {
            Guid lane = Selected.EffectiveLaneId;
            CommitState(_state.Tracks.Select(t => t.EffectiveLaneId == lane ? t with { Muted = MuteCheck.IsChecked == true, Solo = SoloCheck.IsChecked == true } : t).ToArray(), _state.MasterDb, "lane-switch");
        }
    }
    private void OnSelectAll(object sender, RoutedEventArgs e) => UpdateSelected(t => t with { Edit = t.Edit with { StartFrame = 0, EndFrame = t.Source.FrameCount } }, "all");
    private void OnReset(object sender, RoutedEventArgs e) => UpdateSelected(t => t with { Edit = new EditSettings(0, t.Source.FrameCount, 0, 0, 0), PlaybackRate = 1, Reverse = false }, "reset");
    private void OnRemove(object sender, RoutedEventArgs e) { if (_selectedId.HasValue) CommitState(_state.Tracks.Where(t => t.Id != _selectedId).ToArray(), _state.MasterDb, "remove"); }
    private void OnDuplicate(object sender, RoutedEventArgs e)
    {
        if (!CommitNumbers() || Selected is not { } track) return;
        var copy = track with { Id = Guid.NewGuid(), LaneId = track.EffectiveLaneId, OffsetSeconds = AudioMixer.GetEndSeconds(track) };
        CommitState([.. _state.Tracks, copy], _state.MasterDb, "duplicate", copy.Id);
    }
    private void OnUndo(object sender, RoutedEventArgs e) => MoveHistory(-1);
    private void OnRedo(object sender, RoutedEventArgs e) => MoveHistory(1);
    private void MoveHistory(int direction)
    {
        int next = _historyIndex + direction;
        if (_busy || next < 0 || next >= _history.Count) return;
        StopPlayback(true); _peakCancellation?.Cancel();
        _historyIndex = next; _state = _history[next]; _coalesceKind = null;
        if (!_state.Tracks.Any(t => t.Id == _selectedId)) _selectedId = _state.Tracks.LastOrDefault()?.Id;
        RefreshControls(); SchedulePeak(); Status(direction < 0 ? "편집을 취소했습니다." : "편집을 다시 적용했습니다.");
    }

    public Task LoadFileAsync(string path) => AddAudioAsync([path]);
    private async Task AddAudioAsync(string[] paths, Guid? laneId = null)
    {
        if (_busy || paths.Length == 0 || !CommitNumbers()) return;
        SetBusy(true); StopPlayback(true); Status("WAV 파일을 불러오고 있습니다…");
        try
        {
            if (_state.Tracks.Length + paths.Length > AudioMixer.MaximumClips) throw new ArgumentException("클립은 최대 128개까지 추가할 수 있습니다.");
            if (!laneId.HasValue && _state.Tracks.Select(t => t.EffectiveLaneId).Distinct().Count() + paths.Length > AudioMixer.MaximumTracks) throw new ArgumentException("트랙은 최대 32개까지 추가할 수 있습니다.");
            var lane = _state.Tracks.FirstOrDefault(t => t.EffectiveLaneId == laneId);
            double insertAt = laneId.HasValue ? _editCursor : 0;
            var existingSources = new HashSet<AudioClip>(_state.Tracks.Select(t => t.Source), ReferenceEqualityComparer.Instance);
            long sourceSamples = existingSources.Sum(source => (long)source.Samples.Length);
            var added = await Task.Run(() =>
            {
                var result = new List<AudioTrack>();
                foreach (string path in paths)
                {
                    var source = WavCodec.Read(path);
                    sourceSamples += source.Samples.Length;
                    if (sourceSamples > 32_000_000) throw new ArgumentException("원본 데이터 합계가 한도를 넘습니다. 더 짧은 소리로 나누어 추가해 주세요.");
                    int end = (int)Math.Min(source.FrameCount, source.SampleRate * 120L);
                    var addedClip = new AudioTrack(Guid.NewGuid(), source, new EditSettings(0, end, 0, 0, 0), insertAt, Muted: lane?.Muted ?? false, Solo: lane?.Solo ?? false, SourcePath: Path.GetFullPath(path), LaneId: laneId);
                    result.Add(addedClip);
                    if (laneId.HasValue) insertAt = AudioMixer.GetEndSeconds(addedClip);
                }
                return result.ToArray();
            });
            foreach (var track in added) if (track.SourcePath is not null) _protectedSources.Add(track.SourcePath);
            SetBusy(false);
            CommitState([.. _state.Tracks, .. added], _state.MasterDb, "import", added[^1].Id);
        }
        catch (Exception ex) when (IsExpected(ex)) { Status("불러오지 못했습니다. " + ex.Message, true); }
        finally { SetBusy(false); }
    }
    private async void OnAddFiles(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "조합할 WAV 파일 추가", Filter = "WAV 오디오|*.wav", Multiselect = true };
        if (!_busy && dialog.ShowDialog(this) == true) await AddAudioAsync(dialog.FileNames);
    }
    private async void OnFileDrop(object sender, DragEventArgs e)
    {
        if (!_busy && e.Data.GetData(DataFormats.FileDrop) is string[] paths)
        {
            if (paths.Length == 1 && Path.GetExtension(paths[0]).Equals(".sorilab", StringComparison.OrdinalIgnoreCase)) await OpenProjectAsync(paths[0]);
            else await AddAudioAsync(paths);
        }
    }

    private AudioClip RenderSnapshot(Snapshot snapshot, Guid? selected, bool bypass, CancellationToken token = default)
    {
        var tracks = selected.HasValue ? snapshot.Tracks.Where(t => t.Id == selected).Select(t => t with { OffsetSeconds = 0, Muted = false, Solo = false }).ToArray() : snapshot.Tracks;
        if (bypass) tracks = tracks.Select(t => t with { Edit = t.Edit with { GainDb = 0, FadeInMs = 0, FadeOutMs = 0 } }).ToArray();
        return AudioMixer.Render(tracks, 48000, bypass ? 0 : snapshot.MasterDb, token);
    }
    private void SchedulePeak() { _peakDelay.Stop(); _peakDelay.Start(); }
    private async Task UpdatePeakAsync()
    {
        _peakCancellation?.Cancel();
        var cancellation = new CancellationTokenSource(); _peakCancellation = cancellation;
        var snapshot = _state;
        try
        {
            double peak = await Task.Run(() => AudioEditor.AnalyzePeak(RenderSnapshot(snapshot, null, false, cancellation.Token)), cancellation.Token);
            if (_closing || cancellation.IsCancellationRequested || snapshot.Id != _state.Id) return;
            PeakText.Text = peak == 0 ? "무음" : $"{20 * Math.Log10(peak):0.0} dBFS";
            PeakText.Foreground = Brush(peak > 1 ? "#FFB6A3" : "#B8F1DD");
            if (peak > 1) Status("합친 음량이 출력 한도를 넘었습니다. 전체 음량을 낮추면 찌그러짐을 줄일 수 있어요.", true);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (IsExpected(ex)) { if (snapshot.Id == _state.Id && !cancellation.IsCancellationRequested) { PeakText.Text = "—"; Status(ex.Message, true); } }
        finally { if (ReferenceEquals(_peakCancellation, cancellation)) _peakCancellation = null; cancellation.Dispose(); }
    }

    private async void OnPlay(object sender, RoutedEventArgs e) => await PlayAsync();
    private async Task PlayAsync()
    {
        if (_busy || _opening || !CommitNumbers()) return;
        if (_playing) { _player.Pause(); _playing = false; PlayButton.Content = "▶ 이어 듣기"; return; }
        if (_ready) { _player.Play(); _playing = true; PlayButton.Content = "Ⅱ 일시정지"; return; }
        SetBusy(true); _peakCancellation?.Cancel(); StopPlayback(true);
        int request = ++_playRequest;
        string candidate = Path.Combine(Path.GetTempPath(), "SoriLab-" + Guid.NewGuid().ToString("N") + ".wav");
        try
        {
            var snapshot = _state;
            Guid? selected = SelectedOnlyCheck.IsChecked == true ? _selectedId : null;
            bool bypass = BypassCheck.IsChecked == true;
            var rendered = await Task.Run(() => { var audio = RenderSnapshot(snapshot, selected, bypass); WavCodec.WritePcm16(candidate, audio); return audio; });
            if (request != _playRequest || _closing) { TryDelete(candidate); return; }
            _previewPath = candidate; _previewLength = rendered.DurationSeconds; _opening = true;
            _player.Open(new Uri(candidate)); Status("미리 듣기를 준비하고 있습니다…");
        }
        catch (Exception ex) when (IsExpected(ex)) { TryDelete(candidate); StopPlayback(true); Status("재생을 준비하지 못했습니다. " + ex.Message, true); }
        finally { SetBusy(false); }
    }
    private void OnStop(object sender, RoutedEventArgs e) { StopPlayback(false); Status("재생을 정지했습니다."); }
    private void OnListeningChanged(object sender, RoutedEventArgs e) { if (!_syncing) StopPlayback(true); }
    private void StopPlayback(bool invalidate)
    {
        _playRequest++;
        _player.Stop(); _playing = false; _opening = false;
        if (invalidate) { _player.Close(); _ready = false; if (_previewPath is not null) { TryDelete(_previewPath); _previewPath = null; } }
        if (PlayButton is null) return;
        PlayButton.Content = SelectedOnlyCheck.IsChecked == true ? "▶ 선택 재생" : "▶ 전체 재생";
        PositionText.Text = "00:00.000"; Timeline.PlayheadSeconds = 0;
        if (Selected is { } selected) SourceWaveform.PlayheadSeconds = selected.Edit.StartFrame / (double)selected.Source.SampleRate;
    }
    private void UpdatePlayhead()
    {
        if (!_ready || !_playing) return;
        double seconds = Math.Clamp(_player.Position.TotalSeconds, 0, _previewLength);
        PositionText.Text = TimeSpan.FromSeconds(seconds).ToString(@"mm\:ss\.fff");
        Timeline.PlayheadSeconds = seconds + (SelectedOnlyCheck.IsChecked == true ? Selected?.OffsetSeconds ?? 0 : 0);
        if (Selected is { } selected)
        {
            double relative = SelectedOnlyCheck.IsChecked == true ? seconds : seconds - selected.OffsetSeconds;
            double frame = selected.Reverse ? selected.Edit.EndFrame - 1 - relative * selected.Source.SampleRate * selected.PlaybackRate : selected.Edit.StartFrame + relative * selected.Source.SampleRate * selected.PlaybackRate;
            SourceWaveform.PlayheadSeconds = Math.Clamp(frame, selected.Edit.StartFrame, selected.Edit.EndFrame) / selected.Source.SampleRate;
        }
    }

    private async void OnExport(object sender, RoutedEventArgs e)
    {
        if (_busy || _state.Tracks.Length == 0 || !CommitNumbers()) return;
        var dialog = new SaveFileDialog { Title = "들리는 트랙을 하나의 WAV로 저장", Filter = "16비트 WAV|*.wav", DefaultExt = ".wav", AddExtension = true, FileName = "효과음_믹스.wav" };
        if (dialog.ShowDialog(this) != true) return;
        if (IsProtectedPath(dialog.FileName)) { Status("원본이나 프로젝트와 다른 파일 이름으로 저장해 주세요.", true); return; }
        SetBusy(true); StopPlayback(false); _peakCancellation?.Cancel();
        try
        {
            var snapshot = _state;
            var audio = await Task.Run(() => RenderSnapshot(snapshot, null, false));
            double peak = AudioEditor.AnalyzePeak(audio);
            if (peak > 1 && MessageBox.Show(this, $"출력 한도를 {20 * Math.Log10(peak):0.0} dB 넘었습니다. 그대로 내보내면 찌그러질 수 있어요.\n이대로 저장할까요?", "출력 음량 확인", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            await Task.Run(() => WriteAudioAtomic(dialog.FileName, audio));
            Status("WAV를 저장했습니다. 작업을 이어 하려면 프로젝트도 저장해 주세요. · " + dialog.FileName);
        }
        catch (Exception ex) when (IsExpected(ex)) { Status("내보내지 못했습니다. " + ex.Message, true); }
        finally { SetBusy(false); }
    }

    private async void OnNew(object sender, RoutedEventArgs e)
    {
        if (_busy || !await ConfirmLeaveAsync()) return;
        ReplaceProject([], 0, null, null);
        Status("새 작업을 시작합니다.");
    }
    private void ReplaceProject(AudioTrack[] tracks, double masterDb, Guid? selected, string? path)
    {
        StopPlayback(true); _peakCancellation?.Cancel();
        _state = new Snapshot(Guid.NewGuid(), tracks, masterDb); _savedId = _state.Id;
        _history.Clear(); _history.Add(_state); _historyIndex = 0; _coalesceKind = null;
        _selectedId = tracks.Any(t => t.Id == selected) ? selected : tracks.FirstOrDefault()?.Id;
        _projectPath = path; _editCursor = 0; RefreshControls(); SchedulePeak();
    }
    private async Task<bool> ConfirmLeaveAsync()
    {
        if (!CommitNumbers()) return false;
        if (!IsDirty) return true;
        var result = MessageBox.Show(this, "저장하지 않은 작업이 있습니다. 프로젝트로 저장할까요?", "작업 내용 보존", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        return result == MessageBoxResult.No || (result == MessageBoxResult.Yes && await SaveProjectAsync(false));
    }
    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_allowClose || _smokeMode) return;
        e.Cancel = true;
        if (_busy || _handlingClose) return;
        _handlingClose = true;
        try { if (await ConfirmLeaveAsync()) { _allowClose = true; _ = Dispatcher.BeginInvoke(new Action(Close)); } }
        finally { _handlingClose = false; }
    }
    private void SetBusy(bool value)
    {
        _busy = value;
        FileCommands.IsEnabled = AddCommands.IsEnabled = !value;
        Inspector.IsEnabled = !value;
        TrackCommands.IsEnabled = !value && Selected is not null;
        ArrangementCommands.IsEnabled = !value;
        AddToTrackButton.IsEnabled = !value && Selected is not null;
        Timeline.IsEnabled = SourceWaveform.IsEnabled = MasterSlider.IsEnabled = !value;
        PlayButton.IsEnabled = !value && _state.Tracks.Length > 0;
        SelectedOnlyCheck.IsEnabled = BypassCheck.IsEnabled = !value;
        MainMenu.IsEnabled = MixerChannels.IsEnabled = SourcePoolList.IsEnabled = !value;
        RefreshCursorControls();
        RefreshWorkspaceShell();
    }
    private void OnShortcut(object sender, KeyEventArgs e)
    {
        bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        if (e.Key == Key.F2) { EditorToggle.IsChecked = EditorToggle.IsChecked != true; OnToggleEditor(this, new RoutedEventArgs()); e.Handled = true; return; }
        if (e.Key == Key.F3) { MixerToggle.IsChecked = MixerToggle.IsChecked != true; OnToggleMixer(this, new RoutedEventArgs()); e.Handled = true; return; }
        if (e.Key == Key.F5) { BrowserToggle.IsChecked = BrowserToggle.IsChecked != true; OnToggleBrowser(this, new RoutedEventArgs()); e.Handled = true; return; }
        if (ctrl && e.Key == Key.S) { OnSaveProject(this, new RoutedEventArgs()); e.Handled = true; return; }
        if (e.OriginalSource is TextBox) return;
        if (ctrl && e.Key == Key.Z) { MoveHistory(-1); e.Handled = true; }
        else if (ctrl && e.Key == Key.Y) { MoveHistory(1); e.Handled = true; }
        else if (ctrl && e.Key == Key.D) { OnDuplicate(this, new RoutedEventArgs()); e.Handled = true; }
        else if (e.Key == Key.S && Keyboard.Modifiers == ModifierKeys.None) { OnSplit(this, new RoutedEventArgs()); e.Handled = true; }
        else if (e.Key == Key.Delete) { OnRemove(this, new RoutedEventArgs()); e.Handled = true; }
        else if (e.Key == Key.Space && e.OriginalSource is not Button && e.OriginalSource is not CheckBox) { OnPlay(this, new RoutedEventArgs()); e.Handled = true; }
    }
    private bool IsProtectedPath(string path) => string.Equals(Path.GetFullPath(path), _projectPath, StringComparison.OrdinalIgnoreCase) || _protectedSources.Contains(Path.GetFullPath(path));
    private static void WriteAudioAtomic(string path, AudioClip audio)
    {
        string temp = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, ".sorilab-" + Guid.NewGuid().ToString("N") + ".tmp");
        try { WavCodec.WritePcm16(temp, audio); File.Move(temp, path, true); }
        finally { TryDelete(temp); }
    }
    private void Status(string text, bool warning = false) { StatusText.Text = text; StatusText.Foreground = Brush(warning ? "#FFC1A5" : "#A6B5CD"); }
    private static bool ParseNumber(string value, out double number) => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out number) && double.IsFinite(number);
    private static string FormatSeconds(double seconds) => seconds.ToString("0.######", CultureInfo.InvariantCulture);
    private static bool IsExpected(Exception ex) => ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or NotSupportedException or OverflowException;
    private static SolidColorBrush Brush(string value) => new((Color)ColorConverter.ConvertFromString(value));
    private static void TryDelete(string path) { try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { } }

    private void OnGenerate(object sender, RoutedEventArgs e)
    {
        if (_busy || !CommitNumbers()) return;
        StopPlayback(true);
        var dialog = new GeneratorDialog(this);
        if (dialog.ShowDialog() == true && dialog.GeneratedClip is { } source)
        {
            var track = new AudioTrack(Guid.NewGuid(), source, new EditSettings(0, source.FrameCount, 0, 0, 0));
            CommitState([.. _state.Tracks, track], _state.MasterDb, "generate", track.Id);
        }
    }
    private async void OnSaveProject(object sender, RoutedEventArgs e) => await SaveProjectAsync(false);
    private async void OnSaveProjectAs(object sender, RoutedEventArgs e) => await SaveProjectAsync(true);
    private async Task<bool> SaveProjectAsync(bool saveAs)
    {
        if (_busy || !CommitNumbers()) return false;
        string? target = saveAs ? null : _projectPath;
        if (target is null)
        {
            var dialog = new SaveFileDialog { Title = "소리와 편집 설정을 프로젝트로 저장", Filter = "소리공방 프로젝트|*.sorilab", DefaultExt = ".sorilab", AddExtension = true, FileName = _projectPath is null ? "새 효과음.sorilab" : Path.GetFileName(_projectPath) };
            if (dialog.ShowDialog(this) != true) return false;
            target = dialog.FileName;
        }
        target = Path.GetFullPath(target);
        if (_protectedSources.Contains(target)) { Status("원본 WAV와 다른 이름으로 프로젝트를 저장해 주세요.", true); return false; }
        SetBusy(true); _peakCancellation?.Cancel();
        try
        {
            var snapshot = _state;
            var document = new ProjectDocument(snapshot.Tracks, snapshot.MasterDb, _selectedId);
            await Task.Run(() => ProjectFile.Save(target, document));
            _projectPath = target; _savedId = snapshot.Id; _coalesceKind = null;
            RefreshControls(); Status("프로젝트를 저장했습니다. 원본 소리도 함께 보관했습니다. · " + target);
            return true;
        }
        catch (Exception ex) when (IsExpected(ex)) { Status("프로젝트를 저장하지 못했습니다. " + ex.Message, true); return false; }
        finally { SetBusy(false); }
    }
    private async void OnOpenProject(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var dialog = new OpenFileDialog { Title = "소리공방 프로젝트 열기", Filter = "소리공방 프로젝트|*.sorilab" };
        if (dialog.ShowDialog(this) == true) await OpenProjectAsync(dialog.FileName);
    }
    private async Task OpenProjectAsync(string path)
    {
        if (_busy || !await ConfirmLeaveAsync()) return;
        SetBusy(true); StopPlayback(true); _peakCancellation?.Cancel(); Status("프로젝트를 열고 있습니다…");
        try
        {
            var document = await Task.Run(() => ProjectFile.Load(path));
            foreach (var track in document.Tracks) if (track.SourcePath is not null) _protectedSources.Add(track.SourcePath);
            ReplaceProject(document.Tracks, document.MasterGainDb, document.SelectedTrackId, Path.GetFullPath(path));
            Status("프로젝트를 열었습니다. 트랙 배치와 편집 설정을 복원했습니다.");
        }
        catch (Exception ex) when (IsExpected(ex)) { Status("프로젝트를 열지 못했습니다. 기존 작업은 유지됩니다. " + ex.Message, true); }
        finally { SetBusy(false); }
    }
    public Task OpenDocumentAsync(string path) => Path.GetExtension(path).Equals(".sorilab", StringComparison.OrdinalIgnoreCase) ? OpenProjectAsync(path) : LoadFileAsync(path);

    public async Task RunInteractionChecksAsync(string outputBase)
    {
        _smokeMode = true;
        if (Selected is not { } original) throw new InvalidOperationException("검증용 파일이 없습니다.");
        UpdateSelected(t => t with { Edit = t.Edit with { StartFrame = 0, EndFrame = 1 } }, "one-frame");
        if (!CommitNumbers()) throw new InvalidOperationException("한 샘플 구간 검증 실패");
        UpdateSelected(_ => original, "restore");
        var second = original with { Id = Guid.NewGuid(), OffsetSeconds = .35, PlaybackRate = .8, Reverse = true, Edit = original.Edit with { GainDb = -6, FadeInMs = 40, FadeOutMs = 140 } };
        CommitState([original with { Edit = original.Edit with { GainDb = -6 } }, second], -3, "mix", second.Id);
        MoveHistory(-1); if (_state.Tracks.Length != 1) throw new InvalidOperationException("트랙 추가 실행 취소 실패");
        MoveHistory(1); if (_state.Tracks.Length != 2) throw new InvalidOperationException("트랙 추가 다시 실행 실패");
        var output = RenderSnapshot(_state, null, false);
        WriteAudioAtomic(outputBase + ".wav", output);
        if (WavCodec.Read(outputBase + ".wav").FrameCount != output.FrameCount) throw new InvalidOperationException("조합 출력 길이 검증 실패");
        _player.Volume = 0; await PlayAsync();
        DateTime limit = DateTime.UtcNow.AddSeconds(8);
        while (!_playing && DateTime.UtcNow < limit) await Task.Delay(50);
        if (!_playing) throw new InvalidOperationException("조합 미리 듣기 시작 실패");
        await Task.Delay(200); if (_player.Position == TimeSpan.Zero) throw new InvalidOperationException("조합 재생 위치 검증 실패");
        await PlayAsync(); if (_playing) throw new InvalidOperationException("일시정지 실패");
        await PlayAsync(); if (!_playing) throw new InvalidOperationException("이어 듣기 실패");
        StopPlayback(true); _player.Volume = 1;
        var generator = new GeneratorDialog(this) { ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = -20000 };
        generator.Show();
        var generated = SoundGenerator.Generate(generator.ReadSettings());
        Program.SaveScreenshot(generator, outputBase + "-generator.png");
        generator.Close();
        var generatedTrack = new AudioTrack(Guid.NewGuid(), generated, new EditSettings(0, generated.FrameCount, 0, 0, 0), .9);
        CommitState([.. _state.Tracks, generatedTrack], _state.MasterDb, "generate-check", generatedTrack.Id);
        if (_state.Tracks.Length != 3) throw new InvalidOperationException("생성한 소리 트랙 추가 실패");
        await RunRegressionChecksAsync(outputBase);
        await RunArrangementChecksAsync(outputBase);
        SetEditCursor(Selected!.OffsetSeconds + AudioMixer.GetDurationSeconds(Selected) * .5);
        _projectPath = outputBase + ".sorilab";
        var beforeSave = RenderSnapshot(_state, null, false);
        if (!await SaveProjectAsync(false) || IsDirty) throw new InvalidOperationException("프로젝트 저장 상태 검증 실패");
        if (!SplitButton.IsEnabled) throw new InvalidOperationException("저장 후 분할 버튼 활성 복원 실패");
        UpdateSelected(t => t with { OffsetSeconds = t.OffsetSeconds + .1 }, "saved-change");
        if (!IsDirty) throw new InvalidOperationException("저장 후 변경 표시 실패");
        MoveHistory(-1);
        if (IsDirty) throw new InvalidOperationException("저장 지점 실행 취소 실패");
        await OpenProjectAsync(_projectPath);
        if (IsDirty || !beforeSave.Samples.SequenceEqual(RenderSnapshot(_state, null, false).Samples)) throw new InvalidOperationException("프로젝트 재열기 소리 검증 실패");
        await RunWorkspaceChecksAsync(outputBase);
        if (!await SaveProjectAsync(false)) throw new InvalidOperationException("검수용 프로젝트 최종 저장 실패");
        WriteAudioAtomic(outputBase + ".wav", RenderSnapshot(_state, null, false));
        await UpdatePeakAsync();
        Status("조합·트랙 편집·실행 취소·내보내기·재생·프로젝트 저장 검증을 통과했습니다.");
    }
}
