using System.Diagnostics;
using System.IO;
using System.Windows.Threading;
using SoriLab.Core;

namespace SoriLab.App;

public partial class StudioWindow
{
    private async Task RunCursorPlaybackChecksAsync(string outputBase)
    {
        if (!Dispatcher.CheckAccess() || _busy || _closing)
            throw new InvalidOperationException("커서 재생 검증은 다른 처리가 끝난 화면 스레드에서 실행해야 합니다.");

        var originalState = _state;
        var originalHistory = _history.ToArray();
        var originalHistoryIndex = _historyIndex;
        var originalSavedId = _savedId;
        var originalSelectedId = _selectedId;
        var originalProjectPath = _projectPath;
        var originalCursor = _editCursor;
        var originalPreviewStart = _previewTimelineStart;
        var originalPreviewLength = _previewLength;
        var originalLoop = LoopCheck.IsChecked;
        var originalSelectedOnly = SelectedOnlyCheck.IsChecked;
        var originalBypass = BypassCheck.IsChecked;
        var originalProtectedSources = _protectedSources.ToArray();
        var originalCoalesceKind = _coalesceKind;
        var originalLastEdit = _lastEdit;
        var originalVolume = _player.Volume;
        var originalPeakScheduled = _peakDelay.IsEnabled;
        var originalPeakText = PeakText.Text;
        var originalPeakBrush = PeakText.Foreground;
        var originalStatusText = StatusText.Text;
        var originalStatusBrush = StatusText.Foreground;
        var results = new List<string>();

        static void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("커서 재생 검증 실패: " + message);
        }

        static void Near(double actual, double expected, double tolerance, string message) =>
            Check(double.IsFinite(actual) && Math.Abs(actual - expected) <= tolerance,
                $"{message} 기대={expected:0.########}, 실제={actual:0.########}");

        async Task WaitForAsync(Func<bool> condition, string message)
        {
            var timer = Stopwatch.StartNew();
            while (!condition())
            {
                Check(timer.Elapsed < TimeSpan.FromSeconds(8), message + " · " + StatusText.Text);
                await Task.Delay(20);
            }
        }

        async Task StartAsync()
        {
            Check(_player.Volume == 0, "검증 중에는 실제 오디오 출력이 음소거되어야 합니다.");
            await PlayAsync().WaitAsync(TimeSpan.FromSeconds(8));
            await WaitForAsync(() => _playing && _ready && !_opening && !_busy, "재생 시작 시간이 초과됐습니다.");
            Check(_player.Volume == 0, "교체한 재생기가 음소거 설정을 잃었습니다.");
        }

        void UseFixture(AudioTrack track, bool selectedOnly = false)
        {
            StopPlayback(true);
            _syncing = true;
            LoopCheck.IsChecked = false;
            SelectedOnlyCheck.IsChecked = selectedOnly;
            BypassCheck.IsChecked = false;
            _syncing = false;
            ReplaceProject([track], 0, track.Id, null);
            _peakDelay.Stop();
            _peakCancellation?.Cancel();
            _player.Volume = 0;
        }

        void CheckPreviewBytes(AudioClip rendered, int firstFrame, double absoluteStart)
        {
            Check(_previewPath is not null && File.Exists(_previewPath), "미리 듣기 파일이 없습니다.");
            // 기대값은 새 커서 변환 함수를 거치지 않아 같은 계산 실수를 함께 통과시키지 않습니다.
            var expectedAudio = rendered with { Samples = rendered.Samples.AsSpan(firstFrame * rendered.Channels).ToArray() };
            var expectedBytes = WavCodec.EncodePcm16(expectedAudio);
            Check(File.ReadAllBytes(_previewPath!).SequenceEqual(expectedBytes),
                "미리 듣기 WAV가 완성 렌더 결과의 커서 이후 표본과 다릅니다.");
            Near(_previewTimelineStart, absoluteStart, 1.0 / 48000, "미리 듣기 절대 시작 위치가 다릅니다.");
            Near(_editCursor, absoluteStart, 1.0 / 48000, "재생 시작 커서가 다릅니다.");
            Near(_previewLength, expectedAudio.DurationSeconds, 1.0 / 48000, "잘라낸 미리 듣기 길이가 다릅니다.");
        }

        void CheckStoppedAt(double expected)
        {
            Check(!_playing && !_opening, "정지 후에도 재생 또는 열기 대기 상태입니다.");
            Near(Timeline.PlayheadSeconds, expected, 1.0 / 48000, "정지한 재생선이 시작 커서로 돌아오지 않았습니다.");
            Near(_editCursor, expected, 1.0 / 48000, "정지가 편집 커서를 바꿨습니다.");
            Check(PositionText.Text == TimeSpan.FromSeconds(expected).ToString(@"mm\:ss\.fff"),
                "정지 후 시간 표시가 시작 커서와 다릅니다.");
        }

