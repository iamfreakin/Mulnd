using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using SoriLab.Core;

namespace SoriLab.Checks;

public static class ProjectChecks
{
    public static int Run(string dir)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dir);
        Directory.CreateDirectory(dir);
        var original = Fixture(dir);
        var baseline = Path.Combine(dir, "project-baseline.sorilab");
        ProjectFile.Save(baseline, original);

        var checks = new (string Name, Action Check)[]
        {
            ("모든 트랙 설정과 원본 정보의 재열기", () => MetadataRoundtrip(original, ProjectFile.Load(baseline))),
            ("float32 비트와 음수 0의 무손실 저장", () => ExactFloatBits(dir)),
            ("공유 원본의 파일 중복 제거와 참조 복원", () => SharedSources(baseline)),
            ("48kHz 혼합 결과의 비트 일치", () => RenderRoundtrip(original, ProjectFile.Load(baseline))),
            ("외부 원본이 없어도 프로젝트 열기", () => MissingExternalSource(baseline)),
            ("빈 프로젝트 저장과 재열기", () => EmptyProject(dir)),
            ("모든 트랙 음소거 상태 저장", () => AllMutedProject(dir, original)),
            ("기존 정상 프로젝트에 완성본 덮어쓰기", () => OverwriteProject(dir, original)),
            ("잘못된 저장 입력에서 기존 파일 보존", () => InvalidSavePreservesFile(dir, original)),
            ("교체 실패에서 목적지와 임시파일 정리", () => FailedMoveCleansTemporary(dir, original)),
            ("변경된 원본의 SHA-256 불일치 검출", () => TamperedSource(dir, baseline, false)),
            ("일치하는 SHA-256이라도 NaN 샘플 거부", () => TamperedSource(dir, baseline, true)),
            ("지원하지 않는 버전 거부", () => RejectManifest(dir, baseline, json => json["version"] = 2)),
            ("48kHz가 아닌 출력 설정 거부", () => RejectManifest(dir, baseline, json => json["sampleRate"] = 44100)),
            ("중복 트랙 식별자 거부", () => RejectManifest(dir, baseline, json => Track(json, 1)["id"] = Track(json, 0)["id"]!.DeepClone())),
            ("중복 원본 식별자 거부", () => RejectManifest(dir, baseline, json => Source(json, 1)["id"] = Source(json, 0)["id"]!.DeepClone())),
            ("없는 원본 참조 거부", () => RejectManifest(dir, baseline, json => Track(json, 0)["sourceId"] = Guid.NewGuid().ToString())),
            ("없는 선택 트랙 거부", () => RejectManifest(dir, baseline, json => json["selectedTrackId"] = Guid.NewGuid().ToString())),
            ("잘못된 구간과 편집 숫자 거부", () => InvalidEditing(dir, baseline)),
            ("원본 크기·채널·샘플레이트 검증", () => InvalidSourceMetadata(dir, baseline)),
            ("전체 원본 샘플 한도 거부", () => RejectManifest(dir, baseline, json =>
            {
                Source(json, 0)["sampleCount"] = 16_000_001;
                Source(json, 1)["sampleCount"] = 16_000_002;
            })),
            ("원본 개수 한도 거부", () => RejectManifest(dir, baseline, json =>
            {
                var sources = json["sources"]!.AsArray();
                while (sources.Count <= 32) sources.Add(sources[0]!.DeepClone());
            })),
            ("트랙 개수 한도 거부", () => RejectManifest(dir, baseline, json =>
            {
                var tracks = json["tracks"]!.AsArray();
                while (tracks.Count <= 32) tracks.Add(tracks[0]!.DeepClone());
            })),
            ("잘못된 체크섬 문자열 거부", () => RejectManifest(dir, baseline, json => Source(json, 0)["sha256"] = new string('X', 64))),
            ("필수 항목 누락과 null 거부", () => MissingMetadata(dir, baseline)),
            ("중복 JSON 속성 거부", () => DuplicateJsonProperty(dir, baseline)),
            ("알 수 없는 편집 항목 거부", () => RejectManifest(dir, baseline, json => json["unexpected"] = true)),
            ("중복 ZIP 내부 파일 거부", () => RejectPackage(dir, baseline, entries => entries.Add(entries[0]))),
            ("누락된 원본 파일 거부", () => RejectPackage(dir, baseline, entries => entries.RemoveAt(0))),
            ("추가 내부 파일 거부", () => RejectPackage(dir, baseline, entries => entries.Add(new("unexpected.bin", [0])))),
            ("경로 이탈 이름 거부와 추출 방지", () => PathTraversal(dir, baseline)),
            ("내부 파일 개수 한도 거부", () => RejectPackage(dir, baseline, entries =>
            {
                for (var index = 0; entries.Count <= 33; index++) entries.Add(new($"extra-{index}", []));
            })),
            ("압축 해제 후 1MiB를 넘는 편집 정보 거부", () => RejectPackage(dir, baseline, entries =>
            {
                var index = entries.FindIndex(entry => entry.Name == "manifest.json");
                entries[index] = new("manifest.json", Encoding.UTF8.GetBytes(new string(' ', 1024 * 1024 + 1)));
            })),
            ("잘린 원본 데이터 거부", () => RejectPackage(dir, baseline, entries => entries[0] = entries[0] with { Bytes = entries[0].Bytes[..^1] })),
            ("잘린 ZIP 끝부분 거부", () => TruncatedArchive(dir, baseline)),
            ("JSON 문법 오류를 한국어 파일 오류로 변환", () => RejectPackage(dir, baseline, entries =>
            {
                var index = entries.FindIndex(entry => entry.Name == "manifest.json");
                entries[index] = new("manifest.json", "{broken"u8.ToArray());
            }))
        };

        foreach (var (name, check) in checks)
        {
            check();
            Console.WriteLine($"통과: 프로젝트 — {name}");
        }
        return checks.Length;
    }

    private static ProjectDocument Fixture(string dir)
    {
        var mono = new AudioClip("충돌 원본 · 한글", 44100, 1,
            Enumerable.Range(0, 97).Select(index => (float)(Math.Sin(index * 0.23) * 0.4)).ToArray());
        var stereo = new AudioClip("스테레오 원본", 22050, 2,
            Enumerable.Range(0, 128).Select(index => (index % 11 - 5) / 23f).ToArray());
        var first = new AudioTrack(Guid.NewGuid(), mono, new EditSettings(3, 95, -3.25, 0.03125, 0.0625),
            0.00223, 0.75, true, false, true, Path.Combine(dir, "존재하지 않는 원본.wav"));
        var second = new AudioTrack(Guid.NewGuid(), mono, new EditSettings(5, 93, -7.125, 0.12, 0.29),
            0.00123456789, 1.25, false, false, true, null);
        var third = new AudioTrack(Guid.NewGuid(), stereo, new EditSettings(2, 63, 0, 0, 0),
            0.003, 1, false, true, false, "표시 전용 상대 경로.wav");
        return new ProjectDocument([first, second, third], -4.875, second.Id);
    }

    private static void MetadataRoundtrip(ProjectDocument expected, ProjectDocument actual)
    {
        True(expected.MasterGainDb == actual.MasterGainDb && expected.SelectedTrackId == actual.SelectedTrackId,
            "전체 음량 또는 선택 트랙이 달라졌습니다.");
        True(expected.Tracks.Length == actual.Tracks.Length, "트랙 수가 달라졌습니다.");
        for (var index = 0; index < expected.Tracks.Length; index++)
        {
            var before = expected.Tracks[index];
            var after = actual.Tracks[index];
            True(before with { Source = after.Source } == after, "트랙의 순서 또는 편집값이 달라졌습니다.");
            True(before.Source.Name == after.Source.Name && before.Source.SampleRate == after.Source.SampleRate &&
                before.Source.Channels == after.Source.Channels, "원본 정보가 달라졌습니다.");
            SameBits(before.Source.Samples, after.Source.Samples);
        }
    }

    private static void ExactFloatBits(string dir)
    {
        float[] samples = [0f, BitConverter.Int32BitsToSingle(unchecked((int)0x80000000)), float.Epsilon,
            -float.Epsilon, 1f / 3, -1.2345678f, float.MaxValue, -float.MaxValue, 0.000000123456789f];
        var source = new AudioClip("비트 검증", 48000, 1, samples);
        var project = new ProjectDocument([new AudioTrack(Guid.NewGuid(), source, new EditSettings(0, samples.Length, 0, 0, 0))]);
        var path = Path.Combine(dir, "project-bits.sorilab");
        ProjectFile.Save(path, project);
        SameBits(samples, ProjectFile.Load(path).Tracks[0].Source.Samples);

        using var zip = ZipFile.OpenRead(path);
        using var input = zip.Entries.Single(entry => entry.FullName.StartsWith("audio/", StringComparison.Ordinal)).Open();
        var bytes = new byte[samples.Length * 4];
        input.ReadExactly(bytes);
        for (var index = 0; index < samples.Length; index++)
            True(BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(index * 4, 4)) == BitConverter.SingleToInt32Bits(samples[index]),
                "원본 파일의 바이트 순서가 little-endian float32와 다릅니다.");
    }

    private static void SharedSources(string path)
    {
        var project = ProjectFile.Load(path);
        True(ReferenceEquals(project.Tracks[0].Source, project.Tracks[1].Source), "복제 트랙이 원본 객체를 공유하지 않습니다.");
        True(ReferenceEquals(project.Tracks[0].Source.Samples, project.Tracks[1].Source.Samples), "복제 트랙이 원본 배열을 공유하지 않습니다.");
        using var zip = ZipFile.OpenRead(path);
        True(zip.Entries.Count == 3, "공유한 원본은 한 번만 저장해야 합니다.");
        using var input = zip.GetEntry("manifest.json")!.Open();
        var json = JsonNode.Parse(input)!.AsObject();
        True(json["version"]!.GetValue<int>() == 1 && json["sampleRate"]!.GetValue<int>() == 48000,
            "형식 버전과 출력 샘플레이트가 다릅니다.");
    }

    private static void RenderRoundtrip(ProjectDocument before, ProjectDocument after)
    {
        SameBits(AudioMixer.Render(before.Tracks, masterGainDb: before.MasterGainDb).Samples,
            AudioMixer.Render(after.Tracks, masterGainDb: after.MasterGainDb).Samples);
        // 음소거됐던 스테레오 원본도 켜서 두 샘플레이트와 채널 구성의 복원을 확인합니다.
        SameBits(AudioMixer.Render(before.Tracks.Select(track => track with { Muted = false, Solo = false }).ToArray()).Samples,
            AudioMixer.Render(after.Tracks.Select(track => track with { Muted = false, Solo = false }).ToArray()).Samples);
    }

    private static void MissingExternalSource(string path)
    {
        var project = ProjectFile.Load(path);
        True(!File.Exists(project.Tracks[0].SourcePath), "검증용 원본 파일은 없어야 합니다.");
        True(AudioMixer.Render(project.Tracks).Samples.Length > 0, "내장 원본으로 재생하지 못했습니다.");
    }

    private static void EmptyProject(string dir)
    {
        var path = Path.Combine(dir, "project-empty.sorilab");
        ProjectFile.Save(path, new ProjectDocument([], -3));
        var loaded = ProjectFile.Load(path);
        True(loaded.Tracks.Length == 0 && loaded.MasterGainDb == -3 && loaded.SelectedTrackId is null, "빈 프로젝트가 달라졌습니다.");
    }

    private static void AllMutedProject(string dir, ProjectDocument basis)
    {
        var path = Path.Combine(dir, "project-muted.sorilab");
        var muted = basis with { Tracks = basis.Tracks.Select(track => track with { Muted = true }).ToArray() };
        ProjectFile.Save(path, muted);
        MetadataRoundtrip(muted, ProjectFile.Load(path));
    }

    private static void OverwriteProject(string dir, ProjectDocument basis)
    {
        var path = Path.Combine(dir, "project-overwrite.sorilab");
        ProjectFile.Save(path, new ProjectDocument([]));
        ProjectFile.Save(path, basis);
        MetadataRoundtrip(basis, ProjectFile.Load(path));
        True(!Directory.EnumerateFiles(dir, ".sorilab-*.tmp").Any(), "정상 저장 후 임시파일이 남았습니다.");
    }

    private static void InvalidSavePreservesFile(string dir, ProjectDocument basis)
    {
        var path = Path.Combine(dir, "project-preserved.sorilab");
        ProjectFile.Save(path, basis);
        var saved = File.ReadAllBytes(path);
        var brokenSource = basis.Tracks[0].Source with { Samples = [float.NaN] };
        ProjectDocument[] invalid =
        [
            basis with { MasterGainDb = double.NaN },
            basis with { SelectedTrackId = Guid.NewGuid() },
            basis with { Tracks = [basis.Tracks[0] with { Source = brokenSource, Edit = new EditSettings(0, 1, 0, 0, 0) }], SelectedTrackId = null },
            basis with { Tracks = [basis.Tracks[0] with { Source = basis.Tracks[0].Source with { Name = new string('가', 4097) } }], SelectedTrackId = null }
        ];
        foreach (var project in invalid)
        {
            RejectSave(() => ProjectFile.Save(path, project));
            True(saved.SequenceEqual(File.ReadAllBytes(path)), "실패한 저장이 이전 파일을 바꿨습니다.");
        }
    }

    private static void FailedMoveCleansTemporary(string dir, ProjectDocument basis)
    {
        var destination = Path.Combine(dir, "project-directory.sorilab");
        Directory.CreateDirectory(destination);
        RejectSave(() => ProjectFile.Save(destination, basis));
        True(Directory.Exists(destination), "교체 실패가 기존 폴더를 바꿨습니다.");
        True(!Directory.EnumerateFiles(dir, ".sorilab-*.tmp").Any(), "교체 실패 후 임시파일이 남았습니다.");
    }

    private static void TamperedSource(string dir, string path, bool matchingHash)
    {
        RejectPackage(dir, path, entries =>
        {
            var entry = entries[0];
            if (matchingHash)
                BinaryPrimitives.WriteSingleLittleEndian(entry.Bytes.AsSpan(0, 4), float.NaN);
            else
                entry.Bytes[0] ^= 1;
            if (matchingHash)
            {
                var manifestIndex = entries.FindIndex(item => item.Name == "manifest.json");
                var json = JsonNode.Parse(entries[manifestIndex].Bytes)!.AsObject();
                Source(json, 0)["sha256"] = Convert.ToHexString(SHA256.HashData(entry.Bytes));
                entries[manifestIndex] = new("manifest.json", Encoding.UTF8.GetBytes(json.ToJsonString()));
            }
        });
    }

    private static void InvalidEditing(string dir, string path)
    {
        Action<JsonObject>[] mutations =
        [
            json => Track(json, 0)["edit"]!["startFrame"] = -1,
            json => Track(json, 0)["edit"]!["endFrame"] = int.MaxValue,
            json => Track(json, 0)["edit"]!["fadeInMs"] = -1,
            json => Track(json, 0)["edit"]!["gainDb"] = 100000,
            json => Track(json, 0)["playbackRate"] = 0,
            json => Track(json, 0)["offsetSeconds"] = -0.01,
            json => Track(json, 0)["offsetSeconds"] = 120,
            json => json["masterGainDb"] = 13,
            json => json["masterGainDb"] = "NaN"
        ];
        foreach (var mutation in mutations) RejectManifest(dir, path, mutation);
    }

    private static void InvalidSourceMetadata(string dir, string path)
    {
        Action<JsonObject>[] mutations =
        [
            json => Source(json, 0)["sampleRate"] = 0,
            json => Source(json, 0)["channels"] = 3,
            json => Source(json, 0)["sampleCount"] = 0,
            json => Source(json, 0)["sampleCount"] = 32_000_001,
            json => Source(json, 1)["sampleCount"] = 127,
            json => Source(json, 0)["name"] = null,
            json => Source(json, 0)["sampleCount"] = 99
        ];
        foreach (var mutation in mutations) RejectManifest(dir, path, mutation);
    }

    private static void MissingMetadata(string dir, string path)
    {
        Action<JsonObject>[] mutations =
        [
            json => json.Remove("version"),
            json => Track(json, 0).Remove("muted"),
            json => Track(json, 0)["edit"]!.AsObject().Remove("gainDb"),
            json => json["tracks"] = null,
            json => json["sources"] = null,
            json => json["tracks"]![0] = null,
            json => Track(json, 0)["edit"] = null,
            json => json["sources"]![0] = null
        ];
        foreach (var mutation in mutations) RejectManifest(dir, path, mutation);
    }

    private static void DuplicateJsonProperty(string dir, string path)
    {
        RejectPackage(dir, path, entries =>
        {
            var index = entries.FindIndex(entry => entry.Name == "manifest.json");
            var json = Encoding.UTF8.GetString(entries[index].Bytes);
            entries[index] = new("manifest.json", Encoding.UTF8.GetBytes("{\"version\":1," + json[1..]));
        });
    }

    private static void PathTraversal(string dir, string path)
    {
        RejectPackage(dir, path, entries => entries[0] = entries[0] with { Name = "../project-extraction-probe.f32" });
        True(!File.Exists(Path.Combine(dir, "project-extraction-probe.f32")), "압축 항목을 디스크에 추출했습니다.");
    }

    private static void TruncatedArchive(string dir, string path)
    {
        var target = Path.Combine(dir, "project-truncated.sorilab");
        var bytes = File.ReadAllBytes(path);
        File.WriteAllBytes(target, bytes[..^22]);
        ThrowsInvalidData(() => ProjectFile.Load(target));
    }

    private static void RejectManifest(string dir, string path, Action<JsonObject> mutate)
    {
        RejectPackage(dir, path, entries =>
        {
            var index = entries.FindIndex(entry => entry.Name == "manifest.json");
            var json = JsonNode.Parse(entries[index].Bytes)!.AsObject();
            mutate(json);
            entries[index] = new("manifest.json", Encoding.UTF8.GetBytes(json.ToJsonString()));
        });
    }

    private static void RejectPackage(string dir, string path, Action<List<PackageEntry>> mutate)
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
        var target = Path.Combine(dir, "project-malformed.sorilab");
        using (var file = File.Create(target))
        using (var archive = new ZipArchive(file, ZipArchiveMode.Create))
        {
            foreach (var entry in entries)
            {
                using var output = archive.CreateEntry(entry.Name, CompressionLevel.Fastest).Open();
                output.Write(entry.Bytes);
            }
        }
        ThrowsInvalidData(() => ProjectFile.Load(target));
    }

    private static JsonObject Track(JsonObject json, int index) => json["tracks"]![index]!.AsObject();
    private static JsonObject Source(JsonObject json, int index) => json["sources"]![index]!.AsObject();

    private static void SameBits(float[] expected, float[] actual)
    {
        True(expected.Length == actual.Length, "샘플 수가 달라졌습니다.");
        for (var index = 0; index < expected.Length; index++)
            True(BitConverter.SingleToInt32Bits(expected[index]) == BitConverter.SingleToInt32Bits(actual[index]),
                $"{index}번째 샘플의 비트가 달라졌습니다.");
    }

    private static void ThrowsInvalidData(Action action)
    {
        try { action(); }
        catch (InvalidDataException) { return; }
        throw new InvalidOperationException("손상된 프로젝트는 InvalidDataException으로 거부해야 합니다.");
    }

    private static void RejectSave(Action action)
    {
        try { action(); }
        catch (ArgumentException) { return; }
        catch (InvalidDataException) { return; }
        catch (IOException) { return; }
        catch (UnauthorizedAccessException) { return; }
        throw new InvalidOperationException("잘못된 저장 요청은 실패해야 합니다.");
    }

    private static void True(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed record PackageEntry(string Name, byte[] Bytes);
}
