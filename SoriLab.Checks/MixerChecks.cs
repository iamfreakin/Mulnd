using SoriLab.Core;

namespace SoriLab.Checks;

public static class MixerChecks
{
    public static int Run(string dir)
    {
        if (string.IsNullOrWhiteSpace(dir))
            throw new ArgumentException("검증용 임시 폴더가 필요합니다.", nameof(dir));

        var checks = new (string Name, Action Check)[]
        {
            ("같은 속도·샘플레이트의 정확한 스테레오 복사", IdentityStereo),
            ("모노를 양쪽 채널로 복사", MonoToStereo),
            ("트랙 자르기의 끝 프레임 제외", TrimExclusive),
            ("배치 위치와 임펄스 시점", OffsetImpulse),
            ("서로 다른 샘플레이트의 사인파 조합", MixedRates),
            ("다운샘플링의 접힘 잡음 억제", AntiAliasing),
            ("속도에 따른 길이와 음높이 변화", PlaybackRate),
            ("역재생과 스테레오 채널 보존", ReverseStereo),
            ("편집 후 출력 시간 기준의 페이드", FadesAfterSpeed),
            ("겹치는 페이드와 한 프레임 경계", OverlapAndSingleFrame),
            ("음소거된 트랙을 길이에서 제외", MutedLength),
            ("단독 재생 우선순위", SoloRules),
            ("모두 음소거된 프로젝트의 저장과 재생 구분", AllMuted),
            ("개별 렌더는 배치·음소거·단독 설정과 무관", IsolatedTrack),
            ("내부 음량 초과 보존과 전체 음량 적용", GainAndClipping),
            ("조합 후 원본 불변", SourceUnchanged),
            ("긴 원본의 짧은 구간 사용", LongSourceTrim),
            ("120초 끝 경계와 잘못된 수치 거부", Limits),
            ("32트랙 제한과 중복 식별자 거부", TrackCountAndIds),
            ("공유 원본의 메모리 한도 중복 집계 방지", SharedSourceLimit),
            ("서로 다른 원본의 합계 한도", DistinctSourceLimit),
            ("손상된 원본 수치 거부", NonFiniteSource),
            ("이미 취소된 조합과 개별 렌더", Cancellation)
        };

        var passed = 0;
        foreach (var (name, check) in checks)
        {
            check();
            passed++;
            Console.WriteLine($"통과: 조합 — {name}");
        }
        return passed;
    }

    private static void IdentityStereo()
    {
        float[] samples = [-1, 0.7f, -0.5f, 0.3f, 0, -0.2f, 0.5f, -0.9f];
        var source = new AudioClip("스테레오", 48000, 2, samples);
        var result = AudioMixer.Render([Track(source)]);
        Equal(48000, result.SampleRate, "출력 샘플레이트");
        Equal(2, result.Channels, "출력 채널 수");
        SamplesEqual(samples, result.Samples);
    }

    private static void MonoToStereo()
    {
        var result = AudioMixer.Render([Track(Mono([0.1f, -0.3f, 0.7f]))]);
        SamplesEqual([0.1f, 0.1f, -0.3f, -0.3f, 0.7f, 0.7f], result.Samples);
    }

    private static void TrimExclusive()
    {
        var track = Track(Mono([0.1f, 0.2f, 0.3f, 0.4f])) with { Edit = new EditSettings(1, 3, 0, 0, 0) };
        SamplesEqual([0.2f, 0.2f, 0.3f, 0.3f], AudioMixer.Render([track]).Samples);
        Near(2d / 48000, AudioMixer.GetDurationSeconds(track), 1e-12, "구간 길이");
    }

    private static void OffsetImpulse()
    {
        var samples = new float[64];
        samples[8] = 1;
        var track = Track(Mono(samples)) with { OffsetSeconds = 0.1 };
        var result = AudioMixer.Render([track]);
        Equal(4864, result.FrameCount, "무음이 포함된 길이");
        for (var frame = 0; frame < result.FrameCount; frame++)
        {
            Near(frame == 4808 ? 1 : 0, result.Samples[frame * 2], 0, "임펄스 위치");
            Near(result.Samples[frame * 2], result.Samples[frame * 2 + 1], 0, "임펄스 양쪽 채널");
        }
    }

