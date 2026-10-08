using SoriLab.Core;

namespace SoriLab.Checks;

public static class PlaybackChecks
{
    public static int Run(string dir)
    {
        if (string.IsNullOrWhiteSpace(dir))
            throw new ArgumentException("검증용 임시 폴더가 필요합니다.", nameof(dir));

        // 이 검증은 메모리 안의 소리만 사용하므로 파일을 만들지 않습니다.
        var checks = new (string Name, Action Check)[]
        {
            ("전체 시작 위치의 소리 참조 재사용", StartReusesAudio),
            ("스테레오 채널 정렬과 완성된 샘플 보존", StereoSlice),
            ("잘라 낸 소리와 원본의 독립성", SourceUnchanged),
            ("절반 프레임을 위로 반올림", HalfFrameRounding),
            ("실제 시작 프레임으로 시간 표시", QuantizedTimelineStart),
            ("선택 클립의 절대 위치를 각각 반올림", SelectionOffsetRounding),
            ("선택 클립 밖에서는 클립 시작 재생", SelectionOutsideFallback),
            ("전체 소리의 끝 프레임 재생 거부", FullEndBoundary),
            ("마지막 한 프레임과 최소 길이 재생", LastFrame),
            ("잘못된 커서와 클립 위치 거부", InvalidPositions),
            ("잘못된 소리 형식과 유한하지 않은 샘플 거부", InvalidAudio),
            ("큰 샘플레이트와 120초 위치의 정수 범위", LargeFramePositions)
        };

        var passed = 0;
        foreach (var (name, check) in checks)
        {
            check();
            passed++;
            Console.WriteLine($"통과: 커서 재생 — {name}");
        }
        return passed;
    }

    private static AudioClip Mono(float[] samples, int sampleRate = 1000) =>
        new("커서 재생 검증", sampleRate, 1, samples);

    private static void StartReusesAudio()
    {
        var source = Mono([0.25f, -0.5f, 0.75f]);
        var result = PlaybackPreview.FromCursor(source, 0);
        True(ReferenceEquals(source, result.Audio), "첫 프레임부터 재생할 때 불필요하게 소리를 복사했습니다.");
        Near(0, result.TimelineStartSeconds, "전체 재생 시작 위치");
    }

    private static void StereoSlice()
    {
        var source = new AudioClip("완성된 스테레오", 1000, 2, [0, 0, 0.3f, -0.7f, 1.5f, -2, 0.1f, -0.2f]);
        var result = PlaybackPreview.FromCursor(source, 0.001);
        True(result.Audio.Name == source.Name && result.Audio.SampleRate == source.SampleRate && result.Audio.Channels == 2,
            "소리 이름과 스테레오 형식이 달라졌습니다.");
        True(result.Audio.Samples.SequenceEqual(new float[] { 0.3f, -0.7f, 1.5f, -2, 0.1f, -0.2f }),
            "커서에서 자른 소리에 페이드나 음량 제한을 추가했거나 채널이 어긋났습니다.");
        Near(0.001, result.TimelineStartSeconds, "스테레오 시작 위치");
    }

    private static void SourceUnchanged()
    {
        var source = Mono([0.1f, 0.2f, 0.3f, 0.4f]);
        var original = source.Samples.ToArray();
        var result = PlaybackPreview.FromCursor(source, 0.002);
        True(source.Samples.SequenceEqual(original), "재생 준비가 원본 샘플을 바꿨습니다.");
        True(!ReferenceEquals(result.Audio.Samples, source.Samples), "중간부터 재생하는 샘플 배열이 원본과 공유됩니다.");
        result.Audio.Samples[0] = -1;
        True(source.Samples.SequenceEqual(original), "잘라 낸 결과를 바꾸면 원본까지 바뀝니다.");
    }

    private static void HalfFrameRounding()
    {
        var source = Mono([0, 1, 2, 3, 4], 8);
        var half = PlaybackPreview.FromCursor(source, 0.0625);
        True(half.Audio.Samples[0] == 1, "정확히 절반 프레임인 커서를 0에서 멀어지는 방향으로 반올림해야 합니다.");
        var below = PlaybackPreview.FromCursor(source, Math.BitDecrement(0.0625));
        True(ReferenceEquals(source, below.Audio), "절반 프레임보다 작은 커서는 첫 프레임을 재사용해야 합니다.");
    }

    private static void QuantizedTimelineStart()
    {
        var source = Mono([0, 1, 2, 3, 4]);
        var result = PlaybackPreview.FromCursor(source, 0.0017);
        True(result.Audio.Samples[0] == 2, "커서가 가장 가까운 출력 프레임에 맞춰지지 않았습니다.");
        Near(0.002, result.TimelineStartSeconds, "양자화한 재생 시작 위치");
    }

