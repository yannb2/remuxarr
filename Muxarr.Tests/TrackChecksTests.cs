using Muxarr.Core.Extensions;
using Muxarr.Core.Models;
using Muxarr.Data.Extensions;
using Muxarr.Web.Components.Shared.Modals;
using Muxarr.Web.Components.Shared.TrackChecks;
using static Muxarr.Tests.TestData;

namespace Muxarr.Tests;

/// <summary>
/// The track checks in the Tracks table: the "Commentary or AD?" hint, the flags set from a check,
/// and the release label box.
/// </summary>
[TestClass]
public class TrackChecksTests
{
    [TestMethod]
    public void AStereoLossyTrackAfterTheMultichannelOneIsUnsure()
    {
        var tracks = new[]
        {
            Video(0),
            Audio(1, "English", nameof(AudioCodec.TrueHd), channels: 8),
            Audio(2, "English", nameof(AudioCodec.Ac3), channels: 2),
            Audio(3, "English", nameof(AudioCodec.Ac3), channels: 2, commentary: true),
            Audio(4, "French", nameof(AudioCodec.Ac3), channels: 2),
            Audio(5, "English", nameof(AudioCodec.Flac), channels: 2)
        };

        var unsure = TrackQualityScorer.CommentaryOrAudioDescription(tracks);

        // 3 is already a commentary, 4 has no multichannel French before it, 5 is lossless.
        CollectionAssert.AreEquivalent(new[] { 2 }, unsure.ToArray());
    }

    [TestMethod]
    public void AStereoTrackFirstIsNotUnsure()
    {
        var tracks = new[]
        {
            Audio(1, "English", nameof(AudioCodec.Ac3), channels: 2),
            Audio(2, "English", nameof(AudioCodec.TrueHd), channels: 8)
        };

        Assert.IsEmpty(TrackQualityScorer.CommentaryOrAudioDescription(tracks));
    }

    [TestMethod]
    public void PresetsSetTheFlagsWhenTheModalOpens()
    {
        var file = MakeFile("English", Video(0), Audio(1, "English"), Audio(2, "English", channels: 2), Sub(3, "English"));
        var editor = new CustomConversionEditor(file);

        editor.ApplyPresets(new Dictionary<int, TrackFlagPreset>
        {
            [2] = new(VisualImpaired: true),
            [3] = new(HearingImpaired: true, Commentary: false)
        });

        var audio = editor.Tracks.Single(t => t.Index == 2);
        var sub = editor.Tracks.Single(t => t.Index == 3);
        Assert.IsTrue(audio.IsVisualImpaired);
        Assert.IsFalse(audio.IsCommentary, "a flag the preset leaves out stays as it was");
        Assert.IsTrue(sub.IsHearingImpaired);
        Assert.IsFalse(editor.Tracks.Single(t => t.Index == 1).IsVisualImpaired);
    }

    [TestMethod]
    public void TheCommentaryOrAdHintGoesOnceTheRowIsMarked()
    {
        var file = MakeFile("English", Video(0),
            Audio(1, "English", nameof(AudioCodec.TrueHd), channels: 8),
            Audio(2, "English", nameof(AudioCodec.Ac3), channels: 2),
            Audio(3, "English", nameof(AudioCodec.Ac3), channels: 2));
        using var checks = new TrackCheckSession(file);
        var editor = new CustomConversionEditor(file);
        var second = editor.Tracks.Single(t => t.Index == 2);
        var third = editor.Tracks.Single(t => t.Index == 3);

        Assert.IsTrue(TrackCheckResult.ShowUnsure(checks, second));
        Assert.IsTrue(TrackCheckResult.HasAnything(checks, second), "the hint needs a row under the track");
        Assert.IsFalse(TrackCheckResult.HasAnything(checks, editor.Tracks.Single(t => t.Index == 1)));

        editor.ApplyPresets(new Dictionary<int, TrackFlagPreset> { [2] = new(Commentary: true), [3] = new(VisualImpaired: true) });

        Assert.IsFalse(TrackCheckResult.ShowUnsure(checks, second));
        Assert.IsFalse(TrackCheckResult.ShowUnsure(checks, third));
        Assert.IsFalse(TrackCheckResult.HasAnything(checks, second));
    }

