using Vorcyc.Quiver;
using static AllBasicTests.TestHelper;

namespace AllBasicTests;

/// <summary>测试 3：多向量分字段搜索测试。</summary>
public static class SearchTests
{
    public static async Task RunAsync()
    {
        Console.WriteLine("\n═══ 3. 多向量分字段搜索测试（1,000 条）═══");

        for (int f = 0; f < Formats.Length; f++)
        {
            var format = Formats[f];
            var path = $"test_multi_search{Extensions[f]}";
            var random = new Random(42);

            var db = new MyMultiVectorDb(path, format);
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

            var dbRead = new MyMultiVectorDb(path, format);
            await dbRead.LoadAsync();

            random = new Random(777);
            var textQuery = RandomVector(random, 384);
            var imageQuery = RandomVector(random, 512);
            var audioQuery = RandomVector(random, 256);

            // 保存前各字段 Top-5
            var textBefore = db.Items.Search(e => e.TextEmbedding, textQuery, 5);
            var imageBefore = db.Items.Search(e => e.ImageEmbedding, imageQuery, 5);
            var audioBefore = db.Items.Search(e => e.AudioEmbedding, audioQuery, 5);

            // 加载后各字段 Top-5
            var textAfter = dbRead.Items.Search(e => e.TextEmbedding, textQuery, 5);
            var imageAfter = dbRead.Items.Search(e => e.ImageEmbedding, imageQuery, 5);
            var audioAfter = dbRead.Items.Search(e => e.AudioEmbedding, audioQuery, 5);

            Assert(RankEqual(textBefore, textAfter, e => e.Id),
                $"[{format}] TextEmbedding(384d) Top-5 排名一致");
            Assert(RankEqual(imageBefore, imageAfter, e => e.Id),
                $"[{format}] ImageEmbedding(512d) Top-5 排名一致");
            Assert(RankEqual(audioBefore, audioAfter, e => e.Id),
                $"[{format}] AudioEmbedding(256d) Top-5 排名一致");

            // 三字段搜索结果应彼此不同（不同向量空间）
            var textTop1 = textAfter[0].Entity.Id;
            var imageTop1 = imageAfter[0].Entity.Id;
            var audioTop1 = audioAfter[0].Entity.Id;
            var anyDifferent = textTop1 != imageTop1 || imageTop1 != audioTop1;
            Assert(anyDifferent, $"[{format}] 三字段搜索结果互不相同（不同向量空间独立检索）");

            File.Delete(path);
        }
    }

    private static bool RankEqual<T>(List<QuiverSearchResult<T>> a, List<QuiverSearchResult<T>> b, Func<T, string> getId)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
            if (getId(a[i].Entity) != getId(b[i].Entity)) return false;
        return true;
    }
}
