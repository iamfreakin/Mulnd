using SoriLab.Core;

namespace SoriLab.Checks;

public static class GeneratorChecks
{
    public static int Run(string dir)
    {
        if (string.IsNullOrWhiteSpace(dir))
            throw new ArgumentException("검증용 임시 폴더가 필요합니다.", nameof(dir));

        var checks = new (string Name, Action Check)[]
        {
            ("모노 출력과 정확한 길이", OutputContract),
            ("최소·최대 길이의 경계", DurationBounds),
            ("사인파의 알려진 위상과 음량", SinePhaseAndGain),
            ("상승·하강 주파수의 연속 위상", SweepPhase),
            ("자동 페이드의 시작과 끝", AutomaticFades),
            ("요청한 밀리초 길이의 페이드", ExplicitFades),
            ("겹치는 페이드의 곱", OverlappingFades),
            ("같은 시드의 노이즈 재현", RepeatedSeed),
            ("고정 난수식의 알려진 결과", KnownSeed),
            ("다른 시드와 0 시드의 노이즈", DifferentSeeds),
            ("노이즈의 평균과 에너지", NoiseStatistics),
            ("네 파형의 유한한 음량 범위", WaveformBounds),
            ("사각파의 접힘 잡음 감소", SquareAliasing),
            ("삼각파의 접힘 성분 제한", TriangleAliasing),
            ("8kHz와 샘플레이트별 상한", FrequencyBounds),
            ("잘못된 길이·주파수·페이드 거부", InvalidSettings),
            ("취소된 생성 중단", Cancellation)
        };

        var passed = 0;
        foreach (var (name, check) in checks)
        {
            check();
            passed++;
            Console.WriteLine($"통과: 생성 — {name}");
        }
        return passed;
    }

    private static GeneratorSettings Basic(WaveShape shape = WaveShape.Sine) => new(shape, 0.1, 1000, 1000, 0, 0, 0);

    private static void OutputContract()
    {
        var clip = SoundGenerator.Generate(Basic());
        Equal(48000, clip.SampleRate, "샘플레이트");
        Equal(1, clip.Channels, "채널 수");
        Equal(4800, clip.FrameCount, "프레임 수");
        Near(0.1, clip.DurationSeconds, 1e-12, "길이");
        True(clip.Name == "생성 · 사인파", "생성 이름에 시간 등 매번 달라지는 값을 넣으면 안 됩니다.");
    }

    private static void DurationBounds()
    {
        var settings = Basic() with { StartFrequency = 100, EndFrequency = 100 };
        Equal(10, SoundGenerator.Generate(settings with { DurationSeconds = 0.01 }, 1000).FrameCount, "최소 길이");
        Equal(30000, SoundGenerator.Generate(settings with { DurationSeconds = 30 }, 1000).FrameCount, "최대 길이");
        Equal(4805, SoundGenerator.Generate(Basic() with { DurationSeconds = 4804.5 / 48000 }).FrameCount, "샘플 수 반올림");
    }

    private static void SinePhaseAndGain()
    {
        var clip = SoundGenerator.Generate(Basic() with { GainDb = -6.020599913279624 });
        for (var frame = 100; frame < clip.FrameCount - 100; frame++)
            Near(0.5 * Math.Sin(2 * Math.PI * 1000 * frame / 48000), clip.Samples[frame], 1e-6, "사인파 위상과 음량");
    }

    private static void SweepPhase()
    {
        foreach (var (start, end) in new[] { (200d, 3000d), (3000d, 200d) })
        {
            var clip = SoundGenerator.Generate(Basic() with { StartFrequency = start, EndFrequency = end });
            foreach (var frame in new[] { 100, 317, 2111, 4200, 4699 })
            {
                // 선형 주파수의 연속 적분값으로 비교해 샘플별 위상 초기화 오류를 찾습니다.
                var cycles = (start * frame + (end - start) * frame * frame / (2 * (clip.FrameCount - 1))) / 48000;
                Near(Math.Sin(2 * Math.PI * cycles), clip.Samples[frame], 1e-5, "선형 주파수 변화의 위상");
            }
        }
    }

