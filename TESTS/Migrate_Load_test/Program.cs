using NiubiServer.Database;
using System.Diagnostics;
using Vorcyc.Mathematics.Framework.Utilities;
using Vorcyc.Quiver;

//await MigrationRunner.Run();

var sw = Stopwatch.StartNew();

await using var db = new AudioDbContext(@"G:\niubi_media_v4.vdb");
await db.LoadAsync();

sw.Stop();

Console.WriteLine("load done");
Console.WriteLine(sw.Elapsed);

Console.WriteLine(db.Audios.Count);


await db.SaveAsync();

var seed = db.Audios.FirstOrDefault(x => x.Title == "忍者" && x.Artist == "周杰伦");
Console.WriteLine(seed.AudioFilePath);

//var isZeroVector = seed.MertEmbedding.All(e => e == 0f);
//Console.WriteLine(isZeroVector);
seed.MertEmbedding.PrintLine();

if (seed?.MertEmbedding is not null)
{
    var similar = db.Audios.Search(
        x => x.MertEmbedding!,
        seed.MertEmbedding,
        topK: 10);

    foreach (var item in similar)
    {
        Console.WriteLine($"{item.Entity.AudioFilePath} - {item.Similarity}");
    }
}


public class AudioDbContext : QuiverDbContext
{
    public QuiverSet<AudioMediaEntity> Audios { get; set; } = null!;

    public AudioDbContext(string databasePath) : base(new QuiverDbOptions
    {
        DatabasePath = databasePath,
        DefaultMetric = DistanceMetric.Cosine,
        LargeFields = { MemoryMode = GlobalLargeFieldMemoryMode.InMemory },
        Vectors = { MemoryMode = GlobalVectorMemoryMode.MemoryMapped },
    })
    { }
}