        try
        {
            // 소리는 검증에만 사용하며 원본 파일과 프로젝트 파일을 만들거나 바꾸지 않습니다.
            _player.Volume = 0;
            StopPlayback(true);
            _peakDelay.Stop();
            _peakCancellation?.Cancel();
            var samples = new float[3 * 48000];
            for (var frame = 0; frame < samples.Length; frame++)
                samples[frame] = frame < 48000 ? 0.125f : frame < 96000 ? -0.25f : 0.5f;
            var source = new AudioClip("커서 검증용 세 구간", 48000, 1, samples);
            var basic = new AudioTrack(Guid.NewGuid(), source, new EditSettings(0, source.FrameCount, 0, 0, 0));
            UseFixture(basic);
            var full = RenderSnapshot(_state, null, false);

            SetEditCursor(1.25);
            await StartAsync();
            CheckPreviewBytes(full, 60000, 1.25);
            Check(WavCodec.Read(_previewPath!).Samples[0] == -0.25f,
                "첫 재생 표본에 커서 이전의 0초 구간이 들어갔습니다.");
            await WaitForAsync(() => _player.Position.TotalSeconds >= 0.12, "커서에서 시작한 재생 위치가 진행하지 않습니다.");
            UpdatePlayhead();
            Near(Timeline.PlayheadSeconds, 1.25 + _player.Position.TotalSeconds, 0.05, "전체 재생의 절대 시간 표시가 다릅니다.");
            results.Add("통과: 비영 커서에서 전체 재생하며 첫 표본부터 완성 렌더 결과의 해당 구간과 WAV 바이트가 같습니다.");

            var firstPlayer = _player;
            var firstPath = _previewPath;
            await PlayAsync();
            Check(!_playing && _ready, "일시정지 상태가 아닙니다.");
            await Task.Delay(50);
            var pausedAt = _player.Position.TotalSeconds;
            await Task.Delay(120);
            Near(_player.Position.TotalSeconds, pausedAt, 0.02, "일시정지 중 재생 위치가 바뀌었습니다.");
            await PlayAsync();
            await WaitForAsync(() => _playing && _player.Position.TotalSeconds > pausedAt + 0.05,
                "일시정지한 위치부터 이어 재생하지 못했습니다.");
            Check(ReferenceEquals(firstPlayer, _player) && firstPath == _previewPath,
                "이어 재생하면서 미리 듣기 파일을 새로 만들었습니다.");
            Near(_previewTimelineStart, 1.25, 0, "이어 재생이 시작 커서를 바꿨습니다.");
            results.Add("통과: 일시정지 동안 위치를 유지하고 같은 미리 듣기에서 중단 위치부터 이어 재생합니다.");

            StopPlayback(false);
            CheckStoppedAt(1.25);
            Check(_ready && firstPath == _previewPath, "정지하면서 재사용할 미리 듣기가 사라졌습니다.");
            Near(_player.Position.TotalSeconds, 0, 0.001, "정지 후 파일 내부 위치가 처음으로 돌아오지 않았습니다.");
            await StartAsync();
            Check(ReferenceEquals(firstPlayer, _player), "정지 후 같은 커서 재생에서 재생기를 다시 만들었습니다.");
            CheckPreviewBytes(full, 60000, 1.25);
            await WaitForAsync(() => _player.Position.TotalSeconds > 0.05, "정지 후 재시작이 진행하지 않습니다.");
            StopPlayback(true);
            results.Add("통과: 정지하면 시작 커서로 돌아오고 다시 재생하면 같은 커서 구간의 처음부터 시작합니다.");

            var selected = basic with
            {
                OffsetSeconds = 2, PlaybackRate = 2, Reverse = true,
                Edit = new EditSettings(24000, 120000, -6, 100, 150)
            };
            UseFixture(selected, true);
            var selectedRender = RenderSnapshot(_state, selected.Id, false);
            SetEditCursor(2.25);
            await StartAsync();
            CheckPreviewBytes(selectedRender, 12000, 2.25);
            await WaitForAsync(() => _player.Position.TotalSeconds >= 0.08, "선택 클립의 재생 위치가 진행하지 않습니다.");
            UpdatePlayhead();
            Near(Timeline.PlayheadSeconds, 2.25 + _player.Position.TotalSeconds, 0.05,
                "선택 클립의 배치 위치 또는 커서 시작점이 중복 반영됐습니다.");
            var expectedFrame = selected.Edit.EndFrame - 1 -
                (Timeline.PlayheadSeconds - selected.OffsetSeconds) * source.SampleRate * selected.PlaybackRate;
            var expectedSourceSeconds = Math.Clamp(expectedFrame, selected.Edit.StartFrame, selected.Edit.EndFrame - 1) / source.SampleRate;
            Near(SourceWaveform.PlayheadSeconds, expectedSourceSeconds, 1.0 / 48000,
                "역재생·2배속 선택 클립의 원본 재생선 위치가 다릅니다.");
            StopPlayback(false);
            CheckStoppedAt(2.25);
            StopPlayback(true);
            results.Add("통과: 배치·역재생·2배속·페이드가 있는 선택 클립에서 커서 이후 소리와 원본 재생선 위치를 보존합니다.");

            UseFixture(basic);
            SetEditCursor(2.8);
            LoopCheck.IsChecked = true;
            await PlayAsync().WaitAsync(TimeSpan.FromSeconds(8));
            var loopPlayer = _player;
            var loopCount = 0;
            var loopPositionsValid = true;
            EventHandler ended = (_, _) =>
            {
                loopCount++;
                loopPositionsValid &= ReferenceEquals(loopPlayer, _player) && _playing && _ready &&
                    Math.Abs(Timeline.PlayheadSeconds - 2.8) <= 1.0 / 48000 &&
                    loopPlayer.Position.TotalSeconds <= 0.05;
            };
            loopPlayer.MediaEnded += ended;
            try
            {
                await WaitForAsync(() => loopCount >= 2, "실제 반복 재생 종료 이벤트 두 번을 받지 못했습니다.");
                Check(loopPositionsValid, "반복 시작 위치가 현재 커서 또는 잘라낸 파일의 처음과 다릅니다.");
                CheckPreviewBytes(full, 134400, 2.8);
            }
            finally
            {
                loopPlayer.MediaEnded -= ended;
                StopPlayback(true);
                LoopCheck.IsChecked = false;
            }
            results.Add("통과: 실제 MediaEnded 이벤트 두 번에서 전체 파일 0초가 아닌 현재 커서 구간을 반복합니다.");

            UseFixture(basic);
            foreach (var cursor in new[] { 3d, 3.25 })
            {
                SetEditCursor(cursor);
                await PlayAsync().WaitAsync(TimeSpan.FromSeconds(8));
                Check(!_playing && !_opening && !_ready && !_busy && _previewPath is null,
                    "전체 끝과 끝 이후 커서는 재생할 구간이 없는데 재생을 시작했습니다.");
                CheckStoppedAt(cursor);
            }
            UseFixture(selected, true);
            foreach (var outsideCursor in new[] { 1d, 3d, 3.25 })
            {
                SetEditCursor(outsideCursor);
                await StartAsync();
                CheckPreviewBytes(selectedRender, 0, 2);
                StopPlayback(true);
            }
            results.Add("통과: 전체 끝 이상의 커서는 재생을 거절하고, 선택 클립 밖의 커서는 해당 클립 시작으로 맞춥니다.");

            UseFixture(basic with { PlaybackRate = 0.5 });
            SetEditCursor(1.25);
            var preparing = PlayAsync();
            Check(_busy && !preparing.IsCompleted, "준비 중 정지 검증에 필요한 비동기 준비 상태가 없습니다.");
            StopPlayback(false);
            await preparing.WaitAsync(TimeSpan.FromSeconds(8));
            await System.Windows.Threading.Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Check(!_playing && !_opening && !_ready && !_busy && _previewPath is null,
                "렌더 준비 중 정지했는데 뒤늦게 재생이 시작되거나 파일이 남았습니다.");
            CheckStoppedAt(1.25);

            UseFixture(basic);
            SetEditCursor(1.25);
            await PlayAsync().WaitAsync(TimeSpan.FromSeconds(8));
            bool stoppedWhileOpening = _opening && !_playing;
            Check(stoppedWhileOpening || (_ready && _playing), "파일 열기가 시작되지 않았습니다.");
            var openingPath = _previewPath;
            // 빠른 장치에서 이미 열린 경우에는 재생을 정리하고, 관측하지 못한 구간을 통과로 기록하지 않습니다.
            StopPlayback(!stoppedWhileOpening);
            await System.Windows.Threading.Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            await Task.Delay(200);
            Check(!_playing && !_opening && !_ready && !_busy && _previewPath is null,
                "파일 열기 대기 중 정지했는데 늦은 이벤트가 재생을 다시 시작했습니다.");
            Check(openingPath is not null && !File.Exists(openingPath), "중단한 파일 열기의 임시파일이 남았습니다.");
            CheckStoppedAt(1.25);
            results.Add(stoppedWhileOpening
                ? "통과: 렌더 준비 중과 파일 열기 대기 중 정지하면 준비 완료·늦은 이벤트가 재생을 다시 시작하지 않습니다."
                : "통과: 렌더 준비 중 정지가 뒤늦은 재생을 방지합니다. 파일 열기 대기는 이미 열기가 끝나 추가 검증을 건너뛰었습니다.");

            UseFixture(basic);
            var savedState = _state;
            var savedId = _savedId;
            var savedHistory = _history.ToArray();
            var savedIndex = _historyIndex;
            SetEditCursor(0.75);
            await StartAsync();
            await PlayAsync();
            Check(!_playing && _ready, "커서 이동 검증 전에 일시정지하지 못했습니다.");
            var oldPath = _previewPath;
            var oldPlayer = _player;
            SetEditCursor(84001 / 48000d);
            Check(!_playing && !_ready && !_opening && _previewPath is null,
                "일시정지 중 커서를 옮겼는데 이전 미리 듣기가 유지됩니다.");
            Check(oldPath is not null && !File.Exists(oldPath), "커서를 옮긴 뒤 이전 미리 듣기 파일이 남았습니다.");
            Check(ReferenceEquals(_state, savedState) && _savedId == savedId && !IsDirty &&
                _history.SequenceEqual(savedHistory) && _historyIndex == savedIndex,
                "커서 이동 또는 재생이 편집 상태·저장 표시·실행 취소 이력을 바꿨습니다.");
            await StartAsync();
            Check(!ReferenceEquals(oldPlayer, _player), "새 커서 구간이 이전 재생기를 그대로 사용합니다.");
            CheckPreviewBytes(full, 84001, 84001 / 48000d);
            var fractionalPlayer = _player;
            var fractionalPath = _previewPath;
            await PlayAsync();
            Check(!_playing && _ready && ReferenceEquals(fractionalPlayer, _player) && fractionalPath == _previewPath,
                "표시 자릿수보다 정밀한 커서에서 일시정지하면 재생을 새로 준비합니다.");
            OnGoToStart(this, new System.Windows.RoutedEventArgs());
            CheckStoppedAt(0);
            Check(!_ready && _previewPath is null, "처음으로 이동한 뒤 이전 커서의 재생 준비가 남았습니다.");
            StopPlayback(true);
            results.Add("통과: 커서 이동은 편집·저장·이력을 보존하고, 정밀한 커서에서도 일시정지하며 처음으로 이동하면 이전 준비를 버립니다.");
        }
        finally
        {
            try
            {
                // 실행을 마친 재생기와 검증 중 앱이 만든 미리 듣기만 정리합니다.
                StopPlayback(true);
                _peakDelay.Stop();
                _peakCancellation?.Cancel();
                _state = originalState;
                _history.Clear();
                _history.AddRange(originalHistory);
                _historyIndex = originalHistoryIndex;
                _savedId = originalSavedId;
                _selectedId = originalSelectedId;
                _projectPath = originalProjectPath;
                _editCursor = originalCursor;
                _previewTimelineStart = originalPreviewStart;
                _previewLength = originalPreviewLength;
                _coalesceKind = originalCoalesceKind;
                _lastEdit = originalLastEdit;
                _protectedSources.Clear();
                _protectedSources.UnionWith(originalProtectedSources);
                _syncing = true;
                LoopCheck.IsChecked = originalLoop;
                SelectedOnlyCheck.IsChecked = originalSelectedOnly;
                BypassCheck.IsChecked = originalBypass;
                _syncing = false;
                SetBusy(false);
                RefreshControls();
                PeakText.Text = originalPeakText;
                PeakText.Foreground = originalPeakBrush;
                StatusText.Text = originalStatusText;
                StatusText.Foreground = originalStatusBrush;
                if (originalPeakScheduled) SchedulePeak();
            }
            finally
            {
                _player.Volume = originalVolume;
            }
        }

        results.Insert(0, "8개 커서 재생 시나리오를 앱 처리 함수로 검증했습니다. 실제 MediaPlayer 재생은 음소거했고 미리 듣기 WAV의 PCM 바이트와 화면 상태를 비교했습니다.");
        results.Add("검증 후 재생을 정지하고 작업·전체 이력·저장 지점·선택·커서·반복·듣기 설정·경로 보호·음량을 복원했습니다.");
        await File.WriteAllLinesAsync(outputBase + ".cursor.txt", results);
    }
}
