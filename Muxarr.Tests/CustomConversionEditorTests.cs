using Muxarr.Core.Extensions;
using Muxarr.Core.Language;
using Muxarr.Core.Models;
using Muxarr.Data.Entities;
using Muxarr.Web.Components.Shared.Modals;
using static Muxarr.Tests.TestData;

namespace Muxarr.Tests;

/// <summary>
/// The custom conversion modal's state machine: profile overlay, selection,
/// ordering, and what all of that turns into in the plan.
/// </summary>
[TestClass]
public class CustomConversionEditorTests
{
    // --- File title ---

    [TestMethod]
    public void FileTitle_UnchangedLeavesThePlanAlone()
    {
        var file = MakeFile("English", Video(0), Audio(1, "English"));
        file.Snapshot.Title = "Some.Release.2160p";
        var editor = new CustomConversionEditor(file);

        Assert.AreEqual("Some.Release.2160p", editor.FileTitle);
        Assert.IsNull(editor.Build().Title);
    }

    [TestMethod]
    public void FileTitle_EditedIsWritten()
    {
        var file = MakeFile("English", Video(0), Audio(1, "English"));
        file.Snapshot.Title = "Some.Release.2160p";
        var editor = new CustomConversionEditor(file) { FileTitle = "  The Thing (1982)  " };

        Assert.AreEqual("The Thing (1982)", editor.Build().Title);
    }

    [TestMethod]
    public void FileTitle_EmptiedIsAnExplicitClear()
    {
        var file = MakeFile("English", Video(0), Audio(1, "English"));
        file.Snapshot.Title = "Some.Release.2160p";
        var editor = new CustomConversionEditor(file) { FileTitle = "" };

        Assert.AreEqual("", editor.Build().Title);
    }

    [TestMethod]
    public void ApplyProfile_TakesTheProfileOrder_DroppedTracksFollowTheirType()
    {
        var audio = new TrackSettings
        {
            Enabled = true,
            ReorderStrategy = TrackReorderStrategy.MatchLanguagePriority,
            AllowedLanguages = [IsoLanguage.Find("Japanese"), IsoLanguage.Find("English")]
        };
        var profile = MakeProfile(audio: audio);
        var file = MakeFile("Japanese", Video(0),
            Audio(1, "English"),
            Audio(2, "German"),
            Audio(3, "Japanese"),
            Sub(4, "English"));
        file.Profile = profile;
        var editor = new CustomConversionEditor(file);

        editor.ApplyProfile(profile);

        CollectionAssert.AreEqual(new[] { 0, 3, 1, 2, 4 }, editor.Tracks.Select(t => t.Index).ToArray());
        Assert.IsFalse(editor.IsSelected(editor.Tracks[3]), "German is dropped and sits after the kept audio");
        CollectionAssert.AreEqual(new[] { 0, 3, 1, 4 }, editor.Build().Tracks.Select(t => t.Index).ToArray());
    }

    [TestMethod]
    public void ApplyProfile_KeepsWhatTheProfileKeepsAndTakesItsNames()
    {
        var profile = MakeProfile(audio: Standardizing("English"), subtitle: Standardizing("English"));
        var file = MakeFile("English", Video(0),
            Audio(1, "English", trackName: "Some release tag"),
            Audio(2, "French"),
            Sub(3, "Spanish"));
        file.Profile = profile;
        var editor = new CustomConversionEditor(file);

        editor.ApplyProfile(profile);

        CollectionAssert.AreEqual(new[] { 0, 1 }, editor.Build().Tracks.Select(t => t.Index).ToArray());
        Assert.AreEqual("English", editor.Tracks.First(t => t.Index == 1).Name);
    }

