using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using SoriLab.Core;

namespace SoriLab.App;

public partial class StudioWindow
{
    private double _editCursor;

    private void UpdateTimelineViewport()
    {
        double width = TimelineScroll.ViewportWidth;
        if (double.IsFinite(width) && width > 180) Timeline.AvailableWidth = width;
    }

    private void SetEditCursor(double seconds)
    {
        if (_busy || !CommitNumbers()) return;
        _editCursor = Math.Clamp(seconds, 0, AudioMixer.MaximumDurationSeconds);
        StopPlayback(true);
        RefreshCursorControls();
    }

    private void RefreshCursorControls()
    {
        Timeline.CursorSeconds = _editCursor;
        CursorInput.Text = FormatSeconds(_editCursor);
        SplitButton.IsEnabled = !_busy && Selected is { } clip && _editCursor > clip.OffsetSeconds && _editCursor < clip.OffsetSeconds + AudioMixer.GetDurationSeconds(clip);
        ZoomText.Text = $"{Timeline.Zoom * 100:0}%";
    }

    private void OnCursorCommitted(object sender, KeyboardFocusChangedEventArgs e) => CommitCursor();
    private void OnCursorKeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) { CommitCursor(); e.Handled = true; } }
    private bool CommitCursor()
    {
        if (_busy) return false;
        if (CursorInput.Text == FormatSeconds(_editCursor)) return true;
        if (!ParseNumber(CursorInput.Text, out double seconds) || seconds < 0 || seconds > AudioMixer.MaximumDurationSeconds)
        { RefreshCursorControls(); Status("편집 커서는 0~120초 사이로 지정하세요.", true); return false; }
        if (seconds != _editCursor) SetEditCursor(seconds);
        return true;
    }

    private void OnZoomIn(object sender, RoutedEventArgs e) => SetZoom(Timeline.Zoom * 2);
    private void OnZoomOut(object sender, RoutedEventArgs e) => SetZoom(Timeline.Zoom / 2);
    private void OnZoomFit(object sender, RoutedEventArgs e) { SetZoom(1); TimelineScroll.ScrollToHorizontalOffset(0); }
    private void SetZoom(double zoom)
    {
        Timeline.Zoom = Math.Clamp(zoom, 1, 8);
        RefreshCursorControls();
        UpdateTimelineViewport();
    }

    private void MoveClip(Guid id, double offset, Guid targetLane)
    {
        if (_busy || !CommitNumbers()) { RefreshControls(); return; }
        var lane = _state.Tracks.FirstOrDefault(t => t.EffectiveLaneId == targetLane);
        var next = _state.Tracks.Select(t => t.Id == id ? t with
        {
            OffsetSeconds = offset, LaneId = targetLane,
            Muted = lane?.Muted ?? false, Solo = lane?.Solo ?? false, TrackGainDb = lane?.TrackGainDb ?? 0
        } : t).ToArray();
        CommitState(next, _state.MasterDb, "clip-move", id);
        // 실패하거나 같은 위치로 놓은 경우에도 임시 드래그 표시를 실제 상태로 돌립니다.
        RefreshControls();
    }

    private async void OnAddToTrack(object sender, RoutedEventArgs e)
    {
        if (_busy || Selected is not { } selected || !CommitNumbers() || !CommitCursor()) return;
        var dialog = new OpenFileDialog { Title = "선택 트랙의 커서 위치에 클립 추가", Filter = "WAV 오디오|*.wav", Multiselect = true };
        if (dialog.ShowDialog(this) == true) await AddAudioAsync(dialog.FileNames, selected.EffectiveLaneId);
    }

    private void TrimClip(Guid id, ClipTrimEdge edge, double timelineSeconds)
    {
        if (_busy || !CommitNumbers()) { RefreshControls(); return; }
        var original = _state.Tracks.FirstOrDefault(track => track.Id == id);
        if (original is null) return;
        try
        {
            var trimmed = ClipEditing.Trim(original, edge, timelineSeconds);
            var previous = _state.Id;
            CommitState(_state.Tracks.Select(track => track.Id == id ? trimmed : track).ToArray(),
                _state.MasterDb, "clip-trim", id);
            if (_state.Id != previous)
                Status("클립 구간을 조절했습니다. 원본 범위 안에서 다시 늘릴 수 있으며 Ctrl+Z로 되돌릴 수 있습니다.");
        }
        catch (Exception ex) when (IsExpected(ex)) { Status("클립 구간을 조절하지 못했습니다. " + ex.Message, true); }
        finally { RefreshControls(); }
    }

    private async void OnSplit(object sender, RoutedEventArgs e) => await SplitSelectedAsync();
    private async Task SplitSelectedAsync()
    {
        if (_busy || !CommitNumbers() || !CommitCursor() || Selected is not { } selected) return;
        SetBusy(true); StopPlayback(true); _peakCancellation?.Cancel();
        try
        {
            var split = await Task.Run(() => ClipEditing.Split(selected, _editCursor));
            var next = _state.Tracks.SelectMany(t => t.Id == selected.Id ? new[] { split.Left, split.Right } : new[] { t }).ToArray();
            SetBusy(false);
            var previous = _state.Id;
            CommitState(next, _state.MasterDb, "split", split.Right.Id);
            if (_state.Id != previous) Status("클립을 나눴습니다. 현재 효과는 소리에 반영됐으며, Ctrl+Z로 분할 전 상태를 복원할 수 있습니다.");
        }
        catch (Exception ex) when (IsExpected(ex)) { Status("클립을 나누지 못했습니다. " + ex.Message, true); }
        finally { SetBusy(false); RefreshCursorControls(); }
    }

    private async Task RunArrangementChecksAsync(string outputBase)
    {
        var first = _state.Tracks[0];
        SelectTrack(first.Id);
        double beforeLength = AudioMixer.GetDurationSeconds(first);
        var before = RenderSnapshot(_state, null, false);
        int beforeCount = _state.Tracks.Length;
        SetEditCursor(first.OffsetSeconds + beforeLength * .4);
        await SplitSelectedAsync();
        if (_state.Tracks.Length != beforeCount + 1 || _state.Tracks.Count(t => t.EffectiveLaneId == first.EffectiveLaneId) != 2)
            throw new InvalidOperationException("같은 트랙에 두 클립 분할 검증 실패");
        if (!before.Samples.SequenceEqual(RenderSnapshot(_state, null, false).Samples))
            throw new InvalidOperationException("분할 후 전체 소리 일치 검증 실패");
        MuteCheck.IsChecked = true;
        if (_state.Tracks.Where(t => t.EffectiveLaneId == first.EffectiveLaneId).Any(t => !t.Muted))
            throw new InvalidOperationException("트랙 전체 음소거 검증 실패");
        MoveHistory(-1);
        var selected = Selected!;
        var target = _state.Tracks.First(t => t.EffectiveLaneId != selected.EffectiveLaneId);
        MoveClip(selected.Id, 1, target.EffectiveLaneId);
        if (Selected?.EffectiveLaneId != target.EffectiveLaneId || Selected.OffsetSeconds != 1)
            throw new InvalidOperationException("클립의 트랙 사이 이동 실패");
        MoveHistory(-1);
        SelectTrack(_state.Tracks.Last().Id);
        int count = _state.Tracks.Length;
        Guid laneId = Selected!.EffectiveLaneId;
        OnDuplicate(this, new RoutedEventArgs());
        if (_state.Tracks.Length != count + 1 || Selected!.EffectiveLaneId != laneId)
            throw new InvalidOperationException("같은 트랙에 클립 복제 실패");
        SetZoom(4);
        Timeline.UpdateLayout();
        TimelineScroll.UpdateLayout();
        if (Timeline.DesiredSize.Width <= TimelineScroll.ViewportWidth * 2)
            throw new InvalidOperationException("시간축 확대와 가로 스크롤 검증 실패");
        Program.SaveScreenshot(this, outputBase + "-zoom.png");
        OnZoomFit(this, new RoutedEventArgs());
        await File.WriteAllTextAsync(outputBase + ".arrangement.txt", "통과: 분할 전후 전체 소리 일치, 같은 트랙의 클립 분할·복제, 트랙 전체 음소거, 다른 트랙으로 이동과 실행 취소, 시간축 확대와 스크롤 폭. 앱 처리 함수를 통한 검증이며 실제 마우스 조작 자동화는 아닙니다.");
    }
}
