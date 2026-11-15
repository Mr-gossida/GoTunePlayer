// Measures how close "싱크 가사 만들기" gets to real synced lyrics: songs that already have
// LRC lyrics are re-synced from their plain text, and each line's time is compared.
//   dotnet run --project tools/SyncBench -- <model.bin> <songs.txt | folder> [count]
// songs.txt: one audio path per line (made from a folder on the first run, then reused, so
// runs compare the same songs).
// Environment:
//   BENCH_OUT          write the results to this file (UTF-8) instead of the console
//   BENCH_DETAIL=1     every line: real time, found time, ? if estimated, error, text
//   BENCH_VOCAL_MODEL  the vocal separation model (default: next to <model.bin>, where the app
//                      keeps it); BENCH_NOSEP=1 measures without separation
//   BENCH_CACHE        a folder that keeps the separated vocals and what Whisper heard, per song
//                      and pass: with it, a change to the aligner is measured in seconds instead
//                      of minutes. Use a fresh folder after changing the listening itself.
//   BENCH_ALL_PASSES=1 with BENCH_CACHE: listen to every pass (and keep it), not only until one is good enough
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using TuneSync.Core.Services;
using TuneSync.Core.Services.LyricsSync;

var outPath = Environment.GetEnvironmentVariable("BENCH_OUT");
if (!string.IsNullOrEmpty(outPath)) Console.SetOut(new StreamWriter(outPath, false, new System.Text.UTF8Encoding(false)) { AutoFlush = true });
var model = args[0];
var list = args[1];
var count = args.Length > 2 ? int.Parse(args[2]) : 8;
WhisperModelStore.Folder = Path.GetDirectoryName(model)!;
var vocalModel = Environment.GetEnvironmentVariable("BENCH_VOCAL_MODEL") is { Length: > 0 } given ? given : VocalModelStore.ModelPath;
if (Environment.GetEnvironmentVariable("BENCH_NOSEP") == "1" || !File.Exists(vocalModel)) vocalModel = null;
Console.WriteLine(vocalModel is null ? "without vocal separation" : $"vocal separation: {Path.GetFileName(vocalModel)}");
var cache = Environment.GetEnvironmentVariable("BENCH_CACHE") is { Length: > 0 } c ? c : null;
if (cache is not null) Directory.CreateDirectory(cache);

List<string> songs;
if (File.Exists(list)) songs = [.. File.ReadAllLines(list).Where(l => l.Trim().Length > 0)];
else
{
    songs = [];
    var rnd = new Random(3);
    var files = Directory.EnumerateFiles(args[1], "*.*", SearchOption.AllDirectories)
        .Where(f => f.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".flac", StringComparison.OrdinalIgnoreCase))
        .OrderBy(_ => rnd.Next()).ToList();
    foreach (var f in files)
    {
        try
        {
            using var tag = TagLib.File.Create(f);
            var lrc = LrcParser.Parse(tag.Tag.Lyrics);
            if (lrc.Count(l => l.Text.Length > 0) >= 15) songs.Add(f);
        }
        catch { }
        if (songs.Count >= count) break;
    }
    File.WriteAllLines(Path.Combine(AppContext.BaseDirectory, "songs.txt"), songs);
    Console.WriteLine($"picked {songs.Count} songs -> {Path.Combine(AppContext.BaseDirectory, "songs.txt")}");
}

