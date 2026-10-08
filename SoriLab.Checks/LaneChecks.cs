using SoriLab.Core;

namespace SoriLab.Checks;

public static class LaneChecks
{
    public static int Run(string dir)
    {
        if (string.IsNullOrWhiteSpace(dir))
            throw new ArgumentException("검증용 임시 폴더가 필요합니다.", nameof(dir));
        var checks = new (string Name, Action Check)[]
        {
            ("기존 클립의 독립 트랙 호환성", LegacyLaneIds),
            ("한 트랙의 앞 클립만 재생", FrontClipWins),
            ("앞 클립의 무음이 뒤 소리를 가림", FrontSilenceWins),
            ("트랙 사이는 합산하고 트랙 안은 덮어씀", MixBetweenLanes),
            ("클립 배열 순서의 우선순위", ArrayOrder),
            ("32트랙·128클립 경계", LaneAndClipLimits),
            ("32개 서로 다른 원본 경계", SourceLimit),
            ("같은 트랙의 음소거·단독 설정 일치", LaneStateConsistency),
            ("트랙 단위 음소거와 단독 재생", LaneMuteAndSolo),
            ("기본 클립 분할의 원본 공유와 위치", PlainSplit),
            ("소수 프레임 길이 클립을 겹침 없이 이어 붙임", ConsecutiveClips),
            ("속도·역재생·페이드 분할의 동일한 소리", ProcessedSplit),
            ("이미 분할한 렌더 원본의 재사용", RepeatedSplit),
            ("겹친 클립의 배열 위치를 보존한 분할", SplitUnderOverlap),
            ("분할 후 원본 샘플 불변", SplitSourceUnchanged),
            ("최소 한 프레임 분할과 경계 거부", SplitBoundaries)
        };
        var passed = 0;
        foreach (var (name, check) in checks)
        {
            check();
            passed++;
            Console.WriteLine($"통과: 다중 클립 — {name}");
        }
        return passed;
    }

    private static AudioClip Mono(float[] samples, int sampleRate = 48000) => new("클립 검증", sampleRate, 1, samples);
    private static AudioTrack Track(AudioClip source, Guid? laneId = null) =>
        new(Guid.NewGuid(), source, new EditSettings(0, source.FrameCount, 0, 0, 0), LaneId: laneId);

    private static void ConsecutiveClips()
    {
        var first = Track(Mono([.25f, -.5f])) with { PlaybackRate = 1.5, OffsetSeconds = .3 / 48000 };
        var second = first with { Id = Guid.NewGuid(), LaneId = first.EffectiveLaneId, OffsetSeconds = AudioMixer.GetEndSeconds(first) };
        var individual = AudioMixer.RenderTrack(first);
        var combined = AudioMixer.Render([first, second]);
        True(combined.FrameCount == 4, "소수 길이 두 클립은 각각 두 프레임씩 겹침 없이 이어져야 합니다.");
        True(individual.Samples.Concat(individual.Samples).SequenceEqual(combined.Samples), "복제한 소리를 이어 붙일 때 마지막 샘플이 가려졌습니다.");
    }

    private static void LegacyLaneIds()
    {
        var first = Track(Mono([0.25f]));
        var second = Track(Mono([0.5f]));
        True(first.EffectiveLaneId == first.Id && second.EffectiveLaneId == second.Id, "이전 클립은 자신의 식별자를 트랙으로 사용해야 합니다.");
        Near(0.75, AudioMixer.Render([first, second]).Samples[0], 0, "기존 두 트랙 합산");
    }

    private static void FrontClipWins()
    {
        var lane = Guid.NewGuid();
        var back = Track(Mono([0.2f, 0.2f, 0.2f, 0.2f]), lane);
        var front = Track(Mono([0.7f, 0.8f]), lane) with { OffsetSeconds = 1d / 48000 };
        FramesNear([0.2f, 0.7f, 0.8f, 0.2f], AudioMixer.Render([back, front]));
    }

