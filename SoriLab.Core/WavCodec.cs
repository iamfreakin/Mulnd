using System.Text;

namespace SoriLab.Core;

public static class WavCodec
{
    private const long MaximumFileBytes = 64L * 1024 * 1024;
    private static readonly Guid PcmSubFormat = new("00000001-0000-0010-8000-00aa00389b71");
    private static readonly Guid FloatSubFormat = new("00000003-0000-0010-8000-00aa00389b71");

    public static AudioClip Read(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("불러올 WAV 파일을 선택해 주세요.", nameof(path));

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > MaximumFileBytes)
            throw new InvalidDataException("파일이 너무 큽니다. 64MB 이하의 WAV 파일을 선택해 주세요.");
        if (stream.Length < 12)
            throw new InvalidDataException("WAV 파일의 머리글이 없거나 손상되었습니다.");

        using var reader = new BinaryReader(stream, Encoding.ASCII, leaveOpen: true);
        if (ReadId(reader) != "RIFF")
            throw new InvalidDataException("표준 RIFF WAV 파일만 지원합니다.");
        var riffSize = reader.ReadUInt32();
        if (ReadId(reader) != "WAVE")
            throw new InvalidDataException("WAV 형식의 파일이 아닙니다.");
        var riffEnd = 8L + riffSize;
        if (riffSize < 4 || riffEnd > stream.Length)
            throw new InvalidDataException("WAV 파일의 크기 정보가 실제 데이터와 맞지 않습니다.");

        WaveFormat? format = null;
        long dataPosition = -1;
        uint dataLength = 0;

        while (stream.Position < riffEnd)
        {
            if (riffEnd - stream.Position < 8)
                throw new InvalidDataException("WAV 파일 끝에 손상된 데이터 조각이 있습니다.");
            var chunkId = ReadId(reader);
            var chunkLength = reader.ReadUInt32();
            var chunkStart = stream.Position;
            var chunkEnd = chunkStart + chunkLength;
            var paddedEnd = chunkEnd + (chunkLength & 1);
            if (paddedEnd > riffEnd)
                throw new InvalidDataException("WAV 데이터 조각이 파일의 범위를 벗어납니다.");

            if (chunkId == "fmt ")
            {
                if (format is not null)
                    throw new InvalidDataException("WAV 파일에 형식 정보가 중복되어 있습니다.");
                format = ReadFormat(reader, chunkLength);
            }
            else if (chunkId == "data")
            {
                if (dataPosition >= 0)
                    throw new InvalidDataException("소리 데이터가 여러 조각으로 나뉜 WAV는 지원하지 않습니다.");
                dataPosition = chunkStart;
                dataLength = chunkLength;
            }

            // 홀수 길이의 조각 뒤에는 내용 길이에 포함되지 않는 패딩 1바이트가 있습니다.
            stream.Position = paddedEnd;
        }

        if (format is null)
            throw new InvalidDataException("WAV 파일에 소리 형식 정보가 없습니다.");
        if (dataPosition < 0 || dataLength == 0)
            throw new InvalidDataException("WAV 파일에 재생할 소리 데이터가 없습니다.");
        if (dataLength % format.BlockAlign != 0)
            throw new InvalidDataException("WAV 소리 데이터의 길이가 채널 구성과 맞지 않습니다.");

        var sampleCount = (long)dataLength / (format.BitsPerSample / 8);
        if (sampleCount > AudioValidation.MaximumSamples)
            throw new InvalidDataException("소리 데이터가 너무 큽니다. 최대 3,200만 개의 샘플을 지원합니다.");

        stream.Position = dataPosition;
        var samples = new float[(int)sampleCount];
        for (var index = 0; index < samples.Length; index++)
        {
            var sample = format.IsFloat ? reader.ReadSingle() : ReadPcmSample(reader, format.BitsPerSample);
            if (!float.IsFinite(sample))
                throw new InvalidDataException("WAV 소리 데이터에 읽을 수 없는 수치가 있습니다.");
            samples[index] = sample;
        }

