namespace SoriLab.Core;

public sealed record AudioClip(string Name, int SampleRate, int Channels, float[] Samples)
{
    public int FrameCount => Samples.Length / Channels;

    public double DurationSeconds => (double)FrameCount / SampleRate;
}

// 끝 프레임은 포함하지 않아, 붙어 있는 구간을 잘라도 샘플이 중복되지 않습니다.
public sealed record EditSettings(
    int StartFrame,
    int EndFrame,
    double GainDb,
    double FadeInMs,
    double FadeOutMs);

internal static class AudioValidation
{
    internal const int MaximumSamples = 32_000_000;

    internal static void Validate(AudioClip clip)
    {
        if (clip is null)
            throw new ArgumentNullException(nameof(clip), "편집할 소리가 없습니다.");
        if (clip.SampleRate <= 0)
            throw new ArgumentException("샘플레이트는 0보다 커야 합니다.", nameof(clip));
        if (clip.Channels is not (1 or 2))
            throw new ArgumentException("모노 또는 스테레오 소리만 지원합니다.", nameof(clip));
        if (clip.Samples is null || clip.Samples.Length == 0)
            throw new ArgumentException("소리 데이터가 비어 있습니다.", nameof(clip));
        if (clip.Samples.Length > MaximumSamples)
            throw new ArgumentException("소리 데이터가 너무 큽니다. 최대 3,200만 개의 샘플을 지원합니다.", nameof(clip));
        if (clip.Samples.Length % clip.Channels != 0)
            throw new ArgumentException("소리 데이터의 채널 구성이 올바르지 않습니다.", nameof(clip));
    }

    internal static void ValidateSample(float sample)
    {
        if (!float.IsFinite(sample))
            throw new ArgumentException("소리 데이터에 읽을 수 없는 수치가 있습니다.");
    }
}
