using System;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using SoriLab.Core;

namespace SoriLab.App;

/// <summary>샘플 프레임 단위로 구간을 선택하는 파형 화면입니다.</summary>
public sealed class WaveformView : FrameworkElement
{
    private static readonly Brush BackgroundBrush = MakeBrush(0x20, 0x25, 0x2B);
    private static readonly Brush TextBrush = MakeBrush(0xA5, 0xB0, 0xBA);
    private static readonly Brush QuietTextBrush = MakeBrush(0x86, 0x93, 0x9F);
    private static readonly Brush WaveBrush = MakeBrush(0x72, 0xB4, 0xB6);
    private static readonly Brush SelectionBrush = MakeBrush(0x48, 0x9C, 0xD8, 48);
    private static readonly Brush HandleBrush = MakeBrush(0x48, 0x9C, 0xD8);
    private static readonly Pen GridPen = MakePen(MakeBrush(0x34, 0x3C, 0x45), 1);
    private static readonly Pen MidlinePen = MakePen(MakeBrush(0x4A, 0x55, 0x60), 1);
    private static readonly Pen HandlePen = MakePen(HandleBrush, 1.5);
    private static readonly Pen PlayheadPen = MakePen(MakeBrush(0xEA, 0xEE, 0xF2), 1.5);
    private static readonly Typeface LabelTypeface = new("Malgun Gothic");

    private AudioClip? _clip;
    private double _playheadSeconds;
    private StreamGeometry[] _waveGeometry = Array.Empty<StreamGeometry>();
    private Rect _cachedPlot;
    private double _cachedDpi;
    private bool _cacheDirty = true;
    private DragKind _dragKind;
    private int _anchorFrame;
    private int _initialStart;
    private int _initialEnd;

    public WaveformView()
    {
        SnapsToDevicePixels = true;
        ClipToBounds = true;
        Cursor = Cursors.Cross;
    }

    public new AudioClip? Clip
    {
        get => _clip;
        set
        {
            if (ReferenceEquals(_clip, value))
                return;

            // 다른 음원을 열 때 이전 드래그의 완료 이벤트가 새 선택을 덮지 않게 합니다.
            _dragKind = DragKind.None;
            if (IsMouseCaptured)
                ReleaseMouseCapture();
            _clip = value;
            SelectionStart = 0;
            SelectionEnd = value?.FrameCount ?? 0;
            _playheadSeconds = 0;
            _cacheDirty = true;
            InvalidateVisual();
        }
    }

    public int SelectionStart { get; private set; }
    public int SelectionEnd { get; private set; }

    public double PlayheadSeconds
    {
        get => _playheadSeconds;
        set
        {
            double next = double.IsFinite(value) ? Math.Max(0, value) : 0;
            if (_playheadSeconds == next)
                return;
            _playheadSeconds = next;
            InvalidateVisual();
        }
    }

    public event Action<int, int>? SelectionChanged;