    private static void AutomaticFades()
    {
        foreach (var shape in Enum.GetValues<WaveShape>())
        {
            var clip = SoundGenerator.Generate(Basic(shape));
            Near(0, clip.Samples[0], 0, "시작 샘플");
            Near(0, clip.Samples[^1], 0, "마지막 샘플");
        }
        var sine = SoundGenerator.Generate(Basic());
        Near(Math.Sin(2 * Math.PI * 1000 / 48000) / 95, sine.Samples[1], 1e-7, "자동 2ms 페이드");
    }

    private static void ExplicitFades()
    {
        var clip = SoundGenerator.Generate(Basic() with { StartFrequency = 997, EndFrequency = 997, AttackMs = 10, ReleaseMs = 20 });
        Near(Math.Sin(2 * Math.PI * 997 * 240 / 48000) * 240 / 479, clip.Samples[240], 1e-6, "앞쪽 10ms 페이드");
        var frame = clip.FrameCount - 480;
        Near(Math.Sin(2 * Math.PI * 997 * frame / 48000) * 479 / 959, clip.Samples[frame], 1e-6, "뒤쪽 20ms 페이드");
    }

    private static void OverlappingFades()
    {
        var settings = Basic(WaveShape.Noise) with { DurationSeconds = 0.01 };
        var reference = SoundGenerator.Generate(settings);
        var faded = SoundGenerator.Generate(settings with { AttackMs = 30000, ReleaseMs = 30000 });
        Near(reference.Samples[240] * (240d / 479) * (239d / 479), faded.Samples[240], 1e-7, "겹치는 페이드");
        Near(0, faded.Samples[0], 0, "긴 페이드 시작");
        Near(0, faded.Samples[^1], 0, "긴 페이드 끝");
    }

    private static void RepeatedSeed()
    {
        var settings = Basic(WaveShape.Noise) with { Seed = -193 };
        var first = SoundGenerator.Generate(settings);
        var second = SoundGenerator.Generate(settings);
        True(first.Samples.SequenceEqual(second.Samples), "같은 시드의 노이즈는 샘플이 완전히 같아야 합니다.");
        True(!ReferenceEquals(first.Samples, second.Samples), "새 생성 결과는 독립적인 배열이어야 합니다.");
    }

    private static void KnownSeed()
    {
        var settings = new GeneratorSettings(WaveShape.Noise, 0.01, 100, 100, 0, 0, 0, 1);
        var samples = SoundGenerator.Generate(settings, 1000).Samples;
        // xorshift32의 알려진 2~5번째 출력값이며, 이 위치에서는 페이드가 끝난 상태입니다.
        float[] expected = [-0.9685051441192627f, 0.23280811309814453f, -0.8567627668380737f, 0.11697661876678467f];
        for (var index = 0; index < expected.Length; index++)
            Near(expected[index], samples[index + 1], 0, "고정 노이즈 표본");
    }

    private static void DifferentSeeds()
    {
        var first = SoundGenerator.Generate(Basic(WaveShape.Noise) with { Seed = 1 });
        var second = SoundGenerator.Generate(Basic(WaveShape.Noise) with { Seed = 2 });
        var zero = SoundGenerator.Generate(Basic(WaveShape.Noise) with { Seed = 0 });
        True(!first.Samples.SequenceEqual(second.Samples), "서로 다른 시드의 노이즈가 같습니다.");
        True(zero.Samples.Skip(100).Take(100).Distinct().Count() > 90, "0 시드가 고정된 한 값으로 멈췄습니다.");
    }

    private static void NoiseStatistics()
    {
        var samples = SoundGenerator.Generate(Basic(WaveShape.Noise) with { DurationSeconds = 0.5 }).Samples;
        var mean = samples.Average(value => (double)value);
        var energy = Math.Sqrt(samples.Average(value => (double)value * value));
        Near(0, mean, 0.03, "노이즈 평균");
        Near(1 / Math.Sqrt(3), energy, 0.03, "노이즈 에너지");
    }

    private static void WaveformBounds()
    {
        foreach (var shape in Enum.GetValues<WaveShape>())
        {
            var clip = SoundGenerator.Generate(Basic(shape) with { StartFrequency = 20, EndFrequency = 8000 });
            True(clip.Samples.All(value => float.IsFinite(value) && Math.Abs(value) <= 1.000001), "생성 파형이 음량 범위를 넘었습니다.");
            True(clip.Samples.Any(value => Math.Abs(value) > 0.1), "생성한 파형이 무음에 가깝습니다.");
        }
    }

