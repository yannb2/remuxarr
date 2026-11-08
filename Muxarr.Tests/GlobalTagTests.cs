using System.Xml.Linq;
using Muxarr.Core.Extensions;
using Muxarr.Core.FFmpeg;
using Muxarr.Core.MkvToolNix;
using Muxarr.Core.Models;
using Muxarr.Core.Utilities;
using Muxarr.Data.Entities;
using Muxarr.Data.Extensions;
using Muxarr.Web.Components.Shared.Modals;
using Muxarr.Web.Services;

namespace Muxarr.Tests;

/// <summary>
/// Global (file-level) tags: read from ffprobe, listed on the file page and kept
/// in the track name backup.
/// </summary>
[TestClass]
public class GlobalTagTests
{
    private static readonly DateTime Now = new(2026, 11, 3, 12, 0, 0, DateTimeKind.Utc);

    private static List<FileTag> Tags(params (string Name, string Value)[] tags)
    {
        return tags.Select(t => new FileTag { Name = t.Name, Value = t.Value }).ToList();
    }

    [TestMethod]
    public void FromFormatTags_KeepsTagsInOrder_AndSkipsContainerFields()
    {
        var format = new Dictionary<string, string>
        {
            ["title"] = "Movie (2020)",
            ["TMDB"] = "movie/603",
            ["encoder"] = "libebml v1.4.5 + libmatroska v1.7.1",
            ["creation_time"] = "2026-11-03T00:00:00.000000Z",
            ["RELEASE_NAME"] = "Movie.2020.2160p.UHD.BluRay.REMUX"
        };

        var tags = FileTag.FromFormatTags(format);

        CollectionAssert.AreEqual(new[] { "TMDB", "RELEASE_NAME" }, tags.Select(t => t.Name).ToArray());
        Assert.AreEqual("movie/603", tags[0].Value);
        Assert.AreEqual(0, FileTag.FromFormatTags(null).Count);
    }

    [TestMethod]
    public void TagsChanged_OnlyWhenTheFileHasDifferentTags()
    {
        var stored = Tags(("TMDB", "603"));

        Assert.IsFalse(TrackNameBackups.TagsChanged(stored, Tags(("TMDB", "603"))));
        Assert.IsFalse(TrackNameBackups.TagsChanged(stored, []), "a file without tags never clears the backup");
        Assert.IsFalse(TrackNameBackups.TagsChanged(stored, null));
        Assert.IsTrue(TrackNameBackups.TagsChanged(stored, Tags(("TMDB", "604"))));
        Assert.IsTrue(TrackNameBackups.TagsChanged(stored, Tags(("TMDB", "603"), ("IMDB", "tt0133093"))));
        Assert.IsTrue(TrackNameBackups.TagsChanged([], Tags(("TMDB", "603"))));
    }

