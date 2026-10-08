using System.IO;
using System.Windows;
using System.Windows.Input;
using SoriLab.Core;

namespace SoriLab.App;

public partial class StudioWindow
{
    private async Task RunClipTrimChecksAsync(string outputBase)
    {
        var originalState = _state;
        var originalHistory = _history.ToArray();
        int originalIndex = _historyIndex;
        var originalSaved = _savedId;
        var originalSelected = _selectedId;
        var originalPath = _projectPath;
        var originalKind = _coalesceKind;
        var originalLastEdit = _lastEdit;
        double originalCursor = _editCursor;
        double originalZoom = Timeline.Zoom;
        double originalScroll = TimelineScroll.HorizontalOffset;
        bool originalPeakScheduled = _peakDelay.IsEnabled;
        var results = new List<string>();
        string path = outputBase + ".trim-check-" + Guid.NewGuid().ToString("N") + ".sorilab";
        var source = new AudioClip("자르기 검증", 48000, 1,
            Enumerable.Range(0, 192000).Select(i => (float)((i % 101 - 50) / 128d)).ToArray());
        var first = new AudioTrack(Guid.NewGuid(), source, new EditSettings(48000, 144000, -2, 20, 30), 1, TrackGainDb: -6);
        var other = new AudioTrack(Guid.NewGuid(), source, new EditSettings(0, 48000, 0, 0, 0), 3);
        int trimmedEvents = 0;
        int movedEvents = 0;
        void CountTrim(Guid id, ClipTrimEdge edge, double seconds) => trimmedEvents++;
        void CountMove(Guid id, double seconds, Guid lane) => movedEvents++;
        Timeline.ClipTrimmed += CountTrim;
        Timeline.ClipMoved += CountMove;

        static void Check(bool value, string message)
        {
            if (!value) throw new InvalidOperationException("타임라인 자르기 검증 실패: " + message);
        }

        void Reset(AudioTrack? clip = null)
        {
            Timeline.CancelPointer();
            ReplaceProject([clip ?? first, other], -3, first.Id, null);
            _peakDelay.Stop(); _peakCancellation?.Cancel();
            SetZoom(1);
            TimelineScroll.ScrollToHorizontalOffset(0);
            UpdateLayout(); Timeline.UpdateLayout();
            trimmedEvents = movedEvents = 0;
        }

        Point Begin(ClipTrimEdge edge)
        {
            Rect bounds = Timeline.GetClipBounds(first.Id);
            Check(!bounds.IsEmpty, "클립의 화면 위치를 찾지 못했습니다.");
            var point = new Point(edge == ClipTrimEdge.Start ? bounds.Left + 2 : bounds.Right - 2, bounds.Bottom - 4);
            Check(Timeline.BeginPointer(point) && Timeline.HasPointerOperation && Timeline.PointerTrimEdge == edge,
                "양끝 손잡이에서 자르기를 시작하지 못했습니다.");
            return point;
        }

        void Drag(ClipTrimEdge edge, double deltaSeconds)
        {
            Point start = Begin(edge);
            var end = new Point(start.X + deltaSeconds * Timeline.PointerPixelsPerSecond!.Value, start.Y);
            Timeline.UpdatePointer(end);
            Timeline.FinishPointer(end);
        }

        try
        {
            Reset();
            var stateBefore = _state;
            Point start = Begin(ClipTrimEdge.Start);
            var end = new Point(start.X + .5 * Timeline.PointerPixelsPerSecond!.Value, start.Y);
            Timeline.UpdatePointer(end);
            Check(ReferenceEquals(_state, stateBefore) && _history.Count == 1 && !IsDirty,
                "드래그 미리 보기만으로 프로젝트와 이력을 변경했습니다.");
            var preview = Timeline.PointerPreviewTrack;
            Check(preview is not null && preview.Edit.StartFrame == 72000 && preview.Edit.EndFrame == 144000 &&
                Math.Abs(preview.OffsetSeconds - 1.5) < 1e-9, "왼쪽 자르기의 미리 보기 구간이 다릅니다.");
            Timeline.FinishPointer(end);
            Check(trimmedEvents == 1 && movedEvents == 0 && _history.Count == 2 && Selected == preview &&
                ReferenceEquals(Selected!.Source, source) && Selected.Edit.GainDb == -2 && Selected.TrackGainDb == -6,
                "놓을 때 한 번만 적용하거나 원본과 음량을 보존하지 못했습니다.");
            OnUndo(this, new RoutedEventArgs());
            Check(_state == stateBefore && !IsDirty, "한 번의 실행 취소로 자르기 전 상태를 복원하지 못했습니다.");
            OnRedo(this, new RoutedEventArgs());
            Check(Selected == preview, "다시 실행한 자르기 구간이 다릅니다.");
            results.Add("통과: 왼쪽 손잡이 미리 보기, 놓을 때 한 번 적용, 반대 끝 고정, 원본·효과·트랙 음량 보존, 실행 취소·다시 실행");

            Reset();
            Drag(ClipTrimEdge.End, -1);
            Check(Selected!.Edit.StartFrame == 48000 && Selected.Edit.EndFrame == 96000 && Selected.OffsetSeconds == 1,
                "오른쪽 자르기가 시작 위치를 옮겼습니다.");
            Drag(ClipTrimEdge.End, 10);
            Check(Selected!.Edit.EndFrame == source.FrameCount && Selected.OffsetSeconds == 1,
                "오른쪽을 늘릴 때 원본 끝에 멈추지 않았습니다.");
            Drag(ClipTrimEdge.Start, -10);
            Check(Selected!.Edit.StartFrame == 0 && Selected.OffsetSeconds == 0,
                "왼쪽을 늘릴 때 원본 시작과 프로젝트 0초에 멈추지 않았습니다.");
            Drag(ClipTrimEdge.End, -10);
            Check(Selected!.Edit.EndFrame - Selected.Edit.StartFrame == 1,
                "최소 한 프레임을 남기지 않았습니다.");
            results.Add("통과: 오른쪽 자르기, 잘라낸 구간 다시 늘리기, 원본·0초·최소 길이 경계");

            Reset();
            var clean = _state;
            start = Begin(ClipTrimEdge.Start);
            Timeline.FinishPointer(start);
            Check(_state == clean && trimmedEvents == 0, "손잡이를 클릭만 했는데 편집했습니다.");
            start = Begin(ClipTrimEdge.End);
            Timeline.UpdatePointer(new Point(start.X - 30, start.Y));
            Timeline.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(Timeline), 0, Key.Escape)
                { RoutedEvent = Keyboard.KeyDownEvent });
            Check(!Timeline.HasPointerOperation && _state == clean && trimmedEvents == 0,
                "Esc 취소가 드래그 상태나 미리 보기를 남겼습니다.");
            start = Begin(ClipTrimEdge.End);
            Timeline.UpdatePointer(new Point(start.X - 30, start.Y));
            Timeline.ReleaseMouseCapture();
            Check(!Timeline.HasPointerOperation && _state == clean && trimmedEvents == 0,
                "마우스 캡처를 잃었을 때 편집이 적용됐습니다.");
            start = Begin(ClipTrimEdge.End);
            Timeline.UpdatePointer(new Point(start.X - 30, start.Y));
            SetZoom(2);
            Check(!Timeline.HasPointerOperation && _state == clean && trimmedEvents == 0,
                "확대 변경 중 드래그가 남았습니다.");
            results.Add("통과: 클릭만 하기, Esc·캡처 해제·확대 변경 시 편집 없이 취소");

