using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SoriLab.Core;

namespace SoriLab.App;

public partial class StudioWindow
{
    private bool _workspaceInitialized;
    private bool _workspaceSyncing;
    private Guid _workspaceSnapshotId;
    private readonly Dictionary<Guid, WorkspaceChannel> _workspaceChannels = [];
    private readonly Dictionary<AudioClip, ListBoxItem> _workspaceSourceItems = new(ReferenceEqualityComparer.Instance);
    private AudioTrack[][] _workspaceLanes = [];
    private AudioClip[] _workspaceSources = [];
    private TextBlock? _workspaceEmptyChannels;

    private void InitializeWorkspaceShell()
    {
        if (_workspaceInitialized || !HasWorkspaceControls()) return;
        _workspaceSyncing = true;
        try
        {
            EditorToggle.IsChecked = false;
            MixerToggle.IsChecked = true;
            BrowserToggle.IsChecked = true;
            InspectorPane.Visibility = Visibility.Visible;
            InspectorColumn.Width = new GridLength(208);
            _workspaceInitialized = true;
            ApplyWorkspaceLayout();
        }
        finally { _workspaceSyncing = false; }
        RefreshWorkspaceShell();
    }

    private bool HasWorkspaceControls() =>
        InspectorPane is not null && EditorPane is not null && ConsolePane is not null && BrowserPane is not null &&
        InspectorColumn is not null && BrowserColumn is not null && LowerPaneRow is not null &&
        EditorToggle is not null && MixerToggle is not null && BrowserToggle is not null &&
        MixerChannels is not null && SourcePoolList is not null && BrowserTabs is not null;

    private void RefreshWorkspaceShell()
    {
        if (!_workspaceInitialized || _workspaceSyncing || !HasWorkspaceControls()) return;
        _workspaceSyncing = true;
        try
        {
            if (_workspaceSnapshotId != _state.Id)
            {
                _workspaceLanes = _state.Tracks.GroupBy(track => track.EffectiveLaneId).Select(group => group.ToArray()).ToArray();
                var seen = new HashSet<AudioClip>(ReferenceEqualityComparer.Instance);
                _workspaceSources = _state.Tracks.Where(track => seen.Add(track.Source)).Select(track => track.Source).ToArray();
                RefreshWorkspaceChannels();
                RefreshWorkspacePool();
                _workspaceSnapshotId = _state.Id;
            }

            Guid? selectedLane = Selected?.EffectiveLaneId;
            foreach (var lane in _workspaceLanes)
            {
                var first = lane[0];
                var channel = _workspaceChannels[first.EffectiveLaneId];
                channel.SelectButton.IsEnabled = !_busy;
                channel.MuteButton.IsEnabled = !_busy;
                channel.SoloButton.IsEnabled = !_busy;
                channel.Root.BorderBrush = WorkspaceBrush(first.EffectiveLaneId == selectedLane ? "AccentBrush" : "BorderBrush",
                    first.EffectiveLaneId == selectedLane ? "#4E91BD" : "#3C4652");
                channel.MuteButton.Background = first.Muted ? Brush("#756038") : WorkspaceBrush("PanelBrush", "#252D35");
                channel.SoloButton.Background = first.Solo ? Brush("#416B88") : WorkspaceBrush("PanelBrush", "#252D35");
                channel.MuteButton.ToolTip = first.Muted ? "트랙 음소거 끄기" : "트랙 음소거";
                channel.SoloButton.ToolTip = first.Solo ? "트랙 단독 재생 끄기" : "트랙 단독 재생";
            }

            SourcePoolList.IsEnabled = !_busy;
            var selectedSource = Selected?.Source;
            var selectedItem = selectedSource is not null && _workspaceSourceItems.TryGetValue(selectedSource, out var found) ? found : null;
            if (!ReferenceEquals(SourcePoolList.SelectedItem, selectedItem)) SourcePoolList.SelectedItem = selectedItem;
        }
        finally { _workspaceSyncing = false; }
    }

