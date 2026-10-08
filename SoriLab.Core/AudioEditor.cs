namespace SoriLab.Core;

public static class AudioEditor
{
    public static AudioClip Render(AudioClip clip, EditSettings settings)
    {
        AudioValidation.Validate(clip);
        if (settings is null)
            throw new ArgumentNullException(nameof(settings), "편집 설정이 없습니다.");
        if (settings.StartFrame < 0 || settings.EndFrame > clip.FrameCount ||
            settings.EndFrame <= settings.StartFrame)
            throw new ArgumentException("선택 구간은 소리 안에 있어야 하며, 끝이 시작보다 뒤여야 합니다.", nameof(settings));
        if (!double.IsFinite(settings.GainDb))
            throw new ArgumentException("음량에는 유한한 숫자를 입력해 주세요.", nameof(settings));
        if (!double.IsFinite(settings.FadeInMs) || settings.FadeInMs < 0 ||
            !double.IsFinite(settings.FadeOutMs) || settings.FadeOutMs < 0)
            throw new ArgumentException("페이드 길이에는 0 이상의 유한한 숫자를 입력해 주세요.", nameof(settings));

        var gain = Math.Pow(10, settings.GainDb / 20);
        if (!double.IsFinite(gain))
            throw new ArgumentException("음량이 너무 큽니다. 값을 낮춰 주세요.", nameof(settings));

        // 선택 구간 밖의 잘못된 데이터도 먼저 확인해 손상된 원본을 조용히 사용하지 않습니다.
        foreach (var sample in clip.Samples)
            AudioValidation.ValidateSample(sample);

        var frameCount = settings.EndFrame - settings.StartFrame;
        var fadeInFrames = GetFadeFrames(settings.FadeInMs, clip.SampleRate, frameCount);
        var fadeOutFrames = GetFadeFrames(settings.FadeOutMs, clip.SampleRate, frameCount);
        var samples = new float[frameCount * clip.Channels];

        for (var frame = 0; frame < frameCount; frame++)
        {
            var envelope = gain;
            if (frame < fadeInFrames)
                envelope *= fadeInFrames == 1 ? 0 : (double)frame / (fadeInFrames - 1);
            if (frame >= frameCount - fadeOutFrames)
                envelope *= fadeOutFrames == 1 ? 0 : (double)(frameCount - 1 - frame) / (fadeOutFrames - 1);

            for (var channel = 0; channel < clip.Channels; channel++)
            {
                var value = clip.Samples[(settings.StartFrame + frame) * clip.Channels + channel] * envelope;
                if (!double.IsFinite(value) || value > float.MaxValue || value < -float.MaxValue)
                    throw new ArgumentException("편집한 음량이 처리 범위를 넘었습니다. 음량을 낮춰 주세요.", nameof(settings));

                // 음량 초과를 분석할 수 있도록 이 단계에서는 값을 잘라내지 않습니다.
                samples[frame * clip.Channels + channel] = (float)value;
            }
        }

        return new AudioClip(clip.Name, clip.SampleRate, clip.Channels, samples);
    }

    public static double AnalyzePeak(AudioClip clip)
    {
        AudioValidation.Validate(clip);
        var peak = 0d;
        foreach (var sample in clip.Samples)
        {
            AudioValidation.ValidateSample(sample);
            peak = Math.Max(peak, Math.Abs((double)sample));
        }
        return peak;
    }

    private static int GetFadeFrames(double milliseconds, int sampleRate, int frameCount)
    {
        if (milliseconds == 0)
            return 0;

        // 먼저 길이를 제한해 매우 큰 입력에서도 곱셈이 넘치지 않도록 합니다.
        if (milliseconds >= (double)frameCount / sampleRate * 1000)
            return frameCount;

        return Math.Clamp((int)Math.Round(milliseconds * sampleRate / 1000, MidpointRounding.AwayFromZero), 1, frameCount);
    }
}