            Reset(first with { PlaybackRate = 2, Reverse = true });
            Drag(ClipTrimEdge.Start, .25);
            Check(Selected!.Edit.StartFrame == 48000 && Selected.Edit.EndFrame == 120000 &&
                Selected.OffsetSeconds == 1.25 && Selected.Reverse && Selected.PlaybackRate == 2,
                "역재생·2배속 왼쪽 자르기의 원본 구간이 잘못됐습니다.");
            Drag(ClipTrimEdge.End, -.25);
            Check(Selected!.Edit.StartFrame == 72000 && Selected.Edit.EndFrame == 120000 && Selected.OffsetSeconds == 1.25,
                "역재생·2배속 오른쪽 자르기의 원본 구간이 잘못됐습니다.");
            results.Add("통과: 역재생·2배속에서도 화면의 왼쪽과 오른쪽에 맞는 원본 구간 조절");

            Reset(first with { Edit = first.Edit with { EndFrame = 48001 } });
            var bounds = Timeline.GetClipBounds(first.Id);
            start = new Point(bounds.Left + bounds.Width / 2, bounds.Top + 10);
            Check(Timeline.BeginPointer(start) && Timeline.PointerTrimEdge is null, "아주 짧은 클립의 위쪽 몸통을 이동할 수 없습니다.");
            end = new Point(start.X + .5 * Timeline.PointerPixelsPerSecond!.Value, start.Y);
            Timeline.UpdatePointer(end); Timeline.FinishPointer(end);
            Check(movedEvents == 1 && trimmedEvents == 0 && Selected!.Edit.EndFrame - Selected.Edit.StartFrame == 1 &&
                Math.Abs(Selected.OffsetSeconds - 1.5) < 1e-8, "짧은 클립 이동이 자르기로 처리됐습니다.");
            results.Add("통과: 아주 짧은 클립도 위쪽 몸통으로 이동 가능, 기존 이동 이벤트 유지");

            Reset();
            Drag(ClipTrimEdge.Start, .5);
            Drag(ClipTrimEdge.End, -.5);
            _projectPath = path;
            var savedTracks = _state.Tracks;
            var savedAudio = RenderSnapshot(_state, null, false);
            Check(await SaveProjectAsync(false) && !IsDirty, "자른 프로젝트를 저장하지 못했습니다.");
            await OpenProjectAsync(path);
            Check(!IsDirty && _state.Tracks.Length == savedTracks.Length &&
                _state.Tracks.Zip(savedTracks).All(pair => (pair.First with { Source = pair.Second.Source }) == pair.Second) &&
                savedAudio.Samples.SequenceEqual(RenderSnapshot(_state, null, false).Samples),
                "다시 연 프로젝트의 자르기 설정이나 소리가 달라졌습니다.");
            results.Add("통과: 자른 구간의 프로젝트 저장·재열기와 렌더 결과 보존");
        }
        finally
        {
            Timeline.CancelPointer();
            Timeline.ClipTrimmed -= CountTrim;
            Timeline.ClipMoved -= CountMove;
            StopPlayback(true); _peakDelay.Stop(); _peakCancellation?.Cancel();
            _state = originalState; _savedId = originalSaved; _selectedId = originalSelected; _projectPath = originalPath;
            _history.Clear(); _history.AddRange(originalHistory); _historyIndex = originalIndex;
            _coalesceKind = originalKind; _lastEdit = originalLastEdit; _editCursor = originalCursor;
            SetBusy(false); RefreshControls();
            SetZoom(originalZoom); TimelineScroll.ScrollToHorizontalOffset(originalScroll);
            if (originalPeakScheduled) SchedulePeak();
            TryDelete(path);
        }
        results.Insert(0, "실제 입력과 같은 포인터 처리 함수를 좌표로 호출해 화면 상태와 편집 결과를 비교했습니다. 운영체제 마우스 자동화는 아닙니다.");
        await File.WriteAllLinesAsync(outputBase + ".clip-trim.txt", results);
    }
}