    [TestMethod]
    public async Task ClosingAResultEmptiesTheRowAndRedrawsTheTable()
    {
        var file = MakeFile("English", Video(0), Audio(1, "English"), Sub(2, "English"));
        using var checks = new TrackCheckSession(file);
        var redraws = 0;
        checks.Changed += () =>
        {
            redraws++;
            return Task.CompletedTask;
        };
        var state = checks.StateOf(2);
        state.Error = "No captions in the slices read: nothing to check.";
        Assert.IsTrue(state.HasResult);

        await checks.Update(() => state.Error = null);
        await checks.ToggleCompare(2, true);

        Assert.IsFalse(state.HasResult);
        Assert.IsTrue(checks.IsCompared(2));
        Assert.AreEqual(2, redraws, "the table draws again, so the row under the track and Side by side follow");
    }

    [TestMethod]
    public void TheCheckIsComparedWithTheFlagsInTheTable()
    {
        var file = MakeFile("English", Video(0), Audio(1, "English"), Sub(2, "English"));
        var editor = new CustomConversionEditor(file);
        var sub = editor.Tracks.Single(t => t.Index == 2);
        var guess = new Arrkit.Subtitles.TrackGuess(Sdh: true, Commentary: false, Reason: "sound descriptions");

        Assert.IsTrue(TrackCheckSession.FlagsDiffer(sub, guess));
        editor.ApplyPresets(new Dictionary<int, TrackFlagPreset> { [2] = new(HearingImpaired: guess.Sdh, Commentary: guess.Commentary) });
        Assert.IsFalse(TrackCheckSession.FlagsDiffer(sub, guess), "Use these flags makes the row match");
    }

    [TestMethod]
    public void TheReleaseLabelIsTheLabelTag()
    {
        var file = MakeFile("English", Video(0), Audio(1));
        file.Snapshot.ContainerType = "Matroska";
        var editor = new CustomConversionEditor(file);
        Assert.AreEqual("", editor.ReleaseLabel);

        editor.ReleaseLabel = "  Criterion ";

        Assert.AreEqual("Criterion", editor.ReleaseLabel);
        var tag = editor.Build().GlobalTags!.Single();
        Assert.AreEqual(CustomConversionEditor.ReleaseLabelTag, tag.Name);
        Assert.AreEqual("Criterion", tag.Value);

        editor.ReleaseLabel = "";
        Assert.IsNull(editor.Build().GlobalTags, "a label typed and cleared leaves nothing");
        Assert.AreEqual("", editor.Tags.Single(t => t.Name == CustomConversionEditor.ReleaseLabelTag).Value,
            "the LABEL row stays listed, empty");
    }

    [TestMethod]
    public void EmptyingTheReleaseLabelRemovesTheFilesTag()
    {
        var file = MakeFile("English", Video(0), Audio(1));
        file.Snapshot.ContainerType = "Matroska";
        file.Snapshot.GlobalTags = [new FileTag { Name = "DISTRIBUTED_BY", Value = "Arrow" }, new FileTag { Name = "RELEASE_GROUP", Value = "YANNRIP" }];
        var editor = new CustomConversionEditor(file);
        Assert.AreEqual("Arrow", editor.ReleaseLabel);

        editor.ReleaseLabel = "";

        var kept = editor.Build().GlobalTags!;
        Assert.AreEqual("RELEASE_GROUP", kept.Single().Name);

        // Typed again: the label goes to LABEL, the old DISTRIBUTED_BY stays gone.
        editor.ReleaseLabel = "Arrow Video";
        var tags = editor.Build().GlobalTags!;
        Assert.AreEqual("Arrow Video", tags.Single(t => t.Name == "LABEL").Value);
        Assert.IsFalse(tags.Any(t => t.Name == "DISTRIBUTED_BY"));
    }

    [TestMethod]
    public void AnOldDistributedByLabelMovesToLabelOnEdit()
    {
        var file = MakeFile("English", Video(0), Audio(1));
        file.Snapshot.ContainerType = "Matroska";
        file.Snapshot.GlobalTags = [new FileTag { Name = "DISTRIBUTED_BY", Value = "Criterion" }];
        var editor = new CustomConversionEditor(file);
        Assert.AreEqual("Criterion", editor.ReleaseLabel, "shown from the old tag");

        editor.ReleaseLabel = "Criterion";

        var tags = editor.Build().GlobalTags!;
        Assert.AreEqual("Criterion", tags.Single().Value);
        Assert.AreEqual("LABEL", tags.Single().Name);
    }
}