    private void RefreshWorkspaceChannels()
    {
        var laneIds = _workspaceLanes.Select(lane => lane[0].EffectiveLaneId).ToHashSet();
        foreach (var removed in _workspaceChannels.Keys.Where(id => !laneIds.Contains(id)).ToArray())
        {
            MixerChannels.Children.Remove(_workspaceChannels[removed].Root);
            _workspaceChannels.Remove(removed);
        }

        if (_workspaceLanes.Length == 0)
        {
            _workspaceEmptyChannels ??= WorkspaceText("트랙을 추가하면 채널이 표시됩니다.", 12, muted: true);
            _workspaceEmptyChannels.Margin = new Thickness(18, 24, 18, 0);
            if (!MixerChannels.Children.Contains(_workspaceEmptyChannels)) MixerChannels.Children.Add(_workspaceEmptyChannels);
            return;
        }
        if (_workspaceEmptyChannels is not null) MixerChannels.Children.Remove(_workspaceEmptyChannels);

        for (var index = 0; index < _workspaceLanes.Length; index++)
        {
            var lane = _workspaceLanes[index];
            var first = lane[0];
            if (!_workspaceChannels.TryGetValue(first.EffectiveLaneId, out var channel))
            {
                channel = CreateWorkspaceChannel(first.EffectiveLaneId);
                _workspaceChannels.Add(first.EffectiveLaneId, channel);
            }
            channel.Name.Text = first.Source.Name;
            channel.Accent.Background = Brush((index % 4) switch
            {
                0 => "#489CD8",
                1 => "#52A597",
                2 => "#9583BE",
                _ => "#C39A64"
            });
            channel.SelectButton.ToolTip = $"{first.Source.Name} · 트랙 선택";
            channel.ClipCount.Text = $"클립 {lane.Length}개";
            if (MixerChannels.Children.IndexOf(channel.Root) != index)
            {
                MixerChannels.Children.Remove(channel.Root);
                MixerChannels.Children.Insert(index, channel.Root);
            }
        }
    }

    private WorkspaceChannel CreateWorkspaceChannel(Guid laneId)
    {
        var name = WorkspaceText("", 10);
        name.TextTrimming = TextTrimming.CharacterEllipsis;
        name.HorizontalAlignment = HorizontalAlignment.Stretch;
        var select = new Button
        {
            Content = name, Height = 25, MinWidth = 0, MinHeight = 0, Padding = new Thickness(4, 0, 4, 0),
            Margin = new Thickness(0), HorizontalContentAlignment = HorizontalAlignment.Stretch, Tag = laneId
        };
        select.Click += (_, _) =>
        {
            if (_busy) return;
            var first = _state.Tracks.FirstOrDefault(track => track.EffectiveLaneId == laneId);
            if (first is not null) SelectTrack(first.Id);
        };

        var count = WorkspaceText("", 9, muted: true);
        count.Height = 16;
        count.HorizontalAlignment = HorizontalAlignment.Center;
        var mute = WorkspaceLaneButton("M", "트랙 음소거");
        var solo = WorkspaceLaneButton("S", "트랙 단독 재생");
        mute.Tag = solo.Tag = laneId;
        mute.Click += (_, _) => ChangeWorkspaceLaneState(laneId, changeMute: true);
        solo.Click += (_, _) => ChangeWorkspaceLaneState(laneId, changeMute: false);
        var switches = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Height = 25 };
        switches.Children.Add(mute);
        switches.Children.Add(solo);

        var pan = WorkspaceText("팬 · 준비 중", 9, muted: true);
        pan.Height = 15;
        pan.HorizontalAlignment = HorizontalAlignment.Center;
        var effects = WorkspaceText("삽입·보내기 준비 중", 8, muted: true);
        effects.Height = 17;
        effects.HorizontalAlignment = HorizontalAlignment.Center;
        effects.ToolTip = "트랙별 효과 삽입과 보내기는 준비 중입니다.";

