namespace SoriLab.Core;

public static class ClipEditing
{
    private const int ProjectSampleRate = 48000;

    /// <summary>프로젝트 절대 시각에서 클립을 나누고 분할 직전과 동일한 48kHz 소리를 보존합니다.</summary>
    public static (AudioTrack Left, AudioTrack Right) Split(AudioTrack track, double absoluteSeconds)
    {
        if (track is null)
            throw new ArgumentNullException(nameof(track), "분할할 클립이 없습니다.");
        if (!double.IsFinite(absoluteSeconds) || absoluteSeconds is < 0 or > AudioMixer.MaximumDurationSeconds)
            throw new ArgumentException("분할 위치는 0초부터 120초 사이로 설정해 주세요.", nameof(absoluteSeconds));

        AudioMixer.Validate([track], ProjectSampleRate);
        var firstFrame = (long)Math.Round(track.OffsetSeconds * ProjectSampleRate, MidpointRounding.AwayFromZero);
        var splitFrame = (long)Math.Round(absoluteSeconds * ProjectSampleRate, MidpointRounding.AwayFromZero) - firstFrame;
        var canShareOriginal = track.Source.SampleRate == ProjectSampleRate && track.PlaybackRate == 1 && !track.Reverse &&
            track.Edit.GainDb == 0 && track.Edit.FadeInMs == 0 && track.Edit.FadeOutMs == 0;
        var frameCount = canShareOriginal
            ? track.Edit.EndFrame - track.Edit.StartFrame
            : Math.Max(1, Math.Ceiling(AudioMixer.GetDurationSeconds(track) * ProjectSampleRate - 1e-9));
        if (frameCount > AudioValidation.MaximumSamples / 2)
            throw new ArgumentException("분할할 소리가 너무 큽니다. 먼저 선택 구간을 줄여 주세요.", nameof(track));
        if (splitFrame <= 0 || splitFrame >= frameCount)
            throw new ArgumentException("클립의 시작과 끝 사이를 선택해 주세요. 양쪽에 최소 한 프레임이 필요합니다.", nameof(absoluteSeconds));

        // 효과나 속도가 있으면 한 번만 렌더해 보간 경계와 페이드가 분할점에서 다시 시작하지 않도록 합니다.
        var source = canShareOriginal ? track.Source : AudioMixer.RenderTrack(track, ProjectSampleRate);
        var sourceStart = canShareOriginal ? track.Edit.StartFrame : 0;
        var sourceEnd = canShareOriginal ? track.Edit.EndFrame : source.FrameCount;
        var boundary = sourceStart + (int)splitFrame;
        var left = track with
        {
            Source = source,
            Edit = new EditSettings(sourceStart, boundary, 0, 0, 0),
            OffsetSeconds = (double)firstFrame / ProjectSampleRate,
            PlaybackRate = 1,
            Reverse = false,
            SourcePath = canShareOriginal ? track.SourcePath : null,
            LaneId = track.EffectiveLaneId
        };
        var right = left with
        {
            Id = Guid.NewGuid(),
            Edit = new EditSettings(boundary, sourceEnd, 0, 0, 0),
            OffsetSeconds = (double)(firstFrame + splitFrame) / ProjectSampleRate
        };
        return (left, right);
    }
}
