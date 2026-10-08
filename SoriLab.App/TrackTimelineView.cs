using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using SoriLab.Core;

namespace SoriLab.App;

/// <summary>원본을 변경하지 않고 여러 소리의 배치와 선택을 조절합니다.</summary>
public sealed class TrackTimelineView : FrameworkElement
{
    private const double HeaderHeight = 30;
    private const double RowHeight = 70;
    private const double LabelWidth = 150;
    private const double MaximumSeconds = 120;
    private const double RightPadding = 12;
    private const double ClipHeight = 54;
    private const double TrimHandleWidth = 8;
    private const double TrimHandleHeight = 10;
    private static readonly Typeface LabelTypeface = new("Malgun Gothic");
    private static readonly Brush BackgroundBrush = CreateBrush(0x20, 0x25, 0x2B);
    private static readonly Brush HeaderBrush = CreateBrush(0x25, 0x2C, 0x33);
    private static readonly Brush LabelBrush = CreateBrush(0x2C, 0x33, 0x3B);
    private static readonly Brush SelectedLabelBrush = CreateBrush(0x34, 0x46, 0x54);
    private static readonly Brush InactiveClipBrush = CreateBrush(0x32, 0x38, 0x3F);
    private static readonly Brush TextBrush = CreateBrush(0xDF, 0xE5, 0xEB);
    private static readonly Brush MutedTextBrush = CreateBrush(0x9A, 0xA5, 0xAF);
    private static readonly Brush InactiveWaveBrush = CreateBrush(0x77, 0x83, 0x8D);
    private static readonly Pen GridPen = CreatePen(CreateBrush(0x36, 0x3E, 0x46), 1);
    private static readonly Pen ClipPen = CreatePen(CreateBrush(0x53, 0x61, 0x6D), 1);
    private static readonly Pen SelectedPen = CreatePen(CreateBrush(0xC5, 0xE5, 0xFF), 1.5);
    private static readonly Pen PlayheadPen = CreatePen(CreateBrush(0xEA, 0xEE, 0xF2), 1.5);
    private static readonly Pen CursorPen = CreatePen(CreateBrush(0x48, 0x9C, 0xD8), 1);
    private static readonly LanePalette[] LanePalettes =
    [
        new(CreateBrush(0x31, 0x4D, 0x63), CreateBrush(0x3A, 0x5C, 0x76), CreateBrush(0x48, 0x9C, 0xD8), CreateBrush(0xA6, 0xCB, 0xE5)),
        new(CreateBrush(0x2C, 0x51, 0x4F), CreateBrush(0x35, 0x5F, 0x5B), CreateBrush(0x52, 0xA5, 0x97), CreateBrush(0x9D, 0xCF, 0xC4)),
        new(CreateBrush(0x49, 0x44, 0x5C), CreateBrush(0x57, 0x51, 0x6E), CreateBrush(0x95, 0x83, 0xBE), CreateBrush(0xC5, 0xBB, 0xDD)),
        new(CreateBrush(0x62, 0x50, 0x3A), CreateBrush(0x72, 0x5D, 0x42), CreateBrush(0xC3, 0x9A, 0x64), CreateBrush(0xE0, 0xC5, 0x9D))
    ];

    private IReadOnlyList<AudioTrack> _tracks = Array.Empty<AudioTrack>();
    private IReadOnlyList<LaneRow> _lanes = Array.Empty<LaneRow>();
    private Guid? _selectedTrackId;
    private double _playheadSeconds;
    private double _cursorSeconds;
    private double _zoom = 1;
    private double _availableWidth = 900;
    private bool _hasAvailableWidth;
    private readonly Dictionary<Guid, WaveCache> _waveCache = new();
    private DragState? _drag;

    public TrackTimelineView()
    {
        SnapsToDevicePixels = true;
        ClipToBounds = true;
        Focusable = true;
    }