    [TestMethod]
    public void Backup_KeepsTheLatestTags_AndSurvivesAFileThatLostThem()
    {
        var dir = Path.Combine(Path.GetTempPath(), "muxarr-tags-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "Movie (2020).mkv");
            File.WriteAllBytes(path, new byte[1000]);
            List<TrackSnapshot> tracks = [TestData.Video(0, "Video")];

            var first = TrackNameBackups.Sync(path, 1000, 7_200_000, tracks, null, null, Now,
                currentTags: Tags(("TMDB", "603")));
            Assert.AreEqual("603", first.GlobalTags.Single().Value);

            // A retag adds one.
            Assert.IsTrue(TrackNameBackups.NeedsSync(first, path, 1000, Now, currentTags: Tags(("TMDB", "603"), ("IMDB", "tt0133093"))));
            var retagged = TrackNameBackups.Sync(path, 1000, 7_200_000, tracks, null, first, Now,
                currentTags: Tags(("TMDB", "603"), ("IMDB", "tt0133093")));
            Assert.AreEqual(2, retagged.GlobalTags.Count);

            // A remux that dropped them keeps the backed-up set.
            var lost = TrackNameBackups.Sync(path, 900, 7_200_000, tracks, null, retagged, Now.AddDays(1),
                currentTags: []);
            Assert.AreEqual(2, lost.GlobalTags.Count);
            Assert.AreEqual(2, TrackNameBackups.Load(dir)!.Files.Single().GlobalTags.Count, "the sidecar has them too");
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    // The scan reads them through ffprobe, so a real MKV with mkvpropedit-written tags proves the path.
    [TestMethod]
    public async Task SetFileDataFromFFprobe_ReadsMkvGlobalTags()
    {
        var work = Path.Combine(Path.GetTempPath(), $"muxarr_tags_{Guid.NewGuid():N}.mkv");
        var xml = work + ".xml";
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "test.mkv"), work, true);
        File.WriteAllText(xml, """
            <?xml version="1.0" encoding="UTF-8"?>
            <Tags>
              <Tag>
                <Targets><TargetTypeValue>50</TargetTypeValue></Targets>
                <Simple><Name>TMDB</Name><String>movie/603</String></Simple>
                <Simple><Name>RELEASE_GROUP</Name><String>FraMeSToR</String></Simple>
              </Tag>
            </Tags>
            """);
        try
        {
            var edit = await ProcessExecutor.ExecuteProcessAsync("mkvpropedit", $"\"{work}\" --tags global:\"{xml}\"",
                TimeSpan.FromMinutes(1));
            Assert.AreEqual(0, edit.ExitCode, edit.Error);

            var file = new MediaFile { Path = work };
            await file.SetFileDataFromFFprobe();

            var tags = file.Snapshot.GlobalTags.ToDictionary(t => t.Name, t => t.Value);
            Assert.AreEqual("movie/603", tags["TMDB"]);
            Assert.AreEqual("FraMeSToR", tags["RELEASE_GROUP"]);
            Assert.IsFalse(tags.Keys.Any(k => k.Equals("title", StringComparison.OrdinalIgnoreCase)));
            Assert.IsFalse(tags.Keys.Any(k => k.Equals("encoder", StringComparison.OrdinalIgnoreCase)));
        }
        finally
        {
            foreach (var p in new[] { work, xml })
            {
                if (File.Exists(p))
                {
                    File.Delete(p);
                }
            }
        }
    }

    // --- Editing ---

    private const string ExtractedTags = """
        <?xml version="1.0"?>
        <Tags>
          <Tag>
            <Targets><TargetTypeValue>50</TargetTypeValue></Targets>
            <Simple><Name>TMDB</Name><String>movie/603</String></Simple>
            <Simple><Name>IMDB</Name><String>tt0133093</String></Simple>
            <Simple><Name>NOTE</Name><TagLanguage>fre</TagLanguage><String>Version française</String></Simple>
          </Tag>
          <Tag>
            <Targets><TrackUID>12345</TrackUID></Targets>
            <Simple><Name>BPS</Name><String>1000</String></Simple>
          </Tag>
        </Tags>
        """;

    [TestMethod]
    public void Edit_ChangesRemovesAndAdds_KeepingOnlyGlobalTags()
    {
        var edited = MkvGlobalTags.Edit(XDocument.Parse(ExtractedTags),
            Tags(("TMDB", "movie/604"), ("NOTE-fre", "VF"), ("EDITION", "Director's Cut")));

        var tags = edited.Root!.Elements("Tag").ToList();
        Assert.AreEqual(1, tags.Count, "track tags are not global and stay out of the global file");
        Assert.AreEqual("50", tags[0].Element("Targets")!.Element("TargetTypeValue")!.Value);

        var simples = tags[0].Elements("Simple").ToList();
        CollectionAssert.AreEqual(new[] { "TMDB", "NOTE", "EDITION" },
            simples.Select(e => e.Element("Name")!.Value).ToArray());
        Assert.AreEqual("movie/604", simples[0].Element("String")!.Value);
        Assert.AreEqual("VF", simples[1].Element("String")!.Value);
        Assert.AreEqual("fre", simples[1].Element("TagLanguage")!.Value, "the language stays");
        Assert.AreEqual("Director's Cut", simples[2].Element("String")!.Value);
    }

    [TestMethod]
    public void Edit_RemovingEverything_LeavesNoTagBlock()
    {
        var edited = MkvGlobalTags.Edit(XDocument.Parse(ExtractedTags), []);

        Assert.AreEqual(0, edited.Root!.Elements("Tag").Count());
    }