        return new AudioClip(Path.GetFileNameWithoutExtension(path), format.SampleRate, format.Channels, samples);
    }

    public static void WritePcm16(string path, AudioClip clip)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("저장할 WAV 파일의 위치를 선택해 주세요.", nameof(path));

        // 변환을 끝낸 다음 파일을 열어, 잘못된 소리 데이터 때문에 기존 파일이 비워지지 않도록 합니다.
        var bytes = EncodePcm16(clip);
        File.WriteAllBytes(path, bytes);
    }

    public static byte[] EncodePcm16(AudioClip clip)
    {
        AudioValidation.Validate(clip);
        var byteRate = (long)clip.SampleRate * clip.Channels * 2;
        if (byteRate > uint.MaxValue)
            throw new ArgumentException("샘플레이트가 WAV 저장 범위를 넘었습니다.", nameof(clip));

        var dataLength = checked(clip.Samples.Length * 2);
        using var stream = new MemoryStream(44 + dataLength);
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
        writer.Write("RIFF"u8);
        writer.Write((uint)(36 + dataLength));
        writer.Write("WAVE"u8);
        writer.Write("fmt "u8);
        writer.Write(16u);
        writer.Write((ushort)1);
        writer.Write((ushort)clip.Channels);
        writer.Write((uint)clip.SampleRate);
        writer.Write((uint)byteRate);
        writer.Write((ushort)(clip.Channels * 2));
        writer.Write((ushort)16);
        writer.Write("data"u8);
        writer.Write((uint)dataLength);

        foreach (var sample in clip.Samples)
        {
            AudioValidation.ValidateSample(sample);
            // 16비트 PCM의 양수 최댓값은 32767이므로 +1도 마지막에 한 번 더 제한합니다.
            var scaled = Math.Round(Math.Clamp((double)sample, -1, 1) * 32768, MidpointRounding.AwayFromZero);
            writer.Write((short)Math.Clamp(scaled, short.MinValue, short.MaxValue));
        }

        writer.Flush();
        return stream.ToArray();
    }

    private static WaveFormat ReadFormat(BinaryReader reader, uint length)
    {
        if (length < 16)
            throw new InvalidDataException("WAV 형식 정보가 너무 짧습니다.");

        var formatTag = reader.ReadUInt16();
        var channels = reader.ReadUInt16();
        var sampleRate = reader.ReadUInt32();
        var byteRate = reader.ReadUInt32();
        var blockAlign = reader.ReadUInt16();
        var bitsPerSample = reader.ReadUInt16();
        if (channels is not (1 or 2))
            throw new InvalidDataException("모노 또는 스테레오 WAV 파일만 지원합니다.");
        if (sampleRate == 0 || sampleRate > int.MaxValue)
            throw new InvalidDataException("WAV 샘플레이트가 올바르지 않습니다.");

        ushort extensionSize = 0;
        if (length > 16)
        {
            if (length < 18)
                throw new InvalidDataException("WAV 추가 형식 정보가 손상되었습니다.");
            extensionSize = reader.ReadUInt16();
            if (18L + extensionSize > length)
                throw new InvalidDataException("WAV 추가 형식 정보의 길이가 올바르지 않습니다.");
        }

        var isFloat = formatTag == 3;
        if (formatTag == 0xfffe)
        {
            if (extensionSize < 22 || length < 40)
                throw new InvalidDataException("확장 WAV 형식 정보가 손상되었습니다.");
            var validBits = reader.ReadUInt16();
            var channelMask = reader.ReadUInt32();
            var subFormat = new Guid(reader.ReadBytes(16));
            if (subFormat == PcmSubFormat)
                isFloat = false;
            else if (subFormat == FloatSubFormat)
                isFloat = true;
            else
                throw new InvalidDataException("PCM 또는 32비트 실수 형식의 확장 WAV만 지원합니다.");

            if (validBits == 0 || validBits > bitsPerSample || (isFloat && validBits != 32))
                throw new InvalidDataException("확장 WAV의 유효 비트 수가 올바르지 않습니다.");
            if (channelMask != 0 && System.Numerics.BitOperations.PopCount(channelMask) != channels)
                throw new InvalidDataException("확장 WAV의 스피커 정보와 채널 수가 맞지 않습니다.");
            // 정수 PCM의 유효 비트가 더 적으면 데이터는 왼쪽에 정렬되므로 컨테이너 크기로 정규화합니다.
        }
        else if (formatTag is not (1 or 3))
        {
            throw new InvalidDataException("압축 WAV는 지원하지 않습니다. PCM WAV로 변환해 주세요.");
        }

        if (isFloat && bitsPerSample != 32)
            throw new InvalidDataException("실수 형식 WAV는 32비트만 지원합니다.");
        if (!isFloat && bitsPerSample is not (8 or 16 or 24 or 32))
            throw new InvalidDataException("PCM WAV는 8·16·24·32비트만 지원합니다.");

        var expectedBlockAlign = channels * (bitsPerSample / 8);
        if (blockAlign != expectedBlockAlign || byteRate != (long)sampleRate * expectedBlockAlign)
            throw new InvalidDataException("WAV의 샘플 크기 또는 초당 데이터 크기가 올바르지 않습니다.");

        return new WaveFormat((int)sampleRate, channels, bitsPerSample, blockAlign, isFloat);
    }

    private static float ReadPcmSample(BinaryReader reader, int bitsPerSample)
    {
        return bitsPerSample switch
        {
            8 => (reader.ReadByte() - 128) / 128f,
            16 => reader.ReadInt16() / 32768f,
            24 => ReadInt24(reader) / 8388608f,
            32 => (float)(reader.ReadInt32() / 2147483648d),
            _ => throw new InvalidDataException("지원하지 않는 WAV 비트 수입니다.")
        };
    }

    private static int ReadInt24(BinaryReader reader)
    {
        var value = reader.ReadByte() | (reader.ReadByte() << 8) | (reader.ReadByte() << 16);
        return (value & 0x800000) != 0 ? value | unchecked((int)0xff000000) : value;
    }

    private static string ReadId(BinaryReader reader) => Encoding.ASCII.GetString(reader.ReadBytes(4));

    private sealed record WaveFormat(int SampleRate, int Channels, int BitsPerSample, int BlockAlign, bool IsFloat);
}
