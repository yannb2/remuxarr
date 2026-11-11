using Muxarr.Core.Extensions;

namespace Muxarr.Core.Models;

// Desired output state, or delta of changes. Sibling of MediaSnapshot
// (observed state), not a subclass - fields are nullable with inherit-semantics.
// Serialized as JSON on MediaConversion.ConversionPlan.
public class ConversionPlan : IMedia<TrackPlan>
{
    public List<TrackPlan> Tracks { get; set; } = [];

    // null = inherit source. false = strip. true = preserve.
    public bool? HasChapters { get; set; }
    public bool? HasAttachments { get; set; }

    // Only meaningful for MP4-family containers; Matroska tools ignore it.
    public bool? Faststart { get; set; }

    // Container title (MKV segment title / MP4 title atom).
    // null = inherit source, "" = clear. Mirrors TrackPlan.Name semantics.
    public string? Title { get; set; }

    // Cut anything running past the video. A request, not a timestamp: both
    // writers get their cut at execution time, and it only rides an existing remux.
    public bool TrimToVideoLength { get; set; }

    // Chapter titles to write, matched to the source's chapters by start time.
    // null = leave chapters alone. Matroska only; ignored elsewhere.
    public List<ChapterInfo>? Chapters { get; set; }

    // The global tags the file should end up with: values changed, some removed,
    // some added. null = leave them alone. Matroska only; ignored elsewhere.
    public List<FileTag>? GlobalTags { get; set; }

    // Set while a conversion runs: the global tag XML the Matroska tools read.
    // "" = the file keeps no global tags. Never stored.
    [System.Text.Json.Serialization.JsonIgnore]
    public string? TagsFile { get; set; }

    // Set while a conversion runs: the chapter XML the Matroska tools read,
    // built from the source's own chapters with the new titles. Never stored.
    [System.Text.Json.Serialization.JsonIgnore]
    public string? ChaptersFile { get; set; }

    // Set while a conversion runs: SRT files made from SDH picture subtitles,
    // muxed in as extra tracks. Never stored.
    [System.Text.Json.Serialization.JsonIgnore]
    public List<AddedSubtitle>? AddedSubtitles { get; set; }

    // Subtitle tracks taken from other files (the Copy subtitles page). Stored
    // with the conversion; the converter rebuilds Output from the profile.
    public List<DonorTrack>? Donors { get; set; }

    // The extra inputs mkvmerge reads after the source, in file order: first
    // the SRTs made during the conversion, then the donor tracks.
    private List<(TrackPlan Plan, int BeforeIndex, string Order)> ExtraTracks()
    {
        var extras = new List<(TrackPlan, int, string)>();
        var added = AddedSubtitles ?? [];
        for (var n = 0; n < added.Count; n++)
        {
            extras.Add((added[n].ToTrackPlan(n + 1), added[n].BeforeIndex, $"{n + 1}:0"));
        }

        foreach (var donor in Donors ?? [])
        {
            if (donor.Output == null)
            {
                continue;
            }

            var number = extras.Count + 1;
            extras.Add((donor.ToTrackPlan(number), donor.BeforeIndex, $"{number}:{donor.TrackId}"));
        }

        return extras;
    }

    // The tracks the output should have, in order: the planned source tracks
    // with each added subtitle placed before the track it names. An added
    // track has Index -n, n being the number of the file mkvmerge reads it
    // from (the source is file 0).
    public List<TrackPlan> OutputTracks() => OrderedOutput().Select(x => x.Plan).ToList();

    // mkvmerge's --track-order for OutputTracks: "file:track" per output track.
    public List<string> TrackOrder() => OrderedOutput().Select(x => x.Order).ToList();

    private List<(TrackPlan Plan, string Order)> OrderedOutput()
    {
        var extras = ExtraTracks();
        if (extras.Count == 0)
        {
            return Tracks.Select(t => (t, $"0:{t.Index}")).ToList();
        }

        var placed = new HashSet<int>();
        var result = new List<(TrackPlan, string)>();
        foreach (var track in Tracks)
        {
            for (var n = 0; n < extras.Count; n++)
            {
                if (extras[n].BeforeIndex == track.Index && placed.Add(n))
                {
                    result.Add((extras[n].Plan, extras[n].Order));
                }
            }

            result.Add((track, $"0:{track.Index}"));
        }

        for (var n = 0; n < extras.Count; n++)
        {
            if (placed.Add(n))
            {
                result.Add((extras[n].Plan, extras[n].Order));
            }
        }

        return result;
    }

    bool IMedia<TrackPlan>.HasChapters => HasChapters ?? false;
    bool IMedia<TrackPlan>.HasAttachments => HasAttachments ?? false;
}