    [TestMethod]
    public void Edit_AddsABlockToAFileWithoutTags()
    {
        var edited = MkvGlobalTags.Edit(new XDocument(new XElement("Tags")), Tags(("TMDB", "movie/603")));

        var tag = edited.Root!.Elements("Tag").Single();
        Assert.AreEqual("50", tag.Element("Targets")!.Element("TargetTypeValue")!.Value);
        Assert.AreEqual("TMDB", tag.Element("Simple")!.Element("Name")!.Value);
    }

    private static MediaFile TaggedFile(string container = "Matroska")
    {
        var file = TestData.MakeFile("English", TestData.Video(0), TestData.Audio(1));
        file.Snapshot.ContainerType = container;
        file.Snapshot.GlobalTags = Tags(("TMDB", "movie/603"), ("IMDB", "tt0133093"));
        return file;
    }

    [TestMethod]
    public void Editor_UnchangedTagsLeaveThePlanAlone()
    {
        var editor = new CustomConversionEditor(TaggedFile());

        Assert.AreEqual(2, editor.Tags.Count(t => !t.IsSlot));
        Assert.IsNull(editor.Build().GlobalTags);
    }

    [TestMethod]
    public void Editor_ListsTheUsualTags_AndWritesOnlyTheFilledOnes()
    {
        var editor = new CustomConversionEditor(TaggedFile());

        CollectionAssert.AreEqual(CustomConversionEditor.DefaultTags.Select(d => d.Name).ToArray(),
            editor.Tags.Select(t => t.Name).ToArray(), "the usual tags, in order, the file's own values filled in");
        Assert.AreEqual("movie/603", editor.Tags.Single(t => t.Name == "TMDB").Value);
        var edition = editor.Tags.Single(t => t.Name == "EDITION");
        Assert.IsTrue(edition.IsEmptySlot);
        Assert.IsNull(editor.Build().GlobalTags, "empty slots change nothing");

        edition.Value = "Extended";

        CollectionAssert.AreEqual(new[] { "TMDB=movie/603", "IMDB=tt0133093", "EDITION=Extended" },
            editor.Build().GlobalTags!.Select(t => $"{t.Name}={t.Value}").ToArray());
    }

    [TestMethod]
    public void Editor_TheFilesOtherTagsFollowTheUsualOnes()
    {
        var file = TaggedFile();
        file.Snapshot.GlobalTags = Tags(("ENCODER_SETTINGS", "crf 18"), ("imdb", "tt0133093"));
        var editor = new CustomConversionEditor(file);

        Assert.AreEqual("tt0133093", editor.Tags.Single(t => t.Name == "imdb").Value, "matched whatever the case");
        Assert.AreEqual("ENCODER_SETTINGS", editor.Tags.Last().Name);
        Assert.AreEqual(CustomConversionEditor.DefaultTags.Count + 1, editor.Tags.Count);
    }

    [TestMethod]
    public void Editor_ChangedRemovedAndAddedTagsReachThePlan()
    {
        var editor = new CustomConversionEditor(TaggedFile());
        editor.Tags.Single(t => t.Name == "TMDB").Value = "movie/604";
        editor.Tags.Single(t => t.Name == "IMDB").Removed = true;
        editor.AddTag();
        editor.Tags[^1].Name = " EDITION ";
        editor.Tags[^1].Value = "Extended";

        var plan = editor.Build();

        Assert.IsNotNull(plan.GlobalTags);
        CollectionAssert.AreEqual(new[] { "TMDB=movie/604", "EDITION=Extended" },
            plan.GlobalTags.Select(t => $"{t.Name}={t.Value}").ToArray());
        Assert.AreEqual(ConversionPlanner.ConversionStrategy.MetadataEdit,
            ConversionPlanner.Plan(TaggedFile().Snapshot, plan).Strategy);
    }

    [TestMethod]
    public void Editor_BadNamesKeepTheTagsAsTheyAre()
    {
        var editor = new CustomConversionEditor(TaggedFile());
        editor.AddTag();
        editor.Tags[^1].Name = "title";

        Assert.IsNotNull(editor.TagProblem);
        Assert.IsNull(editor.Build().GlobalTags);

        editor.Tags[^1].Name = "TMDB";
        StringAssert.Contains(editor.TagProblem, "twice");
    }

