namespace SoriLab.Core;

/// <summary>생성할 기본 파형의 종류입니다.</summary>
public enum WaveShape
{
    Sine,
    Triangle,
    Square,
    Noise
}

/// <summary>소리 길이, 선형 주파수 변화, 음량과 양끝 페이드를 지정합니다.</summary>
/// <param name="Shape">기본 파형입니다.</param>
/// <param name="DurationSeconds">0.01초부터 30초 사이의 길이입니다.</param>
/// <param name="StartFrequency">시작 주파수입니다. 20Hz부터 8,000Hz 또는 샘플레이트의 40% 중 작은 값까지 지원합니다.</param>
/// <param name="EndFrequency">마지막 샘플에서의 주파수입니다. 노이즈에서는 음색에 영향을 주지 않습니다.</param>
/// <param name="GainDb">-60dB부터 0dB 사이의 음량입니다.</param>
/// <param name="AttackMs">앞쪽 페이드입니다. 0ms를 입력하면 클릭 방지를 위해 2ms를 사용합니다.</param>
/// <param name="ReleaseMs">뒤쪽 페이드입니다. 0ms를 입력하면 클릭 방지를 위해 2ms를 사용합니다.</param>
/// <param name="Seed">같은 노이즈를 다시 만들 때 사용하는 고정 정수입니다.</param>
public sealed record GeneratorSettings(
    WaveShape Shape,
    double DurationSeconds,
    double StartFrequency,
    double EndFrequency,
    double GainDb,
    double AttackMs,
    double ReleaseMs,
    int Seed = 1);

