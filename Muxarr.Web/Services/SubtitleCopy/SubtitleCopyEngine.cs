using Muxarr.Core.Extensions;
using Muxarr.Core.Language;
using Muxarr.Core.Models;
using Muxarr.Core.SubtitleCopy;
using Muxarr.Data.Entities;
using Muxarr.Data.Extensions;
using static Muxarr.Core.SubtitleCopy.SubtitleTiming;

namespace Muxarr.Web.Services.SubtitleCopy;

/// <summary>A file subtitles can be taken from: a video, or a loose .srt/.ass/.sup.</summary>
public sealed class DonorFile
{
    public required string Path { get; init; }
    public string Name => System.IO.Path.GetFileName(Path);
    public EpisodeKey? Key { get; init; }
    public bool IsLoose => SubtitleFiles.IsLooseSubtitle(Path);
    public MediaSnapshot? Snapshot { get; set; }
    public string? Error { get; set; }

    /// <summary>Loose files only: the language and flags the name gives.</summary>
    public SubtitleFiles.NameInfo? NameInfo { get; init; }

    public long DurationMs => Snapshot?.DurationMs ?? 0;

    public List<TrackSnapshot> Subtitles =>
        Snapshot?.Tracks.Where(t => t.Type == MediaTrackType.Subtitles || IsLoose).ToList() ?? [];
}

/// <summary>
/// What kind of subtitle track something is, for the batch-wide track picks:
/// language, forced, SDH, commentary and picture or text.
/// </summary>
public sealed record TrackKind(string LanguageCode, bool Forced, bool Sdh, bool Commentary, bool Pictures, bool FromName = false)
{
    public string Label
    {
        get
        {
            var language = IsoLanguage.Find(LanguageCode);
            var name = language == IsoLanguage.Unknown ? (LanguageCode.Length > 0 ? LanguageCode : "Unknown language") : language.Name;
            var flags = new List<string>();
            if (Forced) flags.Add("forced");
            if (Sdh) flags.Add("SDH");
            if (Commentary) flags.Add("commentary");
            return $"{name}{(flags.Count > 0 ? $" ({string.Join(", ", flags)})" : "")} - {(Pictures ? "picture" : "text")}";
        }
    }
}

/// <summary>One donor file given to one library file, with the timing found or set for it.</summary>
public sealed class Assignment
{
    public required DonorFile Donor { get; init; }
    public long OffsetMs { get; set; }
    public Stretch Stretch { get; set; } = None;
    public Result? Timing { get; set; }
    public string? TimingNote { get; set; }

    /// <summary>The user changed the offset or stretch: a timing check leaves them alone.</summary>
    public bool Manual { get; set; }

    public bool Checking { get; set; }

    /// <summary>Auto-sync: the retimed copy of each text track, by the donor's track number.</summary>
    public Dictionary<int, SyncedSubtitle> Synced { get; } = [];

    /// <summary>Auto-sync failed for this donor: why.</summary>
    public string? SyncError { get; set; }

    public bool Syncing { get; set; }
}

/// <summary>One library episode and the donor files assigned to it.</summary>
public sealed class CopyRow
{
    public required MediaFile Target { get; init; }
    public EpisodeKey? Key { get; init; }

    /// <summary>A movie: donors are matched by title and year instead of episode.</summary>
    public bool IsMovie { get; init; }

    public string Label => Key?.Label ?? (IsMovie ? MovieLabel : "?");

    private string MovieLabel => System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(Target.Path)) ?? "Movie";
    public List<Assignment> Assignments { get; } = [];
    public bool Include { get; set; } = true;

    /// <summary>The dry run: the profile's plan with the donor tracks, and what each donor gives.</summary>
    public ConversionPlan? Plan { get; set; }
    public List<(DonorTrack Donor, TrackSnapshot Track, Assignment From)> Picked { get; set; } = [];
    public string? Problem { get; set; }

    public bool IsMatroska => Target.Snapshot.ContainerType.ToContainerFamily() == ContainerFamily.Matroska;
}

