using System.Globalization;
using System.Text;
using Arrkit.Processes;
using Muxarr.Core.Extensions;
using Muxarr.Core.Models;
using Muxarr.Core.SubtitleCopy;
using Muxarr.Data.Entities;
using static Muxarr.Core.SubtitleCopy.SubtitleTiming;

namespace Muxarr.Web.Services.SubtitleCopy;

/// <summary>What Auto-sync is lined up with: a subtitle track of the library file, or its sound.</summary>
public enum SyncReference
{
    Subtitles,
    Audio
}

/// <summary>A retimed copy of one donor text track, made by alass, waiting to be copied in.</summary>
public sealed record SyncedSubtitle(
    string Path,
    string Codec,
    SubtitleSync.Report Report,
    Result? Score,
    string Note);

/// <summary>
/// Auto-sync: alass retimes a text subtitle from another release against the library
/// file. Unlike the shift and stretch, it finds where the two releases are cut
/// differently (an ad break, a longer scene) and moves each part on its own. The
/// result is a loose .srt/.ass kept in <see cref="Folder"/> until the conversion copies it in.
/// Picture subtitles (PGS, VobSub) cannot be cut up this way: they keep the shift.
/// </summary>
public static class AutoSync
{
    public static string Alass { get; set; } = "alass";
    public static string Ffprobe { get; set; } = "ffprobe";
    public static string Ffmpeg { get; set; } = "ffmpeg";

    /// <summary>Where the retimed copies wait for the queue: in /config, so a restart keeps them.</summary>
    public static string Folder { get; set; } = Directory.Exists("/config")
        ? "/config/subtitle-sync"
        : System.IO.Path.Combine(System.IO.Path.GetTempPath(), "remuxarr-subtitle-sync");

    /// <summary>Copies older than this are deleted when the page opens; their conversions are long done.</summary>
    public static readonly TimeSpan KeepFor = TimeSpan.FromDays(30);

    private static readonly TimeSpan SubtitleTimeout = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan AudioTimeout = TimeSpan.FromMinutes(30);

    public static bool CanSync(TrackKind kind) => !kind.Pictures;