    private static void FrontSilenceWins()
    {
        var lane = Guid.NewGuid();
        var back = Track(Mono([1, 1, 1, 1]), lane);
        var front = Track(Mono([0, 0]), lane) with { OffsetSeconds = 1d / 48000 };
        FramesNear([1, 0, 0, 1], AudioMixer.Render([back, front]));
    }

    private static void MixBetweenLanes()
    {
        var lane = Guid.NewGuid();
        var back = Track(Mono([0.2f, 0.2f, 0.2f, 0.2f]), lane);
        var front = Track(Mono([0.7f, 0.8f]), lane) with { OffsetSeconds = 1d / 48000 };
        var other = Track(Mono([0.1f, 0.1f, 0.1f, 0.1f]));
        FramesNear([0.3f, 0.8f, 0.9f, 0.3f], AudioMixer.Render([back, other, front]));
    }

    private static void ArrayOrder()
    {
        var lane = Guid.NewGuid();
        var first = Track(Mono([0.2f, 0.2f]), lane);
        var second = Track(Mono([0.8f, 0.8f]), lane);
        FramesNear([0.8f, 0.8f], AudioMixer.Render([first, second]));
        FramesNear([0.2f, 0.2f], AudioMixer.Render([second, first]));
    }

    private static void LaneAndClipLimits()
    {
        var source = Mono([0]);
        var lane = Guid.NewGuid();
        AudioMixer.Validate(Enumerable.Range(0, 128).Select(_ => Track(source, lane)).ToArray());
        Throws<ArgumentException>(() => AudioMixer.Validate(Enumerable.Range(0, 129).Select(_ => Track(source, lane)).ToArray()));
        AudioMixer.Validate(Enumerable.Range(0, 32).Select(_ => Track(source)).ToArray());
        Throws<ArgumentException>(() => AudioMixer.Validate(Enumerable.Range(0, 33).Select(_ => Track(source)).ToArray()));
        var lanes = Enumerable.Range(0, 32).Select(_ => Guid.NewGuid()).ToArray();
        AudioMixer.Validate(Enumerable.Range(0, 128).Select(index => Track(source, lanes[index / 4])).ToArray());
        True(AudioMixer.MaximumTracks == 32 && AudioMixer.MaximumClips == 128, "공개 한도 상수가 잘못되었습니다.");
    }

    private static void SourceLimit()
    {
        var lane = Guid.NewGuid();
        AudioMixer.Validate(Enumerable.Range(0, 32).Select(_ => Track(Mono([0]), lane)).ToArray());
        Throws<ArgumentException>(() => AudioMixer.Validate(Enumerable.Range(0, 33).Select(_ => Track(Mono([0]), lane)).ToArray()));
    }

    private static void LaneStateConsistency()
    {
        var lane = Guid.NewGuid();
        var first = Track(Mono([1]), lane);
        var second = Track(first.Source, lane);
        Throws<ArgumentException>(() => AudioMixer.Validate([first, second with { Muted = true }]));
        Throws<ArgumentException>(() => AudioMixer.Validate([first, second with { Solo = true }]));
        AudioMixer.Validate([first with { Muted = true }, second with { Muted = true }]);
        AudioMixer.Validate([first with { Solo = true }, second with { Solo = true }]);
    }

    private static void LaneMuteAndSolo()
    {
        var lane = Guid.NewGuid();
        var first = Track(Mono([0.2f, 0.2f]), lane) with { Solo = true };
        var second = Track(Mono([0.5f]), lane) with { Solo = true, OffsetSeconds = 1d / 48000 };
        var other = Track(Mono([0.9f, 0.9f]));
        FramesNear([0.2f, 0.5f], AudioMixer.Render([first, second, other]));
        FramesNear([0.9f, 0.9f], AudioMixer.Render([first with { Solo = false, Muted = true }, second with { Solo = false, Muted = true }, other]));
    }

