using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SoriLab.Core;

public sealed record ProjectDocument(AudioTrack[] Tracks, double MasterGainDb = 0, Guid? SelectedTrackId = null);

public static class ProjectFile
{
    private const int FormatVersion = 1;
    private const int ProjectSampleRate = 48000;
    private const long MaximumFileBytes = 256L * 1024 * 1024;
    private const int MaximumManifestBytes = 1024 * 1024;
    private const int MaximumSources = 32;
    private const int MaximumEntries = MaximumSources + 1;
    private const string ManifestName = "manifest.json";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 16
    };

    public static void Save(string path, ProjectDocument project)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(project);
        AudioMixer.Validate(project.Tracks, ProjectSampleRate, project.MasterGainDb);

        // 트랙 배열만 고정하고 변경하지 않는 원본 배열은 공유하여 저장 중 큰 복사를 피합니다.
        var tracks = project.Tracks.ToArray();
        var sources = new Dictionary<AudioClip, SourceManifest>(ReferenceEqualityComparer.Instance);
        foreach (var track in tracks)
        {
            if (!sources.ContainsKey(track.Source))
            {
                sources.Add(track.Source, new SourceManifest
                {
                    Id = Guid.NewGuid(), Name = track.Source.Name, SampleRate = track.Source.SampleRate,
                    Channels = track.Source.Channels, SampleCount = track.Source.Samples.Length,
                    Sha256 = new string('0', 64)
                });
            }
        }

        var manifest = new Manifest
        {
            Version = FormatVersion, SampleRate = ProjectSampleRate, MasterGainDb = project.MasterGainDb,
            SelectedTrackId = project.SelectedTrackId, Sources = sources.Values.ToArray(),
            Tracks = tracks.Select(track => new TrackManifest
            {
                Id = track.Id, SourceId = sources[track.Source].Id,
                Edit = new EditManifest
                {
                    StartFrame = track.Edit.StartFrame, EndFrame = track.Edit.EndFrame, GainDb = track.Edit.GainDb,
                    FadeInMs = track.Edit.FadeInMs, FadeOutMs = track.Edit.FadeOutMs
                },
                OffsetSeconds = track.OffsetSeconds, PlaybackRate = track.PlaybackRate,
                Reverse = track.Reverse, Muted = track.Muted, Solo = track.Solo, SourcePath = track.SourcePath,
                LaneId = track.LaneId
            }).ToArray()
        };
        ValidateManifest(manifest);
        _ = EncodeManifest(manifest);

        var destination = Path.GetFullPath(path);
        var temporary = Path.Combine(Path.GetDirectoryName(destination)!, $".sorilab-{Guid.NewGuid():N}.tmp");
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                using (var archive = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: true))
                {
                    foreach (var (source, descriptor) in sources)
                    {
                        var entry = archive.CreateEntry(SourceEntryName(descriptor.Id), CompressionLevel.Fastest);
                        using var output = entry.Open();
                        descriptor.Sha256 = WriteSource(output, source.Samples);
                    }
                    var manifestEntry = archive.CreateEntry(ManifestName, CompressionLevel.Fastest);
                    using var manifestOutput = manifestEntry.Open();
                    manifestOutput.Write(EncodeManifest(manifest));
                }
                if (file.Length > MaximumFileBytes)
                    throw new InvalidDataException("프로젝트 파일이 256MiB를 넘었습니다.");
                file.Flush(flushToDisk: true);
            }
            // ZIP 끝부분까지 기록한 뒤 교체하므로 저장 중 오류가 기존 파일을 비우지 않습니다.
            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            // 실패한 저장에서 만든 임시파일만 정리하며 정리 오류로 원래 오류를 가리지 않습니다.
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    public static ProjectDocument Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        try
        {
            return LoadCore(path);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("프로젝트의 편집 정보가 손상되었거나 필요한 항목이 없습니다.", exception);
        }
        catch (FormatException exception)
        {
            throw new InvalidDataException("프로젝트의 식별자 또는 원본 확인 값이 올바르지 않습니다.", exception);
        }
        catch (ArgumentException exception)
        {
            throw new InvalidDataException("프로젝트의 소리 또는 편집 설정이 올바르지 않습니다.", exception);
        }
        catch (OverflowException exception)
        {
            throw new InvalidDataException("프로젝트의 크기 또는 시간 정보가 처리 범위를 넘었습니다.", exception);
        }
        catch (NotSupportedException exception)
        {
            throw new InvalidDataException("이 프로젝트의 압축 또는 저장 형식은 지원하지 않습니다.", exception);
        }
        catch (EndOfStreamException exception)
        {
            throw new InvalidDataException("프로젝트 파일이 중간에서 끊어졌습니다.", exception);
        }
    }

    private static ProjectDocument LoadCore(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length > MaximumFileBytes)
            throw new InvalidDataException("256MiB 이하의 프로젝트 파일을 선택해 주세요.");
        using var archive = new ZipArchive(file, ZipArchiveMode.Read, leaveOpen: true);
        if (archive.Entries.Count is < 1 or > MaximumEntries)
            throw new InvalidDataException("프로젝트의 내부 파일 개수가 올바르지 않습니다.");

        var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.Ordinal);
        foreach (var entry in archive.Entries)
        {
            if (!entries.TryAdd(entry.FullName, entry))
                throw new InvalidDataException("프로젝트에 같은 이름의 내부 파일이 중복되어 있습니다.");
        }
        if (!entries.TryGetValue(ManifestName, out var manifestEntry) || manifestEntry.Length is <= 0 or > MaximumManifestBytes)
            throw new InvalidDataException("프로젝트의 편집 정보가 없거나 1MiB를 넘었습니다.");

        var manifestBytes = new byte[(int)manifestEntry.Length];
        using (var input = manifestEntry.Open())
        {
            input.ReadExactly(manifestBytes);
            if (input.ReadByte() != -1)
                throw new InvalidDataException("프로젝트 편집 정보의 크기가 맞지 않습니다.");
        }
        using (var json = JsonDocument.Parse(manifestBytes, new JsonDocumentOptions { MaxDepth = 16 }))
            RejectDuplicateProperties(json.RootElement);
        var manifest = JsonSerializer.Deserialize<Manifest>(manifestBytes, JsonOptions)
            ?? throw new InvalidDataException("프로젝트 편집 정보가 비어 있습니다.");
        ValidateManifest(manifest);

        if (entries.Count != manifest.Sources.Length + 1)
            throw new InvalidDataException("프로젝트의 원본 목록과 내부 파일 개수가 다릅니다.");
        // 크기와 모든 참조를 먼저 검사한 뒤에만 원본 배열을 만듭니다. 경로로 파일을 추출하지 않습니다.
        foreach (var source in manifest.Sources)
        {
            if (!entries.TryGetValue(SourceEntryName(source.Id), out var entry) || entry.Length != checked(source.SampleCount * 4L))
                throw new InvalidDataException("프로젝트 원본 파일이 없거나 샘플 수와 크기가 다릅니다.");
        }

        var sources = new Dictionary<Guid, AudioClip>();
        foreach (var source in manifest.Sources)
        {
            using var input = entries[SourceEntryName(source.Id)].Open();
            var samples = ReadSource(input, source);
            sources.Add(source.Id, new AudioClip(source.Name, source.SampleRate, source.Channels, samples));
        }
        var tracks = manifest.Tracks.Select(track => new AudioTrack(
            track.Id, sources[track.SourceId],
            new EditSettings(track.Edit.StartFrame, track.Edit.EndFrame, track.Edit.GainDb, track.Edit.FadeInMs, track.Edit.FadeOutMs),
            track.OffsetSeconds, track.PlaybackRate, track.Reverse, track.Muted, track.Solo, track.SourcePath, track.LaneId)).ToArray();
        AudioMixer.Validate(tracks, ProjectSampleRate, manifest.MasterGainDb);
        return new ProjectDocument(tracks, manifest.MasterGainDb, manifest.SelectedTrackId);
    }

    private static void ValidateManifest(Manifest manifest)
    {
        if (manifest.Version != FormatVersion)
            throw new InvalidDataException("지원하지 않는 프로젝트 버전입니다. 이 버전은 형식 1만 열 수 있습니다.");
        if (manifest.SampleRate != ProjectSampleRate)
            throw new InvalidDataException("프로젝트 출력 샘플레이트는 48,000Hz여야 합니다.");
        if (!double.IsFinite(manifest.MasterGainDb) || manifest.MasterGainDb is < -60 or > 12)
            throw new InvalidDataException("프로젝트 전체 음량이 허용 범위를 벗어났습니다.");
        if (manifest.Sources is null || manifest.Sources.Length > MaximumSources ||
            manifest.Tracks is null || manifest.Tracks.Length > AudioMixer.MaximumClips)
            throw new InvalidDataException("프로젝트는 최대 32개의 원본과 128개의 클립을 지원합니다.");

        var sources = new Dictionary<Guid, SourceManifest>();
        long totalSamples = 0;
        foreach (var source in manifest.Sources)
        {
            if (source is null || !sources.TryAdd(source.Id, source))
                throw new InvalidDataException("프로젝트 원본 정보가 없거나 식별자가 중복되어 있습니다.");
            if (source.Name is null || source.Name.Length > 4096 || source.SampleRate <= 0 || source.Channels is not (1 or 2) ||
                source.SampleCount <= 0 || source.SampleCount > AudioValidation.MaximumSamples || source.SampleCount % source.Channels != 0)
                throw new InvalidDataException("프로젝트 원본의 이름·샘플레이트·채널·길이 정보가 올바르지 않습니다.");
            totalSamples += source.SampleCount;
            if (totalSamples > AudioValidation.MaximumSamples)
                throw new InvalidDataException("프로젝트 원본은 합계 3,200만 개의 샘플까지 지원합니다.");
            if (source.Sha256 is null || source.Sha256.Length != 64 || !source.Sha256.All(Uri.IsHexDigit))
                throw new InvalidDataException("프로젝트 원본의 손상 확인 값이 올바르지 않습니다.");
        }

        var identifiers = new HashSet<Guid>();
        var referencedSources = new HashSet<Guid>();
        var lanes = new Dictionary<Guid, (bool Muted, bool Solo)>();
        var hasSolo = manifest.Tracks.Any(track => track is not null && track.Solo);
        foreach (var track in manifest.Tracks)
        {
            if (track is null || !identifiers.Add(track.Id) || !sources.TryGetValue(track.SourceId, out var source))
                throw new InvalidDataException("트랙 식별자가 중복되었거나 연결된 원본이 없습니다.");
            // 이전 v1에는 laneId가 없으므로 해당 클립을 독립된 트랙으로 복원합니다.
            var laneId = track.LaneId ?? track.Id;
            if (lanes.TryGetValue(laneId, out var laneState))
            {
                if (laneState != (track.Muted, track.Solo))
                    throw new InvalidDataException("같은 트랙의 클립은 음소거와 단독 재생 설정이 같아야 합니다.");
            }
            else
            {
                lanes.Add(laneId, (track.Muted, track.Solo));
                if (lanes.Count > AudioMixer.MaximumTracks)
                    throw new InvalidDataException("프로젝트에는 최대 32개의 트랙을 넣을 수 있습니다.");
            }
            referencedSources.Add(track.SourceId);
            var edit = track.Edit;
            if (edit is null || edit.StartFrame < 0 || edit.EndFrame > source.SampleCount / source.Channels || edit.EndFrame <= edit.StartFrame)
                throw new InvalidDataException("트랙의 선택 구간이 원본 범위를 벗어났습니다.");
            if (!double.IsFinite(edit.GainDb) || !double.IsFinite(Math.Pow(10, edit.GainDb / 20)) ||
                !double.IsFinite(edit.FadeInMs) || edit.FadeInMs < 0 || !double.IsFinite(edit.FadeOutMs) || edit.FadeOutMs < 0)
                throw new InvalidDataException("트랙의 음량 또는 페이드 정보가 올바르지 않습니다.");
            if (!double.IsFinite(track.OffsetSeconds) || track.OffsetSeconds is < 0 or > AudioMixer.MaximumDurationSeconds ||
                !double.IsFinite(track.PlaybackRate) || track.PlaybackRate is < 0.25 or > 4 || track.SourcePath?.Length > 32768)
                throw new InvalidDataException("트랙의 배치 위치·재생 속도·원본 경로 정보가 올바르지 않습니다.");
            if (!track.Muted && (!hasSolo || track.Solo))
            {
                var duration = (double)(edit.EndFrame - edit.StartFrame) / source.SampleRate / track.PlaybackRate;
                var frames = Math.Max(1, Math.Ceiling(duration * ProjectSampleRate - 1e-9));
                var offsetFrames = Math.Round(track.OffsetSeconds * ProjectSampleRate, MidpointRounding.AwayFromZero);
                if (track.OffsetSeconds + duration > AudioMixer.MaximumDurationSeconds + 1e-9 ||
                    frames + offsetFrames > AudioValidation.MaximumSamples / 2)
                    throw new InvalidDataException("조합한 소리는 최대 120초까지 지원합니다.");
            }
        }
        if (referencedSources.Count != sources.Count)
            throw new InvalidDataException("어떤 트랙에서도 사용하지 않는 원본이 프로젝트에 있습니다.");
        if (manifest.SelectedTrackId is Guid selected && !identifiers.Contains(selected))
            throw new InvalidDataException("선택한 트랙이 프로젝트에 없습니다.");
    }

    private static byte[] EncodeManifest(Manifest manifest)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions);
        if (bytes.Length > MaximumManifestBytes)
            throw new InvalidDataException("프로젝트의 편집 정보가 1MiB를 넘었습니다.");
        return bytes;
    }

    private static string WriteSource(Stream output, float[] samples)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[65536];
        for (var offset = 0; offset < samples.Length;)
        {
            var count = Math.Min(buffer.Length / 4, samples.Length - offset);
            for (var index = 0; index < count; index++)
            {
                AudioValidation.ValidateSample(samples[offset + index]);
                BinaryPrimitives.WriteSingleLittleEndian(buffer.AsSpan(index * 4, 4), samples[offset + index]);
            }
            var bytes = buffer.AsSpan(0, count * 4);
            hash.AppendData(bytes);
            output.Write(bytes);
            offset += count;
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static float[] ReadSource(Stream input, SourceManifest source)
    {
        var samples = new float[source.SampleCount];
        var buffer = new byte[65536];
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        for (var offset = 0; offset < samples.Length;)
        {
            var count = Math.Min(buffer.Length / 4, samples.Length - offset);
            var bytes = buffer.AsSpan(0, count * 4);
            input.ReadExactly(bytes);
            hash.AppendData(bytes);
            for (var index = 0; index < count; index++)
            {
                var sample = BinaryPrimitives.ReadSingleLittleEndian(bytes.Slice(index * 4, 4));
                if (!float.IsFinite(sample))
                    throw new InvalidDataException("프로젝트 원본에 유한하지 않은 소리 값이 있습니다.");
                samples[offset + index] = sample;
            }
            offset += count;
        }
        if (input.ReadByte() != -1)
            throw new InvalidDataException("프로젝트 원본이 기록된 샘플 수보다 큽니다.");
        if (!CryptographicOperations.FixedTimeEquals(hash.GetHashAndReset(), Convert.FromHexString(source.Sha256)))
            throw new InvalidDataException("프로젝트 원본이 저장 당시와 다릅니다. 파일이 손상되었을 수 있습니다.");
        return samples;
    }

    private static void RejectDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                    throw new InvalidDataException("프로젝트 편집 정보에 같은 항목이 중복되어 있습니다.");
                RejectDuplicateProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
                RejectDuplicateProperties(item);
        }
    }

    private static string SourceEntryName(Guid id) => $"audio/{id:N}.f32";

    private sealed class Manifest
    {
        public Manifest() { }
        public required int Version { get; init; }
        public required int SampleRate { get; init; }
        public required double MasterGainDb { get; init; }
        public required Guid? SelectedTrackId { get; init; }
        public required SourceManifest[] Sources { get; init; }
        public required TrackManifest[] Tracks { get; init; }
    }

    private sealed class SourceManifest
    {
        public SourceManifest() { }
        public required Guid Id { get; init; }
        public required string Name { get; init; }
        public required int SampleRate { get; init; }
        public required int Channels { get; init; }
        public required int SampleCount { get; init; }
        public required string Sha256 { get; set; }
    }

    private sealed class TrackManifest
    {
        public TrackManifest() { }
        public required Guid Id { get; init; }
        public required Guid SourceId { get; init; }
        public required EditManifest Edit { get; init; }
        public required double OffsetSeconds { get; init; }
        public required double PlaybackRate { get; init; }
        public required bool Reverse { get; init; }
        public required bool Muted { get; init; }
        public required bool Solo { get; init; }
        public required string? SourcePath { get; init; }
        public Guid? LaneId { get; init; }
    }

    private sealed class EditManifest
    {
        public EditManifest() { }
        public required int StartFrame { get; init; }
        public required int EndFrame { get; init; }
        public required double GainDb { get; init; }
        public required double FadeInMs { get; init; }
        public required double FadeOutMs { get; init; }
    }
}