    [TestMethod]
    public void Editor_BackedUpTagsCanBePutBack()
    {
        var file = TaggedFile();
        file.Snapshot.GlobalTags = Tags(("TMDB", "movie/603"));
        file.OriginalTrackNames = new TrackNameBackup { GlobalTags = Tags(("TMDB", "movie/603"), ("IMDB", "tt0133093")) };
        var editor = new CustomConversionEditor(file);

        editor.UseBackedUpTags();

        Assert.IsTrue(FileTag.SameSet(Tags(("TMDB", "movie/603"), ("IMDB", "tt0133093")), editor.Build().GlobalTags!));
    }

    [TestMethod]
    public void Planner_IgnoresTagsOutsideMatroska()
    {
        var file = TaggedFile("MP4/QuickTime");
        var editor = new CustomConversionEditor(file);

        Assert.IsFalse(editor.CanEditTags);
        var desired = editor.Build();
        desired.GlobalTags = Tags(("TMDB", "movie/604"));
        Assert.IsNull(ConversionPlanner.Plan(file.Snapshot, desired).Delta.GlobalTags);
    }

    // Both Matroska writers, on a real file: in place with mkvpropedit, and in a remux with mkvmerge.
    [TestMethod]
    public async Task EditedTags_AreWrittenByMkvPropEditAndMkvMerge()
    {
        var work = Path.Combine(Path.GetTempPath(), $"muxarr_tagedit_{Guid.NewGuid():N}.mkv");
        var remuxed = work + ".out.mkv";
        var xml = work + ".xml";
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "test.mkv"), work, true);
        File.WriteAllText(xml, """
            <?xml version="1.0" encoding="UTF-8"?>
            <Tags>
              <Tag>
                <Targets><TargetTypeValue>50</TargetTypeValue></Targets>
                <Simple><Name>TMDB</Name><String>movie/603</String></Simple>
                <Simple><Name>IMDB</Name><String>tt0133093</String></Simple>
              </Tag>
            </Tags>
            """);
        try
        {
            var seed = await ProcessExecutor.ExecuteProcessAsync("mkvpropedit", $"\"{work}\" --tags global:\"{xml}\"",
                TimeSpan.FromMinutes(1));
            Assert.AreEqual(0, seed.ExitCode, seed.Error);

            // In place: change one, drop one, add one.
            var wanted = Tags(("TMDB", "movie/604"), ("EDITION", "Extended"));
            var written = await MkvGlobalTags.WriteEdited(work, wanted, xml);
            Assert.IsNull(written.Error);
            var edit = await MkvPropEdit.Apply(work, work, new ConversionPlan { TagsFile = xml });
            Assert.AreEqual(0, edit.ExitCode, edit.Error);
            Assert.IsTrue(FileTag.SameSet(wanted, await ProbeTags(work)));

            // Remux: drop them all.
            var probe = await FFmpeg.GetStreamInfo(work);
            var tracks = probe.Result!.Streams
                .Where(s => s.CodecType is "video" or "audio" or "subtitle")
                .Select(s => new TrackPlan
                {
                    Index = s.Index,
                    Type = s.CodecType switch
                    {
                        "video" => MediaTrackType.Video,
                        "audio" => MediaTrackType.Audio,
                        _ => MediaTrackType.Subtitles
                    }
                })
                .ToList();
            var none = await MkvGlobalTags.WriteEdited(work, [], xml);
            Assert.IsFalse(none.HasTags);
            var remux = await MkvMerge.Remux(work, remuxed, new ConversionPlan { Tracks = tracks, TagsFile = "" });
            Assert.IsTrue(remux.ExitCode is 0 or 1, remux.Error);
            Assert.AreEqual(0, (await ProbeTags(remuxed)).Count);
        }
        finally
        {
            foreach (var p in new[] { work, remuxed, xml })
            {
                if (File.Exists(p))
                {
                    File.Delete(p);
                }
            }
        }
    }

    private static async Task<List<FileTag>> ProbeTags(string path)
    {
        var file = new MediaFile { Path = path };
        await file.SetFileDataFromFFprobe();
        return file.Snapshot.GlobalTags;
    }
}
