using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using SoriLab.Core;

namespace SoriLab.Checks;

public static class TrackGainProjectChecks
{
    public static int Run(string dir)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dir);
        Directory.CreateDirectory(dir);
        var original = Fixture(dir);
        var baseline = Path.Combine(dir, "track-gain-baseline.sorilab");
        ProjectFile.Save(baseline, original);

        var checks = new (string Name, Action Check)[]
        {
            ("트랙 음량과 다른 편집값의 왕복 저장", () => CheckMetadata(original, ProjectFile.Load(baseline))),
            ("저장 전후 전체 믹스 비트 일치", () => CheckRender(original, ProjectFile.Load(baseline))),
            ("서로 다른 트랙 음량에서도 원본 공유 유지", () => SharedOriginal(baseline, original)),
            ("-60dB와 +12dB 경계의 저장", () => BoundaryGains(dir, original)),
            ("trackGainDb가 없는 이전 v1은 0dB", () => LegacyProject(dir, baseline, original, false)),
            ("laneId도 없는 초기 v1 호환", () => LegacyProject(dir, baseline, original, true)),
            ("같은 트랙의 누락된 값과 명시적 0dB 호환", () => MixedDefaultGain(dir, baseline, original)),
            ("허용 범위를 벗어난 트랙 음량 거부", () => InvalidRanges(dir, baseline)),
            ("명시적 null은 기본값으로 바꾸지 않고 거부", () => RejectManifest(dir, baseline, json => Track(json, 0)["trackGainDb"] = null)),
            ("숫자 문자열·논리값·객체·배열 거부", () => InvalidTypes(dir, baseline)),
            ("유한하지 않은 숫자로 넘치는 JSON 값 거부", () => NonfiniteNumbers(dir, baseline)),
            ("중복된 trackGainDb JSON 항목 거부", () => DuplicateGain(dir, baseline)),
            ("같은 트랙의 서로 다른 음량과 부분 누락 거부", () => InconsistentGains(dir, baseline)),
            ("잘못된 트랙 음량을 원본 읽기 전에 거부", () => RejectBeforeAudio(dir, baseline, false)),
            ("같은 트랙의 음량 불일치를 원본 읽기 전에 거부", () => RejectBeforeAudio(dir, baseline, true)),
            ("잘못된 저장 요청에서 기존 파일 보존", () => InvalidSavePreservesFile(dir, original))
        };

        foreach (var (name, check) in checks)
        {
            check();
            Console.WriteLine($"통과: 트랙 음량 저장 — {name}");
        }
        return checks.Length;
    }

    private static ProjectDocument Fixture(string dir)
    {
        var source = new AudioClip("트랙 음량 공유 원본", 48000, 1,
            Enumerable.Range(0, 192).Select(index => (float)(0.3 * Math.Sin(index * 0.19))).ToArray());
        var lane = Guid.NewGuid();
        var first = new AudioTrack(Guid.NewGuid(), source, new EditSettings(0, 144, -3, 0.1, 0.2),
            OffsetSeconds: 0.001, SourcePath: Path.Combine(dir, "없는 원본.wav"), LaneId: lane, TrackGainDb: -7.125);
        var second = new AudioTrack(Guid.NewGuid(), source, new EditSettings(32, 192, 2, 0.2, 0.1),
            OffsetSeconds: 0.003, PlaybackRate: 0.75, Reverse: true, LaneId: lane, TrackGainDb: -7.125);
        var third = new AudioTrack(Guid.NewGuid(), source, new EditSettings(12, 180, -1.75, 0, 0),
            OffsetSeconds: 0.002, PlaybackRate: 1.25, LaneId: Guid.NewGuid(), TrackGainDb: 3.5);
        return new ProjectDocument([first, second, third], -2.625, second.Id);
    }

    private static void CheckMetadata(ProjectDocument expected, ProjectDocument actual)
    {
        Check(expected.MasterGainDb == actual.MasterGainDb && expected.SelectedTrackId == actual.SelectedTrackId &&
            expected.Tracks.Length == actual.Tracks.Length, "프로젝트 전체 설정이 달라졌습니다.");
        for (var index = 0; index < expected.Tracks.Length; index++)
        {
            var before = expected.Tracks[index];
            var after = actual.Tracks[index];
            Check(before with { Source = after.Source } == after, "트랙 음량이나 기존 편집값이 달라졌습니다.");
            Check(before.Source.Name == after.Source.Name && before.Source.SampleRate == after.Source.SampleRate &&
                before.Source.Channels == after.Source.Channels, "원본 정보가 달라졌습니다.");
            SameBits(before.Source.Samples, after.Source.Samples);
        }
    }

    private static void CheckRender(ProjectDocument expected, ProjectDocument actual) =>
        SameBits(AudioMixer.Render(expected.Tracks, masterGainDb: expected.MasterGainDb).Samples,
            AudioMixer.Render(actual.Tracks, masterGainDb: actual.MasterGainDb).Samples);

    private static void SharedOriginal(string path, ProjectDocument expected)
    {
        var loaded = ProjectFile.Load(path);
        Check(loaded.Tracks.All(track => ReferenceEquals(track.Source, loaded.Tracks[0].Source)),
            "트랙 음량이 다르다고 같은 원본을 복제했습니다.");
        SameBits(expected.Tracks[0].Source.Samples, loaded.Tracks[0].Source.Samples);
        using var archive = ZipFile.OpenRead(path);
        Check(archive.Entries.Count == 2 && archive.Entries.Count(entry => entry.FullName.StartsWith("audio/", StringComparison.Ordinal)) == 1,
            "공유 원본은 ZIP 안에 한 번만 저장해야 합니다.");
        using var input = archive.GetEntry("manifest.json")!.Open();
        var manifest = JsonNode.Parse(input)!.AsObject();
        Check(manifest["version"]!.GetValue<int>() == 1, "트랙 음량 추가로 파일 형식 버전이 바뀌었습니다.");
        Check(Track(manifest, 0)["trackGainDb"]!.GetValue<double>() == -7.125 &&
            Track(manifest, 2)["trackGainDb"]!.GetValue<double>() == 3.5, "트랙 음량이 JSON 숫자로 기록되지 않았습니다.");
    }

    private static void BoundaryGains(string dir, ProjectDocument original)
    {
        foreach (var gain in new[] { -60d, 12d })
        {
            var project = original with { Tracks = original.Tracks.Select(track => track with { TrackGainDb = gain }).ToArray() };
            var path = Path.Combine(dir, "track-gain-boundary.sorilab");
            ProjectFile.Save(path, project);
            var loaded = ProjectFile.Load(path);
            CheckMetadata(project, loaded);
            CheckRender(project, loaded);
        }
    }

    private static void LegacyProject(string dir, string baseline, ProjectDocument original, bool removeLane)
    {
        var path = MutateManifest(dir, baseline, json =>
        {
            foreach (var item in json["tracks"]!.AsArray())
            {
                item!.AsObject().Remove("trackGainDb");
                if (removeLane) item.AsObject().Remove("laneId");
            }
        });
        var expected = original with
        {
            Tracks = original.Tracks.Select(track => track with { TrackGainDb = 0, LaneId = removeLane ? null : track.LaneId }).ToArray()
        };
        var loaded = ProjectFile.Load(path);
        CheckMetadata(expected, loaded);
        CheckRender(expected, loaded);
    }

    private static void MixedDefaultGain(string dir, string baseline, ProjectDocument original)
    {
        var path = MutateManifest(dir, baseline, json =>
        {
            Track(json, 0).Remove("trackGainDb");
            Track(json, 1)["trackGainDb"] = 0;
        });
        var expected = original with
        {
            Tracks = original.Tracks.Select((track, index) => index < 2 ? track with { TrackGainDb = 0 } : track).ToArray()
        };
        var loaded = ProjectFile.Load(path);
        CheckMetadata(expected, loaded);
        CheckRender(expected, loaded);
    }

    private static void InvalidRanges(string dir, string baseline)
    {
        foreach (var gain in new[] { -60.0001, 12.0001, double.MinValue, double.MaxValue })
            RejectManifest(dir, baseline, json => Track(json, 0)["trackGainDb"] = gain);
    }

    private static void InvalidTypes(string dir, string baseline)
    {
        foreach (var raw in new[] { "\"0\"", "\"-7.125\"", "\"NaN\"", "\"Infinity\"", "true", "{}", "[]" })
            RejectManifest(dir, baseline, json => Track(json, 0)["trackGainDb"] = JsonNode.Parse(raw));
    }

    private static void NonfiniteNumbers(string dir, string baseline)
    {
        // JSON 문법상 숫자여도 double 범위를 넘는 지수는 유한한 음량으로 사용할 수 없습니다.
        foreach (var raw in new[] { "1e400", "-1e400" })
            RejectManifest(dir, baseline, json => Track(json, 0)["trackGainDb"] = JsonNode.Parse(raw));
    }

    private static void DuplicateGain(string dir, string baseline)
    {
        var path = Path.Combine(dir, "track-gain-duplicate.sorilab");
        RewritePackage(baseline, path, entries =>
        {
            var index = entries.FindIndex(entry => entry.Name == "manifest.json");
            var text = Encoding.UTF8.GetString(entries[index].Bytes);
            text = text.Replace("\"trackGainDb\":", "\"trackGainDb\":0,\"trackGainDb\":", StringComparison.Ordinal);
            entries[index] = new("manifest.json", Encoding.UTF8.GetBytes(text));
        });
        RejectLoad(path);
    }

    private static void InconsistentGains(string dir, string baseline)
    {
        RejectManifest(dir, baseline, json => Track(json, 1)["trackGainDb"] = -6);
        RejectManifest(dir, baseline, json => Track(json, 1).Remove("trackGainDb"));
        RejectManifest(dir, baseline, json =>
        {
            foreach (var item in json["tracks"]!.AsArray()) item!["muted"] = true;
            Track(json, 1)["trackGainDb"] = -6;
        });
        // 명시한 laneId와 이전 형식의 Id 기본값이 같으면 같은 트랙입니다.
        RejectManifest(dir, baseline, json =>
        {
            Track(json, 0).Remove("laneId");
            Track(json, 1)["laneId"] = Track(json, 0)["id"]!.DeepClone();
            Track(json, 1)["trackGainDb"] = -6;
        });
    }

    private static void RejectBeforeAudio(string dir, string baseline, bool mismatch)
    {
        var path = MutateManifest(dir, baseline,
            json => Track(json, mismatch ? 1 : 0)["trackGainDb"] = mismatch ? -6 : 13,
            corruptAudio: true);
        var exception = RejectLoad(path);
        Check(exception.Message.Contains(mismatch ? "같은 트랙" : "트랙 음량", StringComparison.Ordinal),
            "손상된 소리 데이터를 읽기 전에 트랙 음량 메타데이터 오류를 알려야 합니다.");
    }

    private static void InvalidSavePreservesFile(string dir, ProjectDocument original)
    {
        var path = Path.Combine(dir, "track-gain-preserved.sorilab");
        ProjectFile.Save(path, original);
        var bytes = File.ReadAllBytes(path);
        foreach (var gain in new[] { -60.001, 12.001, double.NaN, double.PositiveInfinity, double.NegativeInfinity, -6d })
        {
            // -6d 자체는 유효하지만 같은 트랙의 나머지 클립과 달라 저장할 수 없습니다.
            var invalid = original with { Tracks = original.Tracks.Select((track, index) => index == 0 ? track with { TrackGainDb = gain } : track).ToArray() };
            RejectSave(() => ProjectFile.Save(path, invalid));
            Check(bytes.SequenceEqual(File.ReadAllBytes(path)), "실패한 트랙 음량 저장이 기존 파일을 바꿨습니다.");
        }
        CheckMetadata(original, ProjectFile.Load(path));
        Check(!Directory.EnumerateFiles(dir, ".sorilab-*.tmp").Any(), "실패한 저장의 임시파일이 남았습니다.");
    }

    private static void RejectManifest(string dir, string baseline, Action<JsonObject> mutate) =>
        RejectLoad(MutateManifest(dir, baseline, mutate));

    private static string MutateManifest(string dir, string baseline, Action<JsonObject> mutate, bool corruptAudio = false)
    {
        var path = Path.Combine(dir, "track-gain-mutated.sorilab");
        RewritePackage(baseline, path, entries =>
        {
            var index = entries.FindIndex(entry => entry.Name == "manifest.json");
            var json = JsonNode.Parse(entries[index].Bytes)!.AsObject();
            mutate(json);
            entries[index] = new("manifest.json", Encoding.UTF8.GetBytes(json.ToJsonString()));
            if (corruptAudio)
            {
                var source = entries.First(entry => entry.Name.StartsWith("audio/", StringComparison.Ordinal));
                BinaryPrimitives.WriteSingleLittleEndian(source.Bytes.AsSpan(0, 4), float.NaN);
            }
        });
        return path;
    }

    private static void RewritePackage(string path, string target, Action<List<PackageEntry>> mutate)
    {
        var entries = new List<PackageEntry>();
        using (var archive = ZipFile.OpenRead(path))
        {
            foreach (var entry in archive.Entries)
            {
                using var input = entry.Open();
                using var bytes = new MemoryStream();
                input.CopyTo(bytes);
                entries.Add(new(entry.FullName, bytes.ToArray()));
            }
        }
        mutate(entries);
        using var file = File.Create(target);
        using var output = new ZipArchive(file, ZipArchiveMode.Create);
        foreach (var entry in entries)
        {
            using var content = output.CreateEntry(entry.Name, CompressionLevel.Fastest).Open();
            content.Write(entry.Bytes);
        }
    }

    private static JsonObject Track(JsonObject json, int index) => json["tracks"]![index]!.AsObject();

    private static InvalidDataException RejectLoad(string path)
    {
        try { ProjectFile.Load(path); }
        catch (InvalidDataException exception) { return exception; }
        throw new InvalidOperationException("잘못된 트랙 음량 프로젝트는 InvalidDataException으로 거부해야 합니다.");
    }

    private static void RejectSave(Action action)
    {
        try { action(); }
        catch (ArgumentException) { return; }
        catch (InvalidDataException) { return; }
        throw new InvalidOperationException("잘못된 트랙 음량 저장 요청이 거부되지 않았습니다.");
    }

    private static void SameBits(float[] expected, float[] actual)
    {
        Check(expected.Length == actual.Length, "렌더 또는 원본의 샘플 수가 달라졌습니다.");
        for (var index = 0; index < expected.Length; index++)
            Check(BitConverter.SingleToInt32Bits(expected[index]) == BitConverter.SingleToInt32Bits(actual[index]),
                $"{index}번째 표본의 비트가 달라졌습니다.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed record PackageEntry(string Name, byte[] Bytes);
}