    public void SetSelection(int start, int end)
    {
        int frameCount = _clip?.FrameCount ?? 0;
        if (frameCount == 0)
        {
            SelectionStart = 0;
            SelectionEnd = 0;
        }
        else
        {
            int first = Math.Min(start, end);
            int last = Math.Max(start, end);
            SelectionStart = Math.Clamp(first, 0, frameCount - 1);
            SelectionEnd = Math.Clamp(last, SelectionStart + 1, frameCount);
        }
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        dc.DrawRectangle(BackgroundBrush, null,
            new Rect(0, 0, ActualWidth, ActualHeight));

        if (ActualWidth < 80 || ActualHeight < 100)
            return;

        if (_clip is null || _clip.FrameCount == 0)
        {
            Rect emptyPlot = PlotRect;
            for (int line = 1; line < 8; line++)
            {
                double x = emptyPlot.Left + emptyPlot.Width * line / 8;
                dc.DrawLine(GridPen, new Point(x, emptyPlot.Top), new Point(x, emptyPlot.Bottom));
            }
            dc.DrawLine(GridPen, new Point(emptyPlot.Left, emptyPlot.Top + emptyPlot.Height / 2),
                new Point(emptyPlot.Right, emptyPlot.Top + emptyPlot.Height / 2));
            return;
        }

        Rect plot = PlotRect;
        EnsureWaveCache(plot);
        DrawTimeGrid(dc, plot);

        double startX = FrameToX(SelectionStart, plot);
        double endX = FrameToX(SelectionEnd, plot);
        dc.DrawRectangle(SelectionBrush, null,
            new Rect(startX, plot.Top, Math.Max(0, endX - startX), plot.Height));

        int laneCount = Math.Min(2, _clip.Channels);
        double laneHeight = plot.Height / laneCount;
        Pen wavePen = MakePen(WaveBrush, 1 / VisualTreeHelper.GetDpi(this).DpiScaleX);
        for (int channel = 0; channel < laneCount; channel++)
        {
            double middle = plot.Top + (channel + 0.5) * laneHeight;
            dc.DrawLine(MidlinePen, new Point(plot.Left, middle), new Point(plot.Right, middle));
            dc.DrawGeometry(null, wavePen, _waveGeometry[channel]);
            DrawText(dc, laneCount == 1 ? "M" : channel == 0 ? "L" : "R",
                new Point(18, middle - 8), 11, TextBrush);
        }

        DrawHandle(dc, startX, plot, true);
        DrawHandle(dc, endX, plot, false);

        double playheadRatio = _clip.DurationSeconds > 0
            ? Math.Clamp(_playheadSeconds / _clip.DurationSeconds, 0, 1)
            : 0;
        double playheadX = plot.Left + playheadRatio * plot.Width;
        dc.DrawLine(PlayheadPen, new Point(playheadX, plot.Top), new Point(playheadX, plot.Bottom));
        dc.DrawEllipse(PlayheadPen.Brush, null, new Point(playheadX, plot.Top - 3), 3, 3);

        DrawText(dc, "구간 선택: 드래그  ·  길이 조절: 양끝 손잡이",
            new Point(plot.Left, ActualHeight - 23), 10, QuietTextBrush);
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        _cacheDirty = true;
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (_clip is null || _clip.FrameCount == 0)
            return;

        Point position = e.GetPosition(this);
        Rect plot = PlotRect;
        var hitArea = new Rect(plot.Left - 8, plot.Top - 8, plot.Width + 16, plot.Height + 16);
        if (!hitArea.Contains(position))
            return;

        _initialStart = SelectionStart;
        _initialEnd = SelectionEnd;
        _dragKind = HitHandle(position.X, plot);
        if (_dragKind == DragKind.None)
        {
            _dragKind = DragKind.Create;
            // 0번 프레임도 유효하므로 프레임 값과 드래그 상태를 분리합니다.
            _anchorFrame = Math.Clamp(XToFrame(position.X, plot), 0, _clip.FrameCount - 1);
            SetSelection(_anchorFrame, _anchorFrame + 1);
        }

        if (!CaptureMouse())
        {
            _dragKind = DragKind.None;
            SetSelection(_initialStart, _initialEnd);
            return;
        }
        Cursor = _dragKind == DragKind.Create ? Cursors.Cross : Cursors.SizeWE;
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_clip is null || _clip.FrameCount == 0)
            return;

        Point position = e.GetPosition(this);
        Rect plot = PlotRect;
        if (_dragKind == DragKind.None)
        {
            Cursor = position.Y >= plot.Top - 8 && position.Y <= plot.Bottom + 8
                     && HitHandle(position.X, plot) != DragKind.None
                ? Cursors.SizeWE
                : Cursors.Cross;
            return;
        }