    private static void MixedRates()
    {
        var first = Track(Sine(44100, 4410, 1000, 0.4));
        var second = Track(Sine(22050, 2205, 1000, 0.6));
        var result = AudioMixer.Render([first, second]);
        Equal(4800, result.FrameCount, "변환된 조합 길이");
        var squaredError = 0d;
        for (var frame = 100; frame < result.FrameCount - 100; frame++)
        {
            var expected = Math.Sin(2 * Math.PI * 1000 * frame / 48000);
            var error = expected - result.Samples[frame * 2];
            squaredError += error * error;
            Near(result.Samples[frame * 2], result.Samples[frame * 2 + 1], 0, "리샘플링 모노 채널");
        }
        True(Math.Sqrt(squaredError / (result.FrameCount - 200)) < 0.002, "변환된 사인파의 오차가 너무 큽니다.");
    }

    private static void AntiAliasing()
    {
        var high = AudioMixer.RenderTrack(Track(Sine(96000, 9600, 30000, 0.8)));
        var low = AudioMixer.RenderTrack(Track(Sine(96000, 9600, 1000, 0.8)));
        True(RootMeanSquare(high, 100) < 0.01, "출력 범위를 넘는 30kHz 신호가 낮은 주파수로 접혔습니다.");
        Near(0.8 / Math.Sqrt(2), RootMeanSquare(low, 100), 0.003, "낮은 주파수의 음량 보존");
    }

    private static void PlaybackRate()
    {
        var original = Track(Sine(48000, 4800, 1000, 0.5));
        var fast = AudioMixer.RenderTrack(original with { PlaybackRate = 2 });
        var slow = AudioMixer.RenderTrack(original with { PlaybackRate = 0.5 });
        Equal(2400, fast.FrameCount, "2배속 길이");
        Equal(9600, slow.FrameCount, "0.5배속 길이");
        Near(0.05, AudioMixer.GetDurationSeconds(original with { PlaybackRate = 2 }), 1e-12, "2배속 초 단위 길이");
        for (var frame = 64; frame < fast.FrameCount - 64; frame++)
            Near(0.5 * Math.Sin(2 * Math.PI * 2000 * frame / 48000), fast.Samples[frame * 2], 0.001, "2배속 음높이");
        for (var frame = 64; frame < slow.FrameCount - 64; frame++)
            Near(0.5 * Math.Sin(2 * Math.PI * 500 * frame / 48000), slow.Samples[frame * 2], 0.001, "0.5배속 음높이");
    }

    private static void ReverseStereo()
    {
        var source = new AudioClip("역재생", 48000, 2, [0.1f, -0.1f, 0.2f, -0.2f, 0.3f, -0.3f, 0.4f, -0.4f]);
        var track = Track(source) with { Reverse = true, Edit = new EditSettings(1, 4, 0, 0, 0) };
        SamplesEqual([0.4f, -0.4f, 0.3f, -0.3f, 0.2f, -0.2f], AudioMixer.RenderTrack(track).Samples);
    }

    private static void FadesAfterSpeed()
    {
        var track = Track(Mono(Enumerable.Repeat(1f, 4800).ToArray())) with
        {
            PlaybackRate = 2,
            Edit = new EditSettings(0, 4800, 0, 10, 10)
        };
        var result = AudioMixer.RenderTrack(track);
        Equal(2400, result.FrameCount, "페이드 적용 전 변경된 길이");
        Near(0, result.Samples[0], 0, "페이드 시작");
        Near(240d / 479, result.Samples[240 * 2], 0.0001, "출력 시간 기준 5ms 페이드");
        Near(1, result.Samples[800 * 2], 0.0001, "페이드 밖 음량");
        Near(0, result.Samples[^1], 0, "페이드 끝");
    }

