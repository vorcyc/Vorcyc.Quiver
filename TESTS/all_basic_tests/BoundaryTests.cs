using static AllBasicTests.TestHelper;

namespace AllBasicTests;

/// <summary>测试 5：边界条件测试。</summary>
public static class BoundaryTests
{
    public static async Task RunAsync()
    {
        Console.WriteLine("\n═══ 5. 边界条件测试 ═══");

        for (int f = 0; f < Formats.Length; f++)
        {
            var format = Formats[f];

            // 空数据库
            var emptyPath = $"test_empty{Extensions[f]}";
            var dbEmpty = new MyMultiVectorDb(emptyPath, format);
            await dbEmpty.SaveAsync();
            Assert(File.Exists(emptyPath), $"[{format}] 空数据库文件已创建");
            var dbEmptyRead = new MyMultiVectorDb(emptyPath, format);
            await dbEmptyRead.LoadAsync();
            Assert(dbEmptyRead.Items.Count == 0, $"[{format}] 空数据库加载后数量为 0");
            File.Delete(emptyPath);

            // 文件不存在
            var noFile = $"nonexistent{Extensions[f]}";
            try
            {
                var dbNo = new MyMultiVectorDb(noFile, format);
                await dbNo.LoadAsync();
                Assert(dbNo.Items.Count == 0, $"[{format}] 文件不存在时静默返回");
            }
            catch (Exception ex)
            {
                Assert(false, $"[{format}] 文件不存在时不应抛异常：{ex.Message}");
            }
        }
    }
}
