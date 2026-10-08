namespace SoriLab.Core;

/// <summary>미리 듣기에 사용할 소리와 그 첫 샘플의 프로젝트상 위치입니다.</summary>
public record PreparedPlayback(AudioClip Audio, double TimelineStartSeconds);

public static class PlaybackPreview
{
    /// <summary>
    /// 완성된 렌더 결과를 커서 프레임부터 잘라 재생용으로 준비합니다.
    /// 선택 클립의 위치를 지정하면, 클립 밖의 커서는 클립 시작 위치로 돌아갑니다.
    /// </summary>
    public static PreparedPlayback FromCursor(
        AudioClip rendered,
        double projectCursorSeconds,
        double? selectionOffsetSeconds = null)
    {
        AudioValidation.Validate(rendered);
        ValidateSeconds(projectCursorSeconds, nameof(projectCursorSeconds), "재생 커서");
        if (selectionOffsetSeconds is double offsetSeconds)
            ValidateSeconds(offsetSeconds, nameof(selectionOffsetSeconds), "선택 클립 위치");

        // 원본 전체를 검사해, 잘라 낸 구간에 따라 잘못된 데이터가 조용히 통과하지 않게 합니다.
        foreach (var sample in rendered.Samples)
            AudioValidation.ValidateSample(sample);

        var cursorFrame = ToFrame(projectCursorSeconds, rendered.SampleRate);
        var baseFrame = selectionOffsetSeconds is double offset
            ? ToFrame(offset, rendered.SampleRate)
            : 0L;
        var localFrame = cursorFrame - baseFrame;

        if (selectionOffsetSeconds.HasValue)
        {
            if (localFrame < 0 || localFrame >= rendered.FrameCount)
                localFrame = 0;
        }
        else if (localFrame >= rendered.FrameCount)
        {
            throw new ArgumentException("재생 커서를 소리가 끝나기 전으로 옮겨 주세요.", nameof(projectCursorSeconds));
        }

        var startFrame = (int)localFrame;
        var timelineStartSeconds = (baseFrame + startFrame) / (double)rendered.SampleRate;
        if (startFrame == 0)
            return new PreparedPlayback(rendered, timelineStartSeconds);

        // 이미 적용한 페이드와 리샘플링 경계를 다시 계산하지 않고 완성된 샘플을 그대로 사용합니다.
        var samples = new float[(rendered.FrameCount - startFrame) * rendered.Channels];
        Array.Copy(rendered.Samples, startFrame * rendered.Channels, samples, 0, samples.Length);
        var audio = new AudioClip(rendered.Name, rendered.SampleRate, rendered.Channels, samples);
        return new PreparedPlayback(audio, timelineStartSeconds);
    }

    private static long ToFrame(double seconds, int sampleRate) =>
        (long)Math.Round(seconds * sampleRate, MidpointRounding.AwayFromZero);

    private static void ValidateSeconds(double seconds, string parameterName, string label)
    {
        if (!double.IsFinite(seconds) || seconds < 0 || seconds > 120)
            throw new ArgumentException($"{label}는 0초 이상 120초 이하의 수여야 합니다.", parameterName);
    }
}