    // The resolver downgrades a tag MP4 cannot hold and leaves the variant in the
    // name. Read it back the way the next scan will, or the language dropdown
    // contradicts the name sitting next to it.
    [TestMethod]
    public void ApplyProfile_OnMp4_ShowsTheVariantItsNameWillCarry()
    {
        var profile = MakeProfile(audio: Standardizing("French"));
        var file = MakeFile("English", Video(0),
            Audio(1, "French (Canada)", languageCode: "fre", trackName: "VFQ"));
        file.Snapshot.ContainerType = "MP4/QuickTime";
        file.Profile = profile;
        var editor = new CustomConversionEditor(file);

        editor.ApplyProfile(profile);

        var track = editor.Tracks.First(t => t.Index == 1);
        Assert.AreEqual("French (Canada)", track.Name);
        Assert.AreEqual("French (Canada)", track.LanguageName, "the dropdown has to agree with the name");
    }

    // Names belong to whichever profile last wrote them. After Apply Profile that
    // is the applied one, not the file's own, or the pick leaves a name that
    // profile will not recognise and the rename comes back.
    [TestMethod]
    public void SetLanguage_FollowsTheProfileTheUserApplied()
    {
        var applied = MakeProfile(audio: Standardizing("French", "English"));
        var file = MakeFile("English", Video(0), Audio(1, "French", trackName: "Piste 1"));
        // Matroska on purpose: the tag carries the variant there, so regenerating
        // the standardized name is the only thing that can rewrite it.
        file.Snapshot.ContainerType = "Matroska";
        var editor = new CustomConversionEditor(file);

        editor.ApplyProfile(applied);
        Assert.AreEqual("French", editor.Tracks.First(t => t.Index == 1).Name);

        editor.SetLanguage(editor.Tracks.First(t => t.Index == 1), "French (Canada)");

        Assert.AreEqual("French (Canada)", editor.Tracks.First(t => t.Index == 1).Name);
    }

    [TestMethod]
    public void SetLanguage_OnVideo_ChangesOnlyTheLanguage()
    {
        var file = MakeFile("French", Video(0, "HEVC Dolby Vision Profile 7 FEL HDR10"), Audio(1, "French"));
        file.Snapshot.ContainerType = "Matroska";
        var editor = new CustomConversionEditor(file);
        var video = editor.Tracks.First(t => t.Type == MediaTrackType.Video);

        editor.SetLanguage(video, "French (Canada)");

        Assert.AreEqual("French (Canada)", video.LanguageName);
        Assert.AreEqual(IsoLanguage.Find("French (Canada)").WriteCode, video.LanguageCode);
        Assert.AreEqual("HEVC Dolby Vision Profile 7 FEL HDR10", video.Name);
        var plan = editor.Build().Tracks.Single(t => t.Type == MediaTrackType.Video);
        Assert.AreEqual(video.LanguageCode, plan.LanguageCode);
    }

    // Until one is applied, names belong to the profile the file is assigned to,
    // which is the one that re-plans it after the conversion. Not the profile the
    // page happens to be previewing.
    [TestMethod]
    public void SetLanguage_UsesTheFilesOwnProfileUntilOneIsApplied()
    {
        var file = MakeFile("English", Video(0), Audio(1, "French", trackName: "French"));
        file.Snapshot.ContainerType = "Matroska";
        file.Profile = MakeProfile(audio: Standardizing("French", "English"));
        var editor = new CustomConversionEditor(file);

        editor.SetLanguage(editor.Tracks.First(t => t.Index == 1), "French (Canada)");

        Assert.AreEqual("French (Canada)", editor.Tracks.First(t => t.Index == 1).Name);
    }

