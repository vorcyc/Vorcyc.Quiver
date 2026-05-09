using Vorcyc.Quiver;
using static AllBasicTests.TestHelper;

namespace AllBasicTests;

public static partial class MemoryModeValidationTests
{
    public static Task RunAsync()
    {
        Test_LargeField_LazyLoad_RequiresDatabasePath();
        Test_Vector_MemoryMapped_NonPartial_Throws();
        Test_LargeField_PerField_InMemory_Succeeds();
        Test_LargeField_PerField_LazyLoad_PartialAccessor_Succeeds();
        Test_Vector_PerField_MemoryMapped_Succeeds();
        Test_Vector_Auto_SmallFile_UsesInMemory();
        Test_Vector_Nullable_AllowsNullAndSkipsIndex();
        Test_Vector_NonNullable_NullThrows();
        return Task.CompletedTask;
    }

    private static void Test_LargeField_LazyLoad_RequiresDatabasePath()
    {
        Console.WriteLine("\n═══ MemoryMode validation: LargeField LazyLoad requires DatabasePath ═══");
        Assert(Throws<InvalidOperationException>(() => _ = new LargeFieldLazyDb()),
            "LargeFieldMemoryMode.LazyLoad 无 DatabasePath 时抛 InvalidOperationException");
    }

    private static void Test_Vector_MemoryMapped_NonPartial_Throws()
    {
        Console.WriteLine("\n═══ MemoryMode validation: MemoryMapped requires generated accessor ═══");
        Assert(Throws<InvalidOperationException>(() => _ = new NonPartialMappedVectorDb()),
            "VectorMemoryMode.MemoryMapped + 非 partial vector 属性抛清晰异常");
    }

    private static void Test_LargeField_PerField_InMemory_Succeeds()
    {
        Console.WriteLine("\n═══ MemoryMode validation: LargeField PerField/InMemory succeeds ═══");
        Assert(!Throws<Exception>(() => _ = new LargeFieldPerFieldInMemoryDb()),
            "LargeFieldMemoryMode.PerField + 字段 InMemory 可构造");
    }

    private static void Test_LargeField_PerField_LazyLoad_PartialAccessor_Succeeds()
    {
        Console.WriteLine("\n═══ MemoryMode validation: LargeField PerField/LazyLoad partial accessor succeeds ═══");
        Assert(!Throws<Exception>(() => _ = new LargeFieldPerFieldLazyDb()),
            "GlobalLargeFieldMemoryMode.PerField + 字段 LazyLoad partial accessor 可构造");
    }

    private static void Test_Vector_PerField_MemoryMapped_Succeeds()
    {
        Console.WriteLine("\n═══ MemoryMode validation: Vector PerField/MemoryMapped succeeds ═══");
        var path = "per_field_mapped_vector_validation.vdb";
        if (File.Exists(path)) File.Delete(path);
        Assert(!Throws<Exception>(() =>
        {
            using var db = new PerFieldMappedVectorDb(path);
            db.Items.Add(new PerFieldMappedVectorEntity { Id = "ok", Embedding = FilledVector(4, 1f) });
        }), "GlobalVectorMemoryMode.PerField + 字段 MemoryMapped 可构造/写入");
        if (File.Exists(path)) File.Delete(path);
    }

    private static void Test_Vector_Auto_SmallFile_UsesInMemory()
    {
        Console.WriteLine("\n═══ MemoryMode validation: Vector Auto small file uses InMemory ═══");
        var path = "auto_small_vector_validation.vdb";
        if (File.Exists(path)) File.Delete(path);
        Assert(!Throws<Exception>(() =>
        {
            using var db = new AutoSmallVectorDb(path);
            db.Items.Add(new NonPartialMappedVectorEntity { Id = "ok", Embedding = FilledVector(4, 1f) });
        }), "GlobalVectorMemoryMode.Auto 在文件不存在/小文件时回退 InMemory，不要求 partial");
        if (File.Exists(path)) File.Delete(path);
    }

    private static void Test_Vector_Nullable_AllowsNullAndSkipsIndex()
    {
        Console.WriteLine("\n═══ MemoryMode validation: nullable vector skips index ═══");
        using var db = new NullableVectorDb();
        db.Items.Add(new NullableVectorEntity { Id = "null", Embedding = null });
        db.Items.Add(new NullableVectorEntity { Id = "vec", Embedding = FilledVector(4, 1f) });

        var results = db.Items.Search(x => x.Embedding!, FilledVector(4, 1f), 10);
        Assert(db.Items.Count == 2, "Nullable vector 为 null 的实体仍可写入");
        Assert(results.Count == 1 && results[0].Entity.Id == "vec", "Nullable null vector 不进入索引/搜索结果");
    }