        var lower = new Grid { Height = 67, Margin = new Thickness(1, 1, 1, 0) };
        lower.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) });
        lower.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(9) });
        lower.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var scale = new Grid { Height = 60, VerticalAlignment = VerticalAlignment.Top };
        foreach (var label in new[] { "0", "−12", "−24", "−48" })
        {
            var row = scale.RowDefinitions.Count;
            scale.RowDefinitions.Add(new RowDefinition());
            var tick = WorkspaceText(label, 8, muted: true);
            tick.HorizontalAlignment = HorizontalAlignment.Right;
            tick.Margin = new Thickness(0, 0, 4, 0);
            Grid.SetRow(tick, row);
            scale.Children.Add(tick);
        }
        lower.Children.Add(scale);
        // 신호 값은 그리지 않고, 아직 연결되지 않은 미터의 빈 자리와 눈금만 표시합니다.
        var meter = new Border
        {
            Height = 59, Width = 7, VerticalAlignment = VerticalAlignment.Top,
            Background = Brush("#131920"), BorderBrush = WorkspaceBrush("BorderBrush", "#3C4652"),
            BorderThickness = new Thickness(1), ToolTip = "트랙별 음량 미터는 준비 중입니다."
        };
        Grid.SetColumn(meter, 1);
        lower.Children.Add(meter);
        var fader = new Slider
        {
            Orientation = Orientation.Vertical, Minimum = -60, Maximum = 12, Value = 0,
            Height = 61, Width = 27, VerticalAlignment = VerticalAlignment.Top,
            IsEnabled = false, IsTabStop = false, Opacity = 0.4,
            ToolTip = "트랙별 페이더는 준비 중입니다. 클립 음량은 인스펙터에서 조절할 수 있습니다."
        };
        Grid.SetColumn(fader, 2);
        lower.Children.Add(fader);
        var pending = WorkspaceText("페이더·미터 준비 중", 8.5, muted: true);
        pending.HorizontalAlignment = HorizontalAlignment.Center;
        var accent = new Border { Height = 3, Margin = new Thickness(0, 0, 0, 4) };
        var content = new StackPanel();
        content.Children.Add(accent);
        content.Children.Add(select);
        content.Children.Add(count);
        content.Children.Add(switches);
        content.Children.Add(pan);
        content.Children.Add(effects);
        content.Children.Add(lower);
        content.Children.Add(pending);

        var root = new Border
        {
            Width = 92, Padding = new Thickness(5), Margin = new Thickness(0, 0, 3, 0),
            VerticalAlignment = VerticalAlignment.Top, Background = WorkspaceBrush("PanelBrush", "#252D35"),
            BorderBrush = WorkspaceBrush("BorderBrush", "#3C4652"), BorderThickness = new Thickness(1), Child = content
        };
        return new WorkspaceChannel(root, accent, name, count, select, mute, solo);
    }

    private Button WorkspaceLaneButton(string text, string tooltip) => new()
    {
        Content = text, Width = 32, Height = 22, MinWidth = 0, MinHeight = 0, FontSize = 11, Padding = new Thickness(0),
        Margin = new Thickness(2, 0, 2, 0), ToolTip = tooltip,
        Foreground = WorkspaceBrush("TextBrush", "#D7DEE7")
    };

    private void ChangeWorkspaceLaneState(Guid laneId, bool changeMute)
    {
        if (_workspaceSyncing || _syncing) return;
        if (_busy || !CommitNumbers()) { RefreshWorkspaceShell(); return; }
        var first = _state.Tracks.FirstOrDefault(track => track.EffectiveLaneId == laneId);
        if (first is null) return;
        var next = _state.Tracks.Select(track => track.EffectiveLaneId != laneId ? track :
            changeMute ? track with { Muted = !first.Muted } : track with { Solo = !first.Solo }).ToArray();
        CommitState(next, _state.MasterDb, changeMute ? "workspace-mute" : "workspace-solo");
    }

    private void RefreshWorkspacePool()
    {
        var activeSources = new HashSet<AudioClip>(_workspaceSources, ReferenceEqualityComparer.Instance);
        foreach (var removed in _workspaceSourceItems.Keys.Where(source => !activeSources.Contains(source)).ToArray())
        {
            SourcePoolList.Items.Remove(_workspaceSourceItems[removed]);
            _workspaceSourceItems.Remove(removed);
        }
        for (var index = 0; index < _workspaceSources.Length; index++)
        {
            var source = _workspaceSources[index];
            if (!_workspaceSourceItems.TryGetValue(source, out var item))
            {
                var title = WorkspaceText(source.Name, 11);
                title.TextTrimming = TextTrimming.CharacterEllipsis;
                var details = WorkspaceText($"{source.DurationSeconds:0.###}초 · {source.SampleRate:N0}Hz · {(source.Channels == 1 ? "모노" : "스테레오")}", 9, muted: true);
                details.Margin = new Thickness(0, 3, 0, 0);
                var content = new StackPanel();
                content.Children.Add(title);
                content.Children.Add(details);
                item = new ListBoxItem
                {
                    Content = content, Tag = source, Padding = new Thickness(7, 6, 7, 6),
                    HorizontalContentAlignment = HorizontalAlignment.Stretch, ToolTip = source.Name
                };
                _workspaceSourceItems.Add(source, item);
            }
            // 항목을 통째로 다시 만들지 않아 선택과 스크롤, 마우스 위치가 가능한 한 유지됩니다.
            if (SourcePoolList.Items.IndexOf(item) != index)
            {
                SourcePoolList.Items.Remove(item);
                SourcePoolList.Items.Insert(index, item);
            }
        }
    }

    private void OnPoolSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_workspaceInitialized || _workspaceSyncing || _syncing) return;
        if (_busy) { RefreshWorkspaceShell(); return; }
        if (SourcePoolList.SelectedItem is not ListBoxItem { Tag: AudioClip source }) return;
        var first = _state.Tracks.FirstOrDefault(track => ReferenceEquals(track.Source, source));
        if (first is not null) SelectTrack(first.Id);
        RefreshWorkspaceShell();
    }

    private void OnToggleEditor(object sender, RoutedEventArgs e)
    {
        if (!_workspaceInitialized || _workspaceSyncing) return;
        if (sender is MenuItem) EditorToggle.IsChecked = EditorToggle.IsChecked != true;
        if (EditorToggle.IsChecked == true) MixerToggle.IsChecked = false;
        ApplyWorkspaceLayout();
    }

    private void OnToggleMixer(object sender, RoutedEventArgs e)
    {
        if (!_workspaceInitialized || _workspaceSyncing) return;
        if (sender is MenuItem) MixerToggle.IsChecked = MixerToggle.IsChecked != true;
        if (MixerToggle.IsChecked == true) EditorToggle.IsChecked = false;
        ApplyWorkspaceLayout();
    }

    private void OnToggleBrowser(object sender, RoutedEventArgs e)
    {
        if (_workspaceInitialized && !_workspaceSyncing && sender is MenuItem)
            BrowserToggle.IsChecked = BrowserToggle.IsChecked != true;
        if (_workspaceInitialized && !_workspaceSyncing) ApplyWorkspaceLayout();
    }

    private void ToggleEditorPane()
    {
        if (!_workspaceInitialized) return;
        EditorToggle.IsChecked = EditorToggle.IsChecked != true;
        OnToggleEditor(EditorToggle, new RoutedEventArgs());
    }

    private void ToggleMixerPane()
    {
        if (!_workspaceInitialized) return;
        MixerToggle.IsChecked = MixerToggle.IsChecked != true;
        OnToggleMixer(MixerToggle, new RoutedEventArgs());
    }

    private void ToggleBrowserPane()
    {
        if (!_workspaceInitialized) return;
        BrowserToggle.IsChecked = BrowserToggle.IsChecked != true;
        OnToggleBrowser(BrowserToggle, new RoutedEventArgs());
    }

    private void ApplyWorkspaceLayout()
    {
        if (!HasWorkspaceControls()) return;
        bool editor = EditorToggle.IsChecked == true;
        bool mixer = MixerToggle.IsChecked == true && !editor;
        bool browser = BrowserToggle.IsChecked == true;
        EditorPane.Visibility = editor ? Visibility.Visible : Visibility.Collapsed;
        ConsolePane.Visibility = mixer ? Visibility.Visible : Visibility.Collapsed;
        LowerPaneRow.Height = new GridLength(editor || mixer ? 240 : 0);
        BrowserPane.Visibility = browser ? Visibility.Visible : Visibility.Collapsed;
        BrowserColumn.Width = new GridLength(browser ? 238 : 0);
    }

    private void OnPlaceholder(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string message } && !string.IsNullOrWhiteSpace(message))
            Status(message);
        else
            Status("이 기능은 준비 중입니다.");
        e.Handled = true;
    }

    private TextBlock WorkspaceText(string text, double size, bool muted = false) => new()
    {
        Text = text, FontSize = size, FontFamily = new FontFamily("Malgun Gothic"),
        Foreground = WorkspaceBrush(muted ? "MutedBrush" : "TextBrush", muted ? "#8D9CAA" : "#D7DEE7"),
        VerticalAlignment = VerticalAlignment.Center
    };

    private System.Windows.Media.Brush WorkspaceBrush(string key, string fallback) =>
        TryFindResource(key) as System.Windows.Media.Brush ?? Brush(fallback);

    private sealed record WorkspaceChannel(
        Border Root, Border Accent, TextBlock Name, TextBlock ClipCount, Button SelectButton, Button MuteButton, Button SoloButton);
}