    private static void PlainSplit()
    {
        var source = Mono(Enumerable.Range(0, 32).Select(index => (float)(index / 32d - 0.5)).ToArray());
        var track = Track(source) with { Edit = new EditSettings(3, 29, 0, 0, 0), OffsetSeconds = 0.1000004, SourcePath = "검증용원본.wav" };
        var pair = AssertIdenticalSplit(track, 11);
        True(ReferenceEquals(source, pair.Left.Source) && ReferenceEquals(source, pair.Right.Source), "기본 편집은 원본을 복사하지 않아야 합니다.");
        True(pair.Left.Id == track.Id && pair.Right.Id != track.Id, "왼쪽 식별자는 보존하고 오른쪽 식별자는 새로 만들어야 합니다.");
        True(pair.Left.LaneId == track.EffectiveLaneId && pair.Right.LaneId == track.EffectiveLaneId, "양쪽 클립의 트랙을 명시해야 합니다.");
        True(pair.Left.SourcePath == track.SourcePath && pair.Right.SourcePath == track.SourcePath, "원본을 공유하면 경로를 보존해야 합니다.");
        Near(0.1, pair.Left.OffsetSeconds, 0, "왼쪽 샘플 정렬 위치");
        Near(4811d / 48000, pair.Right.OffsetSeconds, 0, "오른쪽 샘플 정렬 위치");
        True(pair.Left.Edit.StartFrame == 3 && pair.Left.Edit.EndFrame == 14 && pair.Right.Edit.StartFrame == 14 && pair.Right.Edit.EndFrame == 29,
            "원본 선택 구간의 분할 경계가 잘못되었습니다.");
    }

    private static void ProcessedSplit()
    {
        var data = new float[3600];
        for (var frame = 0; frame < 1800; frame++)
        {
            data[frame * 2] = (float)(0.4 * Math.Sin(2 * Math.PI * 883 * frame / 44100));
            data[frame * 2 + 1] = (float)(0.6 * Math.Cos(2 * Math.PI * 497 * frame / 44100));
        }
        var source = new AudioClip("처리한 스테레오", 44100, 2, data);
        var baseline = Track(source) with { Edit = new EditSettings(13, 1763, -3, 7, 9), OffsetSeconds = 0.03713, SourcePath = "검증용원본.wav" };
        foreach (var rate in new[] { 0.75, 1, 1.37, 2d })
        {
            foreach (var reverse in new[] { false, true })
            {
                var pair = AssertIdenticalSplit(baseline with { PlaybackRate = rate, Reverse = reverse }, 400);
                True(ReferenceEquals(pair.Left.Source, pair.Right.Source), "한 번 만든 렌더 원본을 양쪽에서 공유해야 합니다.");
                True(pair.Left.SourcePath is null && pair.Right.SourcePath is null, "렌더된 소리를 원본 파일 경로와 혼동하면 안 됩니다.");
                True(pair.Left.PlaybackRate == 1 && !pair.Left.Reverse && pair.Left.Edit.GainDb == 0 && pair.Left.Edit.FadeInMs == 0 && pair.Left.Edit.FadeOutMs == 0,
                    "렌더에 포함된 효과를 다시 적용하면 안 됩니다.");
            }
        }
    }

    private static void RepeatedSplit()
    {
        var source = Mono(Enumerable.Range(0, 128).Select(index => (float)Math.Sin(index * 0.17)).ToArray());
        var original = Track(source) with { PlaybackRate = 0.5, Reverse = true, Edit = new EditSettings(0, 128, -6, 1, 1) };
        var first = AssertIdenticalSplit(original, 100);
        var second = AssertIdenticalSplit(first.Right, 50);
        True(ReferenceEquals(first.Left.Source, second.Left.Source) && ReferenceEquals(first.Left.Source, second.Right.Source), "다시 분할할 때 렌더 배열을 늘리면 안 됩니다.");
        SamplesBitEqual(AudioMixer.Render([original]).Samples, AudioMixer.Render([first.Left, second.Left, second.Right]).Samples);
    }

