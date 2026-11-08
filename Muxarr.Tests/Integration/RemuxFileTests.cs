using Microsoft.EntityFrameworkCore;
using Muxarr.Core.Config;
using Muxarr.Core.Extensions;
using Muxarr.Core.Models;
using Muxarr.Data.Entities;
using Muxarr.Data.Extensions;
using Muxarr.Web.Services;

namespace Muxarr.Tests.Integration;

/// <summary>
/// Fork, Remux a file: a file outside every profile folder is remuxed into the clean
/// folder. The source must come out byte-identical and the library must not keep it.
/// </summary>
[TestClass]
public class RemuxFileTests : IntegrationTestBase
{
    private string _clean = null!;

    private async Task<Profile> SetUp()
    {
        _clean = Path.Combine(TempDir, "import", "clean");
        var profile = await Fixture.SeedProfile();
        // The library is a sub-folder, so files copied into TempDir are outside it.
        var library = Path.Combine(TempDir, "library");
        Directory.CreateDirectory(library);
        await Fixture.WithDbContext(async ctx =>
        {
            var row = await ctx.Profiles.FirstAsync(p => p.Id == profile.Id);
            row.Directories = [library];
            var config = ctx.Configs.GetOrDefault<ProcessingConfig>();
            config.CleanFolder = _clean;
            ctx.Configs.Set(config);
            await ctx.SaveChangesAsync();
            return 0;
        });
        profile.Directories = [library];
        return profile;
    }

    private Task<MediaConversion> LastConversion() =>
        Fixture.WithDbContext(ctx => ctx.MediaConversions.Include(c => c.AfterSnapshot)
            .OrderByDescending(c => c.Id).FirstAsync());

    [TestMethod]
    [DataRow("test_complex.mkv")]
    [DataRow("test_complex.mp4")]
    public async Task Profile_WritesTheCleanCopy_AndLeavesTheSourceAlone(string fixture)
    {
        var profile = await SetUp();
        var source = CopyFixture(fixture);
        var hash = FileAssertions.Sha256(source);

        var queued = await Fixture.Converter.AddOutsideFileToQueue(source, profile, "English");
        Assert.IsTrue(queued.Queued, queued.Error);
        await Fixture.Converter.RunAsync(CancellationToken.None);

        var conversion = await LastConversion();
        var output = Path.Combine(_clean, fixture);
        Assert.AreEqual(ConversionState.Completed, conversion.State,
            string.Join("\n", (await Fixture.WithDbContext(ctx => ctx.MediaConversions.Include(c => c.LogEntries)
                .FirstAsync(c => c.Id == conversion.Id))).LogEntries.Select(l => l.Message)));
        Assert.AreEqual(output, conversion.OutputPath);
        Assert.IsTrue(File.Exists(output), "the clean copy should be written");
        Assert.AreEqual(new FileInfo(output).Length, conversion.SizeAfter);
        Assert.IsNotNull(conversion.AfterSnapshot, "the clean copy's tracks are kept with the conversion");
        FileAssertions.AssertSha256Equals(source, hash, "the source must not be touched");
        FileAssertions.AssertNoStrayArtifacts(_clean, fixture);

        var rows = await Fixture.WithDbContext(ctx => ctx.MediaFiles.CountAsync());
        Assert.AreEqual(0, rows, "an outside file must not stay in the library");
    }

    [TestMethod]
    public async Task Custom_DropsATrack_InTheCopyOnly()
    {
        var profile = await SetUp();
        var source = CopyFixture("test_complex.mkv");
        var before = await FileAssertions.ProbeAsync(source);
        var dropped = before.Snapshot.Tracks.First(t => t.Type == MediaTrackType.Subtitles);
        var plan = before.BuildTargetFromCustom(before.Snapshot.Tracks.Where(t => t.Index != dropped.Index).ToSnapshots());

        var queued = await Fixture.Converter.AddOutsideFileToQueue(source, profile, null, plan);
        Assert.IsTrue(queued.Queued, queued.Error);
        await Fixture.Converter.RunAsync(CancellationToken.None);

        Assert.AreEqual(ConversionState.Completed, (await LastConversion()).State);
        var after = await FileAssertions.ProbeAsync(Path.Combine(_clean, "test_complex.mkv"));
        Assert.AreEqual(before.Snapshot.Tracks.Count - 1, after.Snapshot.Tracks.Count);
        Assert.AreEqual(before.Snapshot.Tracks.Count, (await FileAssertions.ProbeAsync(source)).Snapshot.Tracks.Count);
    }

    [TestMethod]
    public async Task NothingToChange_StillWritesTheCopy()
    {
        var profile = await SetUp();
        var source = CopyFixture("test_complex.mkv");
        var before = await FileAssertions.ProbeAsync(source);
        var plan = before.BuildTargetFromCustom(before.Snapshot.Tracks.ToSnapshots());

        var queued = await Fixture.Converter.AddOutsideFileToQueue(source, profile, null, plan);
        Assert.IsTrue(queued.Queued, queued.Error);
        await Fixture.Converter.RunAsync(CancellationToken.None);

        Assert.AreEqual(ConversionState.Completed, (await LastConversion()).State);
        var after = await FileAssertions.ProbeAsync(Path.Combine(_clean, "test_complex.mkv"));
        Assert.AreEqual(before.Snapshot.Tracks.Count, after.Snapshot.Tracks.Count);
    }

    [TestMethod]
    public async Task ExistingOutput_IsRefused_AndKept()
    {
        var profile = await SetUp();
        var source = CopyFixture("test_complex.mkv");
        Directory.CreateDirectory(_clean);
        var existing = Path.Combine(_clean, "test_complex.mkv");
        await File.WriteAllTextAsync(existing, "keep me");

        var queued = await Fixture.Converter.AddOutsideFileToQueue(source, profile, null);

        Assert.IsFalse(queued.Queued);
        StringAssert.Contains(queued.Error, "already exists");
        Assert.AreEqual("keep me", await File.ReadAllTextAsync(existing));
        Assert.AreEqual(0, await Fixture.WithDbContext(ctx => ctx.MediaConversions.CountAsync()));
    }

    [TestMethod]
    public async Task ScanPurge_KeepsAQueuedOutsideFile()
    {
        var profile = await SetUp();
        var source = CopyFixture("test_complex.mkv");
        Fixture.Converter.TogglePause();
        try
        {
            var queued = await Fixture.Converter.AddOutsideFileToQueue(source, profile, null);
            Assert.IsTrue(queued.Queued, queued.Error);

            await Fixture.Scanner.RunAsync(CancellationToken.None);

            Assert.AreEqual(1, await Fixture.WithDbContext(ctx => ctx.MediaFiles.CountAsync()),
                "the purge must not drop a file that is waiting in the queue");
        }
        finally
        {
            Fixture.Converter.TogglePause();
        }

        await Fixture.Converter.RunAsync(CancellationToken.None);
        Assert.AreEqual(ConversionState.Completed, (await LastConversion()).State);
        Assert.AreEqual(0, await Fixture.WithDbContext(ctx => ctx.MediaFiles.CountAsync()));
    }
}
