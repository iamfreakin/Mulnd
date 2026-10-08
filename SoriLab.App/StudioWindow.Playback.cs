using System.Windows;
using System.Windows.Media;

namespace SoriLab.App;

public partial class StudioWindow
{
    private void AttachPlaybackEvents(MediaPlayer player)
    {
        player.MediaOpened += (_, _) =>
        {
            if (!ReferenceEquals(_player, player) || !_opening || _closing) return;
            _opening = false; _ready = true; _playing = true;
            player.Play(); PlayButton.Content = "Ⅱ 일시정지";
            Status($"{FormatSeconds(_previewTimelineStart)}초부터 듣고 있습니다. 정지하면 시작 커서로 돌아옵니다.");
        };
        player.MediaEnded += (_, _) =>
        {
            if (!ReferenceEquals(_player, player) || !_ready || !_playing || _closing) return;
            if (LoopCheck.IsChecked == true)
            {
                player.Position = TimeSpan.Zero;
                DisplayPlaybackPosition(_previewTimelineStart);
                player.Play();
            }
            else StopPlayback(false);
        };
        player.MediaFailed += (_, e) =>
        {
            if (!ReferenceEquals(_player, player) || (!_opening && !_ready) || _closing) return;
            StopPlayback(true); Status("재생하지 못했습니다. " + e.ErrorException.Message, true);
        };
    }

    private void ReplacePlaybackPlayer()
    {
        // 이전 파일의 늦은 이벤트가 새 재생을 시작하거나 멈추지 않게 세션을 구분합니다.
        var player = new MediaPlayer { Volume = _player.Volume };
        _player.Close();
        _player = player;
        AttachPlaybackEvents(player);
    }

    private void DisplayPlaybackPosition(double projectSeconds)
    {
        PositionText.Text = TimeSpan.FromSeconds(projectSeconds).ToString(@"mm\:ss\.fff");
        Timeline.PlayheadSeconds = projectSeconds;
        if (Selected is not { } selected) return;
        double relative = projectSeconds - selected.OffsetSeconds;
        double distance = relative * selected.Source.SampleRate * selected.PlaybackRate;
        double frame = selected.Reverse ? selected.Edit.EndFrame - 1 - distance : selected.Edit.StartFrame + distance;
        SourceWaveform.PlayheadSeconds = Math.Clamp(frame, selected.Edit.StartFrame, selected.Edit.EndFrame - 1) / selected.Source.SampleRate;
    }

    private void OnGoToStart(object sender, RoutedEventArgs e)
    {
        if (_busy || !CommitNumbers()) return;
        SetEditCursor(0);
        Status("커서를 처음으로 옮겼습니다. Space 또는 재생 버튼으로 들을 수 있습니다.");
    }
}
