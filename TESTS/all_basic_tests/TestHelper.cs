using Vorcyc.Quiver;

namespace AllBasicTests;

/// <summary>
/// 共享测试基础设施：断言、随机向量生成、格式数组、计数器。
/// </summary>
public static class TestHelper
{
    private static int _passed;
    private static int _failed;

    public static int Passed => _passed;
    public static int Failed => _failed;

    public static readonly StorageFormat[] Formats = [StorageFormat.Json, StorageFormat.Xml, StorageFormat.Binary];
    public static readonly string[] Extensions = [".json", ".xml", ".vdb"];

    public static void Assert(bool condition, string testName)
    {
        if (condition)
        {
            Interlocked.Increment(ref _passed);
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"  ✔ {testName}");
        }
        else
        {
            Interlocked.Increment(ref _failed);
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"  ✘ {testName}");
        }
        Console.ResetColor();
    }

    /// <summary>使用指定 Random 实例生成随机向量（非线程安全）。</summary>
    public static float[] RandomVector(Random random, int dim)
    {
        var v = new float[dim];
        for (int i = 0; i < dim; i++) v[i] = random.NextSingle() * 2 - 1;
        return v;
    }

    /// <summary>使用 Random.Shared 生成随机向量（线程安全）。</summary>
    public static float[] ThreadSafeRandomVector(int dim)
    {
        var rng = Random.Shared;
        var v = new float[dim];
        for (int i = 0; i < dim; i++) v[i] = rng.NextSingle() * 2 - 1;
        return v;
    }

    public static void PrintSummary()
    {
        Console.WriteLine($"\n{"",3}══════════════════════════════════════════════════");
        Console.ForegroundColor = _passed > 0 && _failed == 0 ? ConsoleColor.Green : ConsoleColor.Yellow;
        Console.WriteLine($"  测试完成：{_passed} 通过，{_failed} 失败，共 {_passed + _failed} 项");
        Console.ResetColor();
    }
}
