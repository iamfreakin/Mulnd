using System.Buffers.Binary;
using SoriLab.Core;

namespace SoriLab.Checks;

public static class TrackGainChecks
{
    private const double DoubleAmplitudeDb = 6.020599913279624;

    public static int Run(string dir)
    {
        if (string.IsNullOrWhiteSpace(dir))
            throw new ArgumentException("검증용 임시 폴더가 필요합니다.", nameof(dir));

        // 소리와 WAV 결과 모두 메모리에서 검사하므로 검증용 파일을 남기지 않습니다.
        var checks = new (string Name, Action Check)[]
        {
            ("기본 0dB의 기존 샘플 비트 보존", UnityIdentity),
            ("모노와 스테레오의 트랙 음량", GainChannels),
            ("같은 트랙의 모든 클립에 음량 적용", AllClipsInLane),
            ("다른 트랙의 독립적인 음량", IndependentLanes),
            ("클립·트랙·전체 음량의 합성", CombinedGains),
            ("겹친 클립의 우선순위와 무음 보존", OverlappingClips),
            ("트랙 음량과 음소거·단독 재생", MuteAndSolo),
            ("개별 클립 렌더에서 트랙 음량 제외", IsolatedRender),
            ("트랙 음량의 허용 경계와 잘못된 수치 거부", GainLimits),
            ("같은 트랙의 음량 불일치 거부", LaneConsistency),
            ("기본 클립 분할 전후의 비트 일치", PlainSplit),
            ("효과가 있는 클립 분할 전후의 비트 일치", ProcessedSplit),
            ("내부 음량 초과 보존과 WAV 출력 제한", InternalLevel),
            ("렌더와 분할 후 원본·설정 보존", SourceUnchanged)
        };

        var passed = 0;
        foreach (var (name, check) in checks)
        {
            check();
            passed++;
            Console.WriteLine($"통과: 트랙 음량 — {name}");
        }
        return passed;
    }

    private static AudioClip Mono(float[] samples, int sampleRate = 48000) => new("트랙 음량 검증", sampleRate, 1, samples);
    private static AudioTrack Track(AudioClip source, double gainDb = 0, Guid? laneId = null, double offset = 0) =>
        new(Guid.NewGuid(), source, new EditSettings(0, source.FrameCount, 0, 0, 0), offset, LaneId: laneId, TrackGainDb: gainDb);

    private static void UnityIdentity()
    {
        float[] samples = [0.125f, -0.75f, 0.375f, -0.5f, 1.5f, -1.25f];
        var source = new AudioClip("0dB 원본", 48000, 2, samples);
        var legacy = new AudioTrack(Guid.NewGuid(), source, new EditSettings(0, 3, 0, 0, 0));
        True(legacy.TrackGainDb == 0, "이전 생성자의 기본 트랙 음량이 0dB가 아닙니다.");
        BitsEqual(samples, AudioMixer.Render([legacy]).Samples, "0dB 스테레오 렌더");
        BitsEqual(samples, AudioMixer.Render([legacy with { TrackGainDb = 0 }]).Samples, "명시적 0dB 렌더");

        var mono = Track(Mono([0.125f, 0.25f]));
        var other = Track(Mono([0.5f]), offset: 1d / 48000);
        BitsEqual([0.125f, 0.125f, 0.75f, 0.75f], AudioMixer.Render([mono, other]).Samples, "0dB 트랙 합산");
    }

    private static void GainChannels()
    {
        var mono = Track(Mono([0.125f, -0.25f, 0.5f]), DoubleAmplitudeDb);
        SamplesNear([0.25f, 0.25f, -0.5f, -0.5f, 1, 1], AudioMixer.Render([mono]), "모노 두 배 음량");
        var stereo = Track(new AudioClip("좌우 구분", 48000, 2, [0.25f, -0.75f, 0.5f, -0.125f]), -DoubleAmplitudeDb);
        SamplesNear([0.125f, -0.375f, 0.25f, -0.0625f], AudioMixer.Render([stereo]), "스테레오 절반 음량");
    }

