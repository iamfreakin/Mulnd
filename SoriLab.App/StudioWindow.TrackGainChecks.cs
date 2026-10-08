using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using SoriLab.Core;

namespace SoriLab.App;

public partial class StudioWindow
{
    /// <summary>검증 전용 소리로 실제 화면 컨트롤과 트랙 음량의 저장·재생 연결을 확인합니다.</summary>
    private async Task RunTrackGainChecksAsync(string outputBase)
    {
        if (!Dispatcher.CheckAccess() || _busy || _closing || _playing || _opening)
            throw new InvalidOperationException("트랙 음량 검증은 화면 스레드의 정지 상태에서 실행해야 합니다.");

        Snapshot originalState = _state;
        Snapshot[] originalHistory = _history.ToArray();
        int originalHistoryIndex = _historyIndex;
        Guid? originalSelected = _selectedId;
        Guid originalSavedId = _savedId;
        string? originalProjectPath = _projectPath;
        string[] originalProtectedSources = _protectedSources.ToArray();
        string? originalCoalesceKind = _coalesceKind;
        DateTime originalLastEdit = _lastEdit;
        Guid? originalVolumeDrag = _workspaceVolumeDrag;
        double originalCursor = _editCursor;
        double originalVolume = _player.Volume;
        bool? originalSelectedOnly = SelectedOnlyCheck.IsChecked;
        bool? originalBypass = BypassCheck.IsChecked;
        bool? originalLoop = LoopCheck.IsChecked;
        bool peakWasScheduled = _peakDelay.IsEnabled;
        string originalPeakText = PeakText.Text;
        var originalPeakBrush = PeakText.Foreground;
        string originalStatusText = StatusText.Text;
        var originalStatusBrush = StatusText.Foreground;

        string directory = Path.GetDirectoryName(Path.GetFullPath(outputBase))!;
        string prefix = Path.Combine(directory, ".sorilab-track-gain-" + Guid.NewGuid().ToString("N"));
        string wavePath = prefix + ".wav";
        string projectPath = prefix + ".sorilab";
        var results = new List<string>
        {
            "자체 생성한 2개 트랙·3개 클립으로 화면 컨트롤 이벤트와 앱 처리 함수를 검사했습니다.",
            "운영체제 마우스·키보드 입력 자동화와 사람이 듣는 음질 평가는 포함하지 않습니다. 각 검증의 제한 시간은 8초이며 스피커 음량은 0입니다."
        };
        Task? pendingCheck = null;
        Guid laneA = Guid.NewGuid();
        Guid laneB = Guid.NewGuid();
        var sourceA = ConstantSource("음량 검증 첫 소리", .125f);
        var sourceB = ConstantSource("음량 검증 둘째 소리", .0625f);
        var sourceC = ConstantSource("음량 검증 겹치는 소리", .03125f);
        AudioTrack[] fixture =
        [
            new(Guid.NewGuid(), sourceA, new EditSettings(0, 48000, -3, 0, 0), LaneId: laneA, TrackGainDb: -6),
            new(Guid.NewGuid(), sourceB, new EditSettings(0, 48000, 3, 0, 0), 1, LaneId: laneA, TrackGainDb: -6),
            new(Guid.NewGuid(), sourceC, new EditSettings(0, 48000, -2, 0, 0), .5, LaneId: laneB, TrackGainDb: 2)
        ];

        static AudioClip ConstantSource(string name, float amplitude) =>
            new(name, 48000, 1, Enumerable.Repeat(amplitude, 48000).ToArray());

        static void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("트랙 음량 검증 실패: " + message);
        }

        void StopPeak()
        {
            _peakDelay.Stop();
            _peakCancellation?.Cancel();
        }

        void ResetFixture()
        {
            SetBusy(false);
            _workspaceVolumeDrag = null;
            ReplaceProject(fixture, -1, fixture[0].Id, null);
            StopPeak();
        }

        async Task RunCase(string name, Func<Task> test)
        {
            var elapsed = Stopwatch.StartNew();
            try
            {
                pendingCheck = test();
                await pendingCheck.WaitAsync(TimeSpan.FromSeconds(8));
                Check(elapsed.Elapsed < TimeSpan.FromSeconds(8), name + " 검증에 8초 이상 걸렸습니다.");
                results.Add("통과: " + name);
            }
            catch (Exception exception)
            {
                results.Add("실패: " + name + " · " + exception.Message);
                throw;
            }
            finally { StopPeak(); }
        }

