<p align="center">
  <img src="Muxarr.Web/wwwroot/favicon.svg" alt="Remuxarr" width="120"/>
</p>

# Remuxarr

Remuxarr cleans up the audio and subtitle tracks of a media library: it removes the tracks you never use,
orders them, sets the default and forced flags and writes consistent track names. It remuxes with
**mkvmerge** (MKV) or **ffmpeg** stream copy (MP4), so nothing is re-encoded. It hooks into Sonarr and
Radarr for the original language and converts new imports as they arrive.

It is one of a set of homelab apps that share a look and code ([arrkit](https://github.com/yannb2/arrkit)):
[Ripparr](https://github.com/yannb2/ripparr), [Specarr](https://github.com/yannb2/specarr),
[Salvarr](https://github.com/yannb2/salvarr), [Scratcharr](https://github.com/yannb2/scratcharr) and
[Spinarr](https://github.com/yannb2/spinarr).

## Where it comes from

Remuxarr started as a fork of [Muxarr](https://github.com/KirovAir/muxarr) by KirovAir and its
contributors, and keeps their code under the same licence, GPL-3.0 (see [LICENSE](LICENSE.md)).
It no longer follows Muxarr: it has its own name, image and version numbers (from 1.0.0), and the
changes since the fork are in this repository's history. Thanks to the Muxarr authors for the base.

Some names stay as they were so an existing install keeps working:

- the database is still `muxarr.db` in `/config`, and the per-folder track name backup is still the
  hidden `.muxarr-tracks.json`;
- `MUXARR__AUTH__RESET` and `Muxarr__Updates__Enabled` still work next to the new
  `REMUXARR__AUTH__RESET` and `Remuxarr__Updates__Enabled`;
- the webhook it adds to Sonarr and Radarr is now called "Remuxarr"; an old "Muxarr" one is found
  and replaced, not doubled;
- the .NET projects are still `Muxarr.Core`, `Muxarr.Data`, `Muxarr.Web` and `Muxarr.Tests`;
- the port is still 8183.

## Quick start

```yaml
services:
  remuxarr:
    image: ghcr.io/yannb2/remuxarr:dev
    container_name: remuxarr
    environment:
      - TZ=America/Toronto
      - PUID=1000
      - PGID=1000
    volumes:
      - /path/to/appdata:/config
      - /movies:/movies
      - /tv:/tv
    ports:
      - 8183:8183
    restart: unless-stopped
```

Every push to `master` builds `ghcr.io/yannb2/remuxarr:dev` (amd64 and arm64). A published GitHub
release also tags `latest` and the version number. Docker Hub is skipped unless the
`DOCKERHUB_USERNAME` and `DOCKERHUB_TOKEN` secrets are set.

Open `http://your-ip:8183`; the setup wizard sets a login (optional), connects Sonarr and Radarr
(optional) and creates the first profile. Then scan, check the plan and queue.

## Features

- **Strip unwanted tracks** - remove audio (commentary, foreign dubs) and subtitles (SDH, foreign) without re-encoding, so quality is untouched. A typical 4GB file processes in about a minute depending on disk speed. Cutting spare audio tracks alone often saves 10-30%.
- **Original language detection** - integrates with your *arr stack so foreign films and shows always keep the correct audio track
- **Automatic processing** - webhook support to process new imports as they arrive
- **Per-directory profiles** - different language and track rules for different collections (e.g. anime vs western media)
- **Language priority & track limits** - control track ordering per language, limit tracks per language (e.g. keep only the best English audio track), and choose between best quality or smallest size
- **Fallback languages** - hang a language under another so it is only kept when the one above it is missing. "English subs, but only if the file has no German ones"
- **Regional language variants** - tells VFQ apart from VFF/Truefrench, Latino from Castilian, and more. Reads BCP 47 tags and track names, so "keep Quebec French, drop the France dub" just works
- **Smart metadata fixes** - cleans up encoder tags and codec dumps from track names. Uses mkvpropedit for metadata-only changes (instant, no remux needed)
- **Safe by default** - validates the output file before replacing the original. If anything fails, the original is untouched.
- **Library overview** - full breakdown of codecs, resolutions, and languages across your library


Supports Matroska (`.mkv`, `.webm`) and MP4-family (`.mp4`, `.m4v`).

### SDR only at 2160p

`{range}` in the video track name writes "SDR" only for 4K video (2160p and up). Below 4K every file is
SDR, so nothing is written, the same rule as tag_release, Radarr's SDR custom format and Ripparr.

### Track descriptions

`{description}` in a track name template is the text that isn't from the template: the current name
minus the part one of the profile's templates wrote (default or flag override), so a rerun never
nests a name inside itself. The custom conversion has a Description box under each audio and
subtitle name, the same as Ripparr's disc page:

- What you type fills `{description}`, and the name below updates to what will be written.
- A template without `{description}` puts the text in brackets at the end, and keeps it on later runs.
- Text starting with `=` is the whole name, as typed.
- A commentary with nothing typed gets "Commentary", unless its template already says it
  (`{language} (Commentary) - {description} - {codec}`).

### Tracks, chapters and tags on the file page

A file's page opens with its **Tracks** table: the custom conversion, inline and already filled with
what the selected profile would write. Tracks it keeps are ticked, in its order (language priority or
custom rules), with its names, languages and flags; the ones it drops are unticked under their own
type. Each row shows the codec, resolution and HDR or channels and Atmos, and the length, and
**Now:** under a track says how the file has it today when the profile changes it.

Tick or untick tracks, move them with the arrows, and change languages, names, descriptions and flags.
Below the tracks, always open:

- **Chapters**: every chapter with its number, start time and title (and the original title when the
  track name backup has one). **Keep the chapters** follows the profile's "Remove chapters"; turn it off
  to remove them all. Titles can be typed (MKV only), numbered "Chapter 1, 2, 3..." or put back from
  the backup; **Now:** under a changed title says what the file has.
- **Global tags**: the usual tags (the ones tag_release.py and Ripparr write: TMDB, IMDB, TVDB2,
  RELEASE_NAME, RELEASE_GROUP, SOURCE_RELEASE_GROUP, DISC_TITLE, DISC_COUNTRY, SOURCE, VIDEO_CODEC,
  HDR_FORMAT, DV_PROFILE, EDITION, LABEL, ORIGINAL_LANGUAGE, ABSOLUTE_EPISODE, QUALITY, IMPORTED,
  RIPPED_WITH) are always listed, empty when the file doesn't have them; an empty one is not written.
  The file's other tags follow, and **Add tag** adds any other. MKV only.

The file title is at the top. **Queue these changes** converts exactly what is shown. **Add to Queue** at the top still runs the profile as it is, worked out again when the file's
turn comes. Picking another profile at the top, or **Back to the profile**, fills everything again (the profile's
"Remove chapters" and "Clear the file title" included).
The Remux a file page uses the same table.

### Track checks

The checks, taken from Ripparr, sit in the **Check** column of the Tracks table (file page and Remux a
file), on each audio and subtitle row, next to the track's name and flags:

- **Listen** (headphones) plays 20 seconds of an audio track (from a third of the way in), as stereo AAC.
- **Preview** (card) shows up to 24 captions of a subtitle track: pictures for PGS, text for SRT and ASS.
- **Check** (magnifier) reads up to 120 captions (Tesseract OCR for PGS, in the language the row has)
  and says whether the track looks like SDH (sound descriptions, speaker labels, lyrics) or a commentary.

What they find opens on a row under the track, each part with its own Close. When the check disagrees
with the row's flags, **Use these flags** sets SDH and commentary on that row. A lossy stereo track after a
multichannel one in the same language gets a **Commentary or AD?** hint under it (the file can't say
which), with Commentary and AD buttons that set the flag; the hint goes once either is set. Audio rows
have an **AD** flag button (eye) for audio description.

Pick two or more PGS tracks with **Compare** (split button) and **Side by side**, under the table, lines
their captions up by time (from the same slices), as Ripparr's compare page does: handy to tell the
forced track from the full one, or SDH from plain subtitles. The results stay when the table is filled
again from the profile.

Only 12 slices of 40 seconds, spread over the film past the titles and before the credits, are read
with ffmpeg, so a 60 GB remux takes seconds, not a full read. VobSub and DVB picture subtitles are not
read. The image ships Tesseract with English, French, German, Spanish and Italian.

### Release label and progress

- The release label (Criterion, Arrow...) is the `LABEL` row of the global tags, the standard Matroska
  tag ("the imprint on the disc"), as Ripparr does; Jellyfin shows it as "Release: ..." through
  edition_tags.py. Files tagged before 1.1.2 have it in `DISTRIBUTED_BY`, listed under the usual tags:
  copy it to `LABEL` and remove it.
- A running conversion shows two bars, as in Ripparr: the current step (remux, check, swap, rescan...)
  and the whole conversion.
- The sidebar's Docs link opens this file, and the version links to the repository.

### Checking the new file

Before the new file replaces the original, Remuxarr also checks (from Ripparr's verify step):

- chapters the plan keeps are still there (a failure: the original stays);
- in MKV files, each track's name, language and default/forced/SDH/AD/commentary flags as planned (a
  warning in the conversion log: the file plays, a player only shows or picks something else).

### SRT without SDH

The profile's Subtitles section has **Add an SRT without SDH when a language only has SDH subtitles**
(off by default, MKV only), as Ripparr has. When every full subtitle track (not forced, not commentary)
the profile keeps in a language is SDH and one of them is PGS, the conversion:

1. extracts that PGS track with mkvextract and reads every caption with Tesseract;
2. takes the SDH text out ([door slams], JOHN:, ♪) and drops captions that were nothing else;
3. muxes the SRT in with the remux, named and placed by running the profile on the file as if the SRT
   were already there, so the next scan leaves it alone. If the profile would drop an SRT, none is made.

A track that gives no SRT (Tesseract missing, no text) is logged and the file is converted as if the
setting were off. Custom conversions never add one. Reading a film takes a few minutes; files already
in the library get it the next time they are queued.

### Copy subtitles

**Copy subtitles** in the menu adds subtitle tracks from other files to a movie or to the episodes of
a series, for a language the library copy lacks. The donors are files outside the library: another BD remux of the
same show, or loose `.srt`, `.ass`, `.ssa` and `.sup` files. They are read from the folders listed in
Settings > Processing > **Folders outside the library** (default `/downloads` and `/mnt/pc`, which
have to be mounted in the container; read-only is enough).

1. Pick a series or a movie in the library (a folder with S01E02-style names is a series; the
   All / Series / Movies buttons filter the list) and a donor folder (its sub-folders are read too).
2. Episodes are matched by name: `S01E02` (also `S01E02E03` and `S01E02-E03`), `1x02`, or an anime's
   absolute number (`Show - 012`). A movie takes the donors whose name, or folder name, starts with
   its title and has its year (`Corpse.Bride.2005.1080p.BluRay.REMUX-GRP.mkv`, `Corpse Bride 2005.fr.srt`).
   A row without a match gets a dropdown to pick a donor by hand, and any row can take another donor
   or lose one.
3. Tick the kinds of track to copy for the whole batch (language, forced, SDH, commentary, picture or
   text). Languages the series has no full subtitles in start ticked. A loose file's language and flags
   come from its name (`Show S01E02.fr.forced.srt`, `.en.sdh.srt`, `.French.srt`); they can be changed.
4. **Check timing** finds, per donor, the stretch and the shift:
   - the stretch comes from the two lengths: within 0.5 % is none (a different logo is a shift);
     about 4 % is PAL 25 fps against 23.976 or 24 fps. Loose files have no length, so all three are tried;
   - the shift lines up the caption start times of a subtitle track both files have (the same language
     when there is one, read in eight 90 s windows with ffprobe), else the chapters;
   - the badge says how many captions line up. "Weak" is normal when two languages are compared.
     The shift and stretch can be set by hand, and the eye button shows a minute of captions of the file
     and the donor side by side, the donor's at the time they will play.
   - **Auto-sync** is for a release cut differently (an ad break, a longer or shorter scene), where no
     single shift fits: [alass](https://github.com/kaegi/alass) retimes each ticked *text* track part by
     part. It lines the donor up with a subtitle track of the file (only its caption times are read, so
     PGS works as the reference too, and the language does not have to match), or with the file's sound
     ("with the file's sound", also used when the file has no subtitles; slower and less sure). With
     subtitles, alass runs with and without its frame-rate guess and the copy whose captions meet the
     file's best is kept; the badge says how many line up. The note lists the parts it moved
     ("3 parts moved apart: +2.5 s (13 min), -4.0 s (13 min), +1.2 s (9 min)"), the side by side shows the
     retimed copy, and Undo goes back to the shift. The retimed copy (UTF-8, ASS kept as ASS) waits in
     `/config/subtitle-sync` and is what the queue copies in, with the track's language, flags and name;
     copies older than 30 days are deleted when the page opens. Picture subtitles (PGS, VobSub) cannot be
     cut up: they keep the shift and stretch, also when the same donor's text tracks are auto-synced.
5. The last column is the dry run: the tracks each file gets, named and flagged by its profile as if it
   already had them. **Queue** adds one normal conversion per episode. Right before it runs, the
   donors are probed again and the profile runs again with them; mkvmerge reads each donor track as its
   own input (`--sync` for the timing, `--sub-charset windows-1252` for an SRT that is not UTF-8), and
   the usual output checks run before the original is replaced.

MKV files only. Conversions queued from this page show what was added in their log.

### Remux a file

**Remux a file** in the menu remuxes any video file outside the library: a download in `/downloads`,
a file on a USB disk in `/mnt/pc`. The source is only read; the result is written to the clean folder,
`/downloads/import/clean` by default, under the same file name. Import it from there.

1. Browse the folders outside the library (the same list as Copy subtitles; in `/mnt/pc` only the
   disks that are plugged in show) and click a video file. Its tracks are read and shown.
2. Pick a profile, and the original language: Radarr and Sonarr give it for library files, here it is
   guessed from the default audio track and can be changed. The tracks table previews the profile.
3. **Remux with** the profile as it is, or change the **Tracks** table (filled from the profile, as on a
   library file's page: keep, rename, reorder and flag tracks, chapters and tags) and **Remux with these changes**.

The conversion goes through the normal queue and its checks (no lost tracks, HDR or length). It always
remuxes, even when the profile would change nothing, so there is always a clean copy. A file of the
same name already in the clean folder is never overwritten: the page says so and the queue refuses.
Radarr and Sonarr are not told and no post-processing runs. The file only has a library row while its
conversion is queued or running. The **Recent** list on the page links to each conversion.

Settings > Processing > **Remux a file: clean folder** sets the folder. It has to be mounted
read-write, the only folder outside the library Remuxarr writes to:

```yaml
    volumes:
      - /downloads:/downloads:ro
      - /downloads/import/clean:/downloads/import/clean   # Remux a file writes here
      - /mnt/pc:/mnt/pc:ro,rslave
```

The host folder has to exist before the container starts. Settings > Status lists it with its free space.

## Configuration

| Variable | Description | Default |
|---|---|---|
| `TZ` | Timezone | `UTC` |
| `PUID` | User ID for file permissions | `888` |
| `PGID` | Group ID for file permissions | `888` |
| `UMASK` | Permission mask for files Remuxarr writes, e.g. `002` for group-writable libraries | `022` |
| `REMUXARR__AUTH__RESET` | `true` removes the login on the next start, if the password is lost. Remove it again afterwards | unset |
| `Logs__KeepLines` | How many log lines the Logs page's "Last N lines" tab keeps across restarts (100 to 100000), in `/config/logs/remuxarr.jsonl` | `2000` |
| `Remuxarr__Updates__Enabled` | `false` turns off the daily look for newer tool versions (Tools page) | `true` |

| Path | Description |
|---|---|
| `/config` | Database and settings |
| your media folders | Any mount path works; use the same paths as Sonarr and Radarr so webhooks need no mapping |

### Settings

Each Settings tab has its own address (`/settings/processing`, `/settings/status`...), as in the sister apps, so a
tab can be bookmarked. **Status** shows the folders Remuxarr uses with their free space (a folder that is not mounted
shows as missing), the mkvmerge / ffmpeg / dovi_tool / database checks, the integrations and what is in the database.

### Logs

The Logs page is the one every sister app has (arrkit's LogView): **Since start** and **Last N lines**,
exact level filters and a search. Lines are kept in memory and in `/config/logs/remuxarr.jsonl`; `docker logs`
still has everything. A conversion's own log stays on its page under Conversions. The old database log table
is dropped on the first start of this version.

### API

The API is authenticated with the `X-Api-Key` header (Settings > API). `GET /api/stats` gives library
statistics for [Homepage](https://gethomepage.dev/widgets/services/customapi/), `POST /api/queue` queues
every file that needs work and `GET /api/conversions` lists conversions so a script can follow them.

## Shared code: arrkit

The subtitle code behind Track checks (PGS reader, OCR, SDH cleaner, classifier, track sampler) is
shared with Ripparr in [arrkit](https://github.com/yannb2/arrkit), a git submodule in `arrkit/`.
Clone with `--recurse-submodules`, or run `git submodule update --init` in an existing clone:

```sh
git clone --recurse-submodules https://github.com/yannb2/remuxarr
git -C arrkit pull origin main && git add arrkit   # newer arrkit, then commit
```

Its tests live in arrkit, not here.

## Tools and updates

The image pins FFmpeg (BtbN static build), MKVToolNix, dovi_tool and alass (built from crates.io) with the `ARG` lines in
`Muxarr.Web/Dockerfile`; the `env` block of `build-and-deploy.yml` repeats them for the tests and
the image build args (a test checks they match). The **Tools** page (sidebar) shows what each
tool reports, the pin and the newest upstream version, looked up once a day
(`Remuxarr__Updates__Enabled=false` turns that off); Remuxarr never installs anything.
Every Monday the "Tool updates" workflow opens a pull request bumping both files when something
newer is out. It needs Settings > Actions > General > "Allow GitHub Actions to create and approve
pull requests".

## Building and testing

```sh
git clone --recurse-submodules https://github.com/yannb2/remuxarr
dotnet build Muxarr.slnx
dotnet test Muxarr.Tests
```

The tests need `mkvmerge`, `mkvpropedit`, `ffmpeg` and `ffprobe` on the `PATH` (CI installs the pinned
versions). New EF Core migrations go after the latest one in `Muxarr.Data/Migrations`.

## Licence

GPL-3.0, see [LICENSE](LICENSE.md). Based on [Muxarr](https://github.com/KirovAir/muxarr) by KirovAir.
Remuxarr is not affiliated with Muxarr, Sonarr, Radarr or any other *arr project.