    private static void SplitUnderOverlap()
    {
        var lane = Guid.NewGuid();
        var original = Track(Mono(Enumerable.Repeat(0.2f, 128).ToArray()), lane);
        var covering = Track(Mono(Enumerable.Repeat(0.7f, 60).ToArray()), lane) with { OffsetSeconds = 30d / 48000 };
        var before = AudioMixer.Render([original, covering]);
        var split = ClipEditing.Split(original, 64d / 48000);
        // 부모 목록에서 원래 클립 자리를 두 조각으로 교체해야 겹침 우선순위가 유지됩니다.
        SamplesBitEqual(before.Samples, AudioMixer.Render([split.Left, split.Right, covering]).Samples);
    }

    private static void SplitSourceUnchanged()
    {
        var samples = Enumerable.Range(0, 128).Select(index => (float)Math.Cos(index * 0.07)).ToArray();
        var original = (float[])samples.Clone();
        var track = Track(Mono(samples)) with { Reverse = true, PlaybackRate = 0.75, Edit = new EditSettings(3, 124, 2, 1, 1) };
        AssertIdenticalSplit(track, 50);
        SamplesBitEqual(original, samples);
    }

    private static void SplitBoundaries()
    {
        var track = Track(Mono([0.1f, 0.2f, 0.3f, 0.4f])) with { OffsetSeconds = 0.1 };
        AssertIdenticalSplit(track, 1);
        AssertIdenticalSplit(track, 3);
        Throws<ArgumentException>(() => ClipEditing.Split(track, 0.1));
        Throws<ArgumentException>(() => ClipEditing.Split(track, 0.1 + 4d / 48000));
        Throws<ArgumentException>(() => ClipEditing.Split(track, double.NaN));
        Throws<ArgumentException>(() => ClipEditing.Split(track, double.PositiveInfinity));
        Throws<ArgumentException>(() => ClipEditing.Split(track, -1));
        Throws<ArgumentException>(() => ClipEditing.Split(Track(Mono([0.5f])), 0));
    }

    private static (AudioTrack Left, AudioTrack Right) AssertIdenticalSplit(AudioTrack track, int cutFrame)
    {
        var before = AudioMixer.Render([track]);
        var firstFrame = (long)Math.Round(track.OffsetSeconds * 48000, MidpointRounding.AwayFromZero);
        var pair = ClipEditing.Split(track, (firstFrame + cutFrame) / 48000d);
        var after = AudioMixer.Render([pair.Left, pair.Right]);
        SamplesBitEqual(before.Samples, after.Samples);
        return pair;
    }

    private static void FramesNear(float[] expected, AudioClip actual)
    {
        True(actual.Channels == 2 && actual.FrameCount == expected.Length, "출력 채널 수 또는 길이가 다릅니다.");
        for (var frame = 0; frame < expected.Length; frame++)
        {
            Near(expected[frame], actual.Samples[frame * 2], 1e-7, "왼쪽 프레임");
            Near(expected[frame], actual.Samples[frame * 2 + 1], 1e-7, "오른쪽 프레임");
        }
    }

    private static void SamplesBitEqual(float[] expected, float[] actual)
    {
        True(expected.Length == actual.Length, "분할 전후 샘플 수가 다릅니다.");
        for (var index = 0; index < expected.Length; index++)
            True(BitConverter.SingleToInt32Bits(expected[index]) == BitConverter.SingleToInt32Bits(actual[index]), $"샘플 {index}의 비트가 다릅니다.");
    }

    private static void Near(double expected, double actual, double tolerance, string name)
    {
        if (!double.IsFinite(actual) || Math.Abs(expected - actual) > tolerance)
            throw new InvalidOperationException($"{name}: 기대 {expected}, 실제 {actual}");
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