    /// <summary>
    /// Retimes one text track of the donor against the library file. With subtitles as the
    /// reference, alass runs twice (with and without its frame-rate guess) and the copy whose
    /// captions meet the file's best is kept; with the audio, once, guessing the frame rate
    /// only when the timing check found a stretch.
    /// </summary>
    public static async Task<SyncedSubtitle> SyncAsync(MediaFile target, Assignment assignment, TrackSnapshot donorTrack,
        SyncReference wanted, Action<string>? status, CancellationToken token)
    {
        var donor = assignment.Donor;
        var work = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "remuxarr-sync", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            status?.Invoke("Reading the donor's subtitle...");
            var incorrect = await DonorTextAsync(donor, donorTrack, work, token);
            var extension = System.IO.Path.GetExtension(incorrect);

            var targetSubs = target.Snapshot.Tracks.Where(t => t.Type == MediaTrackType.Subtitles).ToList();
            var pair = wanted == SyncReference.Subtitles
                ? SubtitleCopyEngine.ReferencePair(donor, targetSubs, [donorTrack])
                : null;

            string reference;
            string against;
            List<long>? referenceStarts = null;
            if (pair is { } p)
            {
                status?.Invoke($"Reading the caption times of track {p.Target.Index} of the file (the whole file is read)...");
                var spans = await SpansAsync(target.Path, p.Target.Index, p.Target.Codec, token);
                if (spans.Count < 20)
                {
                    throw new InvalidOperationException(
                        $"Track {p.Target.Index} of the file has only {spans.Count} captions, too few to line up with: try the audio.");
                }

                reference = System.IO.Path.Combine(work, "reference.srt");
                await File.WriteAllTextAsync(reference, SubtitleSync.TimingSrt(spans), new UTF8Encoding(false), token);
                referenceStarts = spans.Select(s => s.StartMs).ToList();
                var language = string.IsNullOrWhiteSpace(p.Target.LanguageName) ? p.Target.LanguageCode : p.Target.LanguageName;
                against = $"{language} track {p.Target.Index}";
            }
            else
            {
                reference = target.Path;
                against = "the sound";
            }

            var tries = referenceStarts != null
                ? new[] { false, true }
                : [!assignment.Stretch.IsNone];
            SyncedSubtitle? best = null;
            string? bestFile = null;
            foreach (var guessFrameRate in tries)
            {
                status?.Invoke(referenceStarts != null
                    ? $"alass: lining up with {against}{(guessFrameRate ? ", trying frame rates" : "")}..."
                    : "alass: listening to the file's sound (a minute or two for a film)...");
                var output = System.IO.Path.Combine(work, $"synced-{(guessFrameRate ? "fps" : "plain")}{extension}");
                var arguments = new List<string> { "--encoding-inc", "utf-8" };
                if (referenceStarts != null)
                {
                    arguments.AddRange(["--encoding-ref", "utf-8"]);
                }

                if (!guessFrameRate)
                {
                    arguments.Add("--disable-fps-guessing");
                }

                arguments.AddRange([reference, incorrect, output]);
                var result = await ToolRunner.RunAsync(Alass, arguments,
                    referenceStarts != null ? SubtitleTimeout : AudioTimeout, cancellationToken: token);
                if (!result.Success || !File.Exists(output))
                {
                    throw new InvalidOperationException(result.NotStarted
                        ? "alass is not installed (it comes with the image)."
                        : result.TimedOut
                            ? "alass took too long."
                            : $"alass could not line it up: {LastWords(result)}");
                }

                var report = SubtitleSync.ParseAlass(result.Output + "\n" + result.Error);
                if (SubtitleSync.Implausible(report, target.Snapshot.DurationMs) is { } far)
                {
                    // Seen with the sound as the reference when the file is mostly music: no real cut moves lines this far.
                    throw new InvalidOperationException(
                        $"alass found no sensible fit (it moved lines by {far / 60_000.0:0.#} min): try the other reference, or Check and the Shift box.");
                }
                Result? score = null;
                if (referenceStarts != null)
                {
                    var starts = await CueReader.StartsAsync(output, 0, extension == ".srt" ? "subrip" : "ass", null, token);
                    score = Score(referenceStarts, starts);
                }

                var candidate = new SyncedSubtitle(output, extension == ".srt" ? "subrip" : "ass", report, score,
                    $"Lined up with {against}.");
                // The plain run wins a tie: a frame-rate change it did not need is the
                // guess alass most often gets wrong.
                if (best == null || (candidate.Score?.Matched ?? 0) > (best.Score?.Matched ?? 0) + 2)
                {
                    best = candidate;
                    bestFile = output;
                }
            }

            Directory.CreateDirectory(Folder);
            var stem = System.IO.Path.GetFileNameWithoutExtension(donor.Path);
            var kept = System.IO.Path.Combine(Folder,
                $"{Safe(stem)}.track{donorTrack.Index}.synced-{Guid.NewGuid().ToString("N")[..8]}{extension}");
            File.Move(bestFile!, kept);
            return best! with { Path = kept };
        }
        finally
        {
            try
            {
                Directory.Delete(work, true);
            }
            catch (IOException)
            {
                // Temp files: left for the OS.
            }
        }
    }

    /// <summary>Deletes copies older than <see cref="KeepFor"/>.</summary>
    public static void CleanUp()
    {
        try
        {
            if (!Directory.Exists(Folder))
            {
                return;
            }

            foreach (var file in Directory.EnumerateFiles(Folder))
            {
                if (DateTime.UtcNow - File.GetLastWriteTimeUtc(file) > KeepFor)
                {
                    File.Delete(file);
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Next time.
        }
    }

    /// <summary>
    /// The donor track as a UTF-8 text file alass can read: a loose .srt/.ass/.ssa as it is
    /// (in UTF-8), a track in a video taken out with ffmpeg (ASS kept as ASS so its styles
    /// stay, anything else as SRT).
    /// </summary>
    private static async Task<string> DonorTextAsync(DonorFile donor, TrackSnapshot track, string work, CancellationToken token)
    {
        if (donor.IsLoose)
        {
            var extension = System.IO.Path.GetExtension(donor.Path).ToLowerInvariant();
            if (extension is not (".srt" or ".ass" or ".ssa"))
            {
                throw new InvalidOperationException("Only text subtitles can be auto-synced: picture subtitles keep the shift.");
            }

            var copy = System.IO.Path.Combine(work, "donor" + (extension == ".ssa" ? ".ass" : extension));
            await File.WriteAllTextAsync(copy, SubtitleFiles.ReadText(donor.Path), new UTF8Encoding(false), token);
            return copy;
        }

        var ass = SubtitleCodecExtensions.ParseSubtitleCodec(track.Codec ?? "") == SubtitleCodec.Ass;
        var output = System.IO.Path.Combine(work, ass ? "donor.ass" : "donor.srt");
        var arguments = new List<string> { "-v", "error", "-nostdin", "-y", "-i", donor.Path, "-map", $"0:{track.Index}" };
        arguments.AddRange(ass ? ["-c:s", "copy", "-f", "ass"] : ["-c:s", "srt", "-f", "srt"]);
        arguments.Add(output);
        var result = await ToolRunner.RunAsync(Ffmpeg, arguments, TimeSpan.FromMinutes(15), cancellationToken: token);
        if (!result.Success || !File.Exists(output))
        {
            throw new InvalidOperationException(result.NotStarted
                ? "ffmpeg is not installed."
                : $"ffmpeg could not take out track {track.Index}: {result.LastLine}");
        }

        return output;
    }

    /// <summary>When each caption of a track is on screen, from ffprobe's packet list (the whole file).</summary>
    public static async Task<List<SubtitleSync.Span>> SpansAsync(string path, int streamIndex, string? codec, CancellationToken token)
    {
        var result = await ToolRunner.RunAsync(Ffprobe,
            ["-v", "error", "-select_streams", streamIndex.ToString(CultureInfo.InvariantCulture),
                "-show_entries", "packet=pts_time,duration_time,size", "-of", "csv=p=0", path],
            TimeSpan.FromMinutes(15), cancellationToken: token);
        if (!result.Success)
        {
            throw new InvalidOperationException(result.NotStarted
                ? "ffprobe is not installed."
                : $"ffprobe could not read the subtitles of {System.IO.Path.GetFileName(path)}: {result.LastLine}");
        }

        var packets = new List<SubtitleSync.Packet>();
        foreach (var line in result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            // ffprobe lists the fields in its own order: pts_time,duration_time,size.
            var parts = line.Split(',');
            if (parts.Length < 3
                || !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var pts)
                || !int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var size))
            {
                continue;
            }

            long? duration = double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
                ? (long)Math.Round(d * 1000)
                : null;
            packets.Add(new SubtitleSync.Packet((long)Math.Round(pts * 1000), duration, size));
        }

        return SubtitleSync.Spans(packets, CueReader.IsPgs(codec, path) || Arrkit.Subtitles.TrackSampler.IsPictures(codec));
    }

    private static string LastWords(ToolResult result)
    {
        var lines = (result.Error + "\n" + result.Output).Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return lines.LastOrDefault(l => l.StartsWith("error", StringComparison.OrdinalIgnoreCase))
               ?? lines.LastOrDefault(l => !l.Contains('%'))
               ?? $"exit code {result.ExitCode}";
    }

    private static string Safe(string name)
    {
        var invalid = System.IO.Path.GetInvalidFileNameChars();
        var safe = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
        return safe.Length > 80 ? safe[..80] : safe;
    }
}