    private static void AllClipsInLane()
    {
        var lane = Guid.NewGuid();
        var first = Track(Mono([0.125f, -0.25f]), DoubleAmplitudeDb, lane);
        var second = Track(Mono([0.375f, 0.5f]), DoubleAmplitudeDb, lane, 3d / 48000);
        SamplesNear([0.25f, 0.25f, -0.5f, -0.5f, 0, 0, 0.75f, 0.75f, 1, 1],
            AudioMixer.Render([first, second]), "같은 트랙의 떨어진 두 클립");
        var changed = new[] { first, second }.Select(t => t with { TrackGainDb = -DoubleAmplitudeDb }).ToArray();
        SamplesNear([0.0625f, 0.0625f, -0.125f, -0.125f, 0, 0, 0.1875f, 0.1875f, 0.25f, 0.25f],
            AudioMixer.Render(changed), "트랙 전체 음량 변경");
    }

    private static void IndependentLanes()
    {
        var quiet = Track(Mono([0.5f, -0.5f]), -DoubleAmplitudeDb);
        var loud = Track(Mono([0.125f, 0.25f]), DoubleAmplitudeDb);
        SamplesNear([0.5f, 0.5f, 0.25f, 0.25f], AudioMixer.Render([quiet, loud]), "서로 다른 트랙의 독립 음량");
        SamplesNear([0.75f, 0.75f, 0, 0], AudioMixer.Render([quiet with { TrackGainDb = 0 }, loud]), "한 트랙만 변경");
    }

    private static void CombinedGains()
    {
        var track = Track(Mono([0.125f, -0.25f, 0.5f]), DoubleAmplitudeDb);
        track = track with { Edit = track.Edit with { GainDb = DoubleAmplitudeDb } };
        // 클립 두 배, 트랙 두 배, 전체 절반이면 최종 두 배입니다.
        SamplesNear([0.25f, 0.25f, -0.5f, -0.5f, 1, 1],
            AudioMixer.Render([track], masterGainDb: -DoubleAmplitudeDb), "세 음량의 합성");
        var other = Track(Mono([0.5f, 0.5f, 0.5f]), -DoubleAmplitudeDb);
        SamplesNear([0.375f, 0.375f, -0.375f, -0.375f, 1.125f, 1.125f],
            AudioMixer.Render([track, other], masterGainDb: -DoubleAmplitudeDb), "트랙 합산 후 전체 음량");
    }

    private static void OverlappingClips()
    {
        var lane = Guid.NewGuid();
        var back = Track(Mono([0.5f, 0.5f, 0.5f, 0.5f]), DoubleAmplitudeDb, lane);
        var front = Track(Mono([0, 0.25f]), DoubleAmplitudeDb, lane, 1d / 48000);
        var other = Track(Mono([0.25f, 0.25f, 0.25f, 0.25f]), -DoubleAmplitudeDb);
        SamplesNear([1.125f, 1.125f, 0.125f, 0.125f, 0.625f, 0.625f, 1.125f, 1.125f],
            AudioMixer.Render([back, front, other]), "앞 클립 무음과 다른 트랙 합산");
        SamplesNear([1, 1, 1, 1, 1, 1, 1, 1], AudioMixer.Render([front, back]), "클립 배열 순서 반전");
    }

    private static void MuteAndSolo()
    {
        var lane = Guid.NewGuid();
        var first = Track(Mono([0.25f]), DoubleAmplitudeDb, lane);
        var second = Track(Mono([-0.5f]), DoubleAmplitudeDb, lane, 1d / 48000);
        var other = Track(Mono([0.5f, 0.5f]), -DoubleAmplitudeDb);
        SamplesNear([0.25f, 0.25f, 0.25f, 0.25f],
            AudioMixer.Render([first with { Muted = true }, second with { Muted = true }, other]), "음소거 트랙의 음량 제외");
        SamplesNear([0.5f, 0.5f, -1, -1],
            AudioMixer.Render([first with { Solo = true }, second with { Solo = true }, other]), "단독 트랙 음량");
        SamplesNear([0.25f, 0.25f, 0.25f, 0.25f],
            AudioMixer.Render([first, second, other with { Solo = true }]), "다른 트랙 단독 재생");
        Throws(() => AudioMixer.Render([first with { Solo = true, Muted = true }, second with { Solo = true, Muted = true }, other]),
            "음소거된 단독 트랙 때문에 재생할 소리가 없는 경우를 허용했습니다.");
    }

