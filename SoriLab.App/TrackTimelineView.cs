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
    private static readonly Typeface LabelTypeface = new("Malgun Gothic");
    private static readonly Brush BackgroundBrush = CreateBrush(0x0D, 0x14, 0x21);
    private static readonly Brush HeaderBrush = CreateBrush(0x16, 0x1D, 0x2B);
    private static readonly Brush LabelBrush = CreateBrush(0x14, 0x1B, 0x29);
    private static readonly Brush SelectedLabelBrush = CreateBrush(0x28, 0x24, 0x3C);
    private static readonly Brush ClipBrush = CreateBrush(0x19, 0x39, 0x3C);
    private static readonly Brush SelectedClipBrush = CreateBrush(0x30, 0x29, 0x4A);
    private static readonly Brush InactiveClipBrush = CreateBrush(0x20, 0x27, 0x36);
    private static readonly Brush TextBrush = CreateBrush(0xD6, 0xDE, 0xEB);
    private static readonly Brush MutedTextBrush = CreateBrush(0x82, 0x93, 0xAB);
    private static readonly Brush WaveBrush = CreateBrush(0x62, 0xD5, 0xC5);
    private static readonly Brush InactiveWaveBrush = CreateBrush(0x66, 0x78, 0x8E);
    private static readonly Pen GridPen = CreatePen(CreateBrush(0x22, 0x30, 0x43), 1);
    private static readonly Pen ClipPen = CreatePen(CreateBrush(0x36, 0x6A, 0x67), 1);
    private static readonly Pen SelectedPen = CreatePen(CreateBrush(0xB7, 0x9A, 0xFF), 1.5);
    private static readonly Pen PlayheadPen = CreatePen(CreateBrush(0xFF, 0xD0, 0x7A), 1.5);

    private IReadOnlyList<AudioTrack> _tracks = Array.Empty<AudioTrack>();
    private Guid? _selectedTrackId;
    private double _playheadSeconds;
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
                for (int index = 0; index < Math.Min(32, value.Count); index++)
                {
                    AudioTrack? track = value[index];
                    if (track is not null && ids.Add(track.Id))
                        next.Add(track);
                }
            }
            _tracks = next.AsReadOnly();
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

    public event Action<Guid>? TrackSelected;
    public event Action<Guid, double>? TrackMoved;

    protected override Size MeasureOverride(Size availableSize)
    {
        double width = double.IsFinite(availableSize.Width) ? Math.Max(0, availableSize.Width) : 900;
        return new Size(width, HeaderHeight + Math.Max(1, _tracks.Count) * RowHeight);
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
        DrawText(dc, "소리 레이어", new Point(13, 7), 11, MutedTextBrush, LabelWidth - 24);

        double timelineWidth = TimelineWidth;
        if (timelineWidth <= 0)
            return;
        double duration = _drag?.TimelineDuration ?? GetTimelineDuration();
        double pixelsPerSecond = timelineWidth / duration;
        DrawGrid(dc, duration, pixelsPerSecond);

        if (_tracks.Count == 0)
        {
            DrawText(dc, "소리를 추가하면 시간순으로 겹쳐 배치할 수 있어요.",
                new Point(LabelWidth + 16, HeaderHeight + 25), 12, MutedTextBrush, timelineWidth - 24);
            return;
        }

        bool anySolo = _tracks.Any(track => track.Solo);
        double dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        Pen wavePen = CreatePen(WaveBrush, 1 / dpi);
        Pen inactiveWavePen = CreatePen(InactiveWaveBrush, 1 / dpi);
        for (int row = 0; row < _tracks.Count; row++)
        {
            AudioTrack track = _tracks[row];
            double top = HeaderHeight + row * RowHeight;
            bool selected = track.Id == _selectedTrackId;
            bool audible = !track.Muted && (!anySolo || track.Solo);
            dc.DrawRectangle(selected ? SelectedLabelBrush : LabelBrush, null,
                new Rect(0, top, LabelWidth, RowHeight));
            dc.DrawLine(GridPen, new Point(0, top + RowHeight), new Point(ActualWidth, top + RowHeight));
            string name = track.Source?.Name ?? "읽을 수 없는 소리";
            DrawText(dc, name, new Point(13, top + 10), 12, selected ? TextBrush : MutedTextBrush, LabelWidth - 25);
            string state = track.Muted ? "음소거" : track.Solo ? "단독" : anySolo ? "대기" : "재생";
            string rate = double.IsFinite(track.PlaybackRate) && track.PlaybackRate > 0
                ? track.PlaybackRate.ToString("0.##", CultureInfo.InvariantCulture) + "×"
                : "속도 오류";
            DrawText(dc, state + " · " + rate + (track.Reverse ? " · 역방향" : ""),
                new Point(13, top + 34), 10, audible ? WaveBrush : MutedTextBrush, LabelWidth - 25);

            if (!TryGetClipInfo(track, out ClipInfo info))
            {
                DrawText(dc, "소리 또는 선택 구간을 확인해 주세요.",
                    new Point(LabelWidth + 12, top + 26), 11, MutedTextBrush, timelineWidth - 20);
                continue;
            }

            Rect clipRect = GetClipRect(track, info, row, pixelsPerSecond);
            dc.PushClip(new RectangleGeometry(new Rect(LabelWidth, top, timelineWidth, RowHeight)));
            dc.DrawRoundedRectangle(selected ? SelectedClipBrush : audible ? ClipBrush : InactiveClipBrush,
                selected ? SelectedPen : ClipPen, clipRect, 5, 5);

            double waveWidth = Math.Max(1, clipRect.Width - 8);
            WaveCache cache = GetWaveCache(track, info, waveWidth, dpi);
            dc.PushClip(new RectangleGeometry(clipRect));
            dc.PushTransform(new TranslateTransform(clipRect.Left + 4, clipRect.Top + 5));
            dc.DrawGeometry(null, audible ? wavePen : inactiveWavePen, cache.Geometry);
            dc.Pop();
            dc.Pop();
            dc.Pop();
        }

        if (_playheadSeconds <= duration)
        {
            double x = LabelWidth + _playheadSeconds * pixelsPerSecond;
            dc.DrawLine(PlayheadPen, new Point(x, HeaderHeight),
                new Point(x, Math.Min(ActualHeight, HeaderHeight + _tracks.Count * RowHeight)));
            dc.DrawEllipse(PlayheadPen.Brush, null, new Point(x, HeaderHeight - 3), 3, 3);
        }
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Point point = e.GetPosition(this);
        int row = GetRow(point);
        if (row < 0)
            return;

        Guid id = _tracks[row].Id;
        Focus();
        SelectedTrackId = id;
        TrackSelected?.Invoke(id);
        e.Handled = true;

        // 선택 알림에서 목록이 갱신될 수 있어 최신 모델을 다시 찾습니다.
        row = FindRow(id);
        if (row < 0 || TimelineWidth <= 0)
            return;
        AudioTrack track = _tracks[row];
        if (!TryGetClipInfo(track, out ClipInfo info))
            return;
        double duration = GetTimelineDuration();
        double pixelsPerSecond = TimelineWidth / duration;
        Rect clipRect = GetClipRect(track, info, row, pixelsPerSecond);
        clipRect.Inflate(3, 0);
        if (point.X < LabelWidth || !clipRect.Contains(point))
            return;

        double offset = GetSafeOffset(track.OffsetSeconds, info.Duration);
        _drag = new DragState(id, point.X, offset, offset, info.Duration, duration, pixelsPerSecond);
        if (!CaptureMouse())
        {
            _drag = null;
            return;
        }
        Cursor = Cursors.SizeWE;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        Point point = e.GetPosition(this);
        if (_drag is not null)
        {
            UpdateDrag(point.X);
            e.Handled = true;
            return;
        }

        int row = GetRow(point);
        if (row >= 0 && TimelineWidth > 0 && TryGetClipInfo(_tracks[row], out ClipInfo info))
        {
            Rect clipRect = GetClipRect(_tracks[row], info, row, TimelineWidth / GetTimelineDuration());
            clipRect.Inflate(3, 0);
            Cursor = point.X >= LabelWidth && clipRect.Contains(point) ? Cursors.SizeWE : Cursors.Hand;
        }
        else Cursor = Cursors.Arrow;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_drag is null)
            return;
        UpdateDrag(e.GetPosition(this).X);
        FinishDrag();
        e.Handled = true;
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        FinishDrag();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Escape && _drag is not null)
        {
            CancelDrag();
            e.Handled = true;
        }
    }

    private double TimelineWidth => Math.Max(0, ActualWidth - LabelWidth - 12);

    private int GetRow(Point point)
    {
        if (point.Y < HeaderHeight || point.X < 0 || point.X > ActualWidth)
            return -1;
        int row = (int)((point.Y - HeaderHeight) / RowHeight);
        return row >= 0 && row < _tracks.Count ? row : -1;
    }

    private int FindRow(Guid id)
    {
        for (int row = 0; row < _tracks.Count; row++)
            if (_tracks[row].Id == id)
                return row;
        return -1;
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
        return new Rect(LabelWidth + offset * pixelsPerSecond, HeaderHeight + row * RowHeight + 10,
            Math.Max(6, info.Duration * pixelsPerSecond), RowHeight - 20);
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

    private void UpdateDrag(double x)
    {
        if (_drag is null || !double.IsFinite(x))
            return;
        double offset = GetSafeOffset(_drag.InitialOffset + (x - _drag.StartX) / _drag.PixelsPerSecond, _drag.ClipDuration);
        _drag = _drag with { PreviewOffset = offset };
        InvalidateVisual();
    }

    private void FinishDrag()
    {
        DragState? completed = _drag;
        CancelDrag();
        if (completed is not null && Math.Abs(completed.InitialOffset - completed.PreviewOffset) > 0.000000001)
            TrackMoved?.Invoke(completed.Id, completed.PreviewOffset);
    }

    private void CancelDrag()
    {
        _drag = null;
        if (IsMouseCaptured)
            ReleaseMouseCapture();
        Cursor = Cursors.Arrow;
        InvalidateVisual();
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
            const double waveHeight = RowHeight - 30;
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
    private sealed record DragState(Guid Id, double StartX, double InitialOffset, double PreviewOffset,
        double ClipDuration, double TimelineDuration, double PixelsPerSecond);
    private sealed record WaveCache(AudioClip Source, int StartFrame, int EndFrame, double Width, double Dpi,
        double Rate, bool Reverse, StreamGeometry Geometry);
}