    private static void SquareAliasing()
    {
        var clip = SoundGenerator.Generate(Basic(WaveShape.Square) with { DurationSeconds = 0.2, StartFrequency = 5500, EndFrequency = 5500 });
        var naive = new float[clip.Samples.Length];
        for (var frame = 0; frame < naive.Length; frame++)
            naive[frame] = (5500d * frame / 48000 % 1) < 0.5 ? 1 : -1;
        // 7차 성분 38.5kHz가 접히는 9.5kHz를 정수 개수의 주기에 걸쳐 비교합니다.
        var correctedAlias = Projection(clip.Samples, 9500, 960, 7680);
        var naiveAlias = Projection(naive, 9500, 960, 7680);
        True(correctedAlias < naiveAlias * 0.3, $"사각파 접힘 잡음 감소가 부족합니다: 보정 {correctedAlias}, 단순 파형 {naiveAlias}");
    }

    private static void TriangleAliasing()
    {
        var clip = SoundGenerator.Generate(Basic(WaveShape.Triangle) with { DurationSeconds = 0.2, StartFrequency = 5500, EndFrequency = 5500 });
        True(Projection(clip.Samples, 9500, 960, 7680) < 1e-5, "삼각파에 허용 범위 밖 고조파의 접힘 성분이 있습니다.");
        True(Projection(clip.Samples, 5500, 960, 7680) > 0.8, "삼각파 기본 주파수 성분이 사라졌습니다.");
    }

    private static void FrequencyBounds()
    {
        SoundGenerator.Generate(Basic() with { StartFrequency = 8000, EndFrequency = 8000 });
        SoundGenerator.Generate(Basic() with { StartFrequency = 3200, EndFrequency = 3200 }, 8000);
        Throws<ArgumentException>(() => SoundGenerator.Generate(Basic() with { StartFrequency = 8000.1 }));
        Throws<ArgumentException>(() => SoundGenerator.Generate(Basic() with { EndFrequency = 3200.1 }, 8000));
    }

    private static void InvalidSettings()
    {
        var basis = Basic();
        GeneratorSettings[] invalid =
        [
            basis with { Shape = (WaveShape)99 }, basis with { DurationSeconds = 0.009 }, basis with { DurationSeconds = 30.001 },
            basis with { DurationSeconds = double.NaN }, basis with { StartFrequency = 19.9 }, basis with { EndFrequency = double.PositiveInfinity },
            basis with { GainDb = -60.1 }, basis with { GainDb = 0.001 }, basis with { GainDb = double.NaN },
            basis with { AttackMs = -1 }, basis with { ReleaseMs = 30000.1 }, basis with { AttackMs = double.NaN },
            basis with { ReleaseMs = double.PositiveInfinity }
        ];
        foreach (var settings in invalid)
            Throws<ArgumentException>(() => SoundGenerator.Generate(settings));
        Throws<ArgumentException>(() => SoundGenerator.Generate(basis, sampleRate: 0));
        Throws<ArgumentException>(() => SoundGenerator.Generate(basis, sampleRate: int.MaxValue));
    }

    private static void Cancellation()
    {
        using var source = new CancellationTokenSource();
        source.Cancel();
        Throws<OperationCanceledException>(() => SoundGenerator.Generate(Basic(), cancellationToken: source.Token));
    }

    private static double Projection(float[] samples, double frequency, int start, int count)
    {
        var real = 0d;
        var imaginary = 0d;
        for (var frame = start; frame < start + count; frame++)
        {
            var phase = 2 * Math.PI * frequency * frame / 48000;
            real += samples[frame] * Math.Cos(phase);
            imaginary += samples[frame] * Math.Sin(phase);
        }
        return 2 * Math.Sqrt(real * real + imaginary * imaginary) / count;
    }

    private static void Equal(int expected, int actual, string name)
    {
        if (expected != actual)
            throw new InvalidOperationException($"{name}: 기대 {expected}, 실제 {actual}");
    }

    private static void Near(double expected, double actual, double tolerance, string name)
    {
        if (!double.IsFinite(actual) || Math.Abs(expected - actual) > tolerance)
            throw new InvalidOperationException($"{name}: 기대 {expected}, 실제 {actual}, 허용 오차 {tolerance}");
    }

    private static void True(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private static void Throws<TException>(Action action) where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }
        throw new InvalidOperationException($"{typeof(TException).Name} 예외가 필요합니다.");
    }
}