    private static void IsolatedRender()
    {
        var track = Track(Mono([0.125f, 0.25f, 0.5f, 0.75f], 1000), 12);
        track = track with
        {
            OffsetSeconds = 2, Muted = true, Solo = true, Reverse = true,
            Edit = track.Edit with { GainDb = DoubleAmplitudeDb, FadeInMs = 2, FadeOutMs = 2 }
        };
        SamplesNear([0, 0, 1, 1, 0.5f, 0.5f, 0, 0], AudioMixer.RenderTrack(track, 1000), "클립 효과만 렌더");
        BitsEqual(AudioMixer.RenderTrack(track with { TrackGainDb = -60 }, 1000).Samples,
            AudioMixer.RenderTrack(track, 1000).Samples, "개별 렌더의 트랙 음량 독립성");
    }

    private static void GainLimits()
    {
        var track = Track(Mono([1, -1]));
        var minimum = track with { TrackGainDb = -60 };
        var maximum = track with { TrackGainDb = 12 };
        AudioMixer.Validate([minimum]);
        AudioMixer.Validate([maximum]);
        SamplesNear([0.001f, 0.001f, -0.001f, -0.001f], AudioMixer.Render([minimum]), "-60dB 경계");
        SamplesNear([3.9810717f, 3.9810717f, -3.9810717f, -3.9810717f], AudioMixer.Render([maximum]), "+12dB 경계");
        foreach (var invalid in new[] { -60.0001, 12.0001, double.NaN, double.PositiveInfinity, double.NegativeInfinity })
        {
            var bad = track with { TrackGainDb = invalid };
            Throws(() => AudioMixer.Validate([bad]), "저장 검증이 잘못된 트랙 음량을 허용했습니다.");
            Throws(() => AudioMixer.Render([bad]), "조합이 잘못된 트랙 음량을 허용했습니다.");
            Throws(() => AudioMixer.RenderTrack(bad), "개별 렌더가 잘못된 트랙 설정을 허용했습니다.");
            Throws(() => AudioMixer.Validate([bad with { Muted = true }]), "음소거 트랙의 잘못된 음량을 허용했습니다.");
        }
    }

    private static void LaneConsistency()
    {
        var first = Track(Mono([0.25f]), -3);
        var second = first with { Id = Guid.NewGuid(), LaneId = first.EffectiveLaneId, OffsetSeconds = 1d / 48000, TrackGainDb = -4 };
        Throws(() => AudioMixer.Validate([first, second]), "같은 트랙의 서로 다른 음량을 허용했습니다.");
        Throws(() => AudioMixer.Render([first, second]), "조합이 트랙 음량 불일치를 허용했습니다.");
        AudioMixer.Validate([first, second with { TrackGainDb = first.TrackGainDb }]);
        AudioMixer.Validate([first, second with { LaneId = null }]);
    }

    private static void PlainSplit()
    {
        var source = Mono([0.125f, -0.25f, 0.375f, 0.5f, -0.625f, 0.75f]);
        var track = Track(source, -7, offset: 12.4 / 48000) with { SourcePath = "원본.wav" };
        var split = ClipEditing.Split(track, 15d / 48000);
        True(ReferenceEquals(source, split.Left.Source) && ReferenceEquals(source, split.Right.Source), "트랙 음량만 있는 분할에서 원본을 불필요하게 렌더했습니다.");
        True(split.Left.TrackGainDb == -7 && split.Right.TrackGainDb == -7, "분할한 클립에서 트랙 음량이 사라졌습니다.");
        True(split.Left.SourcePath == track.SourcePath && split.Right.SourcePath == track.SourcePath, "기본 분할의 원본 경로를 잃었습니다.");
        BitsEqual(AudioMixer.Render([track], masterGainDb: -2).Samples,
            AudioMixer.Render([split.Left, split.Right], masterGainDb: -2).Samples, "트랙 음량을 보존한 기본 분할");
    }

