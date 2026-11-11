using System.Text;
using System.Text.RegularExpressions;
using Arrkit.Processes;
using Muxarr.Core.Extensions;
using Muxarr.Core.Models;
using Muxarr.Core.SubtitleCopy;
using Muxarr.Data.Entities;
using Muxarr.Data.Extensions;
using Muxarr.Web.Services.SubtitleCopy;
using static Muxarr.Tests.TestData;

namespace Muxarr.Tests;

// Fork: Auto-sync on the Copy subtitles page (alass).
[TestClass]
public class AutoSyncTests
{
    [TestMethod]
    public void Spans_TextPacketsLastTheirDuration()
    {
        var spans = SubtitleSync.Spans(
        [
            new SubtitleSync.Packet(5_000, 1_500, 30),
            new SubtitleSync.Packet(1_000, 2_000, 20)
        ], pictures: false);

        CollectionAssert.AreEqual(new[] { new SubtitleSync.Span(1_000, 3_000), new SubtitleSync.Span(5_000, 6_500) }, spans);
    }

    [TestMethod]
    public void Spans_PictureCaptionsRunUntilTheClear()
    {
        // PGS: a show set (big), then a clear set (a few dozen bytes); back to back captions have no clear between.
        var spans = SubtitleSync.Spans(
        [
            new SubtitleSync.Packet(1_000, null, 4_000),
            new SubtitleSync.Packet(3_000, null, 30),
            new SubtitleSync.Packet(10_000, null, 5_000),
            new SubtitleSync.Packet(12_000, null, 5_000),
            new SubtitleSync.Packet(13_500, null, 30)
        ], pictures: true);

        CollectionAssert.AreEqual(new[]
        {
            new SubtitleSync.Span(1_000, 3_000), new SubtitleSync.Span(10_000, 12_000), new SubtitleSync.Span(12_000, 13_500)
        }, spans);
    }

    [TestMethod]
    public void TimingSrt_HasOnlyTheTimes()
    {
        var srt = SubtitleSync.TimingSrt([new SubtitleSync.Span(1_000, 2_500), new SubtitleSync.Span(3_723_004, 3_724_000)]);

        Assert.AreEqual("1\n00:00:01,000 --> 00:00:02,500\nx\n\n2\n01:02:03,004 --> 01:02:04,000\nx\n\n", srt);
    }

    private const string AlassOutput = """
        Guessing framerate ratio...
        6 / 6 [=====] 100.00 % 64.18/s
        info: 'reference file FPS/input file FPS' ratio is 23.976/25

        synchronizing 'inc.srt' to reference file 'ref.srt'...
        355 / 355 [=====] 100.00 % 4118.64/s

        shifted block of 150 subtitles with length 0:13:10.814 by -0:00:02.500
        shifted block of 20 subtitles with length 0:01:00.000 by -0:00:02.520
        shifted block of 150 subtitles with length 0:13:26.759 by 0:00:04.000
        shifted block of 100 subtitles with length 0:09:20.249 by -0:00:01.200
        """;

    [TestMethod]
    public void ParseAlass_ReadsThePartsAndTheFrameRate()
    {
        var report = SubtitleSync.ParseAlass(AlassOutput);

        Assert.AreEqual("25 to 23.976 fps", report.FrameRate);
        Assert.AreEqual(3, report.Blocks.Count, "parts 20 ms apart are one");
        Assert.AreEqual(170, report.Blocks[0].Count);
        Assert.AreEqual(-2_502, report.Blocks[0].OffsetMs);
        Assert.AreEqual(4_000, report.Blocks[1].OffsetMs);
        Assert.AreEqual(9 * 60_000 + 20_249, report.Blocks[2].LengthMs);
        Assert.AreEqual("Stretched 25 to 23.976 fps, then 3 parts moved apart (cuts or breaks): -2.502 s (14 min), +4.0 s (13 min), -1.2 s (9 min).",
            SubtitleSync.Describe(report));
    }

    [TestMethod]
    public void Describe_OnePart()
    {
        Assert.AreEqual("Shifted +2.5 s.", SubtitleSync.Describe(SubtitleSync.ParseAlass(
            "shifted block of 400 subtitles with length 0:40:00.000 by 0:00:02.500")));
        Assert.AreEqual("Left in place.", SubtitleSync.Describe(SubtitleSync.ParseAlass(
            "shifted block of 400 subtitles with length 0:40:00.000 by 0:00:00.000")));
    }