var all = new List<double>();
using var engine = new LyricsSyncEngine(model, vocalModel);
using var transcriber = new WhisperTranscriber(model);
foreach (var song in songs)
{
    string lyrics;
    using (var tag = TagLib.File.Create(song)) lyrics = tag.Tag.Lyrics ?? "";
    var truth = LrcParser.Parse(lyrics).Where(l => l.Text.Length > 0).OrderBy(l => l.Time).ToList();
    var plain = string.Join("\n", truth.Select(l => l.Text));
    var clock = Stopwatch.StartNew();
    var result = cache is null ? await engine.SyncAsync(song, plain, null, CancellationToken.None) : await SyncCached(song, plain);
    if (result.Count != truth.Count) { Console.WriteLine($"{Path.GetFileName(song)}: line count {result.Count} vs {truth.Count}"); continue; }
    var errors = truth.Select((l, i) => Math.Abs((result[i].Time - l.Time).TotalSeconds)).ToList();
    all.AddRange(errors);
    Console.WriteLine($"{Path.GetFileName(song),-50} {Summary(errors)}  confident={result.Count(r => r.IsConfident)}/{result.Count}  {clock.Elapsed.TotalSeconds:0}s");
    if (Environment.GetEnvironmentVariable("BENCH_DETAIL") == "1")
        for (var i = 0; i < truth.Count; i++)
            Console.WriteLine($"   {truth[i].Time.TotalSeconds,7:0.00} {result[i].Time.TotalSeconds,7:0.00} {(result[i].IsConfident ? ' ' : '?')} {errors[i],5:0.00}  {truth[i].Text}");
}
Console.WriteLine($"{"ALL",-50} {Summary(all)}");

// The engine's own steps, with the vocals and what was heard kept in the cache folder.
async Task<List<AlignedLine>> SyncCached(string song, string plain)
{
    var lines = LrcWriter.PlainLines(plain);
    var name = Path.Combine(cache!, Path.GetFileName(song));

    float[]? mix = null;
    float[] Mix() => mix ??= AudioDecoder.ReadMono16k(song);
    TimeSpan duration;
    if (File.Exists(name + ".duration")) duration = TimeSpan.FromSeconds(double.Parse(File.ReadAllText(name + ".duration"), CultureInfo.InvariantCulture));
    else
    {
        duration = TimeSpan.FromSeconds(Mix().Length / (double)AudioDecoder.SampleRate);
        File.WriteAllText(name + ".duration", duration.TotalSeconds.ToString(CultureInfo.InvariantCulture));
    }

    float[]? vocals = null;
    if (vocalModel is not null)
    {
        if (File.Exists(name + ".vocals.f32")) vocals = MemoryMarshal.Cast<byte, float>(File.ReadAllBytes(name + ".vocals.f32")).ToArray();
        else
        {
            vocals = engine.SeparateVocals(song, null, CancellationToken.None);
            File.WriteAllBytes(name + ".vocals.f32", MemoryMarshal.AsBytes(vocals.AsSpan()).ToArray());
        }
    }
    var windows = vocals is null ? null : VoiceActivity.Detect(vocals).Windows();

    var passes = LyricsSyncEngine.Passes(vocals is not null);
    if (Environment.GetEnvironmentVariable("BENCH_ALL_PASSES") == "1")
        foreach (var pass in passes) await Listen(pass);
    return await LyricsSyncEngine.SyncAsync(lines, duration, passes, Listen);

    async Task<List<TimedText>> Listen(LyricsSyncEngine.Pass pass)
    {
        var file = $"{name}.{pass.Source}.{(pass.Hinted ? "hinted" : "free")}.json";
        if (File.Exists(file)) return JsonSerializer.Deserialize<List<TimedText>>(File.ReadAllText(file))!;
        var heard = await transcriber.TranscribeAsync(pass.Source == LyricsSyncEngine.Source.Vocals ? vocals! : Mix(),
            LyricsSyncEngine.LanguageOf(lines), pass.Hinted ? LyricsSyncEngine.Prompt(lines) : null, null, CancellationToken.None, windows);
        File.WriteAllText(file, JsonSerializer.Serialize(heard));
        return heard;
    }
}

static string Summary(List<double> e)
{
    if (e.Count == 0) return "-";
    var s = e.OrderBy(x => x).ToList();
    double Share(double t) => 100.0 * e.Count(x => x <= t) / e.Count;
    return $"n={e.Count,3} median={s[s.Count / 2],5:0.00}s  <=0.3s {Share(0.3),3:0}%  <=0.5s {Share(0.5),3:0}%  <=1s {Share(1),3:0}%  >3s {100 - Share(3),3:0}%";
}
