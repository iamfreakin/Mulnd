namespace SoriLab.Core;

public static class AudioMixer
{
    public const int MaximumTracks = 32;
    public const int MaximumClips = 128;
    public const int MaximumSources = 32;
    public const double MaximumDurationSeconds = 120;
    private const int PhaseCount = 1024;
    private const int BaseTapCount = 32;

    public static AudioClip Render(
        IReadOnlyList<AudioTrack> tracks,
        int sampleRate = 48000,
        double masterGainDb = 0,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateCore(tracks, sampleRate, masterGainDb, cancellationToken);
        var audible = GetAudibleTracks(tracks);
        if (audible.Length == 0)
            throw new ArgumentException("재생할 소리가 없습니다. 트랙을 추가하거나 음소거·단독 재생 설정을 확인해 주세요.", nameof(tracks));

        var frameCount = audible.Max(track => GetOffsetFrames(track, sampleRate) + GetOutputFrames(track, sampleRate));
        var summed = new double[frameCount * 2];
        int[]? owners = null;
        foreach (var lane in audible.GroupBy(track => track.EffectiveLaneId))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var clips = lane.ToArray();
            if (clips.Length == 1)
            {
                AddClip(summed, clips[0], sampleRate, null, 0, cancellationToken);
                continue;
            }

            // 트랙마다 오디오 배열을 만들지 않고, 어느 클립이 앞에 있는지만 재사용 배열에 기록합니다.
            owners ??= new int[frameCount];
            Array.Fill(owners, -1);
            for (var index = 0; index < clips.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Array.Fill(owners, index, GetOffsetFrames(clips[index], sampleRate), GetOutputFrames(clips[index], sampleRate));
            }
            for (var index = 0; index < clips.Length; index++)
                AddClip(summed, clips[index], sampleRate, owners, index, cancellationToken);
        }

