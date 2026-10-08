namespace SoriLab.Core;

public enum ClipTrimEdge
{
    Start,
    End
}

public static class ClipEditing
{
    private const int ProjectSampleRate = 48000;
    private const double TimelineRoundingTolerance = 1e-12;

    /// <summary>원본을 유지한 채 타임라인의 한쪽 경계를 원본 프레임 단위로 조절합니다.</summary>
    public static AudioTrack Trim(AudioTrack track, ClipTrimEdge edge, double timelineSeconds)
    {
        if (track is null)
            throw new ArgumentNullException(nameof(track), "길이를 조절할 클립이 없습니다.");
        if (edge is not (ClipTrimEdge.Start or ClipTrimEdge.End))
            throw new ArgumentException("조절할 클립 경계가 올바르지 않습니다.", nameof(edge));
        if (!double.IsFinite(timelineSeconds))
            throw new ArgumentException("클립 경계에는 유한한 초 단위 숫자를 입력해 주세요.", nameof(timelineSeconds));

        // 드래그할 때마다 호출되므로 소리 샘플은 검사하거나 복사하지 않고 메타데이터만 검사합니다.
        _ = AudioMixer.GetDurationSeconds(track);
        var sourceFramesPerSecond = track.Source.SampleRate * track.PlaybackRate;
        var requestedSeconds = Math.Clamp(timelineSeconds, 0, AudioMixer.MaximumDurationSeconds);
        return edge == ClipTrimEdge.Start
            ? TrimStart(track, requestedSeconds, sourceFramesPerSecond)
            : TrimEnd(track, requestedSeconds, sourceFramesPerSecond);
    }

    private static AudioTrack TrimStart(AudioTrack track, double timelineSeconds, double sourceFramesPerSecond)
    {
        var frameCount = track.Edit.EndFrame - track.Edit.StartFrame;
        var fixedEnd = track.OffsetSeconds + frameCount / sourceFramesPerSecond;
        // 음소거 등으로 120초 밖에 남아 있는 클립은 끝 경계를 줄여 먼저 복구해야 합니다.
        if (fixedEnd > AudioMixer.MaximumDurationSeconds + TimelineRoundingTolerance)
            throw new ArgumentException("클립 끝이 120초를 넘습니다. 먼저 오른쪽 끝을 120초 안으로 줄여 주세요.", nameof(track));

        long minimumDelta = track.Reverse
            ? track.Edit.EndFrame - track.Source.FrameCount
            : -track.Edit.StartFrame;
        var projectMinimumDelta = (long)Math.Ceiling(-track.OffsetSeconds * sourceFramesPerSecond);
        // 초와 프레임 사이의 계산 오차 때문에 0초에 닿는 한 프레임을 놓치지 않게 합니다.
        if (track.OffsetSeconds + (projectMinimumDelta - 1) / sourceFramesPerSecond >= -TimelineRoundingTolerance)
            projectMinimumDelta--;
        minimumDelta = Math.Max(minimumDelta, projectMinimumDelta);
        var maximumDelta = frameCount - 1L;
        if (minimumDelta > maximumDelta)
            throw new ArgumentException("프로젝트 안에 최소 한 프레임을 남길 수 없습니다. 클립 위치를 앞당겨 주세요.", nameof(track));

        // 절대 원본 프레임이 아니라 드래그 시작점에서의 변화량을 양방향으로 대칭 반올림합니다.
        var requestedDelta = (long)Math.Round((timelineSeconds - track.OffsetSeconds) * sourceFramesPerSecond, MidpointRounding.AwayFromZero);
        var delta = (int)Math.Clamp(requestedDelta, minimumDelta, maximumDelta);
        if (delta == 0) return track;

        var offset = track.OffsetSeconds + delta / sourceFramesPerSecond;
        if (offset < 0 && offset >= -TimelineRoundingTolerance) offset = 0;
        var edit = track.Reverse
            ? track.Edit with { EndFrame = track.Edit.EndFrame - delta }
            : track.Edit with { StartFrame = track.Edit.StartFrame + delta };
        return track with { OffsetSeconds = offset, Edit = edit };
    }

    private static AudioTrack TrimEnd(AudioTrack track, double timelineSeconds, double sourceFramesPerSecond)
    {
        var sourceMaximumFrames = track.Reverse
            ? track.Edit.EndFrame
            : track.Source.FrameCount - track.Edit.StartFrame;
        var projectMaximumFrames = (long)Math.Floor((AudioMixer.MaximumDurationSeconds - track.OffsetSeconds) * sourceFramesPerSecond);
        // 기존 위치를 빼는 과정에서 생긴 오차만 보정하며, 샘플 경계 자체는 변경하지 않습니다.
        if (track.OffsetSeconds + (projectMaximumFrames + 1) / sourceFramesPerSecond <= AudioMixer.MaximumDurationSeconds + TimelineRoundingTolerance)
            projectMaximumFrames++;
        var maximumFrames = Math.Min(sourceMaximumFrames, projectMaximumFrames);
        if (maximumFrames < 1)
            throw new ArgumentException("프로젝트 안에 최소 한 프레임을 남길 수 없습니다. 클립 위치를 앞당겨 주세요.", nameof(track));

        var requestedFrames = (long)Math.Round((timelineSeconds - track.OffsetSeconds) * sourceFramesPerSecond, MidpointRounding.AwayFromZero);
        var frameCount = (int)Math.Clamp(requestedFrames, 1L, maximumFrames);
        if (frameCount == track.Edit.EndFrame - track.Edit.StartFrame) return track;

        var edit = track.Reverse
            ? track.Edit with { StartFrame = track.Edit.EndFrame - frameCount }
            : track.Edit with { EndFrame = track.Edit.StartFrame + frameCount };
        return track with { Edit = edit };
    }

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