    private static void OverlapAndSingleFrame()
    {
        var track = Track(Mono([1, 1, 1, 1, 1], 1000)) with { Edit = new EditSettings(0, 5, 0, 5, 5) };
        SamplesEqual([0, 0, 0.1875f, 0.1875f, 0.25f, 0.25f, 0.1875f, 0.1875f, 0, 0], AudioMixer.RenderTrack(track, 1000).Samples);
        var tiny = Track(Mono([0.5f])) with { Edit = new EditSettings(0, 1, 0, 1, 1) };
        SamplesEqual([0, 0], AudioMixer.RenderTrack(tiny).Samples);
    }

    private static void MutedLength()
    {
        var audible = Track(Mono([0.5f]));
        var muted = Track(Mono([1f])) with { Muted = true, OffsetSeconds = 119 };
        var result = AudioMixer.Render([audible, muted]);
        Equal(1, result.FrameCount, "음소거된 트랙은 출력 길이에서 제외");
        SamplesEqual([0.5f, 0.5f], result.Samples);
    }

    private static void SoloRules()
    {
        var normal = Track(Mono([0.1f]));
        var solo = Track(Mono([0.3f])) with { Solo = true };
        var mutedSolo = Track(Mono([0.8f])) with { Solo = true, Muted = true };
        SamplesEqual([0.3f, 0.3f], AudioMixer.Render([normal, solo, mutedSolo]).Samples);
        Throws<ArgumentException>(() => AudioMixer.Render([normal, mutedSolo]));
    }

    private static void AllMuted()
    {
        AudioTrack[] tracks = [Track(Mono([1])) with { Muted = true }];
        AudioMixer.Validate(tracks);
        AudioMixer.Validate([]);
        Throws<ArgumentException>(() => AudioMixer.Render(tracks));
        Throws<ArgumentException>(() => AudioMixer.Render([]));
    }

    private static void IsolatedTrack()
    {
        var track = Track(Mono([0.5f, 0.25f])) with { OffsetSeconds = 12, Muted = true, Solo = true };
        SamplesEqual([0.5f, 0.5f, 0.25f, 0.25f], AudioMixer.RenderTrack(track).Samples);
    }

    private static void GainAndClipping()
    {
        var first = Track(Mono([0.8f]));
        var second = Track(Mono([0.8f]));
        Near(1.6, AudioMixer.Render([first, second]).Samples[0], 1e-6, "합산 중 음량 초과 보존");
        Near(0.8, AudioMixer.Render([first, second], masterGainDb: -6.020599913279624).Samples[0], 1e-6, "전체 음량 조절");
        var louder = first with { Edit = first.Edit with { GainDb = 6.020599913279624 } };
        Near(1.6, AudioMixer.RenderTrack(louder).Samples[0], 1e-6, "트랙 음량 조절");
    }

    private static void SourceUnchanged()
    {
        float[] values = [0.1f, 0.2f, 0.3f, 0.4f];
        var before = (float[])values.Clone();
        var track = Track(Mono(values)) with { Reverse = true, PlaybackRate = 0.5, Edit = new EditSettings(1, 4, 6, 1, 1) };
        var result = AudioMixer.Render([track]);
        result.Samples[0] = 99;
        SamplesEqual(before, values);
    }

    private static void LongSourceTrim()
    {
        var source = Mono(new float[121000], 1000);
        var shortTrack = Track(source) with { Edit = new EditSettings(120000, 121000, 0, 0, 0) };
        Equal(48000, AudioMixer.RenderTrack(shortTrack).FrameCount, "긴 원본의 마지막 1초");
        Throws<ArgumentException>(() => AudioMixer.Render([Track(source)]));
    }

