using static AllBasicTests.TestHelper;

namespace AllBasicTests;

/// <summary>测试 5：边界条件测试。</summary>
public static class BoundaryTests
{
    public static async Task RunAsync()
    {
        Console.WriteLine("\n═══ 5. 边界条件测试 ═══");

        // 空数据库
        var emptyPath = "test_empty.vdb";
        var dbEmpty = new MyMultiVectorDb(emptyPath);
        await dbEmpty.SaveAsync();
        Assert(File.Exists(emptyPath), "空数据库文件已创建");
        var dbEmptyRead = new MyMultiVectorDb(emptyPath);
        await dbEmptyRead.LoadAsync();
        Assert(dbEmptyRead.Items.Count == 0, "空数据库加载后数量为 0");
        File.Delete(emptyPath);

        // 文件不存在
        try
        {
            var dbNo = new MyMultiVectorDb("nonexistent.vdb");
            await dbNo.LoadAsync();
            Assert(dbNo.Items.Count == 0, "文件不存在时静默返回");
        }
        catch (Exception ex)
        {
            Assert(false, $"文件不存在时不应抛异常：{ex.Message}");
        }

        // 搜索参数边界
        var searchPath = "test_search_boundaries.vdb";
        try
        {
            var db = new MyFaceDb(searchPath);
            db.Faces.Add(new FaceFeature
            {
                PersonId = "B001",
                Name = "Boundary",
                Embedding = RandomVector(new Random(5), 128)
            });

            var query = RandomVector(new Random(6), 128);
            AssertThrows<ArgumentOutOfRangeException>(() => db.Faces.Search(query, 0), "Search topK=0 抛出 ArgumentOutOfRangeException");
            AssertThrows<ArgumentOutOfRangeException>(() => db.Faces.Search(query, -1), "Search topK<0 抛出 ArgumentOutOfRangeException");
            AssertThrows<ArgumentOutOfRangeException>(() => db.Faces.Search(e => e.Embedding, query, 1, _ => true, 0), "Search overFetchMultiplier=0 抛出 ArgumentOutOfRangeException");
            AssertThrows<ArgumentException>(() => db.Faces.SearchByThreshold(e => e.Embedding, query, float.NaN), "SearchByThreshold threshold=NaN 抛出 ArgumentException");
            AssertThrows<ArgumentNullException>(() => db.Faces.Search(null!, 1), "Search queryVector=null 抛出 ArgumentNullException");
        }
        finally
        {
            if (File.Exists(searchPath)) File.Delete(searchPath);
        }
    }

    private static void AssertThrows<TException>(Action action, string testName)
        where TException : Exception
    {
        try
        {
            action();
            Assert(false, testName);
        }
        catch (TException)
        {
            Assert(true, testName);
        }
        catch (Exception ex)
        {
            Assert(false, $"{testName}（实际异常：{ex.GetType().Name}）");
        }
    }
}