    private static void ProcessedSplit()
    {
        var samples = Enumerable.Range(0, 1024).Select(i => (float)((i % 29 - 14) / 32d)).ToArray();
        foreach (var settings in new[]
        {
            (SampleRate: 48000, Rate: 1d, Reverse: false, ClipDb: -3d, FadeIn: 0d, FadeOut: 0d),
            (SampleRate: 48000, Rate: 1d, Reverse: true, ClipDb: 0d, FadeIn: 3d, FadeOut: 4d),
            (SampleRate: 44100, Rate: 0.8, Reverse: true, ClipDb: -4d, FadeIn: 2d, FadeOut: 5d)
        })
        {
            var source = Mono(samples, settings.SampleRate);
            var track = Track(source, 5, offset: 12.4 / 48000) with
            {
                Edit = new EditSettings(15, 900, settings.ClipDb, settings.FadeIn, settings.FadeOut),
                PlaybackRate = settings.Rate, Reverse = settings.Reverse
            };
            var split = ClipEditing.Split(track, 212d / 48000);
            True(split.Left.TrackGainDb == 5 && split.Right.TrackGainDb == 5, "효과가 있는 분할에서 트랙 음량을 잃었습니다.");
            True(ReferenceEquals(split.Left.Source, split.Right.Source) && !ReferenceEquals(source, split.Left.Source), "분할의 렌더 결과를 두 클립이 공유하지 않습니다.");
            BitsEqual(AudioMixer.RenderTrack(track).Samples, split.Left.Source.Samples, "분할 원본에 트랙 음량을 중복 반영하지 않음");
            BitsEqual(AudioMixer.Render([track], masterGainDb: -4).Samples,
                AudioMixer.Render([split.Left, split.Right], masterGainDb: -4).Samples, "효과가 있는 분할 전후 음량");
        }
    }

    private static void InternalLevel()
    {
        var result = AudioMixer.Render([Track(Mono([0.75f, -0.75f]), DoubleAmplitudeDb)]);
        SamplesNear([1.5f, 1.5f, -1.5f, -1.5f], result, "내부 음량 초과 유지");
        var encoded = WavCodec.EncodePcm16(result);
        True(BinaryPrimitives.ReadInt16LittleEndian(encoded.AsSpan(44, 2)) == short.MaxValue,
            "WAV 출력의 양수 음량 한도 처리가 잘못됐습니다.");
        True(BinaryPrimitives.ReadInt16LittleEndian(encoded.AsSpan(48, 2)) == short.MinValue,
            "WAV 출력의 음수 음량 한도 처리가 잘못됐습니다.");
    }

    private static void SourceUnchanged()
    {
        var source = Mono([0.125f, -0.25f, 0.375f, -0.5f, 0.625f]);
        var original = source.Samples.ToArray();
        var track = Track(source, 9) with { Edit = new EditSettings(0, 5, -3, 0, 0), Reverse = true };
        var originalEdit = track.Edit;
        AudioMixer.Render([track]);
        AudioMixer.RenderTrack(track);
        ClipEditing.Split(track, 2d / 48000);
        BitsEqual(original, source.Samples, "원본 샘플 불변");
        True(ReferenceEquals(source, track.Source) && ReferenceEquals(originalEdit, track.Edit) && track.TrackGainDb == 9 && track.Reverse,
            "렌더나 분할이 원래 클립의 설정을 바꿨습니다.");
    }

    private static void SamplesNear(float[] expected, AudioClip actual, string description)
    {
        True(actual.Channels == 2 && actual.Samples.Length == expected.Length, $"{description}: 스테레오 샘플 수가 다릅니다.");
        for (var i = 0; i < expected.Length; i++)
            True(float.IsFinite(actual.Samples[i]) && Math.Abs(expected[i] - actual.Samples[i]) <= 0.000001,
                $"{description}: 샘플 {i}, 예상 {expected[i]}, 실제 {actual.Samples[i]}");
    }

    private static void BitsEqual(float[] expected, float[] actual, string description)
    {
        True(expected.Length == actual.Length, $"{description}: 샘플 수가 다릅니다.");
        for (var i = 0; i < expected.Length; i++)
            True(BitConverter.SingleToInt32Bits(expected[i]) == BitConverter.SingleToInt32Bits(actual[i]),
                $"{description}: 샘플 {i}의 비트가 다릅니다.");
    }

    private static void True(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void Throws(Action action, string message)
    {
        try { action(); }
        catch (ArgumentException) { return; }
        throw new InvalidOperationException(message);
    }
}
