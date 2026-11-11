using Microsoft.EntityFrameworkCore;
using Muxarr.Core.Config;
using Muxarr.Core.Extensions;
using Muxarr.Core.FFmpeg;
using Muxarr.Core.Models;
using Muxarr.Core.MkvToolNix;
using Muxarr.Core.Utilities;
using Muxarr.Data;
using Muxarr.Data.Entities;
using Muxarr.Data.Extensions;
using Muxarr.Web.Services.Scheduler;
using Muxarr.Web.Services.SubtitleCopy;

namespace Muxarr.Web.Services;

// ReSharper disable once ClassNeverInstantiated.Global
public class MediaConverterService(
    IServiceScopeFactory serviceScopeFactory,
    MediaScannerService scanner,
    ArrUpdateService arrUpdater,
    ILogger<MediaConverterService> logger)
    : ConfigurableServiceBase<ProcessingConfig>(serviceScopeFactory, logger)
{
    private CancellationTokenSource? _currentConversionCts;
    private MediaConversion? _activeConversion;
    private bool _firstRun = true;
    private bool _workedSinceIdle;

    public override TimeSpan? Interval => TimeSpan.FromMinutes(60);

    public bool IsPaused { get; private set; }
    public event Action<ConverterProgressEvent>? ConverterStateChanged;
    public event Action? QueueStateChanged;
    public event Action? QueueFinished;

    public void TogglePause()
    {
        IsPaused = !IsPaused;
        logger.LogInformation("Conversion queue {State}", IsPaused ? "paused" : "resumed");
        QueueStateChanged?.Invoke();

        if (!IsPaused)
        {
            _ = RunAsync(CancellationToken.None);
        }
    }

    // The database only gets the progress when a run ends.
    public int? LiveProgress(int conversionId)
    {
        var active = _activeConversion;
        return active?.Id == conversionId ? active.Progress : null;
    }

    private volatile LiveStepState? _liveStep;

    /// <summary>The step the running conversion is on, and how far into it (null when the tool says nothing).</summary>
    public LiveStepState? LiveStep(int conversionId)
    {
        var step = _liveStep;
        return step?.ConversionId == conversionId ? step : null;
    }

    private void Step(MediaConversion conversion, string name, int? percent = null)
    {
        _liveStep = new LiveStepState(conversion.Id, name, percent);
        ConverterStateChanged?.Invoke(new ConverterProgressEvent(conversion));
    }

    private void StepPercent(MediaConversion conversion, int percent)
    {
        if (_liveStep is { } step && step.ConversionId == conversion.Id && step.Percent != percent)
        {
            _liveStep = step with { Percent = percent };
        }
    }

    public void CancelCurrentConversion()
    {
        try
        {
            if (_currentConversionCts is { IsCancellationRequested: false })
            {
                logger.LogInformation("Cancelling current conversion");
                _currentConversionCts.Cancel();
                MkvMerge.KillExistingProcesses();
                FFmpeg.KillExistingProcesses();
            }
        }
        catch (ObjectDisposedException)
        {
        }
    }

    public async Task CancelQueuedConversion(int conversionId)
    {
        using var scope = ServiceScopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var deleted = await context.MediaConversions
            .Where(x => x.Id == conversionId && x.State == ConversionState.New)
            .ExecuteDeleteAsync();

        if (deleted > 0)
        {
            logger.LogInformation("Removed queued conversion {Id}", conversionId);
        }
    }

    public async Task ClearQueue()
    {
        using var scope = ServiceScopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var deleted = await context.MediaConversions
            .Where(x => x.State == ConversionState.New)
            .ExecuteDeleteAsync();

        logger.LogInformation("Removed {Count} queued conversion(s)", deleted);
        QueueStateChanged?.Invoke();
    }

    /// <summary>
    ///     For now this task handles a single conversion per run and re-calls itself.
    ///     The base class handles the amount of threads using the semaphore.
    /// </summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        ReloadConfig();

        using var scope = ServiceScopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        if (_firstRun)
        {
            _firstRun = false;

            await CleanupLeftoverConversions(context, stoppingToken);
            await CleanupMuxbakFiles(context);
        }

        if (IsPaused)
        {
            return;
        }

        var conversion = await context.MediaConversions
            .Include(x => x.MediaFile)
            .ThenInclude(f => f!.Snapshot)
            .ThenInclude(s => s!.Tracks)
            .Include(x => x.MediaFile)
            .ThenInclude(x => x!.Profile)
            .Where(x => x.State == ConversionState.New)
            .FirstOrDefaultAsync(stoppingToken);

        if (conversion == null)
        {
            // Once per batch, so a media server can refresh a single time.
            if (_workedSinceIdle)
            {
                _workedSinceIdle = false;
                QueueFinished?.Invoke();
            }

            return;
        }

        _workedSinceIdle = true;
        _currentConversionCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        _activeConversion = conversion;
        try
        {
            await HandleConversion(conversion, context, scope, _currentConversionCts.Token);
            if (conversion.OutputPath != null)
            {
                await ForgetOutsideFile(conversion, context);
            }
        }
        finally
        {
            _activeConversion = null;
            _liveStep = null;
            _currentConversionCts.Dispose();
            _currentConversionCts = null;
        }

        // Keep running.
        if (!IsPaused)
        {
            _ = RunAsync(stoppingToken);
        }
    }

    public async Task<bool> AddMediaToQueue(MediaFile media, Profile? profileOverride = null)
    {
        if (!File.Exists(media.Path))
        {
            logger.LogWarning("Media file '{Path}' is not accessible. Cannot queue for conversion.", media.Path);
            return false;
        }

        await MediaLibraryLocks.QueueMutation.WaitAsync();
        try
        {
            return await AddMediaToQueueCore(media, profileOverride);
        }
        finally
        {
            MediaLibraryLocks.QueueMutation.Release();
        }
    }

    private async Task<bool> AddMediaToQueueCore(MediaFile media, Profile? profileOverride)
    {
        using var scope = ServiceScopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        if (!await context.MediaFiles.AnyAsync(x => x.Id == media.Id))
        {
            logger.LogWarning("Media file '{Path}' is no longer in the library. Cannot queue for conversion.", media.Path);
            return false;
        }

        var profile = profileOverride ?? media.Profile ?? context.Profiles.ToList().GetBestCandidate(media.Path);

        if (profile == null)
        {
            logger.LogWarning("Could not find a valid profile for {Path}", media.Path);
            return false;
        }

        if (profile.SkipHardlinkedFiles && HardLinkHelper.IsHardlinked(media.Path))
        {
            logger.LogInformation("Skipping hardlinked file: {Path}", media.Path);
            return false;
        }

        if (await HasActiveConversion(context, media.Id))
        {
            logger.LogInformation("File {Path} is already in the conversion queue", media.Path);
            return false;
        }

        var convert = new MediaConversion
        {
            MediaFileId = media.Id,
            SizeBefore = media.Size,
            ConversionPlan = media.BuildTargetFromProfile(profile),
            BeforeSnapshotId = media.SnapshotId,
            State = ConversionState.New,
            Name = media.GetName()
        };
        context.Add(convert);
        await context.SaveChangesAsync();

        _ = RunAsync(CancellationToken.None);
        return true;
    }

    public async Task<bool> AddMediaToQueue(MediaFile media, ConversionPlan customTarget)
    {
        await MediaLibraryLocks.QueueMutation.WaitAsync();
        try
        {
            using var scope = ServiceScopeFactory.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            if (await TryEnqueueCustom(context, media, customTarget, context.Profiles.ToList()) != QueueResult.Queued)
            {
                return false;
            }

            await context.SaveChangesAsync();
        }
        finally
        {
            MediaLibraryLocks.QueueMutation.Release();
        }

        _ = RunAsync(CancellationToken.None);
        return true;
    }

    /// <summary>
    ///     Queues a custom plan per file under a single lock, scope and save. The
    ///     per-file overload does all three each time, which does not scale to a
    ///     batch. Returns why each file was or was not queued, keyed by file id.
    /// </summary>
    public async Task<Dictionary<int, QueueResult>> AddCustomBatchToQueue(
        IReadOnlyList<(MediaFile File, ConversionPlan Plan)> items)
    {
        var results = new Dictionary<int, QueueResult>();
        if (items.Count == 0)
        {
            return results;
        }

        await MediaLibraryLocks.QueueMutation.WaitAsync();
        try
        {
            using var scope = ServiceScopeFactory.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var profiles = context.Profiles.ToList();

            // HasActiveConversion only sees saved rows, and nothing is saved
            // until the end, so a repeated file has to be caught here.
            foreach (var (media, plan) in items.DistinctBy(x => x.File.Id))
            {
                results[media.Id] = await TryEnqueueCustom(context, media, plan, profiles);
            }

            await context.SaveChangesAsync();
        }
        finally
        {
            MediaLibraryLocks.QueueMutation.Release();
        }

        if (results.Values.Any(r => r == QueueResult.Queued))
        {
            _ = RunAsync(CancellationToken.None);
        }

        return results;
    }

    /// <summary>
    ///     Copy subtitles: queues one profile conversion per file, each carrying the
    ///     donor tracks to add. The converter runs the profile again with them, so
    ///     the plan here only has to name the donors.
    /// </summary>
    public async Task<Dictionary<int, QueueResult>> AddDonorBatchToQueue(
        IReadOnlyList<(MediaFile File, List<DonorTrack> Donors)> items)
    {
        var results = new Dictionary<int, QueueResult>();
        await MediaLibraryLocks.QueueMutation.WaitAsync();
        try
        {
            using var scope = ServiceScopeFactory.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            foreach (var (media, donors) in items.DistinctBy(x => x.File.Id))
            {
                if (!File.Exists(media.Path))
                {
                    results[media.Id] = QueueResult.FileMissing;
                    continue;
                }

                if (!await context.MediaFiles.AnyAsync(x => x.Id == media.Id))
                {
                    results[media.Id] = QueueResult.NotInLibrary;
                    continue;
                }

                if (await HasActiveConversion(context, media.Id))
                {
                    results[media.Id] = QueueResult.AlreadyQueued;
                    continue;
                }

                context.Add(new MediaConversion
                {
                    MediaFileId = media.Id,
                    SizeBefore = media.Size,
                    ConversionPlan = new ConversionPlan
                    {
                        Tracks = media.Snapshot.Tracks.Select(t => t.ToTargetTrack(true)).ToList(),
                        Donors = donors
                    },
                    BeforeSnapshotId = media.SnapshotId,
                    State = ConversionState.New,
                    Name = media.GetName()
                });
                results[media.Id] = QueueResult.Queued;
            }

            await context.SaveChangesAsync();
        }
        finally
        {
            MediaLibraryLocks.QueueMutation.Release();
        }

        if (results.Values.Any(r => r == QueueResult.Queued))
        {
            _ = RunAsync(CancellationToken.None);
        }

        return results;
    }

    // Adds the conversion without saving so a batch can commit in one go.
    private async Task<QueueResult> TryEnqueueCustom(AppDbContext context, MediaFile media,
        ConversionPlan customTarget, List<Profile> profiles)
    {
        if (!File.Exists(media.Path))
        {
            logger.LogWarning("Media file '{Path}' is not accessible. Cannot queue for conversion.", media.Path);
            return QueueResult.FileMissing;
        }

        if (!await context.MediaFiles.AnyAsync(x => x.Id == media.Id))
        {
            logger.LogWarning("Media file '{Path}' is no longer in the library. Cannot queue for conversion.", media.Path);
            return QueueResult.NotInLibrary;
        }

        var profile = media.Profile ?? profiles.GetBestCandidate(media.Path);
        if (profile is { SkipHardlinkedFiles: true } && HardLinkHelper.IsHardlinked(media.Path))
        {
            logger.LogInformation("Skipping hardlinked file: {Path}", media.Path);
            return QueueResult.Hardlinked;
        }

        if (await HasActiveConversion(context, media.Id))
        {
            logger.LogInformation("File {Path} is already in the conversion queue", media.Path);
            return QueueResult.AlreadyQueued;
        }

        context.Add(new MediaConversion
        {
            MediaFileId = media.Id,
            SizeBefore = media.Size,
            ConversionPlan = customTarget,
            BeforeSnapshotId = media.SnapshotId,
            State = ConversionState.New,
            Name = media.GetName(),
            IsCustomConversion = true
        });

        return QueueResult.Queued;
    }

    /// <summary>
    ///     Fork, Remux a file: queues a file from outside the library (the folders in
    ///     Settings &gt; Processing, /downloads and /mnt/pc). The source is only read; the
    ///     result is written to the clean folder under the same name. The file gets a
    ///     library row for the run (the converter works on rows), dropped again at the end.
    /// </summary>
    public async Task<OutsideQueueResult> AddOutsideFileToQueue(string path, Profile profile,
        string? originalLanguage, ConversionPlan? customPlan = null)
    {
        if (!File.Exists(path))
        {
            return OutsideQueueResult.Fail($"'{path}' is not accessible. The file or its disk may be gone.");
        }

        await MediaLibraryLocks.QueueMutation.WaitAsync();
        try
        {
            using var scope = ServiceScopeFactory.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var output = OutsideOutputPath(context.Configs.GetOrDefault<ProcessingConfig>(), path);
            if (output == path)
            {
                return OutsideQueueResult.Fail("This file is already in the clean folder.");
            }

            if (File.Exists(output))
            {
                return OutsideQueueResult.Fail($"{output} already exists. Move or delete it first.");
            }

            if (await context.MediaConversions.AnyAsync(x => x.OutputPath == output &&
                                                             (x.State == ConversionState.New ||
                                                              x.State == ConversionState.Processing)))
            {
                return OutsideQueueResult.Fail($"A conversion that writes {output} is already queued.");
            }

            var profiles = await context.Profiles.ToListAsync();
            profile = profiles.FirstOrDefault(p => p.Id == profile.Id) ?? profile;
            var inLibrary = InLibrary(path, profiles);
            var dbFile = await context.MediaFiles.WithTracks().FirstOrDefaultAsync(x => x.Path == path);
            if (dbFile == null)
            {
                dbFile = new MediaFile { Path = path };
                context.Add(dbFile);
            }
            else if (await HasActiveConversion(context, dbFile.Id))
            {
                return OutsideQueueResult.Fail($"{Path.GetFileName(path)} is already in the conversion queue.");
            }

            // A library file keeps its own row as it is. An outside file has no Radarr or
            // Sonarr entry, so its original language is the one picked on the page.
            var scanProfile = profile;
            if (inLibrary && dbFile.Id != 0)
            {
                scanProfile = profiles.FirstOrDefault(p => p.Id == dbFile.ProfileId) ?? profile;
            }
            else
            {
                dbFile.ProfileId = profile.Id;
                dbFile.Profile = profile;
                dbFile.OriginalLanguage = string.IsNullOrWhiteSpace(originalLanguage) ? null : originalLanguage;
            }

            await scanner.ScanMediaFile(dbFile, true, context, scanProfile);
            if (dbFile.Snapshot is not { Tracks.Count: > 0 })
            {
                if (context.Entry(dbFile).State == EntityState.Added)
                {
                    context.Entry(dbFile).State = EntityState.Detached;
                }
                else if (!inLibrary)
                {
                    context.MediaFiles.Remove(dbFile);
                    await context.SaveChangesAsync();
                }

                return OutsideQueueResult.Fail($"Could not read the tracks of {Path.GetFileName(path)}.");
            }

            context.Add(new MediaConversion
            {
                MediaFileId = dbFile.Id,
                MediaFile = dbFile,
                SizeBefore = dbFile.Size,
                ConversionPlan = customPlan ?? dbFile.BuildTargetFromProfile(profile),
                BeforeSnapshotId = dbFile.SnapshotId,
                State = ConversionState.New,
                Name = Path.GetFileName(path),
                IsCustomConversion = customPlan != null,
                OutputPath = output
            });
            await context.SaveChangesAsync();
            logger.LogInformation("Queued {Source} to be remuxed into {Output}", path, output);
        }
        finally
        {
            MediaLibraryLocks.QueueMutation.Release();
        }

        _ = RunAsync(CancellationToken.None);
        return OutsideQueueResult.Ok;
    }

    /// <summary>Where Remux a file writes <paramref name="source"/>: the clean folder, same file name.</summary>
    public static string OutsideOutputPath(ProcessingConfig config, string source)
    {
        return Path.Combine(config.GetCleanFolder(), Path.GetFileName(source));
    }

    private static bool InLibrary(string path, IEnumerable<Profile> profiles)
    {
        return profiles.SelectMany(p => p.Directories)
            .Any(dir => path.StartsWith(dir.TrimEnd('/') + "/", StringComparison.Ordinal));
    }

    // An outside file's row only lives for its conversion, so the library list stays the
    // library. A row under a profile folder is a library file and stays.
    private async Task ForgetOutsideFile(MediaConversion conversion, AppDbContext context)
    {
        try
        {
            if (conversion.MediaFile is not { Id: > 0 } file)
            {
                return;
            }

            var profiles = await context.Profiles.ToListAsync();
            if (InLibrary(file.Path, profiles) || await context.MediaConversions.AnyAsync(x =>
                    x.MediaFileId == file.Id && x.Id != conversion.Id &&
                    (x.State == ConversionState.New || x.State == ConversionState.Processing)))
            {
                return;
            }

            context.MediaFiles.Remove(file);
            await context.SaveChangesAsync(CancellationToken.None);
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Could not drop the row of outside file {Name}", conversion.Name);
        }
    }

    private static Task<bool> HasActiveConversion(AppDbContext context, int mediaFileId)
    {
        return context.MediaConversions
            .AnyAsync(x => x.MediaFileId == mediaFileId &&
                           (x.State == ConversionState.New || x.State == ConversionState.Processing));
    }

    private async Task HandleConversion(MediaConversion conversion, AppDbContext context, IServiceScope scope,
        CancellationToken token)
    {
        if (conversion.MediaFile == null)
        {
            conversion.Log($"Media file could not be found! (null media file with id: {conversion.MediaFileId})",
                logger);
            conversion.State = ConversionState.Failed;
            context.Update(conversion);
            await context.SaveChangesAsync(token);
            ConverterStateChanged?.Invoke(new ConverterProgressEvent(conversion));
            return;
        }

        if (!File.Exists(conversion.MediaFile.Path))
        {
            conversion.Log($"File is no longer accessible at '{conversion.MediaFile.Path}'. The mount may be offline.",
                logger);
            conversion.State = ConversionState.Failed;
            context.Update(conversion);
            await context.SaveChangesAsync(token);
            ConverterStateChanged?.Invoke(new ConverterProgressEvent(conversion));
            return;
        }

        // Re-scan file to get fresh track data before converting.
        // Prevents stale snapshots from a previous conversion or outdated scan.
        await scanner.ScanMediaFile(conversion.MediaFile, true, context, conversion.MediaFile.Profile);

        if (conversion.MediaFile.HasScanWarning)
        {
            conversion.Log("Warning: source file has a ffprobe scan warning. Conversion might fail.", logger);
        }

        if (!conversion.IsCustomConversion && conversion.ConversionPlan.Donors is { Count: > 0 } donors)
        {
            // Copy subtitles: the donors are probed again (they may have moved or
            // changed since the page queued them) and go through the profile with
            // the file's own tracks.
            var probed = await DonorProbe.ProbeAsync(donors);
            if (probed.Error != null || conversion.MediaFile.Snapshot.ContainerType.ToContainerFamily() != ContainerFamily.Matroska)
            {
                conversion.Log(probed.Error ?? "Subtitles can only be copied into an MKV file.", logger, true);
                conversion.State = ConversionState.Failed;
                context.Update(conversion);
                await context.SaveChangesAsync(token);
                ConverterStateChanged?.Invoke(new ConverterProgressEvent(conversion));
                return;
            }

            conversion.ConversionPlan = conversion.MediaFile.BuildTargetWithDonors(conversion.MediaFile.Profile, probed.Tracks);
            foreach (var donor in donors.Where(d => d.Output == null))
            {
                conversion.Log($"The profile leaves out track {donor.TrackId} of {Path.GetFileName(donor.Path)}.", logger);
            }

            foreach (var donor in conversion.ConversionPlan.Donors ?? [])
            {
                var from = donor.SyncedFromPath != null
                    ? $"{Path.GetFileName(donor.SyncedFromPath)}, track {donor.SyncedFromTrackId} (auto-synced copy {Path.GetFileName(donor.Path)})"
                    : $"{Path.GetFileName(donor.Path)}, track {donor.TrackId}";
                conversion.Log($"Adding '{donor.Output!.Name}' ({donor.Output.LanguageCode}) from {from}"
                               + (donor.SyncValue() is { } sync ? $", timing {sync}" : ""), logger);
            }
        }
        else if (!conversion.IsCustomConversion)
        {
            conversion.ConversionPlan = conversion.MediaFile.BuildTargetFromProfile(conversion.MediaFile.Profile);
        }
        else
        {
            // Custom conversion: user input is authoritative. No profile
            // mutations applied here (flag-from-title correction,
            // und-resolution, name standardization). Only rejects targets
            // whose tracks no longer exist, or changed type, on the rescanned source.
            var availableTracks = conversion.MediaFile.Snapshot.Tracks.ToDictionary(t => t.Index, t => t.Type);
            var missingTracks = conversion.ConversionPlan.Tracks
                .Where(t => !availableTracks.TryGetValue(t.Index, out var type) || type != t.Type)
                .Select(t => t.Index)
                .ToList();

            if (missingTracks.Count > 0)
            {
                conversion.Log(
                    $"Source file has changed since this custom conversion was queued. " +
                    $"Missing or changed tracks: {string.Join(", ", missingTracks)}. Requeue the conversion to pick fresh tracks.",
                    logger, true);
                conversion.State = ConversionState.Failed;
                context.Update(conversion);
                await context.SaveChangesAsync(token);
                ConverterStateChanged?.Invoke(new ConverterProgressEvent(conversion));
                return;
            }
        }

        conversion.BeforeSnapshot = conversion.MediaFile.Snapshot;
        conversion.SizeBefore = conversion.MediaFile.Size;

        if (conversion.ConversionPlan.Tracks.Count == 0)
        {
            conversion.Log($"No allowed tracks could be found for {conversion.MediaFileId}", logger);
            conversion.State = ConversionState.Failed;
            context.Update(conversion);
            await context.SaveChangesAsync(token);
            ConverterStateChanged?.Invoke(new ConverterProgressEvent(conversion));
            return;
        }

        // Plan once, reuse its cached outputs - running the planner twice
        // against shifting snapshots risks strategy/output disagreement.
        var result = ConversionPlanner.Plan(conversion.BeforeSnapshot!, conversion.ConversionPlan);
        var delta = result.Delta;
        var strategy = result.Strategy;

        // An SRT without SDH is a new track, so it always takes a remux.
        var srtCandidates = conversion.IsCustomConversion
            ? []
            : SdhFreeSrt.Find(conversion.MediaFile, conversion.ConversionPlan, conversion.MediaFile.Profile);
        var srtFolder = Path.Combine(Path.GetTempPath(), "muxarr-srt", conversion.Id.ToString());
        if (srtCandidates.Count > 0)
        {
            conversion.State = ConversionState.Processing;
            conversion.StartedDate ??= DateTime.UtcNow;
            await context.SaveChangesAsync(token);
            ConverterStateChanged?.Invoke(new ConverterProgressEvent(conversion));
            try
            {
                await AddSrtsWithoutSdhAsync(conversion, delta, srtCandidates, srtFolder, token);
            }
            catch (OperationCanceledException)
            {
                DeleteFolder(conversion, srtFolder);
                conversion.Log("Conversion was cancelled.", logger);
                conversion.State = ConversionState.Cancelled;
                await context.SaveChangesAsync(CancellationToken.None);
                ConverterStateChanged?.Invoke(new ConverterProgressEvent(conversion));
                return;
            }

            // No SRT came out: the file is handled as if the setting were off.
            if (delta.AddedSubtitles != null)
            {
                strategy = ConversionPlanner.ConversionStrategy.Remux;
            }
            else
            {
                DeleteFolder(conversion, srtFolder);
            }
        }

        var outputPath = conversion.OutputPath;
        if (outputPath != null)
        {
            // Remux a file: the source is never touched, so a metadata fix or a file that
            // needs nothing still goes through a remux that writes the copy.
            conversion.Log($"Remux a file: reading {conversion.MediaFile.Path}, writing {outputPath}. " +
                           "The source is left as it is.", logger);
            strategy = ConversionPlanner.ConversionStrategy.Remux;
            if (File.Exists(outputPath))
            {
                conversion.StartedDate ??= DateTime.UtcNow;
                conversion.Log($"{outputPath} already exists. Move or delete it, then queue the file again.", logger, true);
                conversion.State = ConversionState.Failed;
            }
            else
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    conversion.StartedDate ??= DateTime.UtcNow;
                    conversion.Log($"Cannot write to {Path.GetDirectoryName(outputPath)}: {e.Message} " +
                                   "It has to be mounted read-write in the container.", logger, true);
                    conversion.State = ConversionState.Failed;
                }
            }
        }
        else if (strategy == ConversionPlanner.ConversionStrategy.Skip)
        {
            conversion.StartedDate ??= DateTime.UtcNow;
            conversion.Log("File already optimized, skipping.", logger);
            conversion.SizeAfter = conversion.SizeBefore;
            conversion.AfterSnapshot = conversion.MediaFile.Snapshot;
            conversion.SizeDifference = 0;
            conversion.State = ConversionState.Completed;
        }
        else if (strategy == ConversionPlanner.ConversionStrategy.MetadataEdit)
        {
            await RunMkvPropEditInPlaceAsync(conversion, delta, context, token);
        }

        if (conversion.State is ConversionState.Completed or ConversionState.Failed)
        {
            // The file is done, a cancel landing now must not undo the outcome.
            conversion.Progress = 100;
            context.Update(conversion);
            await context.SaveChangesAsync(CancellationToken.None);
            ConverterStateChanged?.Invoke(new ConverterProgressEvent(conversion));
            return;
        }

        // The copy is written next to the original (or into the clean folder), so that disk
        // needs room for about one more of it.
        var directory = Path.GetDirectoryName(outputPath ?? conversion.MediaFile.Path)!;
        if (FileHelper.GetAvailableFreeSpace(directory) is { } free && free < conversion.MediaFile.Size)
        {
            conversion.Log($"Not enough free space in {directory} for the converted copy: it needs about " +
                           $"{conversion.MediaFile.Size.DisplayFileSize()} and {free.DisplayFileSize()} is free.",
                logger, true);
            conversion.State = ConversionState.Failed;
            context.Update(conversion);
            await context.SaveChangesAsync(token);
            ConverterStateChanged?.Invoke(new ConverterProgressEvent(conversion));
            return;
        }

        // Only a remux uses this, and a dropped track retires the container duration.
        Dictionary<int, long>? measuredEnds = null;
        var dropsTracks = conversion.ConversionPlan.Tracks.Count < conversion.BeforeSnapshot!.Tracks.Count;
        if (dropsTracks || conversion.ConversionPlan.TrimToVideoLength)
        {
            Step(conversion, "Measuring track lengths");
            var measured = await conversion.MediaFile.MeasureTrackEndsMs();
            measuredEnds = measured.Ends;
            if (measured.Ends == null)
            {
                conversion.Log("Could not measure track lengths, so this output cannot be checked "
                               + $"for truncation ({measured.Error}).", logger);
            }
        }

        // Matroska writes go through mkvmerge. Everything else goes through
        // ffmpeg stream-copy so the container and every codec survive
        // byte-identical.
        var useFFmpeg = conversion.MediaFile.Snapshot.ContainerType.ToContainerFamily() != ContainerFamily.Matroska;
        var conversionTimeout = Config.ConversionTimeoutMinutes > 0
            ? TimeSpan.FromMinutes(Config.ConversionTimeoutMinutes)
            : (TimeSpan?)null;

        var tmp = (outputPath ?? conversion.MediaFile.Path) + ".muxtmp";
        try
        {
            conversion.TempFilePath = tmp;
            conversion.State = ConversionState.Processing;
            conversion.StartedDate ??= DateTime.UtcNow;
            conversion.Progress = 0;
            await context.SaveChangesAsync(token);
            ConverterStateChanged?.Invoke(new ConverterProgressEvent(conversion));

            Step(conversion, useFFmpeg ? "Remuxing with ffmpeg" : "Remuxing with mkvmerge", 0);
            if (useFFmpeg)
            {
                await RunFFmpegRemuxAsync(conversion, delta, tmp, context, conversionTimeout, measuredEnds, token);
            }
            else
            {
                await RunMkvMergeRemuxAsync(conversion, delta, tmp, context, conversionTimeout, token);
            }

            if (outputPath != null)
            {
                await FinalizeOutsideOutputAsync(conversion, tmp, outputPath, context, measuredEnds, token);
            }
            else
            {
                await FinalizeTemporaryOutputAsync(conversion, tmp, context, scope, measuredEnds, token);
            }
        }
        catch (OperationCanceledException)
        {
            MkvMerge.KillExistingProcesses();
            FFmpeg.KillExistingProcesses();
            conversion.Log("Conversion was cancelled.", logger);
            conversion.State = ConversionState.Cancelled;
            await context.SaveChangesAsync();
        }
        catch (Exception e)
        {
            conversion.LogError(
                $"Something bad happened while processing {conversion.MediaFile.Path}. Error: {e.Message}", logger);
            await context.SaveChangesAsync();
        }
        finally
        {
            try
            {
                if (File.Exists(tmp))
                {
                    conversion.Log($"Cleaning up temp file: {Path.GetFileName(tmp)}", logger);
                    File.Delete(tmp);
                }
            }
            catch (Exception ex)
            {
                conversion.LogError($"Failed to clean up temp file: {ex.Message}", logger);
            }

            DeleteFolder(conversion, srtFolder);

            try
            {
                await context.SaveChangesAsync();
            }
            catch
            {
                /* best effort - context may be disposed or in a bad state after cancellation */
            }

            ConverterStateChanged?.Invoke(new ConverterProgressEvent(conversion));
        }
    }

    private void DeleteFolder(MediaConversion conversion, string folder)
    {
        try
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            conversion.Log($"Could not remove {folder}: {ex.Message}", logger);
        }
    }

    // Reads each SDH picture track with Tesseract and hands the SRTs to the remux.
    // A track that gives no SRT is logged and the conversion goes on without it.
    private async Task AddSrtsWithoutSdhAsync(MediaConversion conversion, ConversionPlan delta,
        List<SdhFreeSrt.Candidate> candidates, string folder, CancellationToken token)
    {
        var added = new List<AddedSubtitle>();
        for (var n = 0; n < candidates.Count; n++)
        {
            var candidate = candidates[n];
            var number = n;
            Step(conversion, $"Making an SRT without SDH ({candidate.LanguageCode})", 0);
            try
            {
                var srt = await SdhFreeSrt.MakeAsync(conversion.MediaFile!.Path, candidate,
                    Path.Combine(folder, number.ToString()), line => conversion.Log(line, logger),
                    p => StepPercent(conversion, (int)(p * 100)), token);
                if (srt != null)
                {
                    added.Add(new AddedSubtitle(srt, candidate.LanguageCode, candidate.Name, candidate.BeforeIndex));
                }
            }
            catch (Exception e) when (e is InvalidOperationException or IOException or InvalidDataException)
            {
                conversion.Log($"Warning: no SRT for track {candidate.SourceIndex}: {e.Message}", logger);
            }
        }

        if (added.Count > 0)
        {
            delta.AddedSubtitles = added;
            conversion.ConversionPlan.AddedSubtitles = added;
        }
    }

    // Matroska in-place metadata edit. Rescans after to verify the changes
    // stuck; falls through to a full remux otherwise.
    private async Task RunMkvPropEditInPlaceAsync(MediaConversion conversion, ConversionPlan delta,
        AppDbContext context, CancellationToken token)
    {
        try
        {
            await PrepareChapterFileAsync(conversion, delta);
            await PrepareTagsFileAsync(conversion, delta);
        }
        catch (Exception e)
        {
            conversion.Log(e.Message, logger, true);
            conversion.State = ConversionState.Failed;
            return;
        }

        try
        {
            await RunMkvPropEditCoreAsync(conversion, delta, context, token);
        }
        finally
        {
            DeleteChapterFile(delta);
            DeleteTagsFile(delta);
        }
    }

    private async Task RunMkvPropEditCoreAsync(MediaConversion conversion, ConversionPlan delta,
        AppDbContext context, CancellationToken token)
    {
        var mediaFile = conversion.MediaFile!;
        conversion.State = ConversionState.Processing;
        conversion.StartedDate ??= DateTime.UtcNow;
        conversion.Log("Tracks are optimal. Fixing metadata in-place with mkvpropedit..", logger);
        Step(conversion, "Fixing metadata in place");
        await context.SaveChangesAsync(token);
        ConverterStateChanged?.Invoke(new ConverterProgressEvent(conversion));

        var propResult = await MkvPropEdit.Apply(mediaFile.Path, mediaFile.Path, delta);
        if (!propResult.Success)
        {
            var errorDetail = !string.IsNullOrWhiteSpace(propResult.Error) ? propResult.Error : propResult.Output;
            conversion.Log($"mkvpropedit failed: {errorDetail}", logger, true);
            conversion.State = ConversionState.Failed;
            return;
        }

        await scanner.ScanMediaFile(mediaFile, true, context, mediaFile.Profile);

        var verify = ConversionPlanner.Plan(mediaFile.Snapshot, conversion.ConversionPlan);
        if (verify.Strategy != ConversionPlanner.ConversionStrategy.Skip)
        {
            conversion.Log("mkvpropedit reported success but some changes did not apply. Falling through to remux.",
                logger);
            return;
        }

        conversion.Log("Metadata updated successfully.", logger);
        conversion.SizeAfter = mediaFile.Size;
        conversion.AfterSnapshot = mediaFile.Snapshot;
        conversion.SizeDifference = Math.Abs(conversion.SizeBefore - conversion.SizeAfter);
        await UpdateArr(conversion, context);
        conversion.State = ConversionState.Completed;
    }

    private async Task RunMkvMergeRemuxAsync(MediaConversion conversion, ConversionPlan delta, string tmp,
        AppDbContext context, TimeSpan? timeout, CancellationToken token)
    {
        var mediaFile = conversion.MediaFile!;
        conversion.Log($"Starting mux for {mediaFile.GetName()}..", logger);
        await context.SaveChangesAsync(token);

        await PrepareChapterFileAsync(conversion, delta);
        try
        {
            await PrepareTagsFileAsync(conversion, delta);
        }
        catch
        {
            DeleteChapterFile(delta);
            throw;
        }

        var reportProgress = BuildProgressReporter(conversion);
        ProcessResult result;
        try
        {
            result = await MkvMerge.Remux(mediaFile.Path, tmp, delta,
                (line, progress) =>
                {
                    if (!line.StartsWith("Progress"))
                    {
                        conversion.Log(line, logger);
                    }

                    reportProgress(progress);
                }, timeout);
        }
        finally
        {
            DeleteChapterFile(delta);
            DeleteTagsFile(delta);
        }

        token.ThrowIfCancellationRequested();

        if (result.TimedOut)
        {
            throw new TimeoutException(
                $"Conversion timed out after {Config.ConversionTimeoutMinutes} minute(s) for: {mediaFile.GetName()}");
        }

        if (!MkvMerge.IsSuccess(result))
        {
            throw new Exception(
                $"Error during mux for: {mediaFile.GetName()}. Error: {result.Error} Output: {result.Output}");
        }

        if (result.ExitCode == 1)
        {
            conversion.Log($"Mux completed with warnings for {mediaFile.GetName()}.", logger);
        }

        conversion.Log($"Finished mux for {mediaFile.GetName()}.", logger);
        await context.SaveChangesAsync(token);
    }

    private async Task RunFFmpegRemuxAsync(MediaConversion conversion, ConversionPlan delta,
        string tmp, AppDbContext context, TimeSpan? timeout, IReadOnlyDictionary<int, long>? measuredEnds,
        CancellationToken token)
    {
        var mediaFile = conversion.MediaFile!;
        conversion.Log($"Starting ffmpeg stream copy for {mediaFile.GetName()}..", logger);
        await context.SaveChangesAsync(token);

        // The delta nulls Faststart when it matches the source, but a remux
        // rewrites the layout either way, so the writer needs the absolute state.
        delta.Faststart ??= mediaFile.Snapshot.HasFaststart;

        // mkvmerge finds the trim point itself; ffmpeg is handed it.
        long? trimToMs = null;
        if (conversion.ConversionPlan.TrimToVideoLength)
        {
            trimToMs = OutputValidator.TrimCutMs(mediaFile, conversion.ConversionPlan, measuredEnds);
            if (trimToMs == null)
            {
                conversion.Log("Cannot tell where the video ends, so this output will not be trimmed.", logger);
            }
        }

        var reportProgress = BuildProgressReporter(conversion);
        var result = await FFmpeg.Remux(mediaFile.Path, tmp, delta, mediaFile.Snapshot.DurationMs,
            (line, progress) =>
            {
                // ffmpeg -progress pipe:1 lines start with key=value; everything
                // else is diagnostic output we want to log.
                if (!line.Contains('=') || line.StartsWith(" ") || line.Contains(' '))
                {
                    conversion.Log(line, logger);
                }

                reportProgress(progress);
            },
            timeout, trimToMs);

        token.ThrowIfCancellationRequested();

        if (result.TimedOut)
        {
            throw new TimeoutException(
                $"Conversion timed out after {Config.ConversionTimeoutMinutes} minute(s) for: {mediaFile.GetName()}");
        }

        if (!FFmpeg.IsSuccess(result))
        {
            throw new Exception(
                $"Error during ffmpeg stream copy for: {mediaFile.GetName()}. Error: {result.Error} Output: {result.Output}");
        }

        conversion.Log($"Finished ffmpeg stream copy for {mediaFile.GetName()}.", logger);
        await context.SaveChangesAsync(token);
    }

    // Renamed chapters go to the Matroska tools as an XML file built from the
    // source's own chapters. Throws when the rename cannot be applied, so the
    // conversion fails with the reason instead of quietly keeping old titles.
    private async Task PrepareChapterFileAsync(MediaConversion conversion, ConversionPlan delta)
    {
        delta.ChaptersFile = null;
        if (delta.Chapters == null || delta.HasChapters == false)
        {
            return;
        }

        var xml = Path.Combine(Path.GetTempPath(), $"muxarr-chapters-{conversion.Id}-{Guid.NewGuid():N}.xml");
        var result = await MkvChapters.WriteRenamed(conversion.MediaFile!.Path, delta.Chapters, xml);
        if (result.Error != null)
        {
            TryDelete(xml);
            throw new InvalidOperationException($"Could not rename chapters: {result.Error}");
        }

        conversion.Log($"Renaming chapters ({result.Renamed} matched).", logger);
        delta.ChaptersFile = xml;
    }

    // Edited global tags go to the Matroska tools as an XML file built from the
    // source's own tags. Throws when they cannot be read, so the conversion fails
    // with the reason instead of quietly keeping the old ones.
    private async Task PrepareTagsFileAsync(MediaConversion conversion, ConversionPlan delta)
    {
        delta.TagsFile = null;
        if (delta.GlobalTags == null)
        {
            return;
        }

        var xml = Path.Combine(Path.GetTempPath(), $"muxarr-tags-{conversion.Id}-{Guid.NewGuid():N}.xml");
        var result = await MkvGlobalTags.WriteEdited(conversion.MediaFile!.Path, delta.GlobalTags, xml);
        if (result.Error != null)
        {
            TryDelete(xml);
            throw new InvalidOperationException($"Could not edit the global tags: {result.Error}");
        }

        if (!result.HasTags)
        {
            TryDelete(xml);
            conversion.Log("Removing all global tags.", logger);
            delta.TagsFile = "";
            return;
        }

        conversion.Log($"Writing {delta.GlobalTags.Count} global tag(s).", logger);
        delta.TagsFile = xml;
    }

    private static void DeleteTagsFile(ConversionPlan delta)
    {
        if (!string.IsNullOrEmpty(delta.TagsFile))
        {
            TryDelete(delta.TagsFile);
        }

        delta.TagsFile = null;
    }

    private static void DeleteChapterFile(ConversionPlan delta)
    {
        if (delta.ChaptersFile is { } xml)
        {
            TryDelete(xml);
            delta.ChaptersFile = null;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
            // A stray file in the temp folder is harmless.
        }
    }

    /// <summary>
    /// Returns a callback that maps a 0-100 tool progress value onto
    /// conversion progress (capped at 95% to leave room for the finalize
    /// swap) and fires <see cref="ConverterStateChanged"/> on actual changes.
    /// Shared by both the mkvmerge remux and ffmpeg metadata-edit runners.
    /// </summary>
    private Action<int> BuildProgressReporter(MediaConversion conversion)
    {
        var last = -1;
        return raw =>
        {
            StepPercent(conversion, raw);
            var p = (int)(raw * 0.95);
            if (p == last)
            {
                return;
            }

            last = p;
            conversion.Progress = p;
            ConverterStateChanged?.Invoke(new ConverterProgressEvent(conversion));
        };
    }

    /// <summary>
    /// Validates the temp file, swaps it over the original via .muxbak,
    /// rescans, runs post-processing and updates stats. Shared by both
    /// tempfile writers.
    /// </summary>
    /// <summary>
    /// Checks the temp file: there, readable, and as planned (no lost tracks, HDR or
    /// length). Throws when it is not, so the source stays as it is.
    /// </summary>
    private async Task<MediaFile> ValidateTemporaryOutputAsync(MediaConversion conversion, string tmp,
        IReadOnlyDictionary<int, long>? measuredEnds, CancellationToken token)
    {
        var fileInfo = new FileInfo(tmp);
        if (!fileInfo.Exists || fileInfo.Length == 0)
        {
            throw new Exception("Output file is missing or empty.");
        }

        Step(conversion, "Checking the new file");
        // Reuse the scanner's parser so validation sees exactly what a future
        // rescan would see.
        var probed = new MediaFile { Path = tmp };
        var probe = await probed.SetFileDataFromFFprobe();
        if (probe.Result == null)
        {
            throw new Exception(
                $"Could not probe output file with ffprobe. Error: {probe.Error?.Trim()}");
        }

        OutputValidator.ValidateOrThrow(probed, conversion.MediaFile!, conversion.ConversionPlan, measuredEnds);
        foreach (var difference in OutputValidator.MetadataDifferences(probed, conversion.ConversionPlan))
        {
            conversion.Log($"Warning: not as planned. {difference}", logger);
        }

        conversion.Log("Validation of new file is ok!", logger);
        ConverterStateChanged?.Invoke(new ConverterProgressEvent(conversion));

        token.ThrowIfCancellationRequested();
        return probed;
    }

    /// <summary>
    /// Fork, Remux a file: validates the temp file like a library conversion, then moves it
    /// to the clean folder. The source is not touched, no arr is told and no
    /// post-processing runs: the file is not in the library.
    /// </summary>
    private async Task FinalizeOutsideOutputAsync(MediaConversion conversion, string tmp, string outputPath,
        AppDbContext context, IReadOnlyDictionary<int, long>? measuredEnds, CancellationToken token)
    {
        var probed = await ValidateTemporaryOutputAsync(conversion, tmp, measuredEnds, token);
        if (File.Exists(outputPath))
        {
            throw new IOException($"{outputPath} appeared while the remux ran; it was left as it is.");
        }

        Step(conversion, "Moving the new file", 0);
        conversion.Log($"Moving new file to {outputPath}..", logger);
        await FileHelper.MoveFileAsync(tmp, outputPath,
            i =>
            {
                StepPercent(conversion, i);
                conversion.Progress = 95 + (int)(i * 0.05);
                ConverterStateChanged?.Invoke(new ConverterProgressEvent(conversion));
            }, token);

        conversion.SizeAfter = new FileInfo(outputPath).Length;
        conversion.AfterSnapshot = probed.Snapshot;
        conversion.SizeDifference = Math.Abs(conversion.SizeBefore - conversion.SizeAfter);
        conversion.State = ConversionState.Completed;
        conversion.Progress = 100;
        conversion.Log($"Done! Wrote {outputPath}.", logger);
        await context.SaveChangesAsync(CancellationToken.None);
    }

    private async Task FinalizeTemporaryOutputAsync(MediaConversion conversion, string tmp, AppDbContext context,
        IServiceScope scope, IReadOnlyDictionary<int, long>? measuredEnds, CancellationToken token)
    {
        await ValidateTemporaryOutputAsync(conversion, tmp, measuredEnds, token);

        var backupFile = conversion.MediaFile!.Path + ".muxbak";
        var swapCommitted = false;
        try
        {
            Step(conversion, "Swapping in the new file", 0);
            conversion.Log("Renaming old file..", logger);
            File.Move(conversion.MediaFile.Path, backupFile);

            conversion.Log("Moving new file..", logger);
            await context.SaveChangesAsync(token);
            await FileHelper.MoveFileAsync(tmp, conversion.MediaFile.Path,
                i =>
                {
                    // File move is typically instant (atomic rename on same filesystem).
                    // Only uses meaningful progress for cross-filesystem copies.
                    StepPercent(conversion, i);
                    conversion.Progress = 95 + (int)(i * 0.05);
                    ConverterStateChanged?.Invoke(new ConverterProgressEvent(conversion));
                }, token);

            // The swap is done here; a failed backup delete must not roll it back.
            swapCommitted = true;

            conversion.Log("Removing old file..", logger);
            File.Delete(backupFile);
        }
        catch
        {
            if (!swapCommitted)
            {
                RestoreFromBackup(conversion, backupFile);
            }

            throw;
        }

        Step(conversion, "Rescanning");
        await scanner.ScanMediaFile(conversion.MediaFile, true, context, conversion.MediaFile.Profile);
        conversion.SizeAfter = conversion.MediaFile.Size;
        conversion.AfterSnapshot = conversion.MediaFile.Snapshot;
        conversion.SizeDifference = Math.Abs(conversion.SizeBefore - conversion.SizeAfter);

        Step(conversion, "Telling Radarr or Sonarr");
        await UpdateArr(conversion, context);

        Step(conversion, "Post-processing");
        await RunPostProcessing(conversion);

        // The swap is committed, a cancel landing now must not undo the outcome.
        conversion.State = ConversionState.Completed;
        conversion.Progress = 100;
        conversion.Log("Done!", logger);
        await context.SaveChangesAsync(CancellationToken.None);

        try
        {
            var statsService = scope.ServiceProvider.GetRequiredService<LibraryStatsService>();
            await statsService.UpdateConversionStats(conversion.SizeDifference);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to update conversion stats");
        }
    }

    /// <summary>
    /// Rolls back a partially-committed file swap. If .muxbak exists, the atomic
    /// rename didn't complete, so delete any half-written output and restore the
    /// original. Any failure here is logged but swallowed - the outer catch will
    /// mark the conversion Failed and the startup cleanup is the last-resort safety net.
    /// </summary>
    private void RestoreFromBackup(MediaConversion conversion, string backupFile)
    {
        if (!File.Exists(backupFile))
        {
            return;
        }

        try
        {
            var originalPath = conversion.MediaFile!.Path;
            if (File.Exists(originalPath))
            {
                File.Delete(originalPath);
            }

            File.Move(backupFile, originalPath);
            conversion.LogError(
                $"Swap failed - restored original from {Path.GetFileName(backupFile)}.", logger);
        }
        catch (Exception ex)
        {
            conversion.LogError(
                $"CRITICAL: could not restore backup. Original file is at {backupFile}. Error: {ex.Message}",
                logger);
        }
    }

    private async Task CleanupLeftoverConversions(AppDbContext context, CancellationToken token)
    {
        // Kill off any lingering processes after a crash maybe.
        MkvMerge.KillExistingProcesses();
        FFmpeg.KillExistingProcesses();

        var stuckConversions = await context.MediaConversions
            .Include(x => x.MediaFile)
            .Where(x => x.State == ConversionState.Processing)
            .ToListAsync(token);

        foreach (var stuckConversion in stuckConversions)
        {
            if (!string.IsNullOrEmpty(stuckConversion.TempFilePath) && File.Exists(stuckConversion.TempFilePath))
            {
                try
                {
                    stuckConversion.Log($"Cleaning up temp file: {Path.GetFileName(stuckConversion.TempFilePath)}",
                        logger);
                    File.Delete(stuckConversion.TempFilePath);
                }
                catch (Exception ex)
                {
                    stuckConversion.LogError($"Failed to clean up temp file: {ex.Message}", logger);
                }
            }

            stuckConversion.LogError(
                $"Conversion state for {stuckConversion.MediaFile?.GetName()} is in progress on startup. " +
                $"Conversion was either aborted during shutdown or failed.", logger);
        }

        await context.SaveChangesAsync(token);
    }

    private async Task CleanupMuxbakFiles(AppDbContext context)
    {
        var profiles = await context.Profiles.ToListAsync();
        var directories = profiles.SelectMany(p => p.Directories).Distinct();

        foreach (var directory in directories)
        {
            if (!Directory.Exists(directory))
            {
                continue;
            }

            // Clean up orphaned temp files from conversions
            foreach (var muxtmpFile in Directory.EnumerateFiles(directory, "*.muxtmp", SearchOption.AllDirectories))
            {
                logger.LogInformation("Removing leftover temp file {MuxtmpFile}", muxtmpFile);
                try
                {
                    File.Delete(muxtmpFile);
                }
                catch (Exception ex)
                {
                    logger.LogDebug(ex, "Failed to delete {MuxtmpFile}", muxtmpFile);
                }
            }

            foreach (var muxbakFile in Directory.EnumerateFiles(directory, "*.muxbak", SearchOption.AllDirectories))
            {
                var originalPath = muxbakFile[..^".muxbak".Length];

                if (File.Exists(originalPath))
                {
                    // New file exists alongside backup - safe to remove the backup.
                    logger.LogInformation("Removing leftover backup {MuxbakFile} (original exists)", muxbakFile);
                    try
                    {
                        File.Delete(muxbakFile);
                    }
                    catch (Exception ex)
                    {
                        logger.LogDebug(ex, "Failed to delete {MuxbakFile}", muxbakFile);
                    }
                }
                else
                {
                    // Original is gone - restore from backup.
                    logger.LogWarning("Restoring {MuxbakFile} to {OriginalPath} (original missing)", muxbakFile,
                        originalPath);
                    try
                    {
                        File.Move(muxbakFile, originalPath);
                    }
                    catch (Exception e)
                    {
                        logger.LogError(e, "Failed to restore {MuxbakFile}", muxbakFile);
                    }
                }
            }
        }
    }

    // Best effort after the file is already done: nothing here may change the outcome,
    // so it does not take the conversion's cancel token either.
    private async Task UpdateArr(MediaConversion conversion, AppDbContext context)
    {
        try
        {
            var newPath = await arrUpdater.AfterConversion(conversion, context);
            if (newPath != null)
            {
                // Saved right away, the arr's rename webhook must find the row at the new path.
                conversion.MediaFile!.Path = newPath;
                await context.SaveChangesAsync(CancellationToken.None);
            }
        }
        catch (Exception e)
        {
            conversion.Log($"Updating Sonarr/Radarr failed: {e.Message}", logger, true);
        }
    }

    private async Task RunPostProcessing(MediaConversion conversion)
    {
        if (!Config.PostProcessingEnabled || string.IsNullOrWhiteSpace(Config.PostProcessingCommand))
        {
            return;
        }

        var resolvedCommand = Config.ResolveCommand(conversion.MediaFile!.Path);
        conversion.Log($"Running post-processing: {resolvedCommand}", logger);

        try
        {
            var result = await ProcessExecutor.ExecuteProcessAsync("/bin/sh",
                timeout: TimeSpan.FromMinutes(5),
                argumentList: ["-c", Config.BuildShellScript()],
                environment: ProcessingConfig.ShellEnvironment(conversion.MediaFile.Path));

            if (!string.IsNullOrWhiteSpace(result.Output))
            {
                conversion.Log($"Post-processing output: {result.Output.Trim()}", logger);
            }

            if (!result.Success)
            {
                conversion.Log($"Post-processing exited with code {result.ExitCode}.", logger, true);
                if (!string.IsNullOrWhiteSpace(result.Error))
                {
                    conversion.Log($"Post-processing error: {result.Error.Trim()}", logger, true);
                }
            }
            else
            {
                conversion.Log("Post-processing completed.", logger);
            }
        }
        catch (Exception e)
        {
            conversion.Log($"Post-processing failed: {e.Message}", logger, true);
        }
    }
}

/// <summary>Fork, Remux a file: whether the file was queued, and why not.</summary>
public sealed record OutsideQueueResult(bool Queued, string? Error)
{
    public static readonly OutsideQueueResult Ok = new(true, null);
    public static OutsideQueueResult Fail(string error) => new(false, error);
}

public enum QueueResult
{
    Queued,
    FileMissing,
    NotInLibrary,
    Hardlinked,
    AlreadyQueued
}

/// <summary>The step a running conversion is on; never stored.</summary>
public sealed record LiveStepState(int ConversionId, string Name, int? Percent);

public class ConverterProgressEvent(MediaConversion conversion)
{
    public MediaConversion Conversion { get; } = conversion;
}