    private static void Test_Vector_NonNullable_NullThrows()
    {
        Console.WriteLine("\n═══ MemoryMode validation: non-nullable vector rejects null ═══");
        using var db = new NonNullableVectorDb();
        Assert(Throws<ArgumentNullException>(() => db.Items.Add(new NonNullableVectorEntity { Id = "bad", Embedding = null })),
            "默认非 Nullable vector 为 null 时抛 ArgumentNullException");
    }

    private static bool Throws<TException>(Action action) where TException : Exception
    {
        try
        {
            action();
            return false;
        }
        catch (TException)
        {
            return true;
        }
        catch (System.Reflection.TargetInvocationException ex) when (ex.InnerException is TException)
        {
            return true;
        }
    }

public class LargeFieldPerFieldInMemoryDb() : QuiverDbContext(new QuiverDbOptions
{
    LargeFields = { MemoryMode = GlobalLargeFieldMemoryMode.PerField }
})
{
    public QuiverSet<LargeFieldPerFieldInMemoryEntity> Items { get; set; } = null!;
}

public class LargeFieldPerFieldInMemoryEntity
{
    [QuiverKey] public string Id { get; set; } = string.Empty;
    [QuiverLargeField(MemoryMode = LargeFieldMemoryMode.InMemory)] public byte[]? Payload { get; set; }
    [QuiverVector(4)] public float[] Embedding { get; set; } = [];
}

public class LargeFieldPerFieldLazyDb() : QuiverDbContext(new QuiverDbOptions
{
    DatabasePath = "large_field_per_field_lazy_validation.vdb",
    LargeFields = { MemoryMode = GlobalLargeFieldMemoryMode.PerField }
})
{
    public QuiverSet<LargeFieldPerFieldLazyEntity> Items { get; set; } = null!;
}

public partial class LargeFieldPerFieldLazyEntity
{
    [QuiverKey] public string Id { get; set; } = string.Empty;
    [QuiverLargeField(MemoryMode = LargeFieldMemoryMode.LazyLoad)] public partial byte[]? Payload { get; set; }
    [QuiverVector(4)] public float[] Embedding { get; set; } = [];
}

    private static float[] FilledVector(int dim, float value)
    {
        var vector = new float[dim];
        Array.Fill(vector, value);
        return vector;
    }

public class AutoSmallVectorDb(string path) : QuiverDbContext(new QuiverDbOptions
{
    DatabasePath = path,
    Vectors =
    {
        MemoryMode = GlobalVectorMemoryMode.Auto,
        MemoryMapThresholdBytes = long.MaxValue
    }
})
{
    public QuiverSet<NonPartialMappedVectorEntity> Items { get; set; } = null!;
}

public class PerFieldMappedVectorDb(string path) : QuiverDbContext(new QuiverDbOptions
{
    DatabasePath = path,
    Vectors = { MemoryMode = GlobalVectorMemoryMode.PerField }
})
{
    public QuiverSet<PerFieldMappedVectorEntity> Items { get; set; } = null!;
}

public partial class PerFieldMappedVectorEntity
{
    [QuiverKey] public string Id { get; set; } = string.Empty;
    [QuiverVector(4, MemoryMode = VectorMemoryMode.MemoryMapped)] public partial float[]? Embedding { get; set; }
}
}

public class LargeFieldLazyDb() : QuiverDbContext(new QuiverDbOptions
{
    LargeFields = { MemoryMode = GlobalLargeFieldMemoryMode.LazyLoad }
})
{
    public QuiverSet<LargeFieldEntity> Items { get; set; } = null!;
}

public class LargeFieldEntity
{
    [QuiverKey] public string Id { get; set; } = string.Empty;
    [QuiverLargeField] public byte[]? Payload { get; set; }
    [QuiverVector(4)] public float[] Embedding { get; set; } = [];
}

public class NonPartialMappedVectorDb() : QuiverDbContext(new QuiverDbOptions
{
    DatabasePath = "non_partial_mapped_vector_validation.vdb",
    Vectors = { MemoryMode = GlobalVectorMemoryMode.MemoryMapped }
})
{
    public QuiverSet<NonPartialMappedVectorEntity> Items { get; set; } = null!;
}

public class NonPartialMappedVectorEntity
{
    [QuiverKey] public string Id { get; set; } = string.Empty;
    [QuiverVector(4)] public float[] Embedding { get; set; } = [];
}

public class NullableVectorDb : QuiverDbContext
{
    public QuiverSet<NullableVectorEntity> Items { get; set; } = null!;
}

public class NullableVectorEntity
{
    [QuiverKey] public string Id { get; set; } = string.Empty;
    [QuiverVector(4, Nullable = true)] public float[]? Embedding { get; set; }
}

public class NonNullableVectorDb : QuiverDbContext
{
    public QuiverSet<NonNullableVectorEntity> Items { get; set; } = null!;
}

public class NonNullableVectorEntity
{
    [QuiverKey] public string Id { get; set; } = string.Empty;
    [QuiverVector(4)] public float[]? Embedding { get; set; }
}