        var masterGain = Math.Pow(10, masterGainDb / 20);
        var result = new float[summed.Length];
        for (var sample = 0; sample < result.Length; sample++)
        {
            if ((sample & 8191) == 0)
                cancellationToken.ThrowIfCancellationRequested();
            result[sample] = ToFiniteFloat(summed[sample] * masterGain);
        }
        cancellationToken.ThrowIfCancellationRequested();
        return new AudioClip("조합한 소리", sampleRate, 2, result);
    }

    // 개별 결과에는 배치 전 무음과 음소거·단독 재생·트랙 음량을 적용하지 않습니다.
    // 분할용 소리에 트랙 음량을 구워 넣으면 조합할 때 중복 적용되므로 클립 효과만 렌더합니다.
    public static AudioClip RenderTrack(AudioTrack track, int sampleRate = 48000, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateSampleRate(sampleRate);
        ValidateTrack(track);
        ValidateSamples(track.Source, cancellationToken);
        ValidateOutputLength(GetDurationSecondsCore(track), GetOutputFrames(track, sampleRate));
        return RenderTrackCore(track, sampleRate, cancellationToken);
    }

    public static double GetDurationSeconds(AudioTrack track)
    {
        ValidateTrack(track);
        return GetDurationSecondsCore(track);
    }

    // 이어 붙이는 위치도 실제 출력 프레임을 따라야 마지막 샘플이 겹치거나 빠지지 않습니다.
    public static double GetEndSeconds(AudioTrack track, int sampleRate = 48000)
    {
        ValidateSampleRate(sampleRate);
        ValidateTrack(track);
        return ((long)GetOffsetFrames(track, sampleRate) + GetOutputFrames(track, sampleRate)) / (double)sampleRate;
    }

    // 빈 프로젝트와 모두 음소거된 프로젝트도 저장할 수 있어야 하므로 재생 가능 여부는 Render에서 판단합니다.
    public static void Validate(IReadOnlyList<AudioTrack> tracks, int sampleRate = 48000, double masterGainDb = 0) =>
        ValidateCore(tracks, sampleRate, masterGainDb, CancellationToken.None);

    private static void ValidateCore(IReadOnlyList<AudioTrack> tracks, int sampleRate, double masterGainDb, CancellationToken cancellationToken)
    {
        if (tracks is null)
            throw new ArgumentNullException(nameof(tracks), "트랙 목록이 없습니다.");
        if (tracks.Count > MaximumClips)
            throw new ArgumentException("프로젝트에는 최대 128개의 클립을 넣을 수 있습니다.", nameof(tracks));
        ValidateSampleRate(sampleRate);
        if (!double.IsFinite(masterGainDb) || masterGainDb is < -60 or > 12)
            throw new ArgumentException("전체 음량은 -60dB부터 +12dB 사이로 설정해 주세요.", nameof(masterGainDb));

        var sources = new HashSet<AudioClip>(ReferenceEqualityComparer.Instance);
        var identifiers = new HashSet<Guid>();
        var lanes = new Dictionary<Guid, (bool Muted, bool Solo, double TrackGainDb)>();
        long sourceSamples = 0;
        foreach (var track in tracks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ValidateTrack(track);
            if (!identifiers.Add(track.Id))
                throw new ArgumentException("같은 식별자를 가진 클립이 중복되어 있습니다.", nameof(tracks));
            var laneState = (track.Muted, track.Solo, track.TrackGainDb);
            if (lanes.TryGetValue(track.EffectiveLaneId, out var existingState) && existingState != laneState)
                throw new ArgumentException("같은 트랙의 클립은 음소거·단독 재생·트랙 음량 설정이 같아야 합니다.", nameof(tracks));
            lanes[track.EffectiveLaneId] = laneState;
            if (lanes.Count > MaximumTracks)
                throw new ArgumentException("프로젝트에는 최대 32개의 트랙을 넣을 수 있습니다.", nameof(tracks));
            if (sources.Add(track.Source))
            {
                if (sources.Count > MaximumSources)
                    throw new ArgumentException("프로젝트에는 서로 다른 원본을 최대 32개까지 넣을 수 있습니다.", nameof(tracks));
                sourceSamples += track.Source.Samples.Length;
                if (sourceSamples > AudioValidation.MaximumSamples)
                    throw new ArgumentException("프로젝트 원본 데이터가 너무 큽니다. 합계 3,200만 개의 샘플까지 지원합니다.", nameof(tracks));
                ValidateSamples(track.Source, cancellationToken);
            }
        }

        foreach (var track in GetAudibleTracks(tracks))
        {
            var duration = track.OffsetSeconds + GetDurationSecondsCore(track);
            var frames = (long)GetOffsetFrames(track, sampleRate) + GetOutputFrames(track, sampleRate);
            ValidateOutputLength(duration, frames);
        }
    }

    private static void ValidateTrack(AudioTrack track)
    {
        if (track is null)
            throw new ArgumentNullException(nameof(track), "트랙 정보가 없습니다.");
        AudioValidation.Validate(track.Source);
        if (track.Edit is null)
            throw new ArgumentException("트랙의 편집 설정이 없습니다.", nameof(track));
        if (track.Edit.StartFrame < 0 || track.Edit.EndFrame > track.Source.FrameCount || track.Edit.EndFrame <= track.Edit.StartFrame)
            throw new ArgumentException("트랙 선택 구간은 소리 안에 있어야 하며 끝이 시작보다 뒤여야 합니다.", nameof(track));
        if (!double.IsFinite(track.PlaybackRate) || track.PlaybackRate is < 0.25 or > 4)
            throw new ArgumentException("재생 속도는 0.25배부터 4배 사이로 설정해 주세요.", nameof(track));
        if (!double.IsFinite(track.OffsetSeconds) || track.OffsetSeconds is < 0 or > MaximumDurationSeconds)
            throw new ArgumentException("배치 위치는 0초부터 120초 사이로 설정해 주세요.", nameof(track));
        if (!double.IsFinite(track.TrackGainDb) || track.TrackGainDb is < -60 or > 12)
            throw new ArgumentException("트랙 음량은 -60dB부터 +12dB 사이로 설정해 주세요.", nameof(track));
        if (!double.IsFinite(track.Edit.GainDb) || !double.IsFinite(Math.Pow(10, track.Edit.GainDb / 20)))
            throw new ArgumentException("클립 음량이 올바르지 않거나 처리 범위를 넘었습니다.", nameof(track));
        if (!double.IsFinite(track.Edit.FadeInMs) || track.Edit.FadeInMs < 0 ||
            !double.IsFinite(track.Edit.FadeOutMs) || track.Edit.FadeOutMs < 0)
            throw new ArgumentException("페이드 길이에는 0 이상의 유한한 숫자를 입력해 주세요.", nameof(track));
    }

    private static void ValidateSampleRate(int sampleRate)
    {
        if (sampleRate <= 0)
            throw new ArgumentException("프로젝트 샘플레이트는 0보다 커야 합니다.", nameof(sampleRate));
    }

    private static void ValidateOutputLength(double duration, long frames)
    {
        if (duration > MaximumDurationSeconds + 1e-9)
            throw new ArgumentException("조합한 소리는 최대 120초까지 지원합니다. 구간이나 배치 위치를 줄여 주세요.");
        if (frames <= 0 || frames > AudioValidation.MaximumSamples / 2)
            throw new ArgumentException("조합 결과의 샘플 수가 너무 큽니다. 길이 또는 샘플레이트를 줄여 주세요.");
    }

    private static void ValidateSamples(AudioClip source, CancellationToken cancellationToken)
    {
        for (var sample = 0; sample < source.Samples.Length; sample++)
        {
            if ((sample & 8191) == 0)
                cancellationToken.ThrowIfCancellationRequested();
            AudioValidation.ValidateSample(source.Samples[sample]);
        }
    }

    private static AudioTrack[] GetAudibleTracks(IReadOnlyList<AudioTrack> tracks)
    {
        var hasSolo = tracks.Any(track => track.Solo);
        return tracks.Where(track => !track.Muted && (!hasSolo || track.Solo)).ToArray();
    }

    private static void AddClip(double[] summed, AudioTrack track, int sampleRate, int[]? owners, int ownerIndex, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var firstFrame = GetOffsetFrames(track, sampleRate);
        var frames = GetOutputFrames(track, sampleRate);
        if (owners is not null && Array.IndexOf(owners, ownerIndex, firstFrame, frames) < 0)
            return;
        var rendered = RenderTrackCore(track, sampleRate, cancellationToken);
        // 클립 효과가 끝난 뒤 트랙 음량을 한 번 적용하고, 전체 음량은 모든 트랙을 합친 뒤 적용합니다.
        var trackGain = Math.Pow(10, track.TrackGainDb / 20);
        for (var frame = 0; frame < frames; frame++)
        {
            if ((frame & 4095) == 0)
                cancellationToken.ThrowIfCancellationRequested();
            var targetFrame = firstFrame + frame;
            // 앞 클립이 무음이어도 뒤의 소리를 통과시키지 않고 표시된 클립만 재생합니다.
            if (owners is not null && owners[targetFrame] != ownerIndex)
                continue;
            summed[targetFrame * 2] += rendered.Samples[frame * 2] * trackGain;
            summed[targetFrame * 2 + 1] += rendered.Samples[frame * 2 + 1] * trackGain;
        }
    }

    private static double GetDurationSecondsCore(AudioTrack track) =>
        (double)(track.Edit.EndFrame - track.Edit.StartFrame) / track.Source.SampleRate / track.PlaybackRate;

    private static int GetOutputFrames(AudioTrack track, int sampleRate)
    {
        var frames = Math.Ceiling(GetDurationSecondsCore(track) * sampleRate - 1e-9);
        if (!double.IsFinite(frames) || frames > AudioValidation.MaximumSamples / 2)
            throw new ArgumentException("트랙 출력이 너무 큽니다. 선택 구간을 줄여 주세요.");
        return Math.Max(1, (int)frames);
    }

    private static int GetOffsetFrames(AudioTrack track, int sampleRate)
    {
        var frames = Math.Round(track.OffsetSeconds * sampleRate, MidpointRounding.AwayFromZero);
        if (frames > AudioValidation.MaximumSamples / 2)
            throw new ArgumentException("배치 위치가 처리 범위를 넘었습니다. 위치를 앞당겨 주세요.");
        return (int)frames;
    }

    private static AudioClip RenderTrackCore(AudioTrack track, int sampleRate, CancellationToken cancellationToken)
    {
        var source = track.Source;
        var selectedFrames = track.Edit.EndFrame - track.Edit.StartFrame;
        var outputFrames = GetOutputFrames(track, sampleRate);
        var output = new float[outputFrames * 2];
        var step = (double)source.SampleRate / sampleRate * track.PlaybackRate;
        var identity = source.SampleRate == sampleRate && track.PlaybackRate == 1;
        var kernel = identity ? null : new SincKernel(step, cancellationToken);
        var gain = Math.Pow(10, track.Edit.GainDb / 20);
        var fadeInFrames = GetFadeFrames(track.Edit.FadeInMs, sampleRate, outputFrames);
        var fadeOutFrames = GetFadeFrames(track.Edit.FadeOutMs, sampleRate, outputFrames);

        for (var frame = 0; frame < outputFrames; frame++)
        {
            if ((frame & 2047) == 0)
                cancellationToken.ThrowIfCancellationRequested();
            double left;
            double right;
            if (identity)
            {
                var sourceFrame = track.Reverse ? track.Edit.EndFrame - 1 - frame : track.Edit.StartFrame + frame;
                left = source.Samples[sourceFrame * source.Channels];
                right = source.Channels == 1 ? left : source.Samples[sourceFrame * 2 + 1];
            }
            else
            {
                var position = frame * step;
                var anchor = (int)Math.Floor(position);
                var phase = (int)Math.Round((position - anchor) * PhaseCount);
                if (phase == PhaseCount)
                {
                    anchor++;
                    phase = 0;
                }
                left = 0;
                right = 0;
                var coefficients = kernel!.Coefficients;
                var coefficientOffset = phase * kernel.TapCount;
                for (var tap = 0; tap < kernel.TapCount; tap++)
                {
                    var relativeFrame = anchor + tap - kernel.LeftTaps;
                    // 선택 밖의 소리가 잘린 경계로 섞이지 않도록 경계 밖은 무음으로 보간합니다.
                    if (relativeFrame < 0 || relativeFrame >= selectedFrames)
                        continue;
                    var sourceFrame = track.Reverse ? track.Edit.EndFrame - 1 - relativeFrame : track.Edit.StartFrame + relativeFrame;
                    var coefficient = coefficients[coefficientOffset + tap];
                    left += source.Samples[sourceFrame * source.Channels] * coefficient;
                    if (source.Channels == 2)
                        right += source.Samples[sourceFrame * 2 + 1] * coefficient;
                }
                if (source.Channels == 1)
                    right = left;
            }

            // 길이가 바뀐 다음 출력 시간 기준으로 페이드를 적용합니다.
            var envelope = gain;
            if (frame < fadeInFrames)
                envelope *= fadeInFrames == 1 ? 0 : (double)frame / (fadeInFrames - 1);
            if (frame >= outputFrames - fadeOutFrames)
                envelope *= fadeOutFrames == 1 ? 0 : (double)(outputFrames - 1 - frame) / (fadeOutFrames - 1);
            output[frame * 2] = ToFiniteFloat(left * envelope);
            output[frame * 2 + 1] = ToFiniteFloat(right * envelope);
        }
        cancellationToken.ThrowIfCancellationRequested();
        return new AudioClip(source.Name, sampleRate, 2, output);
    }

    private static int GetFadeFrames(double milliseconds, int sampleRate, int outputFrames)
    {
        if (milliseconds == 0)
            return 0;
        if (milliseconds >= (double)outputFrames / sampleRate * 1000)
            return outputFrames;
        return Math.Clamp((int)Math.Round(milliseconds * sampleRate / 1000, MidpointRounding.AwayFromZero), 1, outputFrames);
    }

    private static float ToFiniteFloat(double value)
    {
        if (!double.IsFinite(value) || value > float.MaxValue || value < -float.MaxValue)
            throw new ArgumentException("조합한 음량이 처리 범위를 넘었습니다. 트랙 음량을 낮춰 주세요.");
        return (float)value;
    }

    private sealed class SincKernel
    {
        internal int TapCount { get; }
        internal int LeftTaps => TapCount / 2 - 1;
        internal double[] Coefficients { get; }

        internal SincKernel(double step, CancellationToken cancellationToken)
        {
            // 다운샘플링할 때 필터 폭도 늘려 높은 주파수가 낮은 주파수로 접히는 현상을 줄입니다.
            TapCount = BaseTapCount * (int)Math.Min(16, Math.Ceiling(Math.Max(1, step)));
            Coefficients = new double[TapCount * PhaseCount];
            var cutoff = step > 1 ? 0.95 / step : 1;
            var halfWidth = TapCount / 2d;
            for (var phase = 0; phase < PhaseCount; phase++)
            {
                if ((phase & 31) == 0)
                    cancellationToken.ThrowIfCancellationRequested();
                var fraction = (double)phase / PhaseCount;
                var sum = 0d;
                for (var tap = 0; tap < TapCount; tap++)
                {
                    var distance = tap - LeftTaps - fraction;
                    var value = 0d;
                    if (Math.Abs(distance) < halfWidth)
                    {
                        var scaled = Math.PI * cutoff * distance;
                        var sinc = Math.Abs(scaled) < 1e-12 ? 1 : Math.Sin(scaled) / scaled;
                        var window = 0.42 + 0.5 * Math.Cos(Math.PI * distance / halfWidth) + 0.08 * Math.Cos(2 * Math.PI * distance / halfWidth);
                        value = cutoff * sinc * window;
                    }
                    Coefficients[phase * TapCount + tap] = value;
                    sum += value;
                }
                for (var tap = 0; tap < TapCount; tap++)
                    Coefficients[phase * TapCount + tap] /= sum;
            }
        }
    }
}