    public IReadOnlyList<AudioTrack> Tracks
    {
        get => _tracks;
        set
        {
            CancelDrag();
            // 호출자가 목록을 바꾸더라도 그리는 도중 행 개수가 달라지지 않게 복사합니다.
            var next = new List<AudioTrack>();
            var ids = new HashSet<Guid>();
            if (value is not null)
            {
                for (int index = 0; index < Math.Min(AudioMixer.MaximumClips, value.Count); index++)
                {
                    AudioTrack? track = value[index];
                    if (track is not null && ids.Add(track.Id))
                        next.Add(track);
                }
            }
            _tracks = next.AsReadOnly();
            _lanes = next.GroupBy(track => track.EffectiveLaneId)
                .Select(group => new LaneRow(group.Key, group.ToArray())).ToArray();
            foreach (Guid id in _waveCache.Keys.Where(id => !ids.Contains(id)).ToArray())
                _waveCache.Remove(id);
            InvalidateMeasure();
            InvalidateVisual();
        }
    }

    public Guid? SelectedTrackId
    {
        get => _selectedTrackId;
        set
        {
            if (_selectedTrackId == value)
                return;
            if (_drag is not null && value != _drag.Id)
                CancelDrag();
            _selectedTrackId = value;
            InvalidateVisual();
        }
    }

    public double PlayheadSeconds
    {
        get => _playheadSeconds;
        set
        {
            double next = double.IsFinite(value) ? Math.Clamp(value, 0, MaximumSeconds) : 0;
            if (_playheadSeconds == next)
                return;
            _playheadSeconds = next;
            InvalidateVisual();
        }
    }

    public double CursorSeconds
    {
        get => _cursorSeconds;
        set
        {
            double next = double.IsFinite(value) ? Math.Clamp(value, 0, MaximumSeconds) : 0;
            if (_cursorSeconds == next) return;
            _cursorSeconds = next;
            InvalidateVisual();
        }
    }

    public double Zoom
    {
        get => _zoom;
        set
        {
            double next = double.IsFinite(value) ? Math.Clamp(value, 1, 8) : 1;
            if (_zoom == next) return;
            CancelDrag();
            _zoom = next;
            InvalidateMeasure();
            InvalidateVisual();
        }
    }

    /// <summary>가로 스크롤 영역의 ViewportWidth를 전달하면 1배 확대 기준 폭으로 사용합니다.</summary>
    public double AvailableWidth
    {
        get => _availableWidth;
        set
        {
            if (!double.IsFinite(value) || value <= 0) return;
            double next = Math.Clamp(value, LabelWidth + RightPadding + 1, 32768);
            if (_hasAvailableWidth && Math.Abs(_availableWidth - next) < 0.1) return;
            CancelDrag();
            _availableWidth = next;
            _hasAvailableWidth = true;
            InvalidateMeasure();
            InvalidateVisual();
        }
    }

    public event Action<Guid>? TrackSelected;
    public event Action<Guid, double, Guid>? ClipMoved;
    public event Action<Guid, ClipTrimEdge, double>? ClipTrimmed;
    public event Action<double>? CursorChanged;

    // 화면 입력과 검증이 같은 좌표 판정·미리 보기·확정 경로를 사용합니다.
    internal bool HasPointerOperation => _drag is not null;
    internal ClipTrimEdge? PointerTrimEdge => _drag?.TrimEdge;
    internal AudioTrack? PointerPreviewTrack => _drag?.PreviewTrack;
    internal double? PointerPixelsPerSecond => _drag?.PixelsPerSecond;