        async Task WaitForPlayback()
        {
            var elapsed = Stopwatch.StartNew();
            while (!_playing && elapsed.Elapsed < TimeSpan.FromSeconds(7))
            {
                if (!_opening && !_busy) break;
                await Task.Delay(20);
            }
            Check(_playing && _ready && !_opening && _player.Volume == 0, "음소거된 미리 듣기가 준비되지 않았습니다.");
        }

        static void CheckSamples(AudioClip audio, int frames, Func<int, double> expected, string label)
        {
            Check(audio.SampleRate == 48000 && audio.Channels == 2 && audio.FrameCount == frames,
                label + "의 길이 또는 출력 형식이 다릅니다.");
            for (int frame = 0; frame < frames; frame++)
            {
                double value = expected(frame);
                if (Math.Abs(audio.Samples[frame * 2] - value) > 1e-6 ||
                    Math.Abs(audio.Samples[frame * 2 + 1] - value) > 1e-6)
                    throw new InvalidOperationException($"트랙 음량 검증 실패: {label}의 {frame}번째 샘플이 계산한 값과 다릅니다.");
            }
        }

        try
        {
            _player.Volume = 0;
            StopPlayback(true);
            StopPeak();
            SelectedOnlyCheck.IsChecked = false;
            BypassCheck.IsChecked = false;
            LoopCheck.IsChecked = true;
            WavCodec.WritePcm16(wavePath, sourceC);

            await RunCase("실제 페이더의 같은 트랙 전체 적용, 다른 트랙·클립 음량 유지, 이력 묶음과 저장 지점·초기화 버튼", () =>
            {
                ResetFixture();
                Snapshot initial = _state;
                var channel = _workspaceChannels[laneA];
                Check(channel.VolumeFader.Minimum == -60 && channel.VolumeFader.Maximum == 12,
                    "트랙 페이더의 범위가 -60~+12dB가 아닙니다.");
                channel.VolumeFader.Value = -12;
                channel.VolumeFader.Value = -18;
                Check(_state.Tracks.Where(t => t.EffectiveLaneId == laneA).All(t => t.TrackGainDb == -18),
                    "같은 트랙의 클립 음량이 함께 바뀌지 않았습니다.");
                Check(_state.Tracks[2] == fixture[2] && _state.MasterDb == -1 &&
                      _state.Tracks.Select(t => t.Edit).SequenceEqual(fixture.Select(t => t.Edit)),
                    "트랙 음량 변경이 다른 트랙·전체 음량·클립 효과를 바꿨습니다.");
                Check(_history.Count == 2 && _historyIndex == 1 && IsDirty,
                    "연속된 페이더 변경이 하나의 실행 취소로 묶이지 않았습니다.");
                Snapshot changed = _state;
                OnUndo(this, new RoutedEventArgs());
                Check(ReferenceEquals(_state, initial) && !IsDirty && channel.VolumeFader.Value == -6,
                    "음량 실행 취소가 저장 상태와 페이더를 복원하지 못했습니다.");
                OnRedo(this, new RoutedEventArgs());
                Check(ReferenceEquals(_state, changed) && channel.VolumeFader.Value == -18,
                    "음량 다시 실행이 작업과 페이더를 복원하지 못했습니다.");

                // 저장 식별자가 같을 때 연속 조절이 저장 지점 자체를 덮어쓰지 않아야 합니다.
                _savedId = _state.Id;
                channel.VolumeFader.Value = -24;
                Check(_history.Count == 3 && _history[1].Id == _savedId && IsDirty,
                    "저장 지점 이후 페이더 변경이 이전 저장 이력을 덮어썼습니다.");
                OnUndo(this, new RoutedEventArgs());
                Check(!IsDirty && channel.VolumeFader.Value == -18, "음량 변경 전 저장 지점으로 돌아가지 못했습니다.");
                OnRedo(this, new RoutedEventArgs());
                channel.ResetVolumeButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(_state.Tracks.Where(t => t.EffectiveLaneId == laneA).All(t => t.TrackGainDb == 0) &&
                      channel.VolumeFader.Value == 0 && channel.VolumeValue.Text.Contains("0.0"),
                    "음량 초기화 버튼이 같은 트랙과 화면 값을 0dB로 바꾸지 못했습니다.");
                OnUndo(this, new RoutedEventArgs());
                Check(channel.VolumeFader.Value == -24, "음량 초기화를 한 번에 취소하지 못했습니다.");
                return Task.CompletedTask;
            });

            await RunCase("손잡이 드래그 중 멈춤은 한 이력으로 묶고 다음 드래그는 분리", () =>
            {
                ResetFixture();
                var fader = _workspaceChannels[laneA].VolumeFader;
                fader.RaiseEvent(new DragStartedEventArgs(0, 0) { RoutedEvent = Thumb.DragStartedEvent });
                fader.Value = -8;
                // 실제로 기다리지 않고 마지막 편집 시각을 옮겨 700ms 경계를 재현합니다.
                _lastEdit = DateTime.UtcNow.AddSeconds(-2);
                fader.Value = -10;
                Check(_history.Count == 2, "같은 드래그에서 잠시 멈추자 이력이 나뉘었습니다.");
                fader.RaiseEvent(new DragCompletedEventArgs(0, 0, false) { RoutedEvent = Thumb.DragCompletedEvent });
                fader.RaiseEvent(new DragStartedEventArgs(0, 0) { RoutedEvent = Thumb.DragStartedEvent });
                fader.Value = -12;
                fader.RaiseEvent(new DragCompletedEventArgs(0, 0, false) { RoutedEvent = Thumb.DragCompletedEvent });
                Check(_history.Count == 3 && _workspaceVolumeDrag is null, "서로 다른 드래그가 같은 이력으로 합쳐졌습니다.");
                OnUndo(this, new RoutedEventArgs());
                Check(fader.Value == -10, "두 번째 드래그만 취소하지 못했습니다.");
                return Task.CompletedTask;
            });

            await RunCase("처리 중 페이더·초기화 비활성 및 상태 변경 차단", () =>
            {
                ResetFixture();
                Snapshot before = _state;
                var channel = _workspaceChannels[laneA];
                SetBusy(true);
                Check(!channel.VolumeFader.IsEnabled && !channel.ResetVolumeButton.IsEnabled,
                    "처리 중 트랙 음량 컨트롤이 활성 상태입니다.");
                channel.VolumeFader.Value = -30;
                channel.ResetVolumeButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(ReferenceEquals(before, _state) && _history.Count == 1 && channel.VolumeFader.Value == -6,
                    "처리 중 음량 이벤트가 작업을 변경하거나 화면에 잘못된 값을 남겼습니다.");
                SetBusy(false);
                Check(channel.VolumeFader.IsEnabled && channel.ResetVolumeButton.IsEnabled,
                    "처리가 끝나도 트랙 음량 컨트롤이 비활성입니다.");
                return Task.CompletedTask;
            });

            await RunCase("분할·복제의 트랙 음량 유지와 분할 전후 소리 일치", async () =>
            {
                ResetFixture();
                AudioClip before = RenderSnapshot(_state, null, false);
                SetEditCursor(.5);
                await SplitSelectedAsync();
                Check(_state.Tracks.Length == 4 && _state.Tracks.Count(t => t.EffectiveLaneId == laneA) == 3 &&
                      _state.Tracks.Where(t => t.EffectiveLaneId == laneA).All(t => t.TrackGainDb == -6),
                    "분할 후 같은 트랙의 음량이 유지되지 않았습니다.");
                Check(before.Samples.SequenceEqual(RenderSnapshot(_state, null, false).Samples),
                    "분할할 때 트랙 음량이 사라지거나 중복 적용됐습니다.");
                Guid originalClip = Selected!.Id;
                OnDuplicate(this, new RoutedEventArgs());
                Check(_state.Tracks.Length == 5 && Selected!.Id != originalClip &&
                      Selected.EffectiveLaneId == laneA && Selected.TrackGainDb == -6,
                    "복제한 클립이 기존 트랙 음량을 유지하지 못했습니다.");
            });

            await RunCase("다른 트랙 이동 시 대상 음량 상속, 빈 트랙 이동 시 0dB", () =>
            {
                ResetFixture();
                MoveClip(fixture[0].Id, 2, laneB);
                Check(Selected!.EffectiveLaneId == laneB && Selected.TrackGainDb == 2 &&
                      Selected.Edit == fixture[0].Edit && _state.Tracks.Single(t => t.Id == fixture[1].Id).TrackGainDb == -6,
                    "다른 트랙으로 옮길 때 대상 음량 상속 또는 기존 트랙 보존에 실패했습니다.");
                Guid emptyLane = Guid.NewGuid();
                MoveClip(fixture[0].Id, 2, emptyLane);
                Check(Selected!.EffectiveLaneId == emptyLane && Selected.TrackGainDb == 0 &&
                      _state.Tracks.Single(t => t.Id == fixture[2].Id).TrackGainDb == 2,
                    "새 트랙으로 옮긴 클립의 음량이 0dB가 아니거나 기존 트랙 음량이 달라졌습니다.");
                return Task.CompletedTask;
            });

            await RunCase("자체 WAV를 기존 트랙에 추가하면 음량 상속, 새 트랙은 0dB", async () =>
            {
                ResetFixture();
                SetEditCursor(3);
                await AddAudioAsync([wavePath], laneA);
                Check(_state.Tracks.Length == 4 && Selected!.EffectiveLaneId == laneA &&
                      Selected.TrackGainDb == -6 && Selected.OffsetSeconds == 3 && Selected.Edit.GainDb == 0,
                    "기존 트랙에 가져온 클립의 음량·위치·클립 효과가 잘못됐습니다.");
                await AddAudioAsync([wavePath]);
                Check(_state.Tracks.Length == 5 && Selected!.EffectiveLaneId != laneA &&
                      Selected.EffectiveLaneId != laneB && Selected.TrackGainDb == 0 && IsProtectedPath(wavePath),
                    "새 트랙으로 가져온 소리의 0dB 또는 원본 경로 보호가 잘못됐습니다.");
            });

            await RunCase("전체·선택 재생의 계산된 샘플과 우회 시 클립·트랙·전체 음량 제외", async () =>
            {
                ResetFixture();
                Snapshot snapshot = _state;
                await Task.Run(() =>
                {
                    double first = .125 * Math.Pow(10, (-3 - 6) / 20.0);
                    double second = .0625 * Math.Pow(10, (3 - 6) / 20.0);
                    double other = .03125 * Math.Pow(10, (-2 + 2) / 20.0);
                    double master = Math.Pow(10, -1 / 20.0);
                    CheckSamples(RenderSnapshot(snapshot, null, false), 96000,
                        frame => ((frame < 48000 ? first : second) + (frame >= 24000 && frame < 72000 ? other : 0)) * master,
                        "전체 재생");
                    CheckSamples(RenderSnapshot(snapshot, fixture[1].Id, false), 48000, _ => second * master, "선택 재생");
                    CheckSamples(RenderSnapshot(snapshot, null, true), 96000,
                        frame => (frame < 48000 ? .125 : .0625) + (frame >= 24000 && frame < 72000 ? .03125 : 0),
                        "전체 우회 재생");
                    CheckSamples(RenderSnapshot(snapshot, fixture[1].Id, true), 48000, _ => .0625, "선택 우회 재생");
                });
            });

            await RunCase("자체 프로젝트 저장·재열기의 트랙 음량·소리·선택·저장 상태 보존", async () =>
            {
                ResetFixture();
                _workspaceChannels[laneA].VolumeFader.Value = -11;
                SelectTrack(fixture[1].Id);
                _projectPath = projectPath;
                Snapshot saved = _state;
                AudioClip savedAudio = RenderSnapshot(saved, null, false);
                Check(await SaveProjectAsync(false) && !IsDirty && File.Exists(projectPath), "검증용 프로젝트를 저장하지 못했습니다.");
                _workspaceChannels[laneA].VolumeFader.Value = -17;
                Check(IsDirty, "저장 후 음량을 바꿨는데 미저장 상태가 아닙니다.");
                OnUndo(this, new RoutedEventArgs());
                Check(!IsDirty && _state.Id == saved.Id, "저장한 음량 상태로 되돌리지 못했습니다.");
                // 저장 지점에서 열기 때문에 확인 대화상자가 필요하지 않습니다.
                await OpenProjectAsync(projectPath);
                Check(!IsDirty && _selectedId == fixture[1].Id && _state.Tracks.Length == saved.Tracks.Length &&
                      _state.MasterDb == saved.MasterDb && _projectPath == Path.GetFullPath(projectPath),
                    "다시 연 프로젝트의 저장 상태·선택·전체 음량이 달라졌습니다.");
                for (int index = 0; index < saved.Tracks.Length; index++)
                {
                    AudioTrack expected = saved.Tracks[index];
                    AudioTrack actual = _state.Tracks[index];
                    Check((actual with { Source = expected.Source }) == expected &&
                          actual.Source.Samples.SequenceEqual(expected.Source.Samples),
                        "프로젝트의 트랙 음량·배치·효과 또는 원본 샘플이 저장 전과 다릅니다.");
                }
                Check(savedAudio.Samples.SequenceEqual(RenderSnapshot(_state, null, false).Samples) &&
                      _workspaceChannels[laneA].VolumeFader.Value == -11,
                    "다시 연 프로젝트의 소리 또는 실제 페이더 값이 다릅니다.");
            });

            await RunCase("일시정지 후 페이더 변경이 이전 미리 듣기를 폐기하고 새 음량으로 준비", async () =>
            {
                ResetFixture();
                await PlayAsync();
                await WaitForPlayback();
                await PlayAsync();
                Check(!_playing && _ready && _previewPath is not null, "미리 듣기를 일시정지하지 못했습니다.");
                string pausedPath = _previewPath!;
                _workspaceChannels[laneA].VolumeFader.Value = -18;
                Check(!_playing && !_ready && !_opening && _previewPath is null,
                    "일시정지 중 음량을 바꾼 뒤 이전 미리 듣기가 재사용 가능한 상태입니다.");
                await PlayAsync();
                await WaitForPlayback();
                Check(_previewPath is not null && _previewPath != pausedPath, "새 음량용 미리 듣기 파일을 준비하지 않았습니다.");
                var preview = WavCodec.Read(_previewPath!);
                double expected = .125 * Math.Pow(10, (-3 - 18 - 1) / 20.0);
                Check(Math.Abs(preview.Samples[0] - expected) < 2.0 / 32768,
                    "새 미리 듣기 파일에 바뀐 트랙 음량이 적용되지 않았습니다.");
                StopPlayback(true);
            });

            await RunCase("재생 중 트랙 음량 변경은 재생 정지와 미리 듣기 무효화", async () =>
            {
                ResetFixture();
                await PlayAsync();
                await WaitForPlayback();
                _workspaceChannels[laneB].VolumeFader.Value = -4;
                Check(!_playing && !_ready && !_opening && _previewPath is null &&
                      _state.Tracks.Single(t => t.EffectiveLaneId == laneB).TrackGainDb == -4,
                    "재생 중 음량 변경 후 예전 소리가 계속 재생되거나 미리 듣기가 남았습니다.");
            });
        }
        finally
        {
            try
            {
                StopPlayback(true);
                // 제한 시간을 넘긴 처리도 원래 상태 복원 뒤 늦게 덮어쓰지 않게 먼저 끝냅니다.
                if (pendingCheck is { IsCompleted: false })
                {
                    try { await pendingCheck; }
                    catch (Exception exception) { results.Add("정리 중 처리 오류: " + exception.Message); }
                }
                StopPlayback(true);
                StopPeak();
                _state = originalState;
                _history.Clear();
                _history.AddRange(originalHistory);
                _historyIndex = originalHistoryIndex;
                _selectedId = originalSelected;
                _savedId = originalSavedId;
                _projectPath = originalProjectPath;
                _coalesceKind = originalCoalesceKind;
                _lastEdit = originalLastEdit;
                _workspaceVolumeDrag = originalVolumeDrag;
                _editCursor = originalCursor;
                _protectedSources.Clear();
                _protectedSources.UnionWith(originalProtectedSources);
                _syncing = true;
                try
                {
                    SelectedOnlyCheck.IsChecked = originalSelectedOnly;
                    BypassCheck.IsChecked = originalBypass;
                    LoopCheck.IsChecked = originalLoop;
                }
                finally { _syncing = false; }
                SetBusy(false);
                RefreshControls();
                DisplayPlaybackPosition(originalCursor);
                PeakText.Text = originalPeakText;
                PeakText.Foreground = originalPeakBrush;
                StatusText.Text = originalStatusText;
                StatusText.Foreground = originalStatusBrush;
                if (peakWasScheduled) SchedulePeak();
                results.Add("검증 전 작업·전체 이력·선택·저장 지점·프로젝트 경로·커서·듣기 옵션·원본 경로 보호를 복원했습니다.");
            }
            finally
            {
                _player.Volume = originalVolume;
                TryDelete(wavePath);
                TryDelete(projectPath);
                await File.WriteAllLinesAsync(outputBase + ".track-gain.txt", results);
            }
        }
    }
}