    // Track order is what makes a conversion a remux rather than a metadata edit,
    // so the plan has to come out in display order. Video is never the user's to
    // drop or move.
    [TestMethod]
    public void Move_ReordersThePlan_AndVideoStaysPut()
    {
        var file = MakeFile("English", Video(0), Audio(1, "English"), Audio(2, "French"));
        var editor = new CustomConversionEditor(file);
        var french = editor.Tracks.First(t => t.Index == 2);
        var video = editor.Tracks.First(t => t.Type == MediaTrackType.Video);

        editor.Move(french, -1);
        editor.ToggleTrack(video);

        CollectionAssert.AreEqual(new[] { 0, 2, 1 }, editor.Build().Tracks.Select(t => t.Index).ToArray());
        Assert.IsTrue(editor.IsSelected(video), "the video checkbox may not come unticked");
        Assert.IsFalse(editor.CanMoveUp(editor.Tracks[1]), "nothing may move above the video");
    }

    // --- Description box ---

    [TestMethod]
    public void Description_FillsTheProfileTemplate()
    {
        var audio = Standardizing("English");
        audio.TrackNameTemplate = "{language} - {codec} {channels}";
        audio.TrackNameOverrides[TrackFlag.Commentary] = "{language} - {codec} {channels} ({description})";
        var profile = MakeProfile(audio: audio);
        var file = MakeFile("English", Video(0),
            Audio(1, "English", codec: nameof(AudioCodec.Ac3), channels: 2, commentary: true, trackName: "English - AC-3 2.0 (Commentary)"));
        file.Profile = profile;
        var editor = new CustomConversionEditor(file);
        var track = editor.Tracks.Single(t => t.Type == MediaTrackType.Audio);

        Assert.AreEqual("Commentary", editor.DescriptionOf(track));

        editor.Describe(track, "Commentary by John Carpenter");
        Assert.AreEqual("English - AC-3 2.0 (Commentary by John Carpenter)", track.Name);
        Assert.AreEqual("Commentary by John Carpenter", editor.DescriptionOf(track));

        editor.Describe(track, "=My own name");
        Assert.AreEqual("My own name", track.Name);
    }

    [TestMethod]
    public void Description_WithoutTheTokenGoesInBrackets()
    {
        var audio = Standardizing("English");
        audio.TrackNameTemplate = "{language} - {codec} {channels}";
        var profile = MakeProfile(audio: audio);
        var file = MakeFile("English", Video(0), Audio(1, "English", codec: nameof(AudioCodec.Ac3), channels: 2));
        file.Profile = profile;
        var editor = new CustomConversionEditor(file);
        var track = editor.Tracks.Single(t => t.Type == MediaTrackType.Audio);

        editor.Describe(track, "Isolated score");

        Assert.AreEqual("English - AC-3 2.0 (Isolated score)", track.Name);
        Assert.AreEqual("Isolated score", editor.DescriptionOf(track));
    }

    [TestMethod]
    public void Description_HiddenWhenTheProfileDoesNotNameTracks()
    {
        var file = MakeFile("English", Video(0), Audio(1, "English"));
        file.Profile = MakeProfile();
        var editor = new CustomConversionEditor(file);

        Assert.IsNull(editor.DescriptionOf(editor.Tracks.Single(t => t.Type == MediaTrackType.Audio)));
        Assert.IsNull(editor.DescriptionOf(editor.Tracks.Single(t => t.Type == MediaTrackType.Video)));
    }

    [TestMethod]
    public void Description_EmptyForANameTheTemplateWouldDrop()
    {
        var audio = Standardizing("English");
        audio.TrackNameTemplate = "{language} - {codec} {channels}";
        var file = MakeFile("English", Video(0), Audio(1, "English", trackName: "Surround 5.1"));
        file.Profile = MakeProfile(audio: audio);
        var editor = new CustomConversionEditor(file);

        Assert.AreEqual("", editor.DescriptionOf(editor.Tracks.Single(t => t.Type == MediaTrackType.Audio)));
    }

    private static TrackSettings Standardizing(params string[] languages)
    {
        return new TrackSettings
        {
            Enabled = true,
            AllowedLanguages = languages.Select(l => new LanguagePreference(IsoLanguage.Find(l))).ToList(),
            StandardizeTrackNames = true,
            TrackNameTemplate = "{language}"
        };
    }
}