    [TestMethod]
    public void Implausible_RejectsMovesNoCutMakes()
    {
        var wild = SubtitleSync.ParseAlass("shifted block of 320 subtitles with length 0:22:16.696 by 0:22:23.912");
        var cut = SubtitleSync.ParseAlass("shifted block of 300 subtitles with length 1:40:00.000 by 0:12:00.000");

        Assert.IsNotNull(SubtitleSync.Implausible(wild, 22 * 60_000));
        Assert.IsNull(SubtitleSync.Implausible(cut, 120 * 60_000), "a 12 min longer cut of a 2 h film");
        Assert.IsNull(SubtitleSync.Implausible(SubtitleSync.ParseAlass(AlassOutput), 22 * 60_000));
    }

    [TestMethod]
    public void Score_CountsCaptionsThatMeet()
    {
        var target = Enumerable.Range(0, 20).Select(i => i * 5_000L).ToList();
        var good = target.Select(t => t + 100).ToList();
        var off = target.Select(t => t + 2_000).ToList();

        Assert.AreEqual(SubtitleTiming.Quality.Good, SubtitleTiming.Score(target, good).Quality);
        Assert.AreEqual(20, SubtitleTiming.Score(target, good).Matched);
        Assert.AreEqual(SubtitleTiming.Quality.NoMatch, SubtitleTiming.Score(target, off).Quality);
    }

    [TestMethod]
    public void Pick_TakesTheSyncedCopyWithTheTracksLanguageFlagsAndName()
    {
        var donor = new DonorFile
        {
            Path = "/d/show.s01e01.mkv",
            Snapshot = new MediaSnapshot { Tracks = [Sub(2, "French", trackName: "Français"), Sub(3, "French", codec: nameof(SubtitleCodec.Pgs))] }
        };
        var assignment = new Assignment { Donor = donor, OffsetMs = 1_200 };
        assignment.Synced[2] = new SyncedSubtitle("/config/subtitle-sync/show.track2.synced-1.srt", "subrip",
            new SubtitleSync.Report([], null), null, "");
        var row = new CopyRow { Target = MakeFile("English", Video(0)) };
        row.Assignments.Add(assignment);
        var kinds = donor.Subtitles.Select(t => SubtitleCopyEngine.KindOf(donor, t)).ToHashSet();

        var picked = SubtitleCopyEngine.Pick(row, kinds, new Dictionary<TrackKind, TrackKind>());

        Assert.AreEqual(2, picked.Count);
        var synced = picked[0].Donor;
        Assert.AreEqual("/config/subtitle-sync/show.track2.synced-1.srt", synced.Path);
        Assert.AreEqual(0, synced.TrackId);
        Assert.IsNull(synced.SyncValue(), "already in the file's times");
        Assert.AreEqual("Français", synced.Name);
        Assert.AreEqual(picked[0].Track.LanguageCode, synced.LanguageCode);
        Assert.AreEqual("/d/show.s01e01.mkv", synced.SyncedFromPath);
        Assert.AreEqual(2, synced.SyncedFromTrackId);
        Assert.AreEqual("1200", picked[1].Donor.SyncValue(), "the picture track keeps the shift");

        var probed = new TrackSnapshot { Index = 0, Type = MediaTrackType.Subtitles, Codec = "Srt", Name = "" };
        var snapshot = DonorTargets.DonorSnapshot(probed, synced);
        Assert.AreEqual("Français", snapshot.Name, "the retimed copy has no name of its own");
    }