public static class SoundGenerator
{
    /// <summary>설정으로 모노 소리를 만듭니다. 길이는 가장 가까운 샘플 수로 맞추며 시작과 끝은 0입니다.</summary>
    /// <remarks>주파수는 첫 샘플부터 마지막 샘플까지 선형으로 변합니다. 두 페이드가 겹치면 곱합니다.</remarks>
    public static AudioClip Generate(GeneratorSettings settings, int sampleRate = 48000, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Validate(settings, sampleRate);
        var requestedFrames = Math.Round(settings.DurationSeconds * sampleRate, MidpointRounding.AwayFromZero);
        if (requestedFrames > AudioValidation.MaximumSamples)
            throw new ArgumentException("생성할 소리 데이터가 너무 큽니다. 길이 또는 샘플레이트를 줄여 주세요.", nameof(settings));

        var frameCount = Math.Max(1, (int)requestedFrames);
        var samples = new float[frameCount];
        var gain = Math.Pow(10, settings.GainDb / 20);
        var attackFrames = GetFadeFrames(settings.AttackMs, sampleRate, frameCount);
        var releaseFrames = GetFadeFrames(settings.ReleaseMs, sampleRate, frameCount);
        var frequencySlope = frameCount > 1 ? (settings.EndFrequency - settings.StartFrequency) / (frameCount - 1) : 0;
        var phase = 0d;
        var randomState = unchecked((uint)settings.Seed);
        if (randomState == 0)
            randomState = 0xa341316c;

        for (var frame = 0; frame < frameCount; frame++)
        {
            if ((frame & 2047) == 0)
                cancellationToken.ThrowIfCancellationRequested();

            var frequency = settings.StartFrequency + frequencySlope * frame;
            var value = settings.Shape switch
            {
                WaveShape.Sine => Math.Sin(2 * Math.PI * phase),
                WaveShape.Triangle => Triangle(phase, frequency, sampleRate),
                WaveShape.Square => Square(phase, frequency / sampleRate),
                WaveShape.Noise => NextNoise(ref randomState),
                _ => throw new ArgumentException("지원하지 않는 파형입니다.", nameof(settings))
            };
            var envelope = gain;
            if (frame < attackFrames)
                envelope *= attackFrames == 1 ? 0 : (double)frame / (attackFrames - 1);
            if (frame >= frameCount - releaseFrames)
                envelope *= releaseFrames == 1 ? 0 : (double)(frameCount - 1 - frame) / (releaseFrames - 1);
            samples[frame] = (float)(value * envelope);

            // 선형으로 변하는 주파수를 샘플 사이에서 적분해 주파수 변경 때 위상이 끊기지 않도록 합니다.
            phase += (frequency + frequencySlope * 0.5) / sampleRate;
            phase -= Math.Floor(phase);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var label = settings.Shape switch
        {
            WaveShape.Sine => "사인파",
            WaveShape.Triangle => "삼각파",
            WaveShape.Square => "사각파",
            WaveShape.Noise => "노이즈",
            _ => "소리"
        };
        return new AudioClip($"생성 · {label}", sampleRate, 1, samples);
    }

    private static void Validate(GeneratorSettings settings, int sampleRate)
    {
        if (settings is null)
            throw new ArgumentNullException(nameof(settings), "소리 생성 설정이 없습니다.");
        if (sampleRate < 50)
            throw new ArgumentException("샘플레이트가 너무 낮아 20Hz 소리를 만들 수 없습니다.", nameof(sampleRate));
        if (!Enum.IsDefined(settings.Shape))
            throw new ArgumentException("지원하지 않는 파형입니다.", nameof(settings));
        if (!double.IsFinite(settings.DurationSeconds) || settings.DurationSeconds is < 0.01 or > 30)
            throw new ArgumentException("생성 길이는 0.01초부터 30초 사이로 설정해 주세요.", nameof(settings));
        var maximumFrequency = Math.Min(8000, sampleRate * 0.4);
        if (!double.IsFinite(settings.StartFrequency) || !double.IsFinite(settings.EndFrequency) ||
            settings.StartFrequency < 20 || settings.EndFrequency < 20 ||
            settings.StartFrequency > maximumFrequency || settings.EndFrequency > maximumFrequency)
            throw new ArgumentException($"주파수는 20Hz부터 {maximumFrequency:0.##}Hz 사이로 설정해 주세요.", nameof(settings));
        if (!double.IsFinite(settings.GainDb) || settings.GainDb is < -60 or > 0)
            throw new ArgumentException("생성 음량은 -60dB부터 0dB 사이로 설정해 주세요.", nameof(settings));
        if (!double.IsFinite(settings.AttackMs) || !double.IsFinite(settings.ReleaseMs) ||
            settings.AttackMs is < 0 or > 30000 || settings.ReleaseMs is < 0 or > 30000)
            throw new ArgumentException("앞뒤 페이드는 0ms부터 30,000ms 사이로 설정해 주세요.", nameof(settings));
    }

    private static int GetFadeFrames(double milliseconds, int sampleRate, int frameCount)
    {
        var effectiveMs = milliseconds == 0 ? 2 : milliseconds;
        var frames = Math.Ceiling(effectiveMs * sampleRate / 1000);
        return (int)Math.Min(frameCount, Math.Max(2, frames));
    }

    private static double Square(double phase, double phaseStep)
    {
        var value = phase < 0.5 ? 1d : -1d;
        value += PolyBlep(phase, phaseStep);
        var halfShifted = phase + 0.5;
        if (halfShifted >= 1)
            halfShifted -= 1;
        value -= PolyBlep(halfShifted, phaseStep);
        return value;
    }

    private static double PolyBlep(double phase, double phaseStep)
    {
        // 불연속의 양쪽 표본에 2차식 보정을 더해 사각파의 접힘 잡음을 줄입니다.
        if (phase < phaseStep)
        {
            var distance = phase / phaseStep;
            return 2 * distance - distance * distance - 1;
        }
        if (phase > 1 - phaseStep)
        {
            var distance = (phase - 1) / phaseStep;
            return distance * distance + 2 * distance + 1;
        }
        return 0;
    }

    private static double Triangle(double phase, double frequency, int sampleRate)
    {
        var nyquist = sampleRate / 2d;
        var value = 0d;
        var sign = 1d;
        // 최대 31차 홀수 성분을 사용하고, 나이퀴스트 경계에서는 성분을 부드럽게 줄입니다.
        for (var harmonic = 1; harmonic <= 31; harmonic += 2)
        {
            var harmonicFrequency = harmonic * frequency;
            if (harmonicFrequency >= nyquist)
                break;
            var taper = 1d;
            if (harmonicFrequency > nyquist * 0.9)
            {
                var position = (harmonicFrequency / nyquist - 0.9) / 0.1;
                taper = 1 - position * position * (3 - 2 * position);
            }
            value += sign * taper * Math.Sin(2 * Math.PI * harmonic * phase) / (harmonic * harmonic);
            sign = -sign;
        }
        return value * (8 / (Math.PI * Math.PI));
    }

    private static double NextNoise(ref uint state)
    {
        // 고정 xorshift32 식과 상위 24비트를 사용해 실행 환경의 Random 구현에 의존하지 않습니다.
        state ^= state << 13;
        state ^= state >> 17;
        state ^= state << 5;
        return (state >> 8) / 8388608d - 1;
    }
}