// A subtitle file muxed into the output next to the source's tracks.
// BeforeIndex: the source track it goes in front of; -1 puts it last.
public sealed record AddedSubtitle(string Path, string LanguageCode, string Name, int BeforeIndex)
{
    public TrackPlan ToTrackPlan(int fileNumber) => new()
    {
        Index = -fileNumber,
        Type = MediaTrackType.Subtitles,
        Name = Name,
        LanguageCode = LanguageCode,
        IsDefault = false,
        IsForced = false,
        IsHearingImpaired = false,
        IsVisualImpaired = false,
        IsCommentary = false
    };
}

// Desired state for a single track, or a delta of changes from source.
// null  = no opinion / inherit source
// value = desired value
// Name "" = explicit clear (distinct from null)
// NameLocked = planner must not rewrite (user-authored or profile override)
public class TrackPlan : IMediaTrack
{
    public int Index { get; set; }
    public MediaTrackType Type { get; set; }

    public string? Name { get; set; }
    public string? LanguageCode { get; set; }
    public bool? IsDefault { get; set; }
    public bool? IsForced { get; set; }
    public bool? IsHearingImpaired { get; set; }
    public bool? IsVisualImpaired { get; set; }
    public bool? IsCommentary { get; set; }
    public bool? IsOriginal { get; set; }
    public bool? IsDub { get; set; }

    public bool NameLocked { get; set; }

    // Explicit IMediaTrack impls — coerce nullable plan values to the non-null
    // observed-track shape. Plans carry no codec/channel/duration info.
    string IMediaTrack.LanguageCode => LanguageCode ?? string.Empty;
    string IMediaTrack.LanguageName => string.Empty;
    string IMediaTrack.Codec => string.Empty;
    int IMediaTrack.AudioChannels => 0;
    AudioFeature IMediaTrack.AudioFeature => AudioFeature.None;
    string? IMediaTrack.HdrFormat => null;
    string? IMediaTrack.DvProfile => null;
    string? IMediaTrack.DvLayer => null;
    string? IMediaTrack.Resolution => null;
    long IMediaTrack.DurationMs => 0;
    bool IMediaTrack.IsDefault => IsDefault ?? false;
    bool IMediaTrack.IsForced => IsForced ?? false;
    bool IMediaTrack.IsCommentary => IsCommentary ?? false;
    bool IMediaTrack.IsHearingImpaired => IsHearingImpaired ?? false;
    bool IMediaTrack.IsVisualImpaired => IsVisualImpaired ?? false;
    bool IMediaTrack.IsOriginal => IsOriginal ?? false;
    bool IMediaTrack.IsDub => IsDub ?? false;
}

// A subtitle track copied in from another file: an MKV (TrackId = its track
// number there) or a loose .srt/.ass/.sup (TrackId 0). The donor's timestamps
// are multiplied by StretchNumerator/StretchDenominator, then OffsetMs is
// added, as mkvmerge's --sync does.
public sealed class DonorTrack
{
    public string Path { get; set; } = string.Empty;
    public int TrackId { get; set; }
    public long OffsetMs { get; set; }
    public long StretchNumerator { get; set; } = 1;
    public long StretchDenominator { get; set; } = 1;

    // What the user set on the page; null keeps what the donor says.
    public string? LanguageCode { get; set; }
    public bool? IsForced { get; set; }
    public bool? IsHearingImpaired { get; set; }

    // Auto-sync: the donor is a retimed copy of a track (a loose .srt/.ass that
    // has lost the track's name and commentary flag), so those are carried here.
    public string? Name { get; set; }
    public bool? IsCommentary { get; set; }

    // Auto-sync: the file and track the retimed copy was made from, for the log.
    public string? SyncedFromPath { get; set; }
    public int? SyncedFromTrackId { get; set; }

    // Filled when the plan is built from the profile: the name, language and
    // flags the output track gets (all set, nothing inherited), and the source
    // track it goes in front of (-1 = last). Null Output = the profile drops it.
    public TrackPlan? Output { get; set; }
    public int BeforeIndex { get; set; } = -1;

    public bool IsStretched => StretchNumerator != StretchDenominator;

    // mkvmerge's --sync value, or null when the timing is left as it is.
    public string? SyncValue()
    {
        if (OffsetMs == 0 && !IsStretched)
        {
            return null;
        }

        return IsStretched
            ? $"{OffsetMs},{StretchNumerator}/{StretchDenominator}"
            : OffsetMs.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    public TrackPlan ToTrackPlan(int fileNumber)
    {
        var output = Output ?? new TrackPlan();
        return new TrackPlan
        {
            Index = -fileNumber,
            Type = MediaTrackType.Subtitles,
            Name = output.Name ?? string.Empty,
            LanguageCode = output.LanguageCode ?? LanguageCode ?? "und",
            IsDefault = output.IsDefault ?? false,
            IsForced = output.IsForced ?? false,
            IsHearingImpaired = output.IsHearingImpaired ?? false,
            IsVisualImpaired = output.IsVisualImpaired ?? false,
            IsCommentary = output.IsCommentary ?? false,
            IsOriginal = output.IsOriginal ?? false,
            NameLocked = true
        };
    }
}
