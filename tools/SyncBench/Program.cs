// Measures how close "싱크 가사 만들기" gets to real synced lyrics: songs that already have
// LRC lyrics are re-synced from their plain text, and each line's time is compared.
//   dotnet run --project tools/SyncBench -- <model.bin> <songs.txt | folder> [count]
// songs.txt: one audio path per line (made from a folder on the first run, then reused, so
// runs compare the same songs).
using System.Diagnostics;
using TuneSync.Core.Services;
using TuneSync.Core.Services.LyricsSync;

var outPath = Environment.GetEnvironmentVariable("BENCH_OUT");
if (!string.IsNullOrEmpty(outPath)) Console.SetOut(new StreamWriter(outPath, false, new System.Text.UTF8Encoding(false)) { AutoFlush = true });
var model = args[0];
var list = args[1];
var count = args.Length > 2 ? int.Parse(args[2]) : 8;
WhisperModelStore.Folder = Path.GetDirectoryName(model)!;

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
using var engine = new LyricsSyncEngine(model);
foreach (var song in songs)
{
    string lyrics;
    using (var tag = TagLib.File.Create(song)) lyrics = tag.Tag.Lyrics ?? "";
    var truth = LrcParser.Parse(lyrics).Where(l => l.Text.Length > 0).OrderBy(l => l.Time).ToList();
    var plain = string.Join("\n", truth.Select(l => l.Text));
    var clock = Stopwatch.StartNew();
    var result = await engine.SyncAsync(song, plain, null, CancellationToken.None);
    if (result.Count != truth.Count) { Console.WriteLine($"{Path.GetFileName(song)}: line count {result.Count} vs {truth.Count}"); continue; }
    var errors = truth.Select((l, i) => Math.Abs((result[i].Time - l.Time).TotalSeconds)).ToList();
    all.AddRange(errors);
    Console.WriteLine($"{Path.GetFileName(song),-50} {Summary(errors)}  confident={result.Count(r => r.IsConfident)}/{result.Count}  {clock.Elapsed.TotalSeconds:0}s");
    if (Environment.GetEnvironmentVariable("BENCH_DETAIL") == "1")
        for (var i = 0; i < truth.Count; i++)
            Console.WriteLine($"   {truth[i].Time.TotalSeconds,7:0.00} {result[i].Time.TotalSeconds,7:0.00} {(result[i].IsConfident ? ' ' : '?')} {errors[i],5:0.00}  {truth[i].Text}");
}
Console.WriteLine($"{"ALL",-50} {Summary(all)}");

static string Summary(List<double> e)
{
    if (e.Count == 0) return "-";
    var s = e.OrderBy(x => x).ToList();
    double Share(double t) => 100.0 * e.Count(x => x <= t) / e.Count;
    return $"n={e.Count,3} median={s[s.Count / 2],5:0.00}s  <=0.3s {Share(0.3),3:0}%  <=0.5s {Share(0.5),3:0}%  <=1s {Share(1),3:0}%  >3s {100 - Share(3),3:0}%";
}
