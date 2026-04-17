using Vorcyc.Quiver;
using static AllBasicTests.TestHelper;

namespace AllBasicTests;

/// <summary>测试 3：多向量分字段搜索测试。</summary>
public static class SearchTests
{
    public static async Task RunAsync()
    {
        Console.WriteLine("\n═══ 3. 多向量分字段搜索测试（1,000 条）═══");

        var path = "test_multi_search.vdb";
        var random = new Random(42);

        var db = new MyMultiVectorDb(path);
        for (int i = 0; i < 1000; i++)
        {
            db.Items.Add(new MultiVectorEntity
            {
                Id = $"MS{i:D4}",
                Label = $"搜索实体_{i}",
                Score = i,
                IsActive = true,
                TextEmbedding = RandomVector(random, 384),
                ImageEmbedding = RandomVector(random, 512),
                AudioEmbedding = RandomVector(random, 256)
            });
        }
        await db.SaveAsync();

        var dbRead = new MyMultiVectorDb(path);
        await dbRead.LoadAsync();

        random = new Random(777);
        var textQuery = RandomVector(random, 384);
        var imageQuery = RandomVector(random, 512);
        var audioQuery = RandomVector(random, 256);

        var textBefore = db.Items.Search(e => e.TextEmbedding, textQuery, 5);
        var imageBefore = db.Items.Search(e => e.ImageEmbedding, imageQuery, 5);
        var audioBefore = db.Items.Search(e => e.AudioEmbedding, audioQuery, 5);

        var textAfter = dbRead.Items.Search(e => e.TextEmbedding, textQuery, 5);
        var imageAfter = dbRead.Items.Search(e => e.ImageEmbedding, imageQuery, 5);
        var audioAfter = dbRead.Items.Search(e => e.AudioEmbedding, audioQuery, 5);

        Assert(RankEqual(textBefore, textAfter, e => e.Id), "TextEmbedding(384d) Top-5 排名一致");
        Assert(RankEqual(imageBefore, imageAfter, e => e.Id), "ImageEmbedding(512d) Top-5 排名一致");
        Assert(RankEqual(audioBefore, audioAfter, e => e.Id), "AudioEmbedding(256d) Top-5 排名一致");

        var textTop1 = textAfter[0].Entity.Id;
        var imageTop1 = imageAfter[0].Entity.Id;
        var audioTop1 = audioAfter[0].Entity.Id;
        Assert(textTop1 != imageTop1 || imageTop1 != audioTop1, "三字段搜索结果互不相同（不同向量空间独立检索）");

        File.Delete(path);
    }

    private static bool RankEqual<T>(List<QuiverSearchResult<T>> a, List<QuiverSearchResult<T>> b, Func<T, string> getId)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
            if (getId(a[i].Entity) != getId(b[i].Entity)) return false;
        return true;
    }
}