    protected override Size MeasureOverride(Size availableSize)
    {
        double baseWidth = _hasAvailableWidth ? _availableWidth :
            double.IsFinite(availableSize.Width) ? Math.Max(LabelWidth + RightPadding + 1, availableSize.Width) : 900;
        double width = LabelWidth + (baseWidth - LabelWidth - RightPadding) * _zoom + RightPadding;
        // 마지막 소리도 빈 행으로 옮겨 독립된 새 트랙을 만들 수 있게 공간을 남깁니다.
        return new Size(width, HeaderHeight + (Math.Max(1, _lanes.Count) + 1) * RowHeight);
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        CancelDrag();
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        if (ActualWidth <= 0 || ActualHeight <= 0)
            return;

        dc.DrawRectangle(BackgroundBrush, null, new Rect(0, 0, ActualWidth, ActualHeight));
        dc.DrawRectangle(HeaderBrush, null, new Rect(0, 0, ActualWidth, HeaderHeight));
        DrawText(dc, "트랙", new Point(13, 7), 11, MutedTextBrush, LabelWidth - 24);

        double timelineWidth = TimelineWidth;
        if (timelineWidth <= 0)
            return;
        double duration = _drag?.TimelineDuration ?? GetTimelineDuration();
        double pixelsPerSecond = timelineWidth / duration;
        DrawGrid(dc, duration, pixelsPerSecond);

        if (_tracks.Count == 0)
        {
            DrawText(dc, "오디오를 추가해 편집을 시작하세요.",
                new Point(LabelWidth + 16, HeaderHeight + 25), 11, MutedTextBrush, timelineWidth - 24);
        }

        bool anySolo = _tracks.Any(track => track.Solo);
        double dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        Pen inactiveWavePen = CreatePen(InactiveWaveBrush, 1 / dpi);
        for (int row = 0; row < _lanes.Count; row++)
        {
            LaneRow lane = _lanes[row];
            LanePalette palette = LanePalettes[row % LanePalettes.Length];
            Pen wavePen = CreatePen(palette.Wave, 1 / dpi);
            double top = HeaderHeight + row * RowHeight;
            bool selected = lane.Clips.Any(track => track.Id == _selectedTrackId);
            dc.DrawRectangle(selected ? SelectedLabelBrush : LabelBrush, null,
                new Rect(0, top, LabelWidth, RowHeight));
            dc.DrawRectangle(palette.Accent, null, new Rect(0, top, 3, RowHeight));
            dc.DrawLine(GridPen, new Point(0, top + RowHeight), new Point(ActualWidth, top + RowHeight));
            string name = lane.Clips[0].Source?.Name ?? "읽을 수 없는 소리";
            DrawText(dc, name, new Point(13, top + 10), 12, selected ? TextBrush : MutedTextBrush, LabelWidth - 25);
            string state = lane.Clips[0].Muted ? "음소거" : lane.Clips[0].Solo ? "단독" : anySolo ? "재생 제외" : "오디오";
            DrawText(dc, $"{state} · {lane.Clips.Length}개 클립",
                new Point(13, top + 34), 10, MutedTextBrush, LabelWidth - 25);

            // 배열 뒤쪽 클립이 앞에 보이며, 클릭 판정도 같은 순서를 거꾸로 확인합니다.
            foreach (AudioTrack track in lane.Clips)
            {
                if (_drag is { HasMoved: true } && _drag.Id == track.Id) continue;
                if (!TryGetClipInfo(track, out ClipInfo info))
                {
                    DrawText(dc, "소리 또는 선택 구간을 확인해 주세요.",
                        new Point(LabelWidth + 12, top + 26), 11, MutedTextBrush, timelineWidth - 20);
                    continue;
                }
                DrawClip(dc, track, info, row, pixelsPerSecond, dpi,
                    !track.Muted && (!anySolo || track.Solo), wavePen, inactiveWavePen);
            }
        }

        if (_tracks.Count > 0)
        {
            double emptyTop = HeaderHeight + _lanes.Count * RowHeight;
            bool newLaneTarget = _drag is { HasMoved: true } && _drag.PreviewLaneId == _drag.NewLaneId;
            dc.DrawRectangle(newLaneTarget ? SelectedLabelBrush : LabelBrush, null,
                new Rect(0, emptyTop, LabelWidth, RowHeight));
            DrawText(dc, "새 트랙", new Point(13, emptyTop + 12), 11, MutedTextBrush, LabelWidth - 25);
            if (!newLaneTarget)
                DrawText(dc, "클립을 이 행으로 드래그해 새 트랙으로 이동",
                    new Point(LabelWidth + 13, emptyTop + 27), 10, MutedTextBrush, timelineWidth - 24);
        }

        // 이동 또는 길이 조절 중인 클립만 맨 앞에 그려 바뀐 구간과 파형을 보여줍니다.
        if (_drag is { HasMoved: true } moving && TryGetClipInfo(moving.PreviewTrack, out ClipInfo movingInfo))
        {
            AudioTrack movingTrack = moving.PreviewTrack;
            int targetRow = moving.PreviewLaneId == moving.NewLaneId ? _lanes.Count : FindLaneRow(moving.PreviewLaneId);
            if (targetRow >= 0)
            {
                Pen wavePen = CreatePen(LanePalettes[targetRow % LanePalettes.Length].Wave, 1 / dpi);
                DrawClip(dc, movingTrack, movingInfo, targetRow, pixelsPerSecond, dpi,
                    !movingTrack.Muted && (!anySolo || movingTrack.Solo), wavePen, inactiveWavePen);
            }
        }

        DrawPosition(dc, _cursorSeconds, duration, pixelsPerSecond, CursorPen, true);
        DrawPosition(dc, _playheadSeconds, duration, pixelsPerSecond, PlayheadPen, false);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (BeginPointer(e.GetPosition(this))) e.Handled = true;
    }

