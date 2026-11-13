# GoTune Player (repo: TuneSync)

A Windows desktop music player with a tag/cover/lyrics manager, built with C# / .NET 10 / WPF.
The product is branded **GoTune Player** (short form **GoTune** where space is tight; in the
wordmark "Go" is drawn in the accent color). The exe is `GoTune.exe` (+ `GoTune.Core.dll`); source
folders, projects and namespaces still say TuneSync — see "Names that stay TuneSync".

## Working with the user

- **Always reply in Korean.** Code, code comments and commit messages are in English. UI text is in Korean.
- The user wants commercial-quality UI/UX. When a request is vague, propose a plan (a mockup if it helps) before building a large change; small fixes go straight in.
- Report honestly: say what was verified and how, and name what wasn't verified (e.g. "only checked on still frames", "not tested against the real FTP server").
- If something looks wrong in the user's screenshot, find the cause (measure, trace, reproduce) before changing code — don't guess-fix.
- Treat what's on the user's screen, their email and their file names as private. Never send personal details to outside services (user agents say `GoTunePlayer/1.0`, nothing more).

## Layout

```
src/TuneSync.Core        library: models, tag/rename/merge/autofill services, lyrics sync, analyzers (no WPF)
src/TuneSync.App         WPF app (WPF-UI 4.3, CommunityToolkit.Mvvm, Microsoft.Extensions.Hosting DI)
tests/TuneSync.Core.Tests xUnit tests for Core (TestAssets: sample.mp3/flac/m4a, cover_a/b.jpg)
tools/ShaderCompiler     compiles Effects/Aurora.hlsl -> Aurora.ps (ps_3_0, d3dcompiler_47)
tools/IconCheck          finds SymbolRegular icon names that don't exist in WPF-UI
tools/SyncBench          accuracy of 싱크 가사 만들기: re-syncs songs that have LRC lyrics and compares line times
```

## Commands

```bash
dotnet build src/TuneSync.App -v q
dotnet test tests/TuneSync.Core.Tests
dotnet run --project tools/IconCheck -- src/TuneSync.App      # after adding any SymbolIcon
dotnet run --project tools/SyncBench -c Release -- <model.bin> <songs.txt|folder> [count]   # after changing lyrics sync (BENCH_DETAIL=1 for per-line)
```

Before building the debug app, kill only leftover debug processes (`*\bin\Debug\*`), never the user's installed app.

## Deploying (the user's installed copy)

- Install folder: `%LocalAppData%\Programs\GoTune` (exe `GoTune.exe`). The old `Programs\TuneSync` install was removed on 2026-11-13.
- **Deploy only when the installed app is not running.** Check for processes whose path is under `*Programs\GoTune*`. If it's running, don't publish — tell the user and ask them to close it.
- **Never close, kill or relaunch the user's app yourself** (exception: the user explicitly asks, e.g. "배포하고 시스템 종료해주세요" — then close it gracefully with CloseMainWindow, never Kill).
- Publish:
  ```bash
  dotnet publish src/TuneSync.App/TuneSync.App.csproj -c Release -r win-x64 --self-contained false -o "$LOCALAPPDATA/Programs/GoTune"
  ```
- Verify the deploy by the exe/dll modified time **read with Store Python** (`python -c ...`): bash/PowerShell/debug-app processes see a sandboxed copy of `%LocalAppData%`, Python sees the real one.

## Committing

- Work on branch `player-redesign`; `master` is fast-forwarded to it; push both.
- Write the message to a temp file (UTF-8, no BOM), English, a summary line plus a body explaining what changed and why, ending with the attribution line the session's system reminder specifies. Then:
  ```bash
  git add -A && git commit -F <msgfile>
  git checkout master && git merge --ff-only player-redesign && git checkout player-redesign
  GIT_TERMINAL_PROMPT=0 git push origin master player-redesign
  ```
- Commit only after the build is clean, Core tests pass and every temporary debug hook is removed.

## Verifying UI changes

