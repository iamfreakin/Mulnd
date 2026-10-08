using SoriLab.Core;

namespace SoriLab.Checks;

public static class ClipTrimChecks
{
    public static int Run(string dir)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dir);
        Directory.CreateDirectory(dir);
        var checks = new (string Name, Action Check)[]
        {
            ("정방향 시작을 줄이고 다시 늘리기", () => StartChanges(false)),
            ("정방향 끝을 줄이고 다시 늘리기", () => EndChanges(false)),
            ("역방향 시작에서 원본 끝 경계 변경", () => StartChanges(true)),
            ("역방향 끝에서 원본 시작 경계 변경", () => EndChanges(true)),
            ("재생 속도에 따른 원본 프레임 이동", PlaybackRates),
            ("가장 가까운 프레임과 중간값 반올림", FrameRounding),
            ("원본 범위와 최소 한 프레임 유지", SourceBounds),
            ("원본·식별자·트랙·음량·페이드 설정 보존", PreserveSettings),
            ("원래 손잡이 위치는 변화 없고 반대쪽 자연 끝 유지", NaturalEndAndNoChange),
            ("0초와 120초 안으로 조정", TimelineBounds),
            ("비정상 숫자와 잘못된 손잡이 거부", InvalidRequests),
            ("120초를 넘는 음소거 클립의 복구 가능 범위", OversizedMutedClip),
            ("모노·스테레오의 자른 원본 구간 렌더", RenderSelectedSamples),
            ("자른 클립의 저장 왕복과 공유 원본 유지", () => SaveRoundTrip(dir))
        };

        foreach (var (name, check) in checks)
        {
            check();
            Console.WriteLine($"통과: 클립 양끝 자르기 — {name}");
        }
        return checks.Length;
    }

    private static AudioTrack Fixture(bool reverse = false, double rate = 1, double offset = 1) =>
        new(Guid.NewGuid(), Source(1024, 2, 1024), new EditSettings(256, 768, 0, 0, 0),
            OffsetSeconds: offset, PlaybackRate: rate, Reverse: reverse, LaneId: Guid.NewGuid());

    private static AudioClip Source(int sampleRate, int channels, int frames)
    {
        var samples = new float[frames * channels];
        for (var frame = 0; frame < frames; frame++)
        {
            samples[frame * channels] = (frame + 1) / (float)(frames * 4);
            if (channels == 2) samples[frame * 2 + 1] = -(frame + 1) / (float)(frames * 8);
        }
        return new AudioClip("자르기 검사 원본", sampleRate, channels, samples);
    }

    private static void StartChanges(bool reverse)
    {
        var original = Fixture(reverse);
        var shortened = ClipEditing.Trim(original, ClipTrimEdge.Start, 1.125);
        Expect(shortened, reverse ? 256 : 384, reverse ? 640 : 768, 1.125);
        Near(NaturalEnd(original), NaturalEnd(shortened), "시작을 줄일 때 반대쪽 끝이 움직였습니다.");

        var extended = ClipEditing.Trim(original, ClipTrimEdge.Start, 0.875);
        Expect(extended, reverse ? 256 : 128, reverse ? 896 : 768, 0.875);
        Near(NaturalEnd(original), NaturalEnd(extended), "시작을 늘릴 때 반대쪽 끝이 움직였습니다.");
        Check(ClipEditing.Trim(shortened, ClipTrimEdge.Start, original.OffsetSeconds) == original,
            "줄인 시작을 다시 늘렸는데 원래 구간으로 돌아오지 않았습니다.");
    }

    private static void EndChanges(bool reverse)
    {
        var original = Fixture(reverse);
        var shortened = ClipEditing.Trim(original, ClipTrimEdge.End, 1.375);
        Expect(shortened, reverse ? 384 : 256, reverse ? 768 : 640, 1);
        var extended = ClipEditing.Trim(original, ClipTrimEdge.End, 1.625);
        Expect(extended, reverse ? 128 : 256, reverse ? 768 : 896, 1);
        Check(ClipEditing.Trim(shortened, ClipTrimEdge.End, NaturalEnd(original)) == original,
            "줄인 끝을 다시 늘렸는데 원래 구간으로 돌아오지 않았습니다.");
    }

    private static void PlaybackRates()
    {
        foreach (var reverse in new[] { false, true })
        foreach (var rate in new[] { 0.25, 0.5, 1.25, 2d, 4d })
        {
            var original = Fixture(reverse, rate);
            var moved = 64d / (original.Source.SampleRate * rate);
            var start = ClipEditing.Trim(original, ClipTrimEdge.Start, original.OffsetSeconds + moved);
            Expect(start, reverse ? 256 : 320, reverse ? 704 : 768, original.OffsetSeconds + moved);
            Near(NaturalEnd(original), NaturalEnd(start), "속도가 있는 시작 자르기에서 반대쪽 끝이 바뀌었습니다.");
            var end = ClipEditing.Trim(original, ClipTrimEdge.End, NaturalEnd(original) - moved);
            Expect(end, reverse ? 320 : 256, reverse ? 768 : 704, original.OffsetSeconds);
        }
    }

    private static void FrameRounding()
    {
        foreach (var reverse in new[] { false, true })
        {
            var original = Fixture(reverse);
            // 시작은 원래 손잡이에서 이동한 양을 반올림하므로 좌우의 반 프레임이 대칭입니다.
            foreach (var (delta, rounded) in new[] { (0.49, 0), (0.5, 1), (0.51, 1), (-0.49, 0), (-0.5, -1), (-0.51, -1) })
            {
                var start = ClipEditing.Trim(original, ClipTrimEdge.Start, original.OffsetSeconds + delta / 1024);
                Expect(start, reverse ? 256 : 256 + rounded, reverse ? 768 - rounded : 768,
                    original.OffsetSeconds + rounded / 1024d);
            }
            // 끝은 시작 위치에서의 전체 새 길이를 반올림합니다.
            foreach (var (frames, rounded) in new[] { (400.49, 400), (400.5, 401), (400.51, 401) })
            {
                var end = ClipEditing.Trim(original, ClipTrimEdge.End, original.OffsetSeconds + frames / 1024);
                Expect(end, reverse ? 768 - rounded : 256, reverse ? 768 : 256 + rounded, original.OffsetSeconds);
            }
        }
    }

    private static void SourceBounds()
    {
        foreach (var reverse in new[] { false, true })
        {
            var original = Fixture(reverse);
            var earliest = ClipEditing.Trim(original, ClipTrimEdge.Start, double.MinValue);
            Expect(earliest, reverse ? 256 : 0, reverse ? 1024 : 768, 0.75);
            var latest = ClipEditing.Trim(original, ClipTrimEdge.End, double.MaxValue);
            Expect(latest, reverse ? 0 : 256, reverse ? 768 : 1024, 1);

            var lastFrame = ClipEditing.Trim(original, ClipTrimEdge.Start, double.MaxValue);
            Expect(lastFrame, reverse ? 256 : 767, reverse ? 257 : 768, 1.5 - 1d / 1024);
            var firstFrame = ClipEditing.Trim(original, ClipTrimEdge.End, double.MinValue);
            Expect(firstFrame, reverse ? 767 : 256, reverse ? 768 : 257, 1);
            Check(lastFrame.Edit.EndFrame - lastFrame.Edit.StartFrame == 1 && firstFrame.Edit.EndFrame - firstFrame.Edit.StartFrame == 1,
                "손잡이가 서로 지나갔을 때 최소 한 프레임을 남겨야 합니다.");
        }
    }

    private static void PreserveSettings()
    {
        foreach (var hasLane in new[] { false, true })
        foreach (var edge in new[] { ClipTrimEdge.Start, ClipTrimEdge.End })
        {
            var original = Fixture(reverse: true, rate: 1.25) with
            {
                Edit = new EditSettings(256, 768, -4.25, 800, 900),
                TrackGainDb = -6.75,
                Muted = true,
                Solo = true,
                SourcePath = "원본 경로는 자르면서 열지 않습니다.wav",
                LaneId = hasLane ? Guid.NewGuid() : null
            };
            var samples = original.Source.Samples.ToArray();
            var request = edge == ClipTrimEdge.Start ? original.OffsetSeconds + 0.1 : NaturalEnd(original) - 0.1;
            var result = ClipEditing.Trim(original, edge, request);
            Check(ReferenceEquals(original.Source, result.Source), "자르기가 원본 객체를 복제했습니다.");
            Check(original == (result with
            {
                Edit = result.Edit with { StartFrame = original.Edit.StartFrame, EndFrame = original.Edit.EndFrame },
                OffsetSeconds = original.OffsetSeconds
            }), "구간 경계와 배치 위치 외의 클립 설정이 바뀌었습니다.");
            SameBits(samples, original.Source.Samples);
        }
    }

    private static void NaturalEndAndNoChange()
    {
        foreach (var reverse in new[] { false, true })
        {
            var original = new AudioTrack(Guid.NewGuid(), Source(44100, 1, 512), new EditSettings(23, 401, -2, 7, 9),
                OffsetSeconds: 0.1234567, PlaybackRate: 1.25, Reverse: reverse, TrackGainDb: -3);
            var naturalEnd = NaturalEnd(original);
            Check(Math.Abs(AudioMixer.GetEndSeconds(original) - naturalEnd) > 1e-8,
                "검사 원본의 자연 끝과 출력 프레임 끝이 달라야 합니다.");
            Check(ClipEditing.Trim(original, ClipTrimEdge.Start, original.OffsetSeconds) == original,
                "시작 손잡이를 그대로 두었는데 클립이 바뀌었습니다.");
            Check(ClipEditing.Trim(original, ClipTrimEdge.End, naturalEnd) == original,
                "끝 손잡이를 그대로 두었는데 클립이 바뀌었습니다.");
            var moved = 37d / (original.Source.SampleRate * original.PlaybackRate);
            var shortened = ClipEditing.Trim(original, ClipTrimEdge.Start, original.OffsetSeconds + moved);
            Expect(shortened, reverse ? 23 : 60, reverse ? 364 : 401, original.OffsetSeconds + moved);
            Near(naturalEnd, NaturalEnd(shortened), "시작 자르기가 출력 프레임 기준 끝으로 클립을 이동했습니다.");
        }
    }

    private static void TimelineBounds()
    {
        foreach (var reverse in new[] { false, true })
        {
            var early = Fixture(reverse, offset: 0.125);
            var atZero = ClipEditing.Trim(early, ClipTrimEdge.Start, -100);
            Expect(atZero, reverse ? 256 : 128, reverse ? 896 : 768, 0);
            Near(NaturalEnd(early), NaturalEnd(atZero), "0초 경계에서 반대쪽 끝이 움직였습니다.");

            var late = Fixture(reverse, offset: 119.75) with { Edit = new EditSettings(256, 384, 0, 0, 0) };
            var atLimit = ClipEditing.Trim(late, ClipTrimEdge.End, 1000);
            Expect(atLimit, reverse ? 128 : 256, reverse ? 384 : 512, 119.75);
            Near(120, NaturalEnd(atLimit), "끝을 120초 경계까지만 늘려야 합니다.");
            AudioMixer.Validate([atZero]);
            AudioMixer.Validate([atLimit]);
        }
    }

    private static void InvalidRequests()
    {
        var original = Fixture();
        foreach (var edge in new[] { ClipTrimEdge.Start, ClipTrimEdge.End })
        foreach (var time in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity })
            Reject(() => ClipEditing.Trim(original, edge, time));
        Reject(() => ClipEditing.Trim(original, (ClipTrimEdge)(-1), 1));
        Reject(() => ClipEditing.Trim(original, (ClipTrimEdge)99, 1));
        Reject(() => ClipEditing.Trim(null!, ClipTrimEdge.Start, 0));
    }

    private static void OversizedMutedClip()
    {
        foreach (var reverse in new[] { false, true })
        {
            var original = Fixture(reverse, offset: 119.75) with { Muted = true };
            AudioMixer.Validate([original]);
            Reject(() => ClipEditing.Trim(original, ClipTrimEdge.Start, 119.8));
            var repaired = ClipEditing.Trim(original, ClipTrimEdge.End, double.MaxValue);
            Expect(repaired, reverse ? 512 : 256, reverse ? 768 : 512, 119.75);
            Near(120, NaturalEnd(repaired), "범위를 넘는 음소거 클립의 끝을 120초로 줄이지 못했습니다.");
            AudioMixer.Validate([repaired with { Muted = false }]);

            var noRoom = original with { OffsetSeconds = 120 };
            Reject(() => ClipEditing.Trim(noRoom, ClipTrimEdge.Start, 119));
            Reject(() => ClipEditing.Trim(noRoom, ClipTrimEdge.End, 120));
        }
    }

    private static void RenderSelectedSamples()
    {
        foreach (var channels in new[] { 1, 2 })
        foreach (var reverse in new[] { false, true })
        foreach (var edge in new[] { ClipTrimEdge.Start, ClipTrimEdge.End })
        {
            var original = new AudioTrack(Guid.NewGuid(), Source(48000, channels, 256), new EditSettings(32, 224, 0, 0, 0),
                OffsetSeconds: 0.001, Reverse: reverse);
            var request = edge == ClipTrimEdge.Start ? original.OffsetSeconds + 32d / 48000 : NaturalEnd(original) - 32d / 48000;
            var trimmed = ClipEditing.Trim(original, edge, request);
            var expectedStart = reverse == (edge == ClipTrimEdge.Start) ? 32 : 64;
            var expectedEnd = reverse == (edge == ClipTrimEdge.Start) ? 192 : 224;
            Check(trimmed.Edit.StartFrame == expectedStart && trimmed.Edit.EndFrame == expectedEnd,
                "렌더 검사에서 원본 프레임 경계가 잘못 바뀌었습니다.");
            var expected = new float[160 * 2];
            for (var frame = 0; frame < 160; frame++)
            {
                var sourceFrame = reverse ? expectedEnd - 1 - frame : expectedStart + frame;
                expected[frame * 2] = original.Source.Samples[sourceFrame * channels];
                expected[frame * 2 + 1] = original.Source.Samples[sourceFrame * channels + (channels == 1 ? 0 : 1)];
            }
            SameBits(expected, AudioMixer.RenderTrack(trimmed).Samples);
            var offsetFrames = edge == ClipTrimEdge.Start ? 80 : 48;
            var expectedMix = new float[(offsetFrames + 160) * 2];
            expected.CopyTo(expectedMix, offsetFrames * 2);
            SameBits(expectedMix, AudioMixer.Render([trimmed]).Samples);
        }
    }

    private static void SaveRoundTrip(string dir)
    {
        var first = Fixture(reverse: true, rate: 1.25, offset: 0.25) with
        {
            Edit = new EditSettings(256, 768, -3, 5, 7),
            TrackGainDb = -4.75,
            SourcePath = Path.Combine(dir, "사용하지 않는 원본.wav")
        };
        var second = first with { Id = Guid.NewGuid(), OffsetSeconds = 0.8, Reverse = false };
        first = ClipEditing.Trim(first, ClipTrimEdge.Start, 0.35);
        second = ClipEditing.Trim(second, ClipTrimEdge.End, 1.3);
        var project = new ProjectDocument([first, second], -2, second.Id);
        var path = Path.Combine(dir, "clip-trim-roundtrip.sorilab");
        ProjectFile.Save(path, project);
        var loaded = ProjectFile.Load(path);
        Check(loaded.MasterGainDb == project.MasterGainDb && loaded.SelectedTrackId == project.SelectedTrackId && loaded.Tracks.Length == 2,
            "자른 프로젝트의 전체 설정이 달라졌습니다.");
        Check(ReferenceEquals(loaded.Tracks[0].Source, loaded.Tracks[1].Source), "저장 왕복 뒤 공유 원본이 분리되었습니다.");
        for (var index = 0; index < project.Tracks.Length; index++)
        {
            var before = project.Tracks[index];
            var after = loaded.Tracks[index];
            Check(before with { Source = after.Source } == after, "저장 왕복 뒤 자른 클립의 설정이 달라졌습니다.");
            SameBits(before.Source.Samples, after.Source.Samples);
        }
        SameBits(AudioMixer.Render(project.Tracks, masterGainDb: project.MasterGainDb).Samples,
            AudioMixer.Render(loaded.Tracks, masterGainDb: loaded.MasterGainDb).Samples);
    }

    private static double NaturalEnd(AudioTrack track) => track.OffsetSeconds + AudioMixer.GetDurationSeconds(track);

    private static void Expect(AudioTrack track, int start, int end, double offset)
    {
        Check(track.Edit.StartFrame == start && track.Edit.EndFrame == end,
            $"원본 구간 예상 [{start}, {end}), 실제 [{track.Edit.StartFrame}, {track.Edit.EndFrame})입니다.");
        Near(offset, track.OffsetSeconds, "자른 뒤 배치 위치가 다릅니다.");
    }

    private static void Near(double expected, double actual, string message) =>
        Check(double.IsFinite(actual) && Math.Abs(expected - actual) <= 1e-10, $"{message} 예상 {expected:R}, 실제 {actual:R}");

    private static void SameBits(float[] expected, float[] actual)
    {
        Check(expected.Length == actual.Length, "원본 또는 렌더의 표본 수가 다릅니다.");
        for (var index = 0; index < expected.Length; index++)
            Check(BitConverter.SingleToInt32Bits(expected[index]) == BitConverter.SingleToInt32Bits(actual[index]),
                $"{index}번째 표본이 원본의 선택 구간과 다릅니다.");
    }

    private static void Reject(Action action)
    {
        try { action(); }
        catch (ArgumentException) { return; }
        throw new InvalidOperationException("잘못된 자르기 요청을 ArgumentException 계열로 거부해야 합니다.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