    internal bool BeginPointer(Point point)
    {
        if (!IsEnabled || !double.IsFinite(point.X) || !double.IsFinite(point.Y) ||
            point.X < 0 || point.Y < 0 || point.X > ActualWidth || point.Y > ActualHeight || TimelineWidth <= 0)
            return false;

        CancelDrag();

        AudioTrack? hit = HitClip(point);
        ClipTrimEdge? trimEdge = hit is null ? null : HitTrimEdge(hit, point);
        int row = GetRow(point);
        Focus();

        if (point.X < LabelWidth)
        {
            if (row >= 0 && row < _lanes.Count)
                SelectClip(_lanes[row].Clips[0].Id);
            return true;
        }

        if (hit is null)
        {
            SetCursorFromUser(TimeAt(point.X));
            return true;
        }

        Guid id = hit.Id;
        SelectClip(id);

        // 선택 알림에서 목록이나 배치가 갱신될 수 있어 최신 모델을 다시 찾습니다.
        AudioTrack? track = FindTrack(id);
        if (_selectedTrackId != id || track is null || !TryGetClipInfo(track, out ClipInfo info) || TimelineWidth <= 0)
            return true;
        double duration = GetTimelineDuration();
        double pixelsPerSecond = TimelineWidth / duration;
        double offset = GetSafeOffset(track.OffsetSeconds, info.Duration);
        _drag = new DragState(id, point.X, point.Y, offset, offset, info.Duration, duration, pixelsPerSecond,
            track.EffectiveLaneId, track.EffectiveLaneId, Guid.NewGuid(), false,
            Math.Clamp((point.X - LabelWidth) / pixelsPerSecond, 0, MaximumSeconds),
            track, track, trimEdge, trimEdge == ClipTrimEdge.End ? track.OffsetSeconds + info.Duration : track.OffsetSeconds);
        if (!CaptureMouse())
        {
            _drag = null;
            return true;
        }
        Cursor = trimEdge.HasValue ? Cursors.SizeWE : Cursors.SizeAll;
        return true;
    }