public static class SubtitleCopyEngine
{
    /// <summary>The donor files in a folder and below it, sorted by name.</summary>
    public static List<string> FindDonorFiles(string folder)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.System
        };

        return Directory.EnumerateFiles(folder, "*", options)
            .Where(SubtitleFiles.IsDonor)
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}.recycle{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .Where(p => !Path.GetFileNameWithoutExtension(p).EndsWith("sample", StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Probes one donor with the scanner's own ffprobe pass, so its tracks read like the library's.</summary>
    public static async Task<DonorFile> ProbeAsync(string path)
    {
        var donor = new DonorFile
        {
            Path = path,
            Key = EpisodeKey.Parse(path),
            NameInfo = SubtitleFiles.IsLooseSubtitle(path) ? SubtitleFiles.ParseName(path) : null
        };

        var file = new MediaFile { Path = path };
        try
        {
            var probe = await file.SetFileDataFromFFprobe();
            if (probe.Result == null)
            {
                donor.Error = "ffprobe could not read it.";
                return donor;
            }

            donor.Snapshot = file.Snapshot;
            if (donor.Subtitles.Count == 0)
            {
                donor.Error = "No subtitle tracks.";
            }
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            donor.Error = e.Message;
        }

        return donor;
    }

    /// <summary>The kind of a donor track, with a loose file's name taking the place of its (empty) tags.</summary>
    public static TrackKind KindOf(DonorFile donor, TrackSnapshot track)
    {
        var language = track.LanguageCode;
        var forced = track.IsForced;
        var sdh = track.IsHearingImpaired;
        if (donor.NameInfo is { } info)
        {
            language = info.LanguageCode ?? (string.IsNullOrEmpty(language) ? "und" : language);
            forced = forced || info.IsForced;
            sdh = sdh || info.IsHearingImpaired;
        }

        return new TrackKind(string.IsNullOrEmpty(language) ? "und" : language, forced, sdh, track.IsCommentary,
            CueReader.IsPgs(track.Codec, donor.Path) || Arrkit.Subtitles.TrackSampler.IsPictures(track.Codec), donor.IsLoose);
    }

    /// <summary>Gives each library episode the donors with the same episode in their name, and a movie those named after it.</summary>
    public static void Match(IReadOnlyList<CopyRow> rows, IReadOnlyList<DonorFile> donors)
    {
        foreach (var row in rows)
        {
            row.Assignments.Clear();
            if (row.IsMovie)
            {
                // A movie takes donors named after it: title, then year. Episodes never.
                var folder = System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(row.Target.Path)) ?? "";
                foreach (var donor in donors.Where(d => d.Key?.HasSeasonEpisode != true && MovieName.Matches(folder, d.Path)))
                {
                    row.Assignments.Add(new Assignment { Donor = donor });
                }

                continue;
            }

            if (row.Key == null)
            {
                continue;
            }

            foreach (var donor in donors.Where(d => d.Key != null && d.Key.SameEpisode(row.Key)))
            {
                row.Assignments.Add(new Assignment { Donor = donor });
            }
        }
    }

    /// <summary>
    /// The donor tracks the row takes: every subtitle track of each assigned donor whose
    /// kind is ticked, read through <paramref name="renamed"/> (a kind the user changed,
    /// for loose files whose names say too little).
    /// </summary>
    public static List<(DonorTrack Donor, TrackSnapshot Track, Assignment From)> Pick(CopyRow row,
        IReadOnlySet<TrackKind> wanted, IReadOnlyDictionary<TrackKind, TrackKind> renamed)
    {
        var picked = new List<(DonorTrack, TrackSnapshot, Assignment)>();
        foreach (var assignment in row.Assignments)
        {
            foreach (var track in assignment.Donor.Subtitles)
            {
                var kind = KindOf(assignment.Donor, track);
                if (!wanted.Contains(kind))
                {
                    continue;
                }

                var final = renamed.GetValueOrDefault(kind, kind);
                if (assignment.Synced.TryGetValue(track.Index, out var synced))
                {
                    // The retimed copy goes in instead: already in the file's times, and a
                    // loose file, so the language, flags and name are all set here.
                    picked.Add((new DonorTrack
                    {
                        Path = synced.Path,
                        TrackId = 0,
                        LanguageCode = final.LanguageCode,
                        IsForced = final.Forced,
                        IsHearingImpaired = final.Sdh,
                        IsCommentary = track.IsCommentary,
                        Name = track.Name ?? "",
                        SyncedFromPath = assignment.Donor.Path,
                        SyncedFromTrackId = track.Index
                    }, track, assignment));
                    continue;
                }

                picked.Add((new DonorTrack
                {
                    Path = assignment.Donor.Path,
                    TrackId = track.Index,
                    OffsetMs = assignment.OffsetMs,
                    StretchNumerator = assignment.Stretch.Numerator,
                    StretchDenominator = assignment.Stretch.Denominator,
                    LanguageCode = final.LanguageCode != track.LanguageCode ? final.LanguageCode : null,
                    IsForced = final.Forced != track.IsForced ? final.Forced : null,
                    IsHearingImpaired = final.Sdh != track.IsHearingImpaired ? final.Sdh : null
                }, track, assignment));
            }
        }

        return picked;
    }

    /// <summary>The dry run for one row: what the profile makes of the file with the picked tracks.</summary>
    public static void Plan(CopyRow row, IReadOnlySet<TrackKind> wanted, IReadOnlyDictionary<TrackKind, TrackKind> renamed)
    {
        row.Plan = null;
        row.Problem = null;
        row.Picked = [];
        if (!row.IsMatroska)
        {
            row.Problem = "Only MKV files can take subtitles.";
            return;
        }

        if (row.Assignments.Count == 0)
        {
            row.Problem = "No donor.";
            return;
        }

        row.Picked = Pick(row, wanted, renamed);
        if (row.Picked.Count == 0)
        {
            row.Problem = "The donor has none of the ticked tracks.";
            return;
        }

        row.Plan = row.Target.BuildTargetWithDonors(row.Target.Profile,
            row.Picked.Select(p => (p.Donor, p.Track)).ToList());
        if (row.Plan.Donors is not { Count: > 0 })
        {
            row.Problem = "The profile leaves out every picked track (language or duplicate rules).";
        }
    }

    private const long MaxOffsetMs = 30_000;
    private const int WindowCount = 8;
    private const long WindowMs = 90_000;

    /// <summary>
    /// Finds the stretch and the shift for one donor. MKV donors: the stretch from the
    /// two lengths, the shift from a subtitle track in the same language on both sides
    /// (or any full one on each), read in eight 90 s windows; chapters when the library
    /// file has no subtitles. Loose files: the whole file against the library's track,
    /// trying no stretch and both PAL stretches.
    /// </summary>
    public static async Task CheckTimingAsync(MediaFile target, Assignment assignment, CancellationToken token)
    {
        var donor = assignment.Donor;
        assignment.TimingNote = null;
        var targetSubs = target.Snapshot.Tracks.Where(t => t.Type == MediaTrackType.Subtitles).ToList();
        var donorSubs = donor.Subtitles;
        if (donorSubs.Count == 0)
        {
            assignment.Timing = null;
            assignment.TimingNote = donor.Error ?? "The donor has no subtitles.";
            return;
        }

        var pair = ReferencePair(donor, targetSubs, donorSubs);
        if (pair == null)
        {
            CheckWithChapters(target, assignment);
            return;
        }

        var (targetTrack, donorTrack) = pair.Value;
        var duration = target.Snapshot.DurationMs;
        var windows = Windows(duration);
        var targetStarts = await CueReader.StartsAsync(target.Path, targetTrack.Index, targetTrack.Codec, windows, token);
        if (targetStarts.Count == 0)
        {
            CheckWithChapters(target, assignment);
            return;
        }

        var stretches = donor.IsLoose
            ? new List<Stretch> { None, PalToFilm, FilmToPal }
            : [StretchFor(duration, donor.DurationMs)];

        Result? best = null;
        var bestStretch = None;
        List<long>? looseStarts = donor.IsLoose
            ? await CueReader.StartsAsync(donor.Path, donorTrack.Index, donorTrack.Codec, null, token)
            : null;
        foreach (var stretch in stretches)
        {
            var donorWindows = windows?.Select(w => new CueReader.Window(
                Math.Max(0, (long)((w.FromMs - MaxOffsetMs) / stretch.Factor)),
                (long)((w.ToMs + MaxOffsetMs) / stretch.Factor))).ToList();
            var raw = looseStarts ?? await CueReader.StartsAsync(donor.Path, donorTrack.Index, donorTrack.Codec, donorWindows, token);
            var scaled = raw.Select(ms => Apply(ms, stretch, 0))
                .Where(ms => windows == null || windows.Any(w => ms >= w.FromMs - MaxOffsetMs && ms <= w.ToMs + MaxOffsetMs))
                .ToList();
            var result = FindOffset(targetStarts, scaled, MaxOffsetMs);
            if (best == null || result.Matched > best.Matched)
            {
                best = result;
                bestStretch = stretch;
            }
        }

        assignment.Timing = best;
        assignment.Stretch = bestStretch;
        assignment.OffsetMs = best!.Quality == Quality.NoMatch ? 0 : best.OffsetMs;
        var sameLanguage = string.Equals(targetTrack.LanguageCode, KindOf(donor, donorTrack).LanguageCode, StringComparison.OrdinalIgnoreCase);
        var donorWhat = donor.IsLoose
            ? "the file"
            : $"{donorTrack.LanguageName.OrDefault(KindOf(donor, donorTrack).LanguageCode)} track {donorTrack.Index}";
        assignment.TimingNote = $"Lined up {donorWhat} with "
                                + $"{targetTrack.LanguageName.OrDefault(targetTrack.LanguageCode)} track {targetTrack.Index}"
                                + (sameLanguage ? "" : " (two languages, so fewer captions meet)")
                                + (best.Quality == Quality.NoMatch ? ". No shift lines them up: check by hand." : ".");
        if (!donor.IsLoose && bestStretch.IsNone && duration > 0 && donor.DurationMs > 0
            && Math.Abs(duration - donor.DurationMs) > 5_000)
        {
            assignment.TimingNote += $" The lengths differ by {Math.Abs(duration - donor.DurationMs) / 1000.0:0.#} s: maybe another cut.";
        }
    }

    // The tracks the check compares: the same language on both sides when there is one
    // (picture with picture first, then full before forced), else the first full one of each.
    public static (TrackSnapshot Target, TrackSnapshot Donor)? ReferencePair(DonorFile donor,
        List<TrackSnapshot> targetSubs, List<TrackSnapshot> donorSubs)
    {
        if (targetSubs.Count == 0)
        {
            return null;
        }

        var pairs =
            from t in targetSubs
            from d in donorSubs
            let dk = KindOf(donor, d)
            let tk = new TrackKind(t.LanguageCode, t.IsForced, t.IsHearingImpaired, t.IsCommentary, CueReader.IsPgs(t.Codec, ""))
            orderby string.Equals(t.LanguageCode, dk.LanguageCode, StringComparison.OrdinalIgnoreCase) descending,
                tk.Forced == dk.Forced && !dk.Forced descending,
                !tk.Forced descending,
                !dk.Forced descending,
                !tk.Commentary && !dk.Commentary descending,
                tk.Pictures == dk.Pictures descending,
                tk.Sdh == dk.Sdh descending
            select (t, d);
        return pairs.First();
    }

    // No subtitles to line up with: BD remuxes keep the disc's chapters, so the shift
    // between their starts (the first one, at 0, says nothing) stands in.
    private static void CheckWithChapters(MediaFile target, Assignment assignment)
    {
        var donor = assignment.Donor;
        var mine = target.Snapshot.Chapters.Where(c => c.StartMs > 0).ToList();
        var theirs = donor.Snapshot?.Chapters.Where(c => c.StartMs > 0).ToList() ?? [];
        var stretch = donor.IsLoose ? None : StretchFor(target.Snapshot.DurationMs, donor.DurationMs);
        assignment.Stretch = stretch;
        if (mine.Count < 3 || theirs.Count < 3)
        {
            assignment.Timing = null;
            assignment.OffsetMs = 0;
            assignment.TimingNote = "Nothing to line up with (no subtitles or chapters on both sides): set the shift by hand if needed.";
            return;
        }

        var result = FindOffset(mine.Select(c => c.StartMs).ToList(), theirs.Select(c => Apply(c.StartMs, stretch, 0)).ToList(), MaxOffsetMs);
        var quality = result.Matched >= 3 && result.Share >= 0.6 ? Quality.Good : result.Matched >= 2 ? Quality.Weak : Quality.NoMatch;
        assignment.Timing = result with { Quality = quality };
        assignment.OffsetMs = quality == Quality.NoMatch ? 0 : result.OffsetMs;
        assignment.TimingNote = "Lined up the chapters (the file has no subtitles to compare with).";
    }

    // Eight 90 s windows spread from 3 % to 92 % of the file; a short file is read whole.
    private static List<CueReader.Window>? Windows(long durationMs)
    {
        if (durationMs < WindowCount * WindowMs * 2)
        {
            return null;
        }

        var first = (long)(durationMs * 0.03);
        var last = (long)(durationMs * 0.92) - WindowMs;
        return Enumerable.Range(0, WindowCount)
            .Select(i => first + (last - first) * i / (WindowCount - 1))
            .Select(start => new CueReader.Window(start, start + WindowMs))
            .ToList();
    }

    /// <summary>One line of the preview: a caption of the library file and the donor's at the same time.</summary>
    public sealed record PreviewRow(long AtMs, CueReader.Caption? Mine, CueReader.Caption? Theirs);

    /// <summary>
    /// A minute of captions from a third of the way in: the library track the check used
    /// and the donor's track, both in the library file's times (the donor's after stretch
    /// and shift), paired when they start within 0.6 s of each other.
    /// </summary>
    public static async Task<List<PreviewRow>> PreviewAsync(MediaFile target, Assignment assignment, TrackSnapshot donorTrack,
        string workFolder, CancellationToken token)
    {
        var duration = Math.Max(target.Snapshot.DurationMs, 60_000);
        var from = duration / 3;
        var to = from + 60_000;
        var targetSubs = target.Snapshot.Tracks.Where(t => t.Type == MediaTrackType.Subtitles).ToList();
        var pair = ReferencePair(assignment.Donor, targetSubs, [donorTrack]);
        var mine = pair == null
            ? []
            : await CueReader.CaptionsAsync(target.Path, pair.Value.Target.Index, pair.Value.Target.Codec, from, to,
                Path.Combine(workFolder, "mine"), token);

        // An auto-synced track shows its retimed copy, which is in the file's times already.
        var synced = assignment.Synced.GetValueOrDefault(donorTrack.Index);
        var stretch = synced != null ? None : assignment.Stretch;
        var offset = synced != null ? 0 : assignment.OffsetMs;
        var donorFrom = Math.Max(0, (long)((from - offset) / stretch.Factor) - 5_000);
        var donorTo = (long)((to - offset) / stretch.Factor) + 5_000;
        var theirs = (await CueReader.CaptionsAsync(synced?.Path ?? assignment.Donor.Path, synced != null ? 0 : donorTrack.Index,
                synced?.Codec ?? donorTrack.Codec, donorFrom, donorTo, Path.Combine(workFolder, "theirs"), token))
            .Select(c => c with
            {
                StartMs = Apply(c.StartMs, stretch, offset),
                EndMs = Apply(c.EndMs, stretch, offset)
            })
            .Where(c => c.StartMs >= from && c.StartMs <= to)
            .ToList();

        var rows = new List<PreviewRow>();
        var used = new HashSet<CueReader.Caption>();
        foreach (var caption in mine)
        {
            var match = theirs.Where(t => !used.Contains(t) && Math.Abs(t.StartMs - caption.StartMs) <= 600)
                .OrderBy(t => Math.Abs(t.StartMs - caption.StartMs)).FirstOrDefault();
            if (match != null)
            {
                used.Add(match);
            }

            rows.Add(new PreviewRow(caption.StartMs, caption, match));
        }

        rows.AddRange(theirs.Where(t => !used.Contains(t)).Select(t => new PreviewRow(t.StartMs, null, t)));
        return rows.OrderBy(r => r.AtMs).ToList();
    }

    private static string OrDefault(this string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value;
}
