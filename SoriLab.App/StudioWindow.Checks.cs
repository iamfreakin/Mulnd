using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using SoriLab.Core;

namespace SoriLab.App;

public partial class StudioWindow
{
    /// <summary>운영체제 입력 대신 앱의 처리 함수와 화면 상태를 직접 검증합니다.</summary>
    private async Task RunRegressionChecksAsync(string outputBase)
    {
        if (!Dispatcher.CheckAccess())
            throw new InvalidOperationException("회귀 검증은 화면을 관리하는 스레드에서 실행해야 합니다.");
        if (_state.Tracks.Length == 0 || _busy || _closing)
            throw new InvalidOperationException("회귀 검증은 소리를 하나 이상 연 뒤 다른 처리가 끝난 상태에서 실행해야 합니다.");

        Snapshot originalState = _state;
        Snapshot[] originalHistory = _history.ToArray();
        int originalHistoryIndex = _historyIndex;
        Guid? originalSelected = _selectedId;
        Guid originalSavedId = _savedId;
        string? originalProjectPath = _projectPath;
        string[] originalProtectedSources = _protectedSources.ToArray();
        string? originalCoalesceKind = _coalesceKind;
        DateTime originalLastEdit = _lastEdit;
        double originalVolume = _player.Volume;
        bool? originalSelectedOnly = SelectedOnlyCheck.IsChecked;
        bool? originalBypass = BypassCheck.IsChecked;
        bool peakWasScheduled = _peakDelay.IsEnabled;
        string originalPeakText = PeakText.Text;
        var originalPeakBrush = PeakText.Foreground;
        string originalStatusText = StatusText.Text;
        var originalStatusBrush = StatusText.Foreground;

        string directory = Path.GetDirectoryName(Path.GetFullPath(outputBase))!;
        string prefix = Path.Combine(directory, ".sorilab-regression-" + Guid.NewGuid().ToString("N"));
        string validPath = prefix + "-source.wav";
        string damagedPath = prefix + "-damaged.wav";
        var results = new List<string>();

        static void Check(bool condition, string message)
        {
            if (!condition)
                throw new InvalidOperationException("회귀 검증 실패: " + message);
        }

        try
        {
            // 실제 재생이 잘못 시작되는 회귀가 있어도 검증 중 스피커로 소리를 내지 않습니다.
            _player.Volume = 0;
            StopPlayback(true);
            _peakDelay.Stop();
            _peakCancellation?.Cancel();
            SelectedOnlyCheck.IsChecked = false;
            BypassCheck.IsChecked = false;

            // 사용자 원본은 쓰지 않고, 별도의 파일로 가져오기와 경로 보호를 함께 확인합니다.
            var fixture = new AudioClip("회귀 검증용 무음", 44100, 1, new float[88200]);
            WavCodec.WritePcm16(validPath, fixture);
            await File.WriteAllBytesAsync(damagedPath, new byte[] { 0x52, 0x49, 0x46 });
            ReplaceProject([], 0, null, null);
            _peakDelay.Stop();
            await AddAudioAsync([validPath]);
            _peakDelay.Stop();
            Check(_state.Tracks.Length == 1, "검증용 정상 WAV를 추가하지 못했습니다.");
            AudioTrack importedTrack = _state.Tracks[0];
            Check(importedTrack.SourcePath == Path.GetFullPath(validPath), "가져온 원본 경로가 기록되지 않았습니다.");

            Snapshot beforeDamagedImport = _state;
            Snapshot[] historyBeforeDamagedImport = _history.ToArray();
            int indexBeforeDamagedImport = _historyIndex;
            Guid? selectionBeforeDamagedImport = _selectedId;
            await AddAudioAsync([damagedPath]);
            Check(ReferenceEquals(_state, beforeDamagedImport), "손상된 WAV를 거절하면서 기존 작업 상태가 달라졌습니다.");
            Check(_history.SequenceEqual(historyBeforeDamagedImport) && _historyIndex == indexBeforeDamagedImport &&
                  _selectedId == selectionBeforeDamagedImport && !_busy,
                "손상된 WAV를 거절한 뒤 이력·선택 또는 처리 중 상태가 바뀌었습니다.");
            results.Add("통과: 손상된 WAV를 추가해도 작업·이력·선택이 유지되고 조작 가능한 상태로 돌아옵니다.");

            Snapshot beforeRemoval = _state;
            OnRemove(this, new RoutedEventArgs());
            _peakDelay.Stop();
            Check(_state.Tracks.Length == 0, "마지막 트랙을 제거하지 못했습니다.");
            Check(Inspector.IsEnabled && UndoButton.IsEnabled, "빈 프로젝트에서 실행 취소 버튼을 사용할 수 없습니다.");
            Check(IsProtectedPath(validPath), "트랙을 제거하자 원본 경로의 덮어쓰기 보호가 사라졌습니다.");
            results.Add("통과: 마지막 트랙을 제거해도 편집 패널과 실행 취소 버튼이 활성 상태입니다.");
            results.Add("통과: 제거한 트랙의 원본 경로도 덮어쓰기에서 보호됩니다.");
            OnUndo(this, new RoutedEventArgs());
            _peakDelay.Stop();
            Check(ReferenceEquals(_state, beforeRemoval) && _state.Tracks.Length == 1 && Selected?.Id == importedTrack.Id,
                "빈 프로젝트에서 실행 취소했을 때 제거한 트랙이 복원되지 않았습니다.");
            results.Add("통과: 빈 프로젝트에서 실행 취소하면 제거한 트랙과 선택이 복원됩니다.");

            // 실제 저장 API 대신 저장 지점의 식별자만 고정해 이력 한도와 수정 판정을 검증합니다.
            _savedId = _state.Id;
            for (int edit = 0; edit < 105; edit++)
                CommitState([], edit % 2 == 0 ? -1 : -2, "regression:history");
            _peakDelay.Stop();
            Check(_history.Count == 100 && _history.All(snapshot => snapshot.Id != _savedId),
                "100개 이력 제한에서 검증용 저장 지점이 제거되지 않았습니다.");
            Check(IsDirty, "저장 지점이 이력에서 제거되자 미저장 작업을 저장된 것으로 판단했습니다.");
            while (_historyIndex > 0)
                OnUndo(this, new RoutedEventArgs());
            _peakDelay.Stop();
            Check(IsDirty && _state.Id == _history[0].Id,
                "가장 오래 남은 이력으로 되돌리자 미저장 작업을 저장된 것으로 판단했습니다.");
            results.Add("통과: 저장 지점이 100개 이력 밖으로 밀려나도 남아 있는 가장 오래된 편집은 미저장 상태입니다.");

            // 재생 준비가 비동기로 진행되는 동안 정지를 호출하는 순서를 직접 재현합니다.
            var playbackTrack = importedTrack with { PlaybackRate = 0.5, OffsetSeconds = 0, Muted = false, Solo = false };
            ReplaceProject([playbackTrack], 0, playbackTrack.Id, null);
            _peakDelay.Stop();
            _player.Volume = 0;
            Task preparation = PlayAsync();
            bool preparationWasPending = _busy && !preparation.IsCompleted;
            StopPlayback(false);
            await preparation;
            await System.Windows.Threading.Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Check(preparationWasPending, "재생 준비 중 정지 검증에 필요한 비동기 준비 구간을 관찰하지 못했습니다.");
            Check(!_playing && !_opening && !_ready && _previewPath is null && !_busy,
                "재생 준비 중 정지했는데 준비 완료 후 재생이 다시 시작되거나 시작 대기 상태가 남았습니다.");
            results.Add("통과: 준비 중 정지하면 준비 작업이 끝나도 재생·시작 대기 상태나 임시 재생 파일이 남지 않습니다.");
        }
        finally
        {
            try
            {
                StopPlayback(true);
                _peakDelay.Stop();
                _peakCancellation?.Cancel();
                _state = originalState;
                _history.Clear();
                _history.AddRange(originalHistory);
                _historyIndex = originalHistoryIndex;
                _selectedId = originalSelected;
                _savedId = originalSavedId;
                _projectPath = originalProjectPath;
                _coalesceKind = originalCoalesceKind;
                _lastEdit = originalLastEdit;
                _protectedSources.Clear();
                _protectedSources.UnionWith(originalProtectedSources);
                _syncing = true;
                SelectedOnlyCheck.IsChecked = originalSelectedOnly;
                BypassCheck.IsChecked = originalBypass;
                _syncing = false;
                SetBusy(false);
                RefreshControls();
                PeakText.Text = originalPeakText;
                PeakText.Foreground = originalPeakBrush;
                StatusText.Text = originalStatusText;
                StatusText.Foreground = originalStatusBrush;
                if (peakWasScheduled) SchedulePeak();
            }
            finally
            {
                _player.Volume = originalVolume;
                TryDelete(validPath);
                TryDelete(damagedPath);
            }
        }

        results.Insert(0, "앱 처리 함수와 화면 상태를 직접 검증했습니다. 운영체제의 마우스·키보드 입력 자동화와 실제 저장 API는 검사하지 않았습니다.");
        results.Add("검증 전 작업 상태·전체 이력·선택·저장 지점·원본 경로 보호·음량을 복원했습니다.");
        await File.WriteAllLinesAsync(outputBase + ".regression.txt", results);
    }
}