    private static void Limits()
    {
        var track = Track(Mono(new float[1000], 1000));
        AudioMixer.Validate([track with { OffsetSeconds = 119 }]);
        Throws<ArgumentException>(() => AudioMixer.Validate([track with { OffsetSeconds = 119.001 }]));
        Throws<ArgumentException>(() => AudioMixer.Validate([track with { PlaybackRate = 0.24 }]));
        Throws<ArgumentException>(() => AudioMixer.Validate([track with { PlaybackRate = 4.01 }]));
        Throws<ArgumentException>(() => AudioMixer.Validate([track with { OffsetSeconds = -1 }]));
        Throws<ArgumentException>(() => AudioMixer.Validate([track with { OffsetSeconds = double.NaN }]));
        Throws<ArgumentException>(() => AudioMixer.Validate([track], masterGainDb: 12.001));
        Throws<ArgumentException>(() => AudioMixer.Validate([track], masterGainDb: -60.001));
        Throws<ArgumentException>(() => AudioMixer.Validate([track], sampleRate: 0));
    }

    private static void TrackCountAndIds()
    {
        var source = Mono([0]);
        AudioMixer.Validate(Enumerable.Range(0, 32).Select(_ => Track(source)).ToArray());
        Throws<ArgumentException>(() => AudioMixer.Validate(Enumerable.Range(0, 33).Select(_ => Track(source)).ToArray()));
        var same = Track(source);
        Throws<ArgumentException>(() => AudioMixer.Validate([same, same]));
    }

    private static void SharedSourceLimit()
    {
        // 같은 원본을 여러 트랙에서 쓰는 경우만 원본 참조 단위로 한 번 계산해야 합니다.
        var source = Mono(new float[1_100_000]);
        AudioMixer.Validate(Enumerable.Range(0, 32).Select(_ => Track(source)).ToArray());
    }

    private static void DistinctSourceLimit()
    {
        // 배열은 공유하더라도 서로 다른 원본 객체이면 각각 별도 재료로 계산합니다.
        var data = new float[1_100_000];
        var tracks = Enumerable.Range(0, 30).Select(_ => Track(Mono(data))).ToArray();
        Throws<ArgumentException>(() => AudioMixer.Validate(tracks));
    }

    private static void NonFiniteSource()
    {
        foreach (var value in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity })
        {
            var track = Track(Mono([0, value])) with { Edit = new EditSettings(0, 1, 0, 0, 0) };
            Throws<ArgumentException>(() => AudioMixer.Render([track]));
        }
    }

    private static void Cancellation()
    {
        using var source = new CancellationTokenSource();
        source.Cancel();
        var track = Track(Mono([0.5f]));
        Throws<OperationCanceledException>(() => AudioMixer.Render([track], cancellationToken: source.Token));
        Throws<OperationCanceledException>(() => AudioMixer.RenderTrack(track, cancellationToken: source.Token));
    }

    private static AudioClip Mono(float[] samples, int sampleRate = 48000) => new("검증 재료", sampleRate, 1, samples);
    private static AudioTrack Track(AudioClip source) => new(Guid.NewGuid(), source, new EditSettings(0, source.FrameCount, 0, 0, 0));

    private static AudioClip Sine(int sampleRate, int frameCount, double frequency, double amplitude)
    {
        var samples = new float[frameCount];
        for (var frame = 0; frame < frameCount; frame++)
            samples[frame] = (float)(amplitude * Math.Sin(2 * Math.PI * frequency * frame / sampleRate));
        return Mono(samples, sampleRate);
    }

    private static double RootMeanSquare(AudioClip clip, int trimFrames)
    {
        var sum = 0d;
        for (var frame = trimFrames; frame < clip.FrameCount - trimFrames; frame++)
            sum += (double)clip.Samples[frame * 2] * clip.Samples[frame * 2];
        return Math.Sqrt(sum / (clip.FrameCount - 2 * trimFrames));
    }

    private static void SamplesEqual(float[] expected, float[] actual)
    {
        Equal(expected.Length, actual.Length, "샘플 수");
        for (var index = 0; index < expected.Length; index++)
            Near(expected[index], actual[index], 0, $"샘플 {index}");
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
