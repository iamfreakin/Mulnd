using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace SoriLab.App;

public partial class StudioWindow
{
    private async Task RunWorkspaceChecksAsync(string outputBase)
    {
        RefreshWorkspaceShell();
        int lanes = _state.Tracks.Select(t => t.EffectiveLaneId).Distinct().Count();
        if (MixerChannels.Children.Count != lanes || SourcePoolList.Items.Count == 0)
            throw new InvalidOperationException("믹서 트랙과 소스 탐색기 연결 실패");
        var first = _state.Tracks[0];
        var before = _state.Id;
        _workspaceChannels[first.EffectiveLaneId].MuteButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        if (_state.Tracks.Where(t => t.EffectiveLaneId == first.EffectiveLaneId).Any(t => !t.Muted))
            throw new InvalidOperationException("믹서 M 버튼의 트랙 음소거 실패");
        OnUndo(this, new RoutedEventArgs());
        if (_state.Id != before) throw new InvalidOperationException("믹서 음소거 실행 취소 실패");
        _workspaceChannels[first.EffectiveLaneId].SoloButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        if (_state.Tracks.Where(t => t.EffectiveLaneId == first.EffectiveLaneId).Any(t => !t.Solo))
            throw new InvalidOperationException("믹서 S 버튼의 트랙 단독 재생 실패");
        OnUndo(this, new RoutedEventArgs());
        SourcePoolList.SelectedItem = _workspaceSourceItems[first.Source];
        if (!ReferenceEquals(Selected?.Source, first.Source)) throw new InvalidOperationException("소스 탐색기의 클립 선택 실패");
        EditorToggle.IsChecked = true;
        OnToggleEditor(this, new RoutedEventArgs());
        if (EditorPane.Visibility != Visibility.Visible || ConsolePane.Visibility != Visibility.Collapsed)
            throw new InvalidOperationException("하단 편집기 전환 실패");
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Program.SaveScreenshot(this, outputBase + "-editor.png");
        MixerToggle.IsChecked = true;
        OnToggleMixer(this, new RoutedEventArgs());
        if (ConsolePane.Visibility != Visibility.Visible || EditorPane.Visibility != Visibility.Collapsed)
            throw new InvalidOperationException("하단 믹서 전환 실패");
        OnToggleMixer(new MenuItem(), new RoutedEventArgs());
        if (ConsolePane.Visibility != Visibility.Collapsed || LowerPaneRow.Height.Value != 0)
            throw new InvalidOperationException("보기 메뉴에서 믹서 접기 실패");
        OnToggleMixer(new MenuItem(), new RoutedEventArgs());
        if (ConsolePane.Visibility != Visibility.Visible)
            throw new InvalidOperationException("보기 메뉴에서 믹서 펼치기 실패");
        BrowserToggle.IsChecked = false;
        OnToggleBrowser(this, new RoutedEventArgs());
        if (BrowserPane.Visibility != Visibility.Collapsed || BrowserColumn.Width.Value != 0)
            throw new InvalidOperationException("탐색기 접기 실패");
        BrowserToggle.IsChecked = true;
        OnToggleBrowser(this, new RoutedEventArgs());
        if (BrowserPane.Visibility != Visibility.Visible || BrowserColumn.Width.Value <= 0)
            throw new InvalidOperationException("탐색기 펼치기 실패");
        BrowserTabs.SelectedIndex = 1;
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Program.SaveScreenshot(this, outputBase + "-effects.png");
        BrowserTabs.SelectedIndex = 0;
        await File.WriteAllTextAsync(outputBase + ".workspace.txt", "통과: 실제 트랙의 믹서 채널 표시, M/S 버튼과 실행 취소, 소스 탐색기의 클립 선택, 편집기/믹서 전환, 탐색기 접기/펼치기, 미구현 효과 탭 화면. 앱 처리 함수와 레이아웃 검증이며 운영체제 마우스 자동화가 아닙니다.");
    }
}