    private static void SelectionOffsetRounding()
    {
        var source = Mono([10, 20, 30, 40], 8);
        // 절대 커서 11.5프레임과 위치 8.25프레임은 각각 12, 8이 됩니다.
        // 차이를 먼저 반올림하면 3프레임이 되어, 여기서는 끝 경계 처리가 달라집니다.
        var atEnd = PlaybackPreview.FromCursor(source, 1.4375, 1.03125);
        True(ReferenceEquals(source, atEnd.Audio), "선택 클립 위치와 커서를 먼저 각각 반올림해야 합니다.");
        Near(1, atEnd.TimelineStartSeconds, "선택 클립 시작 위치의 양자화");
        var inside = PlaybackPreview.FromCursor(source, 1.1875, 1.03125);
        True(inside.Audio.Samples.SequenceEqual(new float[] { 30, 40 }), "선택 클립 안의 커서에서 정확히 두 프레임을 잘라야 합니다.");
        Near(1.25, inside.TimelineStartSeconds, "선택 클립 내부의 절대 시작 위치");
    }

    private static void SelectionOutsideFallback()
    {
        var source = Mono([0.2f, 0.4f, 0.6f, 0.8f], 8);
        foreach (var cursor in new[] { 0d, 0.875, 1.5, 1.625, 120 })
        {
            var result = PlaybackPreview.FromCursor(source, cursor, 1);
            True(ReferenceEquals(source, result.Audio), "선택 클립 밖의 커서는 소리 시작으로 돌아가야 합니다.");
            Near(1, result.TimelineStartSeconds, "선택 클립 밖 커서의 시작 위치");
        }
        True(ReferenceEquals(source, PlaybackPreview.FromCursor(source, 1, 1).Audio), "클립 시작 경계는 원본을 재사용해야 합니다.");
    }

    private static void FullEndBoundary()
    {
        var source = Mono([1, 2, 3, 4], 8);
        foreach (var cursor in new[] { 0.4375, 0.5, 0.625, 120 })
            Throws(() => PlaybackPreview.FromCursor(source, cursor), "끝으로 반올림되거나 끝을 지난 전체 재생 커서를 허용했습니다.");
    }

    private static void LastFrame()
    {
        var source = new AudioClip("끝 프레임", 8, 2, [1, -1, 2, -2, 3, -3]);
        var last = PlaybackPreview.FromCursor(source, 0.25);
        True(last.Audio.FrameCount == 1 && last.Audio.Samples.SequenceEqual(new float[] { 3, -3 }),
            "마지막 프레임을 버리거나 채널 한쪽을 누락했습니다.");
        var single = Mono([0.7f], 8);
        True(ReferenceEquals(single, PlaybackPreview.FromCursor(single, 0).Audio), "한 프레임짜리 소리는 시작부터 재생할 수 있어야 합니다.");
        True(ReferenceEquals(single, PlaybackPreview.FromCursor(single, 2, 2).Audio), "한 프레임짜리 선택 클립의 시작 재생이 실패했습니다.");
    }

    private static void InvalidPositions()
    {
        var source = Mono([1, 2, 3]);
        foreach (var invalid in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity, -0.001, 120.001 })
        {
            Throws(() => PlaybackPreview.FromCursor(source, invalid), "잘못된 커서 위치를 허용했습니다.");
            Throws(() => PlaybackPreview.FromCursor(source, 0, invalid), "잘못된 선택 클립 위치를 허용했습니다.");
        }
    }

    private static void InvalidAudio()
    {
        var source = Mono([1, 2, 3]);
        Throws(() => PlaybackPreview.FromCursor(null!, 0), "소리 누락을 허용했습니다.");
        foreach (var invalid in new[]
        {
            source with { SampleRate = 0 }, source with { SampleRate = -1 },
            source with { Channels = 0 }, source with { Channels = 3 },
            source with { Channels = 2 }, source with { Samples = [] }, source with { Samples = null! }
        })
            Throws(() => PlaybackPreview.FromCursor(invalid, 0), "잘못된 소리 형식을 허용했습니다.");

        foreach (var invalidSample in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity })
        {
            Throws(() => PlaybackPreview.FromCursor(Mono([invalidSample, 1, 2]), 0), "원본 재사용 경로에서 잘못된 샘플을 허용했습니다.");
            Throws(() => PlaybackPreview.FromCursor(Mono([invalidSample, 1, 2]), 0.001), "잘라 낸 부분에 있는 잘못된 샘플을 놓쳤습니다.");
            Throws(() => PlaybackPreview.FromCursor(Mono([1, invalidSample, 2]), 0.001), "재생할 부분에 있는 잘못된 샘플을 놓쳤습니다.");
        }
    }

    private static void LargeFramePositions()
    {
        var source = Mono([0.25f], int.MaxValue);
        Throws(() => PlaybackPreview.FromCursor(source, 120), "매우 큰 커서 프레임이 정수 범위를 넘어 잘못 허용되었습니다.");
        var result = PlaybackPreview.FromCursor(source, 120, 120);
        True(ReferenceEquals(source, result.Audio), "큰 프레임 위치에서 선택 클립 시작을 올바르게 찾지 못했습니다.");
        Near(120, result.TimelineStartSeconds, "120초 선택 클립 위치");
    }

    private static void True(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private static void Near(double expected, double actual, string description)
    {
        if (!double.IsFinite(actual) || Math.Abs(expected - actual) > 1e-12)
            throw new InvalidOperationException($"{description}: 예상 {expected}, 실제 {actual}");
    }

    private static void Throws(Action action, string message)
    {
        try { action(); }
        catch (ArgumentException) { return; }
        throw new InvalidOperationException(message);
    }
}
