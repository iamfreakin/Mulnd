using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SoriLab.Core;

namespace SoriLab.App;

public sealed class GeneratorDialog : Window
{
    private readonly ComboBox _preset = new() { Height = 34, Foreground = Brushes.Black };
    private readonly ComboBox _shape = new() { Height = 34, Foreground = Brushes.Black };
    private readonly TextBox _duration = new(), _start = new(), _end = new(), _gain = new(), _attack = new(), _release = new(), _seed = new();
    private readonly TextBlock _error = new() { Foreground = Brushes.LightSalmon, TextWrapping = TextWrapping.Wrap };
    private readonly Button _preview = new() { Content = "▶ 미리 듣기", Margin = new Thickness(0, 0, 10, 0) };
    private readonly Button _add = new() { Content = "트랙에 추가", Background = new SolidColorBrush(Color.FromRgb(140, 117, 232)) };
    private readonly MediaPlayer _player = new() { Volume = 1 };
    private string? _temporary;
    private bool _busy;
    public AudioClip? GeneratedClip { get; private set; }

    public GeneratorDialog(Window owner)
    {
        Owner = owner;
        Title = "소리공방 · 간단한 소리 생성";
        Width = 500; Height = 740; ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(26, 33, 48));
        Foreground = Brushes.White;
        FontFamily = new FontFamily("Malgun Gothic"); FontSize = 13;
        Resources.MergedDictionaries.Add(owner.Resources);
        var root = new StackPanel { Margin = new Thickness(25, 22, 25, 20) };
        root.Children.Add(new TextBlock { Text = "작은 재료에서 시작하세요.", FontSize = 22, FontWeight = FontWeights.Bold });
        root.Children.Add(new TextBlock { Text = "전자음과 노이즈를 만든 뒤 다른 소리와 겹쳐 보세요.", Foreground = Brushes.LightSteelBlue, Margin = new Thickness(0, 8, 0, 18), FontSize = 12 });
        foreach (string name in new[] { "레이저", "버튼", "충격", "부드러운 알림" }) _preset.Items.Add(name);
        foreach (string name in new[] { "사인파 · 부드러운 전자음", "삼각파 · 가벼운 전자음", "사각파 · 선명한 전자음", "노이즈 · 충격과 바람의 재료" }) _shape.Items.Add(name);
        root.Children.Add(Field("시작 설정", _preset));
        root.Children.Add(Field("소리 종류", _shape));
        root.Children.Add(Pair("길이 · 초", _duration, "음량 · dB", _gain));
        root.Children.Add(Pair("시작 음높이 · Hz", _start, "끝 음높이 · Hz", _end));
        root.Children.Add(Pair("시작 페이드 · ms", _attack, "끝 페이드 · ms", _release));
        root.Children.Add(Field("노이즈 번호 · 같은 번호면 같은 소리", _seed));
        root.Children.Add(new TextBlock { Text = "길이 0.01~30초 · 음높이 20~8,000Hz\n생성한 소리도 일반 트랙처럼 자르고 변형할 수 있어요.", Foreground = Brushes.LightSteelBlue, FontSize = 11, Margin = new Thickness(0, 3, 0, 12) });
        root.Children.Add(_error);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        actions.Children.Add(_preview); actions.Children.Add(_add); root.Children.Add(actions);
        Content = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        _preset.SelectionChanged += (_, _) => ApplyPreset(_preset.SelectedIndex);
        _shape.SelectionChanged += (_, _) => { bool tone = _shape.SelectedIndex != 3; _start.IsEnabled = _end.IsEnabled = tone; _seed.IsEnabled = !tone; };
        _preview.Click += async (_, _) => await PreviewAsync();
        _add.Click += async (_, _) =>
        {
            var clip = await GenerateAsync();
            if (clip is not null) { GeneratedClip = clip; DialogResult = true; }
        };
        _player.MediaOpened += (_, _) => _player.Play();
        _player.MediaFailed += (_, e) => _error.Text = "미리 듣기에 실패했습니다. " + e.ErrorException.Message;
        Closing += (_, e) => { if (_busy) e.Cancel = true; };
        Closed += (_, _) => { _player.Close(); DeleteTemporary(); };
        _preset.SelectedIndex = 0;
    }

    private void ApplyPreset(int index)
    {
        var values = index switch
        {
            1 => (WaveShape.Sine, .12, 1100d, 700d, -14d, 3d, 90d),
            2 => (WaveShape.Noise, .5, 180d, 70d, -12d, 2d, 470d),
            3 => (WaveShape.Triangle, .7, 480d, 960d, -12d, 10d, 350d),
            _ => (WaveShape.Square, .45, 1500d, 140d, -15d, 3d, 260d)
        };
        _shape.SelectedIndex = (int)values.Item1;
        _duration.Text = Number(values.Item2); _start.Text = Number(values.Item3); _end.Text = Number(values.Item4);
        _gain.Text = Number(values.Item5); _attack.Text = Number(values.Item6); _release.Text = Number(values.Item7); _seed.Text = "1";
        _error.Text = "";
    }

    internal GeneratorSettings ReadSettings()
    {
        if (!int.TryParse(_seed.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int seed)) throw new ArgumentException("노이즈 번호는 정수로 입력해 주세요.");
        bool noise = _shape.SelectedIndex == (int)WaveShape.Noise;
        return new GeneratorSettings((WaveShape)_shape.SelectedIndex, Parse(_duration), noise ? 440 : Parse(_start), noise ? 440 : Parse(_end), Parse(_gain), Parse(_attack), Parse(_release), seed);
    }
    private async Task<AudioClip?> GenerateAsync()
    {
        if (_busy) return null;
        _busy = true; _preview.IsEnabled = _add.IsEnabled = false; _error.Text = "";
        try { var settings = ReadSettings(); return await Task.Run(() => SoundGenerator.Generate(settings)); }
        catch (ArgumentException ex) { _error.Text = ex.Message; return null; }
        finally { _busy = false; _preview.IsEnabled = _add.IsEnabled = true; }
    }
    private async Task PreviewAsync()
    {
        var clip = await GenerateAsync();
        if (clip is null) return;
        try
        {
            _player.Close(); DeleteTemporary();
            _temporary = Path.Combine(Path.GetTempPath(), "SoriLab-generator-" + Guid.NewGuid().ToString("N") + ".wav");
            WavCodec.WritePcm16(_temporary, clip);
            _player.Open(new Uri(_temporary));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _error.Text = "미리 듣기를 준비하지 못했습니다. " + ex.Message; }
    }
    private static FrameworkElement Field(string label, Control input)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        panel.Children.Add(new TextBlock { Text = label, Foreground = Brushes.LightSteelBlue, FontSize = 12, Margin = new Thickness(0, 0, 0, 5) });
        panel.Children.Add(input); return panel;
    }
    private static FrameworkElement Pair(string leftLabel, Control left, string rightLabel, Control right)
    {
        var grid = new Grid(); grid.ColumnDefinitions.Add(new ColumnDefinition()); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) }); grid.ColumnDefinitions.Add(new ColumnDefinition());
        var first = Field(leftLabel, left); var second = Field(rightLabel, right); Grid.SetColumn(second, 2);
        grid.Children.Add(first); grid.Children.Add(second); return grid;
    }
    private static double Parse(TextBox input)
    {
        if (!double.TryParse(input.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) || !double.IsFinite(value)) throw new ArgumentException("모든 값은 유효한 숫자로 입력해 주세요.");
        return value;
    }
    private static string Number(double value) => value.ToString(CultureInfo.InvariantCulture);
    private void DeleteTemporary() { if (_temporary is not null) { try { File.Delete(_temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { } _temporary = null; } }
}