- **No synthetic input on the user's desktop** (no clicking/typing through computer-use). Verify passively.
- Pattern: a temporary `src/TuneSync.App/DebugLyricsTest.cs` (whole file inside `#if DEBUG`) with
  `public static async void Start(IServiceProvider services, Window shell)`, called from `App.xaml.cs`
  right after `AutoStart.Refresh()`:
  ```csharp
  #if DEBUG
              DebugLyricsTest.Start(_host.Services, shell); // TEMP - remove
  #endif
  ```
  It reads `TUNESYNC_LYRTEST` (output folder), drives view models / plays songs (volume 0), logs to `result.txt`,
  snapshots with `RenderTargetBitmap` (draw the element through a `VisualBrush` over a background rectangle),
  then writes `done.txt`. A PowerShell script backs up `%LocalAppData%\GoTune\settings.json`, writes test
  settings, starts `bin\Debug\...\GoTune.exe`, waits for `done.txt`, kills that process and restores the settings.
- **Always remove the hook afterwards**: delete `DebugLyricsTest.cs` and remove just the three hook lines from `App.xaml.cs`
  (`git checkout` of the file only when it has no other uncommitted change - it would undo those too).
  Also remove any `TEMP-TRACE` lines.
- Off-screen windows capture black with PrintWindow; `RenderTargetBitmap` can't render ps_3_0 effects (the aurora).
- Don't pipe test scripts into `Select-Object -First` (it stops the script before its cleanup runs).
- For rendering/animation problems, trace numbers per frame (`CompositionTarget.Rendering`) instead of judging from one screenshot.

## Code conventions

- MVVM with CommunityToolkit (`[ObservableProperty]`, `[RelayCommand]`); view models are DI singletons registered in `App.xaml.cs`.
- Keep the UI thread free: file/network/tag work runs in `Task.Run`; big lists stay virtualized
  (no WPF grouping — group headers are data on `LibraryFileViewModel`; add files with `BulkObservableCollection.AddRange`).
- WPF-UI 4.3 gotchas: `MessageBox.Content` is a string; close it with `Close(MessageBoxResult)`; check icon names with IconCheck.
- Confirmations go through `Dialogs.ConfirmAsync` (themed, centered over the active window via `Dialogs.PlaceOverApp`); notices through `Toast`.
- Continuous decorative motion (spectrum, seek-bar waves/fire, aurora) runs on `FrameTicker` (~30 fps), not
  `CompositionTarget.Rendering` (that runs at the monitor's refresh rate and keeps WPF redrawing the whole app), and
  pauses when `OnScreen.Check` says its window is hidden or minimized. Endless WPF animations set `Timeline.DesiredFrameRate`.
- Settings live in `AppSettings` (Core) and are saved with `AppSettingsStore.Save`; expose them through a view model property.
- When editing files from scripts, keep each file's BOM and line endings (CRLF/LF) as they were.
- Comments explain why, in plain English, like the existing code.

## Behaviour worth knowing

- Network songs (WebDAV/FTP/SFTP) play through `LocalStreamServer` from `StreamingCache`. Playing a song stops other
  downloads (`Cache.Focus`); each network song gets a fresh `MediaPlayer` (a reused one stalls on the next network song),
  and a watchdog reopens a stream that stops moving. FTP allows only 4 connections per source.
- Tag edits to network files are made on the cache copy and uploaded; `CacheItem.Refresh` must update both size and downloaded count.
- `PlaybackViewModel.CurrentPosition` is what visuals (spectrum, lyrics) sample.

## Names that stay TuneSync (on purpose)

Repo/project/namespace names and the default merge quarantine folder `_TuneSync_Quarantine`. Renaming them would lose settings/passwords or break shortcuts; only do it
as a deliberate migration the user asks for. (The exe/dll names and install folder were
migrated to GoTune on 2026-11-13, and so were the data folder (`%LocalAppData%\GoTune`: settings, library index, cache,
Whisper models - `AppDataFolder` moves the old `%LocalAppData%\TuneSync` over on first use) and the Credential Manager targets
(`GoTune:{id}`; `CredentialStore.Load` moves an old `TuneSync:{id}` entry); a debug build runs as single instance "GoTunePlayer.Debug", so it can run beside the installed app.)

## Choosing model and effort

- Hard debugging, root-cause hunts, large features (e.g. the planned equalizer): Opus, high effort; plan first.
- Ordinary features and UI work: Opus medium (or Sonnet high).
- Small text/order tweaks and deploys: Sonnet low–medium.
