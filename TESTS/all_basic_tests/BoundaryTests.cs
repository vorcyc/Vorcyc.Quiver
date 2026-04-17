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
    }
}
