namespace SoriLab.Core;

public sealed record AudioTrack(
    Guid Id,
    AudioClip Source,
    EditSettings Edit,
    double OffsetSeconds = 0,
    double PlaybackRate = 1,
    bool Reverse = false,
    bool Muted = false,
    bool Solo = false,
    string? SourcePath = null);
