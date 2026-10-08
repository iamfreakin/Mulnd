using System.Buffers.Binary;
using System.Text;
using SoriLab.Core;

namespace SoriLab.Checks;

internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length != 1 || string.IsNullOrWhiteSpace(args[0]))
        {
            Console.Error.WriteLine("검증용 파일을 만들 임시 폴더를 첫 번째 인수로 지정해 주세요.");
            return 1;
        }

        FixtureStore files;
        try
        {
            files = new FixtureStore(args[0]);
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"임시 폴더를 준비하지 못했습니다: {error.Message}");
            return 1;
        }

        var tests = new (string Name, Action Test)[]
        {
            ("스테레오 PCM16 저장과 다시 읽기", () => CheckStereoRoundTrip(files)),
            ("PCM8의 무음과 양끝 음량", () => CheckPcm8(files)),
            ("PCM24의 부호 확장", () => CheckPcm24(files)),
            ("PCM32의 정규화", () => CheckPcm32(files)),
            ("float32의 음량 초과 보존", () => CheckFloat(files)),
            ("확장 PCM의 유효 비트와 왼쪽 정렬", () => CheckExtensiblePcm(files)),
            ("확장 스테레오 float32", () => CheckExtensibleFloat(files)),
            ("알 수 없는 홀수 청크와 패딩", () => CheckOddChunk(files)),
            ("형식 정보 앞에 오는 소리 데이터", () => CheckDataBeforeFormat(files)),
            ("잘린 WAV 파일 거부", () => CheckTruncatedFile(files)),
            ("경계를 넘는 데이터 청크 거부", () => CheckOversizedChunk(files)),
            ("정렬되지 않은 채널 데이터 거부", () => CheckMisalignedData(files)),
            ("너무 짧은 형식 청크 거부", () => CheckShortFormat(files)),
            ("잘못된 샘플 크기 정보 거부", () => CheckInvalidBlockAlign(files)),
            ("지원하지 않는 확장 subtype 거부", () => CheckUnsupportedSubtype(files)),
            ("비어 있는 WAV 데이터 거부", () => CheckEmptyData(files)),
            ("중복 데이터 청크 거부", () => CheckDuplicateData(files)),
            ("NaN 소리 데이터 거부", () => CheckNonFiniteFile(files, float.NaN, "nan")),
            ("양의 무한대 소리 데이터 거부", () => CheckNonFiniteFile(files, float.PositiveInfinity, "positive-infinity")),
            ("음의 무한대 소리 데이터 거부", () => CheckNonFiniteFile(files, float.NegativeInfinity, "negative-infinity")),
            ("스테레오 구간의 끝 프레임 제외", CheckTrim),
            ("약 6.0206dB에서 음량 두 배", CheckGain),
            ("페이드 인의 시작 0", CheckFadeIn),
            ("페이드 아웃의 마지막 0", CheckFadeOut),
            ("겹치는 두 페이드의 곱", CheckOverlappingFades),
            ("한 프레임 소리의 페이드", CheckSingleFrameFade),
            ("한 프레임보다 짧은 페이드", CheckSubFrameFade),
            ("선택 구간보다 긴 페이드", CheckLongFade),
            ("편집 후 원본 배열 보존", CheckOriginalPreserved),
            ("내부 음량 초과와 최대 음량 분석", CheckInternalClippingPreserved),
            ("PCM16 출력 시 음량 제한", CheckExportClamp),
            ("잘못된 구간과 편집 수치 거부", CheckInvalidSettings),
            ("잘못된 오디오 구성 거부", CheckInvalidClips),
            ("메모리의 NaN·무한대 거부", CheckNonFiniteMemory),
            ("잘못된 소리 저장 시 기존 파일 보존", () => CheckInvalidWritePreservesFile(files))
        };

        var passed = 0;
        foreach (var (name, test) in tests)
        {
            try
            {
                test();
                passed++;
                Console.WriteLine($"통과: {name}");
            }
            catch (Exception error)
            {
                Console.Error.WriteLine($"실패: {name} — {error.GetType().Name}: {error.Message}");
            }
        }

        Console.WriteLine($"검증 결과: {passed}/{tests.Length} 통과, {tests.Length - passed} 실패");
        try
        {
            int mixerPassed = MixerChecks.Run(args[0]);
            Console.WriteLine($"조합 검증: {mixerPassed}개 통과");
            int generatorPassed = GeneratorChecks.Run(args[0]);
            Console.WriteLine($"생성 검증: {generatorPassed}개 통과");
            int projectPassed = ProjectChecks.Run(args[0]);
            Console.WriteLine($"프로젝트 검증: {projectPassed}개 통과");
            int lanePassed = LaneChecks.Run(args[0]);
            Console.WriteLine($"트랙 배치 검증: {lanePassed}개 통과");
            int playbackPassed = PlaybackChecks.Run(args[0]);
            Console.WriteLine($"커서 재생 검증: {playbackPassed}개 통과");
        }
        catch (Exception error) { Console.Error.WriteLine("추가 기능 검증 실패: " + error); return 1; }
        return passed == tests.Length ? 0 : 1;
    }

    private static void CheckStereoRoundTrip(FixtureStore files)
    {
        var source = new AudioClip("원본", 48000, 2, [-1, 0.5f, -0.25f, 0.125f, 0, 0.999f]);
        var encoded = WavCodec.EncodePcm16(source);
        Equal(44 + source.Samples.Length * 2, encoded.Length, "PCM16 파일 크기");
        Equal(2, BinaryPrimitives.ReadUInt16LittleEndian(encoded.AsSpan(22)), "저장된 채널 수");
        Equal(48000, BinaryPrimitives.ReadInt32LittleEndian(encoded.AsSpan(24)), "저장된 샘플레이트");
        var result = WavCodec.Read(files.Write("stereo-roundtrip", encoded));
        Equal(source.SampleRate, result.SampleRate, "샘플레이트");
        Equal(source.Channels, result.Channels, "채널 수");
        Equal(3, result.FrameCount, "프레임 수");
        Near(3d / 48000, result.DurationSeconds, 1e-12, "소리 길이");
        SamplesNear(source.Samples, result.Samples, 1d / 32768);
    }

    private static void CheckPcm8(FixtureStore files)
    {
        var bytes = Wave(("fmt ", Format(1, 1, 8000, 8)), ("data", [0, 64, 128, 192, 255]));
        var result = WavCodec.Read(files.Write("pcm8", bytes));
        SamplesNear([-1, -0.5f, 0, 0.5f, 127f / 128], result.Samples);
    }

    private static void CheckPcm24(FixtureStore files)
    {
        var bytes = Wave(("fmt ", Format(1, 1, 48000, 24)),
            ("data", Pcm24Data([-8388608, -1, 0, 4194304, 8388607])));
        var result = WavCodec.Read(files.Write("pcm24", bytes));
        SamplesNear([-1, -1f / 8388608, 0, 0.5f, 8388607f / 8388608], result.Samples);
    }

    private static void CheckPcm32(FixtureStore files)
    {
        var data = Bytes(writer =>
        {
            foreach (var sample in new[] { int.MinValue, -1073741824, 0, 1073741824, int.MaxValue })
                writer.Write(sample);
        });
        var result = WavCodec.Read(files.Write("pcm32", Wave(("fmt ", Format(1, 1, 48000, 32)), ("data", data))));
        SamplesNear([-1, -0.5f, 0, 0.5f, 1], result.Samples);
    }

    private static void CheckFloat(FixtureStore files)
    {
        float[] expected = [-1.25f, -0.5f, 0, 0.5f, 1.25f];
        var bytes = Wave(("fmt ", Format(3, 1, 44100, 32)), ("data", FloatData(expected)));
        SamplesNear(expected, WavCodec.Read(files.Write("float32", bytes)).Samples);
    }

    private static void CheckExtensiblePcm(FixtureStore files)
    {
        var format = ExtensibleFormat(1, 48000, 24, 20, 4, new Guid("00000001-0000-0010-8000-00aa00389b71"));
        var data = Pcm24Data([-8388608, 0, 4194304, 8388592]);
        var result = WavCodec.Read(files.Write("extensible-pcm", Wave(("fmt ", format), ("data", data))));
        SamplesNear([-1, 0, 0.5f, 8388592f / 8388608], result.Samples);
    }

    private static void CheckExtensibleFloat(FixtureStore files)
    {
        float[] expected = [-0.25f, 0.75f, 1.125f, -1.125f];
        var format = ExtensibleFormat(2, 48000, 32, 32, 3, new Guid("00000003-0000-0010-8000-00aa00389b71"));
        var result = WavCodec.Read(files.Write("extensible-float", Wave(("fmt ", format), ("data", FloatData(expected)))));
        Equal(2, result.Channels, "확장 형식의 채널 수");
        SamplesNear(expected, result.Samples);
    }

    private static void CheckOddChunk(FixtureStore files)
    {
        var bytes = Wave(("JUNK", [71, 29, 5]), ("fmt ", Format(1, 1, 8000, 8)),
            ("LIST", [4]), ("data", [128, 192, 64]), ("TAIL", [7]));
        SamplesNear([0, 0.5f, -0.5f], WavCodec.Read(files.Write("odd-chunks", bytes)).Samples);
    }

    private static void CheckDataBeforeFormat(FixtureStore files)
    {
        var bytes = Wave(("data", [128, 192]), ("fmt ", Format(1, 1, 8000, 8)));
        SamplesNear([0, 0.5f], WavCodec.Read(files.Write("data-first", bytes)).Samples);
    }

    private static void CheckTruncatedFile(FixtureStore files)
    {
        var bytes = Wave(("fmt ", Format(1, 1, 8000, 16)), ("data", [0, 0, 0, 1]));
        Array.Resize(ref bytes, bytes.Length - 1);
        RejectFile(files, "truncated", bytes);
    }

    private static void CheckOversizedChunk(FixtureStore files)
    {
        var bytes = Wave(("fmt ", Format(1, 1, 8000, 16)), ("data", [0, 0]));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(40), uint.MaxValue);
        RejectFile(files, "oversized-chunk", bytes);
    }

    private static void CheckMisalignedData(FixtureStore files) =>
        RejectFile(files, "misaligned", Wave(("fmt ", Format(1, 2, 8000, 16)), ("data", [0, 0, 0])));

    private static void CheckShortFormat(FixtureStore files) =>
        RejectFile(files, "short-format", Wave(("fmt ", new byte[15]), ("data", [0, 0])));

    private static void CheckInvalidBlockAlign(FixtureStore files)
    {
        var format = Format(1, 2, 48000, 16);
        BinaryPrimitives.WriteUInt16LittleEndian(format.AsSpan(12), 2);
        RejectFile(files, "bad-alignment", Wave(("fmt ", format), ("data", [0, 0, 0, 0])));
    }

    private static void CheckUnsupportedSubtype(FixtureStore files)
    {
        var format = ExtensibleFormat(1, 48000, 16, 16, 4, new Guid("00000002-0000-0010-8000-00aa00389b71"));
        RejectFile(files, "unsupported-subtype", Wave(("fmt ", format), ("data", [0, 0])));
    }

    private static void CheckEmptyData(FixtureStore files) =>
        RejectFile(files, "empty-data", Wave(("fmt ", Format(1, 1, 8000, 16)), ("data", [])));

    private static void CheckDuplicateData(FixtureStore files) =>
        RejectFile(files, "duplicate-data", Wave(("fmt ", Format(1, 1, 8000, 16)), ("data", [0, 0]), ("data", [0, 0])));

    private static void CheckNonFiniteFile(FixtureStore files, float value, string name) =>
        RejectFile(files, name, Wave(("fmt ", Format(3, 1, 48000, 32)), ("data", FloatData([0, value]))));

    private static void CheckTrim()
    {
        var source = new AudioClip("구간", 1000, 2, [0, 0.1f, 0.2f, 0.3f, 0.4f, 0.5f, 0.6f, 0.7f, 0.8f, 0.9f]);
        var result = AudioEditor.Render(source, new EditSettings(1, 4, 0, 0, 0));
        Equal(3, result.FrameCount, "선택된 프레임 수");
        SamplesNear([0.2f, 0.3f, 0.4f, 0.5f, 0.6f, 0.7f], result.Samples);
        True(result.Name == source.Name, "편집 후 소리 이름을 보존해야 합니다.");
        Equal(1000, result.SampleRate, "편집 후 샘플레이트");
    }

    private static void CheckGain()
    {
        var result = AudioEditor.Render(Mono([0.1f, -0.25f, 0.5f]), new EditSettings(0, 3, 6.020599913279624, 0, 0));
        SamplesNear([0.2f, -0.5f, 1], result.Samples);
    }

    private static void CheckFadeIn() =>
        SamplesNear([0, 0.25f, 0.5f, 0.75f, 1], AudioEditor.Render(Mono([1, 1, 1, 1, 1]), new EditSettings(0, 5, 0, 5, 0)).Samples);

    private static void CheckFadeOut() =>
        SamplesNear([1, 0.75f, 0.5f, 0.25f, 0], AudioEditor.Render(Mono([1, 1, 1, 1, 1]), new EditSettings(0, 5, 0, 0, 5)).Samples);

    private static void CheckOverlappingFades() =>
        SamplesNear([0, 0.1875f, 0.25f, 0.1875f, 0], AudioEditor.Render(Mono([1, 1, 1, 1, 1]), new EditSettings(0, 5, 0, 5, 5)).Samples);

    private static void CheckSingleFrameFade()
    {
        SamplesNear([0], AudioEditor.Render(Mono([0.5f]), new EditSettings(0, 1, 0, 1, 0)).Samples);
        SamplesNear([0], AudioEditor.Render(Mono([0.5f]), new EditSettings(0, 1, 0, 0, 1)).Samples);
        SamplesNear([0], AudioEditor.Render(Mono([0.5f]), new EditSettings(0, 1, 0, 1, 1)).Samples);
    }

    private static void CheckSubFrameFade() =>
        SamplesNear([0, 1, 0], AudioEditor.Render(Mono([1, 1, 1]), new EditSettings(0, 3, 0, 0.01, 0.01)).Samples);

    private static void CheckLongFade() =>
        SamplesNear([0, 0.25f, 0], AudioEditor.Render(Mono([1, 1, 1]), new EditSettings(0, 3, 0, double.MaxValue, double.MaxValue)).Samples);

    private static void CheckOriginalPreserved()
    {
        float[] original = [0.1f, 0.2f, 0.3f, 0.4f, 0.5f];
        var before = (float[])original.Clone();
        var source = Mono(original);
        var result = AudioEditor.Render(source, new EditSettings(1, 4, 6, 1, 1));
        True(!ReferenceEquals(original, result.Samples), "편집 결과는 별도 배열이어야 합니다.");
        SamplesNear(before, original, 0);
        result.Samples[1] = 99;
        SamplesNear(before, original, 0);
    }

    private static void CheckInternalClippingPreserved()
    {
        var result = AudioEditor.Render(Mono([-0.8f, 0.6f, 0]), new EditSettings(0, 3, 6.020599913279624, 0, 0));
        SamplesNear([-1.6f, 1.2f, 0], result.Samples);
        Near(1.6, AudioEditor.AnalyzePeak(result), 1e-6, "음량 초과의 최대값");
    }

    private static void CheckExportClamp()
    {
        var source = Mono([-2, -1, 0, 1, 2]);
        var encoded = WavCodec.EncodePcm16(source);
        short[] expected = [short.MinValue, short.MinValue, 0, short.MaxValue, short.MaxValue];
        for (var index = 0; index < expected.Length; index++)
            Equal(expected[index], BinaryPrimitives.ReadInt16LittleEndian(encoded.AsSpan(44 + index * 2)), $"출력 샘플 {index}");
        SamplesNear([-2, -1, 0, 1, 2], source.Samples, 0);
    }

    private static void CheckInvalidSettings()
    {
        var source = Mono([1, 1, 1]);
        EditSettings[] invalid =
        [
            new(-1, 2, 0, 0, 0), new(0, 4, 0, 0, 0), new(1, 1, 0, 0, 0), new(2, 1, 0, 0, 0),
            new(0, 3, double.NaN, 0, 0), new(0, 3, double.PositiveInfinity, 0, 0), new(0, 3, double.NegativeInfinity, 0, 0),
            new(0, 3, 0, -1, 0), new(0, 3, 0, 0, -1),
            new(0, 3, 0, double.NaN, 0), new(0, 3, 0, 0, double.NaN),
            new(0, 3, 0, double.PositiveInfinity, 0), new(0, 3, 0, 0, double.PositiveInfinity),
            new(0, 3, double.MaxValue, 0, 0)
        ];
        foreach (var settings in invalid)
            Throws<ArgumentException>(() => AudioEditor.Render(source, settings));
    }

    private static void CheckInvalidClips()
    {
        AudioClip[] invalid =
        [
            new("빈 소리", 1000, 1, []), new("0 채널", 1000, 0, [0]), new("3 채널", 1000, 3, [0, 0, 0]),
            new("0 샘플레이트", 0, 1, [0]), new("불완전한 프레임", 1000, 2, [0, 0, 0])
        ];
        foreach (var clip in invalid)
        {
            Throws<ArgumentException>(() => AudioEditor.AnalyzePeak(clip));
            Throws<ArgumentException>(() => WavCodec.EncodePcm16(clip));
        }
    }

    private static void CheckNonFiniteMemory()
    {
        foreach (var sample in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity })
        {
            var clip = Mono([0, sample]);
            Throws<ArgumentException>(() => AudioEditor.AnalyzePeak(clip));
            Throws<ArgumentException>(() => AudioEditor.Render(clip, new EditSettings(0, 1, 0, 0, 0)));
            Throws<ArgumentException>(() => WavCodec.EncodePcm16(clip));
        }
    }

    private static void CheckInvalidWritePreservesFile(FixtureStore files)
    {
        byte[] original = [17, 29, 43, 71];
        var path = files.Write("existing-target", original);
        Throws<ArgumentException>(() => WavCodec.WritePcm16(path, Mono([float.NaN])));
        True(original.SequenceEqual(File.ReadAllBytes(path)), "실패한 저장으로 기존 파일이 바뀌면 안 됩니다.");
    }

    private static AudioClip Mono(float[] samples) => new("검증 소리", 1000, 1, samples);

    private static void RejectFile(FixtureStore files, string name, byte[] bytes)
    {
        var path = files.Write(name, bytes);
        Throws<InvalidDataException>(() => WavCodec.Read(path));
    }

    private static byte[] Format(ushort tag, ushort channels, uint sampleRate, ushort bits) => Bytes(writer =>
    {
        writer.Write(tag);
        writer.Write(channels);
        writer.Write(sampleRate);
        writer.Write(sampleRate * channels * (bits / 8u));
        writer.Write((ushort)(channels * (bits / 8)));
        writer.Write(bits);
    });

    private static byte[] ExtensibleFormat(ushort channels, uint sampleRate, ushort bits, ushort validBits, uint mask, Guid subtype) => Bytes(writer =>
    {
        writer.Write(Format(0xfffe, channels, sampleRate, bits));
        writer.Write((ushort)22);
        writer.Write(validBits);
        writer.Write(mask);
        writer.Write(subtype.ToByteArray());
    });

    // 코덱과 독립적으로 파일을 만들어 저장과 읽기의 동일한 오류가 서로 가려지지 않도록 합니다.
    private static byte[] Wave(params (string Id, byte[] Data)[] chunks)
    {
        var payload = Bytes(writer =>
        {
            writer.Write(Encoding.ASCII.GetBytes("WAVE"));
            foreach (var (id, data) in chunks)
            {
                writer.Write(Encoding.ASCII.GetBytes(id));
                writer.Write((uint)data.Length);
                writer.Write(data);
                if (data.Length % 2 != 0)
                    writer.Write((byte)0);
            }
        });
        return Bytes(writer =>
        {
            writer.Write(Encoding.ASCII.GetBytes("RIFF"));
            writer.Write((uint)payload.Length);
            writer.Write(payload);
        });
    }

    private static byte[] Pcm24Data(int[] samples) => Bytes(writer =>
    {
        foreach (var sample in samples)
        {
            writer.Write((byte)(sample & 0xff));
            writer.Write((byte)((sample >> 8) & 0xff));
            writer.Write((byte)((sample >> 16) & 0xff));
        }
    });

    private static byte[] FloatData(float[] samples) => Bytes(writer =>
    {
        foreach (var sample in samples)
            writer.Write(sample);
    });

    private static byte[] Bytes(Action<BinaryWriter> write)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
        write(writer);
        writer.Flush();
        return stream.ToArray();
    }

    private static void SamplesNear(float[] expected, float[] actual, double tolerance = 1e-7)
    {
        Equal(expected.Length, actual.Length, "샘플 수");
        for (var index = 0; index < expected.Length; index++)
            Near(expected[index], actual[index], tolerance, $"샘플 {index}");
    }

    private static void Near(double expected, double actual, double tolerance, string label)
    {
        if (!double.IsFinite(actual) || Math.Abs(expected - actual) > tolerance)
            throw new InvalidOperationException($"{label}: 기대값 {expected}, 실제값 {actual}, 허용 오차 {tolerance}");
    }

    private static void Equal(int expected, int actual, string label)
    {
        if (expected != actual)
            throw new InvalidOperationException($"{label}: 기대값 {expected}, 실제값 {actual}");
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

    private sealed class FixtureStore
    {
        private readonly string root;

        internal FixtureStore(string path)
        {
            root = Path.GetFullPath(path);
            Directory.CreateDirectory(root);
        }

        internal string Write(string name, byte[] bytes)
        {
            var path = Path.Combine(root, name + ".wav");
            File.WriteAllBytes(path, bytes);
            return path;
        }
    }
}