        UpdateDrag(position.X, plot);
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_dragKind == DragKind.None)
            return;

        UpdateDrag(e.GetPosition(this).X, PlotRect);
        FinishDrag();
        e.Handled = true;
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        if (_dragKind != DragKind.None)
            FinishDrag();
    }

    private Rect PlotRect => new(44, 36, Math.Max(1, ActualWidth - 64), Math.Max(1, ActualHeight - 70));

    private void UpdateDrag(double x, Rect plot)
    {
        if (_clip is null || _clip.FrameCount == 0)
            return;

        int frame = XToFrame(x, plot);
        switch (_dragKind)
        {
            case DragKind.Start:
                SetSelection(Math.Clamp(frame, 0, SelectionEnd - 1), SelectionEnd);
                break;
            case DragKind.End:
                SetSelection(SelectionStart, Math.Clamp(frame, SelectionStart + 1, _clip.FrameCount));
                break;
            case DragKind.Create:
                SetSelection(Math.Min(frame, _anchorFrame), Math.Max(frame, _anchorFrame + 1));
                break;
        }
    }

    private void FinishDrag()
    {
        // 캡처 해제에 따라 다시 호출되는 이벤트에서 완료 알림을 중복 발행하지 않습니다.
        _dragKind = DragKind.None;
        if (IsMouseCaptured)
            ReleaseMouseCapture();
        Cursor = Cursors.Cross;
        if (_initialStart != SelectionStart || _initialEnd != SelectionEnd)
            SelectionChanged?.Invoke(SelectionStart, SelectionEnd);
    }

    private DragKind HitHandle(double x, Rect plot)
    {
        double startDistance = Math.Abs(x - FrameToX(SelectionStart, plot));
        double endDistance = Math.Abs(x - FrameToX(SelectionEnd, plot));
        if (Math.Min(startDistance, endDistance) > 8)
            return DragKind.None;
        return startDistance <= endDistance ? DragKind.Start : DragKind.End;
    }

    private int XToFrame(double x, Rect plot) => _clip is null ? 0 :
        (int)Math.Round(Math.Clamp((x - plot.Left) / plot.Width, 0, 1) * _clip.FrameCount);

    private double FrameToX(int frame, Rect plot) => _clip is null || _clip.FrameCount == 0
        ? plot.Left
        : plot.Left + (double)frame / _clip.FrameCount * plot.Width;

    private void EnsureWaveCache(Rect plot)
    {
        if (_clip is null)
            return;

        double dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        if (!_cacheDirty && plot == _cachedPlot && dpi == _cachedDpi)
            return;

        int laneCount = Math.Min(2, _clip.Channels);
        int columns = Math.Min(_clip.FrameCount, Math.Max(1, (int)Math.Ceiling(plot.Width * dpi)));
        var geometries = new StreamGeometry[laneCount];
        double laneHeight = plot.Height / laneCount;
        double amplitude = laneHeight * 0.39;

        // 한 화면 픽셀 구간의 최솟값·최댓값을 보존해 짧은 충격음도 생략하지 않습니다.
        // 완성된 도형은 재생 위치나 선택 영역이 바뀌어도 다시 계산하지 않습니다.
        for (int channel = 0; channel < laneCount; channel++)
        {
            var geometry = new StreamGeometry();
            using (StreamGeometryContext context = geometry.Open())
            {
                double middle = plot.Top + (channel + 0.5) * laneHeight;
                for (int column = 0; column < columns; column++)
                {
                    int firstFrame = (int)((long)column * _clip.FrameCount / columns);
                    int lastFrame = (int)((long)(column + 1) * _clip.FrameCount / columns);
                    float minimum = float.PositiveInfinity;
                    float maximum = float.NegativeInfinity;
                    for (int frame = firstFrame; frame < lastFrame; frame++)
                    {
                        float sample = _clip.Samples[frame * _clip.Channels + channel];
                        if (!float.IsFinite(sample))
                            sample = 0;
                        minimum = Math.Min(minimum, sample);
                        maximum = Math.Max(maximum, sample);
                    }

                    double x = plot.Left + (column + 0.5) / columns * plot.Width;
                    double top = middle - Math.Clamp(maximum, -1, 1) * amplitude;
                    double bottom = middle - Math.Clamp(minimum, -1, 1) * amplitude;
                    if (bottom - top < 0.7 / dpi)
                        bottom = top + 0.7 / dpi;
                    context.BeginFigure(new Point(x, top), false, false);
                    context.LineTo(new Point(x, bottom), true, false);
                }
            }
            geometry.Freeze();
            geometries[channel] = geometry;
        }

        _waveGeometry = geometries;
        _cachedPlot = plot;
        _cachedDpi = dpi;
        _cacheDirty = false;
    }

    private void DrawTimeGrid(DrawingContext dc, Rect plot)
    {
        if (_clip is null || _clip.DurationSeconds <= 0)
            return;

        double duration = _clip.DurationSeconds;
        double idealInterval = duration / Math.Max(2, Math.Floor(plot.Width / 90));
        double unit = Math.Pow(10, Math.Floor(Math.Log10(idealInterval)));
        double scaledInterval = idealInterval / unit;
        double interval = (scaledInterval <= 1 ? 1 : scaledInterval <= 2 ? 2 : scaledInterval <= 5 ? 5 : 10) * unit;
        for (int tick = 0; tick * interval <= duration + interval * 0.0001; tick++)
        {
            double seconds = tick * interval;
            double x = plot.Left + seconds / duration * plot.Width;
            dc.DrawLine(GridPen, new Point(x, plot.Top), new Point(x, plot.Bottom));
            int decimals = interval < 1 ? Math.Clamp((int)-Math.Floor(Math.Log10(interval)), 1, 6) : 0;
            string format = decimals == 0 ? "0" : "0." + new string('0', decimals);
            string label = seconds.ToString(format, CultureInfo.InvariantCulture) + "초";
            DrawText(dc, label, new Point(x, 10), 11, TextBrush, true);
        }
    }

    private static void DrawHandle(DrawingContext dc, double x, Rect plot, bool start)
    {
        dc.DrawLine(HandlePen, new Point(x, plot.Top), new Point(x, plot.Bottom));
        dc.DrawRoundedRectangle(HandleBrush, null,
            new Rect(start ? x : x - 6, plot.Top, 6, 16), 2, 2);
    }

    private void DrawText(DrawingContext dc, string text, Point point, double size, Brush brush, bool centered = false)
    {
        var label = new FormattedText(text, CultureInfo.GetCultureInfo("ko-KR"), FlowDirection.LeftToRight,
            LabelTypeface, size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        if (centered)
            point.X -= label.Width / 2;
        dc.DrawText(label, point);
    }

    private static SolidColorBrush MakeBrush(byte red, byte green, byte blue, byte alpha = 255)
    {
        var brush = new SolidColorBrush(Color.FromArgb(alpha, red, green, blue));
        brush.Freeze();
        return brush;
    }

    private static Pen MakePen(Brush brush, double width)
    {
        var pen = new Pen(brush, width);
        pen.Freeze();
        return pen;
    }

    private enum DragKind
    {
        None,
        Create,
        Start,
        End
    }
}