    private void SelectClip(Guid id)
    {
        SelectedTrackId = id;
        TrackSelected?.Invoke(id);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        Point point = e.GetPosition(this);
        if (_drag is not null)
        {
            UpdatePointer(point);
            e.Handled = true;
            return;
        }

        int row = GetRow(point);
        AudioTrack? hit = HitClip(point);
        Cursor = hit is not null ? HitTrimEdge(hit, point).HasValue ? Cursors.SizeWE : Cursors.SizeAll :
            point.X >= LabelWidth ? Cursors.IBeam :
            row >= 0 && row < _lanes.Count ? Cursors.Hand : Cursors.Arrow;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_drag is null)
            return;
        FinishPointer(e.GetPosition(this));
        e.Handled = true;
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        CancelPointer();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Escape && _drag is not null)
        {
            CancelPointer();
            e.Handled = true;
        }
    }

    private double TimelineWidth => Math.Max(0, ActualWidth - LabelWidth - RightPadding);

    private int GetRow(Point point)
    {
        if (point.Y < HeaderHeight || point.X < 0 || point.X > ActualWidth)
            return -1;
        int row = (int)((point.Y - HeaderHeight) / RowHeight);
        return row >= 0 && row < _lanes.Count ? row : -1;
    }

    private int FindLaneRow(Guid id)
    {
        for (int row = 0; row < _lanes.Count; row++)
            if (_lanes[row].Id == id)
                return row;
        return -1;
    }

    private AudioTrack? FindTrack(Guid id) => _tracks.FirstOrDefault(track => track.Id == id);

    private AudioTrack? HitClip(Point point)
    {
        int row = GetRow(point);
        if (row < 0 || point.X < LabelWidth || TimelineWidth <= 0) return null;
        double pixelsPerSecond = TimelineWidth / (_drag?.TimelineDuration ?? GetTimelineDuration());
        AudioTrack[] clips = _lanes[row].Clips;
        for (int index = clips.Length - 1; index >= 0; index--)
        {
            AudioTrack track = clips[index];
            if (!TryGetClipInfo(track, out ClipInfo info)) continue;
            Rect bounds = GetClipRect(track, info, row, pixelsPerSecond);
            bounds.Inflate(2, 0);
            if (bounds.Contains(point)) return track;
        }
        return null;
    }

    internal Rect GetClipBounds(Guid id)
    {
        AudioTrack? track = FindTrack(id);
        if (track is null || TimelineWidth <= 0) return Rect.Empty;
        Guid lane = track.EffectiveLaneId;
        if (_drag is { HasMoved: true } drag && drag.Id == id)
        {
            track = drag.PreviewTrack;
            lane = drag.PreviewLaneId;
        }
        int row = _drag is { HasMoved: true } moving && moving.Id == id && lane == moving.NewLaneId
            ? _lanes.Count : FindLaneRow(lane);
        if (row < 0 || !TryGetClipInfo(track, out ClipInfo info)) return Rect.Empty;
        double pixelsPerSecond = _drag?.PixelsPerSecond ?? TimelineWidth / GetTimelineDuration();
        return GetClipRect(track, info, row, pixelsPerSecond);
    }

    private ClipTrimEdge? HitTrimEdge(AudioTrack track, Point point)
    {
        Rect bounds = GetClipBounds(track.Id);
        if (bounds.IsEmpty || point.Y < bounds.Bottom - TrimHandleHeight || point.Y > bounds.Bottom)
            return null;
        // 아주 짧은 클립도 양쪽 손잡이를 구분하고 위쪽 몸통은 이동용으로 남깁니다.
        double width = Math.Min(TrimHandleWidth, bounds.Width / 2);
        if (point.X >= bounds.Left - 2 && point.X <= bounds.Left + width && point.X <= bounds.Left + bounds.Width / 2)
            return ClipTrimEdge.Start;
        if (point.X >= bounds.Right - width && point.X <= bounds.Right + 2)
            return ClipTrimEdge.End;
        return null;
    }

    private double TimeAt(double x)
    {
        if (TimelineWidth <= 0) return 0;
        double duration = _drag?.TimelineDuration ?? GetTimelineDuration();
        return Math.Clamp((x - LabelWidth) * duration / TimelineWidth, 0, MaximumSeconds);
    }

    private void SetCursorFromUser(double seconds)
    {
        CursorSeconds = seconds;
        CursorChanged?.Invoke(_cursorSeconds);
    }

    private double GetTimelineDuration()
    {
        double end = 0;
        foreach (AudioTrack track in _tracks)
            if (TryGetClipInfo(track, out ClipInfo info))
                end = Math.Max(end, GetSafeOffset(track.OffsetSeconds, info.Duration) + info.Duration);
        return Math.Clamp(end + 0.25, 3, MaximumSeconds);
    }

    private Rect GetClipRect(AudioTrack track, ClipInfo info, int row, double pixelsPerSecond)
    {
        double offset = _drag?.Id == track.Id ? _drag.PreviewOffset : GetSafeOffset(track.OffsetSeconds, info.Duration);
        return new Rect(LabelWidth + offset * pixelsPerSecond, HeaderHeight + row * RowHeight + 8,
            Math.Max(6, info.Duration * pixelsPerSecond), ClipHeight);
    }

    private static double GetSafeOffset(double offset, double duration) =>
        double.IsFinite(offset) ? Math.Clamp(offset, 0, Math.Max(0, MaximumSeconds - duration)) : 0;

    private static bool TryGetClipInfo(AudioTrack track, out ClipInfo info)
    {
        info = default;
        AudioClip? source = track.Source;
        EditSettings? edit = track.Edit;
        if (source?.Samples is null || source.SampleRate <= 0 || source.Channels is not (1 or 2) ||
            source.Samples.Length == 0 || source.Samples.Length % source.Channels != 0 || edit is null ||
            !double.IsFinite(track.PlaybackRate) || track.PlaybackRate <= 0)
            return false;
        int frames = source.Samples.Length / source.Channels;
        if (edit.StartFrame < 0 || edit.EndFrame > frames || edit.EndFrame <= edit.StartFrame)
            return false;
        double duration = (double)(edit.EndFrame - edit.StartFrame) / source.SampleRate / track.PlaybackRate;
        if (!double.IsFinite(duration) || duration <= 0 || duration > MaximumSeconds)
            return false;
        info = new ClipInfo(edit.StartFrame, edit.EndFrame, duration);
        return true;
    }

    internal void UpdatePointer(Point point)
    {
        if (_drag is null || !double.IsFinite(point.X) || !double.IsFinite(point.Y))
            return;
        DragState drag = _drag;
        bool hasMoved = drag.HasMoved ||
            Math.Abs(point.X - drag.StartX) >= SystemParameters.MinimumHorizontalDragDistance ||
            (!drag.TrimEdge.HasValue && Math.Abs(point.Y - drag.StartY) >= SystemParameters.MinimumVerticalDragDistance);
        if (!hasMoved) return;

        if (drag.TrimEdge is ClipTrimEdge edge)
        {
            double originalEdge = edge == ClipTrimEdge.Start
                ? drag.OriginalTrack.OffsetSeconds : drag.OriginalTrack.OffsetSeconds + drag.ClipDuration;
            // 표시 최소 폭과 손잡이 안의 클릭 위치에 영향받지 않도록 처음 누른 위치의 이동량만 더합니다.
            double requested = Math.Clamp(originalEdge + (point.X - drag.StartX) / drag.PixelsPerSecond, 0, MaximumSeconds);
            AudioTrack preview;
            try { preview = ClipEditing.Trim(drag.OriginalTrack, edge, requested); }
            catch (ArgumentException) { preview = drag.OriginalTrack; }
            _drag = drag with
            {
                PreviewTrack = preview, PreviewOffset = preview.OffsetSeconds,
                PreviewLaneId = drag.InitialLaneId, RequestedEdgeSeconds = requested, HasMoved = true
            };
            InvalidateVisual();
            return;
        }

        double offset = GetSafeOffset(drag.InitialOffset + (point.X - drag.StartX) / drag.PixelsPerSecond, drag.ClipDuration);
        int row = Math.Clamp((int)Math.Floor((point.Y - HeaderHeight) / RowHeight), 0, _lanes.Count);
        Guid lane = row == _lanes.Count ? drag.NewLaneId : _lanes[row].Id;
        _drag = drag with
        {
            PreviewOffset = offset, PreviewLaneId = lane, HasMoved = true,
            PreviewTrack = drag.OriginalTrack with { OffsetSeconds = offset, LaneId = lane }
        };
        InvalidateVisual();
    }

    internal void FinishPointer(Point point, bool commit = true)
    {
        if (commit) UpdatePointer(point);
        DragState? completed = _drag;
        CancelDrag();
        if (completed is null || !commit) return;
        if (completed.TrimEdge is ClipTrimEdge edge)
        {
            if (completed.HasMoved && completed.PreviewTrack != completed.OriginalTrack)
                ClipTrimmed?.Invoke(completed.Id, edge, completed.RequestedEdgeSeconds);
            return;
        }
        if (!completed.HasMoved)
        {
            SetCursorFromUser(completed.ClickSeconds);
            return;
        }
        if (Math.Abs(completed.InitialOffset - completed.PreviewOffset) > 0.000000001 ||
            completed.InitialLaneId != completed.PreviewLaneId)
            ClipMoved?.Invoke(completed.Id, completed.PreviewOffset, completed.PreviewLaneId);
    }

    internal void CancelPointer() => CancelDrag();

    private void CancelDrag()
    {
        _drag = null;
        if (IsMouseCaptured)
            ReleaseMouseCapture();
        Cursor = Cursors.Arrow;
        InvalidateVisual();
    }

    private void DrawClip(DrawingContext dc, AudioTrack track, ClipInfo info, int row, double pixelsPerSecond,
        double dpi, bool audible, Pen wavePen, Pen inactiveWavePen)
    {
        Rect clipRect = GetClipRect(track, info, row, pixelsPerSecond);
        bool selected = track.Id == _selectedTrackId;
        LanePalette palette = LanePalettes[row % LanePalettes.Length];
        double top = HeaderHeight + row * RowHeight;
        dc.PushClip(new RectangleGeometry(new Rect(LabelWidth, top, TimelineWidth, RowHeight)));
        dc.DrawRoundedRectangle(audible ? selected ? palette.SelectedFill : palette.Fill : InactiveClipBrush,
            selected ? SelectedPen : ClipPen, clipRect, 2, 2);
        dc.PushClip(new RectangleGeometry(clipRect));
        dc.DrawRectangle(audible ? palette.Accent : InactiveWaveBrush, null,
            new Rect(clipRect.Left + 1, clipRect.Top + 1, Math.Max(0, clipRect.Width - 2), 2));
        if (clipRect.Width >= 36)
        {
            string rate = track.PlaybackRate.ToString("0.##", CultureInfo.InvariantCulture) + "×";
            string suffix = (track.Reverse ? " · 역방향" : "") + (track.Muted ? " · 음소거" : track.Solo ? " · 단독" : "");
            DrawText(dc, track.Source.Name + " · " + rate + suffix,
                new Point(clipRect.Left + 6, clipRect.Top + 4), 10, audible ? TextBrush : MutedTextBrush, clipRect.Width - 12);
        }
        double waveWidth = Math.Max(1, clipRect.Width - 8);
        WaveCache cache = GetWaveCache(track, info, waveWidth, dpi);
        dc.PushTransform(new TranslateTransform(clipRect.Left + 4, clipRect.Top + 21));
        dc.DrawGeometry(null, audible ? wavePen : inactiveWavePen, cache.Geometry);
        dc.Pop();
        DrawTrimHandles(dc, clipRect, selected ? TextBrush : audible ? palette.Wave : InactiveWaveBrush);
        dc.Pop();
        dc.Pop();
    }

    private static void DrawTrimHandles(DrawingContext dc, Rect bounds, Brush brush)
    {
        double width = Math.Min(5, Math.Max(1, (bounds.Width - 2) / 2));
        var pen = CreatePen(brush, 1.4);
        double left = bounds.Left + 1;
        double right = bounds.Right - 1;
        double bottom = bounds.Bottom - 1.5;
        dc.DrawLine(pen, new Point(left, bottom - 6), new Point(left, bottom));
        dc.DrawLine(pen, new Point(left, bottom), new Point(left + width, bottom));
        dc.DrawLine(pen, new Point(right, bottom - 6), new Point(right, bottom));
        dc.DrawLine(pen, new Point(right - width, bottom), new Point(right, bottom));
    }

    private void DrawPosition(DrawingContext dc, double seconds, double duration, double pixelsPerSecond, Pen pen, bool editCursor)
    {
        if (seconds > duration) return;
        double x = LabelWidth + seconds * pixelsPerSecond;
        dc.DrawLine(pen, new Point(x, HeaderHeight), new Point(x, ActualHeight));
        if (editCursor)
        {
            var marker = new StreamGeometry();
            using (StreamGeometryContext context = marker.Open())
            {
                context.BeginFigure(new Point(x - 4, HeaderHeight - 9), true, true);
                context.LineTo(new Point(x + 4, HeaderHeight - 9), true, false);
                context.LineTo(new Point(x, HeaderHeight - 3), true, false);
            }
            marker.Freeze();
            dc.DrawGeometry(pen.Brush, null, marker);
        }
        else dc.DrawEllipse(pen.Brush, null, new Point(x, HeaderHeight - 3), 3, 3);
    }

    private WaveCache GetWaveCache(AudioTrack track, ClipInfo info, double width, double dpi)
    {
        if (_waveCache.TryGetValue(track.Id, out WaveCache? cached) && ReferenceEquals(cached.Source, track.Source) &&
            cached.StartFrame == info.StartFrame && cached.EndFrame == info.EndFrame && cached.Width == width &&
            cached.Dpi == dpi && cached.Rate == track.PlaybackRate && cached.Reverse == track.Reverse)
            return cached;

        AudioClip source = track.Source;
        int count = info.EndFrame - info.StartFrame;
        int columns = Math.Min(count, Math.Max(1, (int)Math.Min(8192, Math.Ceiling(width * dpi))));
        var geometry = new StreamGeometry();
        using (StreamGeometryContext context = geometry.Open())
        {
            const double waveHeight = 28;
            double laneHeight = waveHeight / source.Channels;
            for (int channel = 0; channel < source.Channels; channel++)
            {
                double middle = (channel + 0.5) * laneHeight;
                double amplitude = laneHeight * 0.43;
                for (int column = 0; column < columns; column++)
                {
                    int first = (int)((long)column * count / columns);
                    int last = (int)((long)(column + 1) * count / columns);
                    int start = track.Reverse ? info.EndFrame - last : info.StartFrame + first;
                    int end = track.Reverse ? info.EndFrame - first : info.StartFrame + last;
                    float minimum = float.PositiveInfinity;
                    float maximum = float.NegativeInfinity;
                    for (int frame = start; frame < end; frame++)
                    {
                        float sample = source.Samples[frame * source.Channels + channel];
                        if (!float.IsFinite(sample)) sample = 0;
                        minimum = Math.Min(minimum, sample);
                        maximum = Math.Max(maximum, sample);
                    }
                    double x = (column + 0.5) * width / columns;
                    double top = middle - Math.Clamp(maximum, -1, 1) * amplitude;
                    double bottom = middle - Math.Clamp(minimum, -1, 1) * amplitude;
                    if (bottom - top < 0.65 / dpi) bottom = top + 0.65 / dpi;
                    context.BeginFigure(new Point(x, top), false, false);
                    context.LineTo(new Point(x, bottom), true, false);
                }
            }
        }
        geometry.Freeze();
        cached = new WaveCache(source, info.StartFrame, info.EndFrame, width, dpi, track.PlaybackRate, track.Reverse, geometry);
        _waveCache[track.Id] = cached;
        return cached;
    }

    private void DrawGrid(DrawingContext dc, double duration, double pixelsPerSecond)
    {
        double ideal = duration / Math.Max(2, Math.Floor(TimelineWidth / 85));
        double unit = Math.Pow(10, Math.Floor(Math.Log10(ideal)));
        double scaled = ideal / unit;
        double interval = (scaled <= 1 ? 1 : scaled <= 2 ? 2 : scaled <= 5 ? 5 : 10) * unit;
        for (int tick = 0; tick * interval <= duration + interval * 0.00001; tick++)
        {
            double seconds = tick * interval;
            double x = LabelWidth + seconds * pixelsPerSecond;
            dc.DrawLine(GridPen, new Point(x, HeaderHeight), new Point(x, ActualHeight));
            DrawText(dc, seconds.ToString(interval < 1 ? "0.0" : "0", CultureInfo.InvariantCulture) + "초",
                new Point(x + 4, 7), 10, MutedTextBrush, Math.Max(1, ActualWidth - x - 4));
        }
    }

    private void DrawText(DrawingContext dc, string text, Point point, double fontSize, Brush brush, double maxWidth)
    {
        if (maxWidth <= 0) return;
        var formatted = new FormattedText(text, CultureInfo.GetCultureInfo("ko-KR"), FlowDirection.LeftToRight,
            LabelTypeface, fontSize, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip)
        {
            MaxTextWidth = maxWidth,
            MaxLineCount = 1,
            Trimming = TextTrimming.CharacterEllipsis
        };
        dc.DrawText(formatted, point);
    }

    private static SolidColorBrush CreateBrush(byte red, byte green, byte blue)
    {
        var brush = new SolidColorBrush(Color.FromRgb(red, green, blue));
        brush.Freeze();
        return brush;
    }

    private static Pen CreatePen(Brush brush, double width)
    {
        var pen = new Pen(brush, width);
        pen.Freeze();
        return pen;
    }

    private readonly record struct ClipInfo(int StartFrame, int EndFrame, double Duration);
    private sealed record LanePalette(Brush Fill, Brush SelectedFill, Brush Accent, Brush Wave);
    private sealed record LaneRow(Guid Id, AudioTrack[] Clips);
    private sealed record DragState(Guid Id, double StartX, double StartY, double InitialOffset, double PreviewOffset,
        double ClipDuration, double TimelineDuration, double PixelsPerSecond, Guid InitialLaneId, Guid PreviewLaneId,
        Guid NewLaneId, bool HasMoved, double ClickSeconds, AudioTrack OriginalTrack, AudioTrack PreviewTrack,
        ClipTrimEdge? TrimEdge, double RequestedEdgeSeconds);
    private sealed record WaveCache(AudioClip Source, int StartFrame, int EndFrame, double Width, double Dpi,
        double Rate, bool Reverse, StreamGeometry Geometry);
}