    /// <summary>
    /// The real thing: a library MKV with an English SRT track, and a French SRT from a
    /// release cut differently (three parts, each off by its own amount). alass, with the
    /// file's caption times as the reference, puts every line back.
    /// </summary>
    [TestMethod]
    public async Task SyncAsync_PutsEveryPartBack()
    {
        var folder = Path.Combine(Path.GetTempPath(), "muxarr-autosync-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var savedFolder = AutoSync.Folder;
        AutoSync.Folder = Path.Combine(folder, "synced");
        try
        {
            var version = await ToolRunner.RunAsync(AutoSync.Alass, ["--version"], TimeSpan.FromSeconds(10));
            if (!version.Success)
            {
                Assert.Inconclusive("alass is not installed.");
            }

            var random = new Random(7);
            var cues = new List<(long Start, long End)>();
            long t = 5_000;
            for (var i = 0; i < 300; i++)
            {
                t += random.Next(1_500, 9_000);
                cues.Add((t, t + random.Next(900, 4_000)));
            }

            var english = Path.Combine(folder, "english.srt");
            await File.WriteAllTextAsync(english, Srt(cues, (_, _) => 0, "Line"));
            var video = Path.Combine(folder, "video.mkv");
            var made = await ToolRunner.RunAsync("ffmpeg", ["-v", "error", "-y", "-f", "lavfi", "-i", "testsrc=duration=2:size=64x48:rate=5",
                "-c:v", "mpeg4", video], TimeSpan.FromSeconds(60));
            if (!made.Success)
            {
                Assert.Inconclusive("ffmpeg could not make the test video: " + made.LastLine);
            }

            var target = Path.Combine(folder, "Show S01E01.mkv");
            var muxed = await ToolRunner.RunAsync("mkvmerge", ["-q", "-o", target, video, "--language", "0:eng", english], TimeSpan.FromSeconds(60));
            if (!muxed.Success && muxed.ExitCode != 1)
            {
                Assert.Inconclusive("mkvmerge could not make the test file: " + muxed.LastLine);
            }

            // The other release: 2.5 s late for the first 100 lines, 4 s early for the next 100, 1.2 s late after.
            var donorPath = Path.Combine(folder, "show.s01e01.fr.srt");
            await File.WriteAllTextAsync(donorPath, Srt(cues, (i, _) => i < 100 ? 2_500 : i < 200 ? -4_000 : 1_200, "Ligne"),
                Encoding.Latin1);

            var file = new MediaFile { Path = target };
            Assert.IsNotNull((await file.SetFileDataFromFFprobe()).Result);
            var donor = await SubtitleCopyEngine.ProbeAsync(donorPath);
            Assert.IsNull(donor.Error, donor.Error);
            var assignment = new Assignment { Donor = donor };

            var synced = await AutoSync.SyncAsync(file, assignment, donor.Subtitles[0], SyncReference.Subtitles, null, CancellationToken.None);

            Assert.IsTrue(File.Exists(synced.Path));
            StringAssert.StartsWith(synced.Path, AutoSync.Folder);
            Assert.AreEqual(3, synced.Report.Blocks.Count, SubtitleSync.Describe(synced.Report));
            Assert.AreEqual(SubtitleTiming.Quality.Good, synced.Score!.Quality);
            Assert.IsTrue(synced.Score.Matched >= 295, $"{synced.Score.Matched} of 300 meet");
            var starts = Regex.Matches(await File.ReadAllTextAsync(synced.Path), @"(\d\d):(\d\d):(\d\d),(\d\d\d) -->")
                .Select(m => long.Parse(m.Groups[1].Value) * 3_600_000 + long.Parse(m.Groups[2].Value) * 60_000
                             + long.Parse(m.Groups[3].Value) * 1000 + long.Parse(m.Groups[4].Value))
                .ToList();
            Assert.AreEqual(300, starts.Count);
            // A line or two where two parts meet may land on its neighbour's side of the cut.
            var off = Enumerable.Range(0, 300).Where(i => Math.Abs(starts[i] - cues[i].Start) > 50).ToList();
            Assert.IsTrue(off.Count <= 5, "off: " + string.Join(", ", off.Select(i => $"line {i} {starts[i] - cues[i].Start:+0;-0} ms")));

            StringAssert.Contains(await File.ReadAllTextAsync(synced.Path), "Ligne", "the text stays");
        }
        finally
        {
            AutoSync.Folder = savedFolder;
            Directory.Delete(folder, recursive: true);
        }
    }

    private static string Srt(List<(long Start, long End)> cues, Func<int, long, long> shift, string text)
    {
        var srt = new StringBuilder();
        for (var i = 0; i < cues.Count; i++)
        {
            var s = shift(i, cues[i].Start);
            srt.Append(i + 1).Append('\n')
                .Append(SubtitleSync.SrtTime(cues[i].Start + s)).Append(" --> ").Append(SubtitleSync.SrtTime(cues[i].End + s)).Append('\n')
                .Append(text).Append(' ').Append(i).Append(" é\n\n");
        }

        return srt.ToString();
    }
}
