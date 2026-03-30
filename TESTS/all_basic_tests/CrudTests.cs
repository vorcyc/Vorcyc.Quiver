using static AllBasicTests.TestHelper;

namespace AllBasicTests;

/// <summary>测试 6：Upsert + 删除持久化测试（多向量）。</summary>
public static class CrudTests
{
    public static async Task RunAsync()
    {
        Console.WriteLine("\n═══ 6. Upsert + 删除持久化测试（多向量）═══");

        for (int f = 0; f < Formats.Length; f++)
        {
            var format = Formats[f];
            var path = $"test_crud{Extensions[f]}";
            var random = new Random(42);

            var db = new MyMultiVectorDb(path, format);

            for (int i = 0; i < 200; i++)
            {
                db.Items.Add(new MultiVectorEntity
                {
                    Id = $"C{i:D3}",
                    Label = $"原始_{i}",
                    Score = i,
                    IsActive = true,
                    TextEmbedding = RandomVector(random, 384),
                    ImageEmbedding = RandomVector(random, 512),
                    AudioEmbedding = RandomVector(random, 256)
                });
            }

            // Upsert 前 100 条
            for (int i = 0; i < 100; i++)
            {
                db.Items.Upsert(new MultiVectorEntity
                {
                    Id = $"C{i:D3}",
                    Label = $"已更新_{i}",
                    Score = i + 1000,
                    IsActive = false,
                    TextEmbedding = RandomVector(random, 384),
                    ImageEmbedding = RandomVector(random, 512),
                    AudioEmbedding = RandomVector(random, 256)
                });
            }

            // 删除后 50 条（C150~C199）
            for (int i = 150; i < 200; i++)
                db.Items.RemoveByKey($"C{i:D3}");

            Assert(db.Items.Count == 150, $"[{format}] CRUD 后内存数量 150");

            await db.SaveAsync();
            var dbRead = new MyMultiVectorDb(path, format);
            await dbRead.LoadAsync();

            Assert(dbRead.Items.Count == 150, $"[{format}] 持久化后加载数量 150");

            var upserted = dbRead.Items.Find("C000");
            Assert(upserted?.Label == "已更新_0" && upserted.Score > 999,
                $"[{format}] Upsert 数据持久化正确");

            var untouched = dbRead.Items.Find("C100");
            Assert(untouched?.Label == "原始_100" && untouched.IsActive,
                $"[{format}] 未 Upsert 的数据不变");

            Assert(dbRead.Items.Find("C199") == null, $"[{format}] 已删除数据不存在");

            File.Delete(path);
        }
    }
}
