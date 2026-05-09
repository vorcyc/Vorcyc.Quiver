using System.Runtime.InteropServices;
using Vorcyc.Quiver;
using Vorcyc.Quiver.Migration;
using static AllBasicTests.TestHelper;

namespace AllBasicTests;

/// <summary>
/// Schema 迁移专项测试。
/// <para>
/// 通过 WriteLegacyQdb 手写旧格式 QDB 二进制文件，
/// 以精确模拟历史 Schema（属性重命名、类型演变、字段删除/新增），
/// 再通过带迁移规则的上下文加载，验证 BinaryStorageProvider 的迁移逻辑。
/// </para>
/// <para>覆盖场景：</para>
/// <list type="number">
///   <item>属性重命名（OldTitle → Title）</item>
///   <item>值类型自动强转（int Score → double Score）</item>
///   <item>字段删除（Legacy 字段在新版不存在，加载时自动跳过）</item>
///   <item>字段新增（NewField 在旧文件中不存在，加载后取默认值 "default"）</item>
/// </list>
/// </para>
/// </summary>
public static class MigrationTests
{
    // ── 旧版 Schema TypeCode 常量（对应 BinaryStorageProvider.TypeCode 枚举值） ──
    private const byte TC_String     = 0;   // TypeCode.String
    private const byte TC_Int32      = 1;   // TypeCode.Int32
    private const byte TC_FloatArray = 9;   // TypeCode.FloatArray

    /// <summary>
    /// 手写一个旧格式 QDB 文件（当前实体类型名称 + 旧 Schema 属性 Layout）。
    /// 旧属性（按名称字母序）：Embedding(float[]), Id(string), Legacy(string), OldTitle(string), Score(int)
    /// </summary>
    private static void WriteLegacyQdb(string path,
        IEnumerable<(string Id, string OldTitle, int Score, string Legacy, float[] Embedding)> entities)
    {
        var magic = new byte[] { (byte)'Q', (byte)'D', (byte)'B', 0x03 };

        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        using var bw = new BinaryWriter(fs);

        bw.Write(magic);
        bw.Write(1);  // SetCount

        var typeName = typeof(MigrationEntity).FullName!;
        bw.Write(typeName);

        // 旧属性描述符（5 个），字母序：Embedding, Id, Legacy, OldTitle, Score
        bw.Write(5);
        bw.Write("Embedding");  bw.Write(TC_FloatArray);
        bw.Write("Id");         bw.Write(TC_String);
        bw.Write("Legacy");     bw.Write(TC_String);
        bw.Write("OldTitle");   bw.Write(TC_String);
        bw.Write("Score");      bw.Write(TC_Int32);

        var list = entities.ToList();
        bw.Write(list.Count);

        foreach (var (id, oldTitle, score, legacy, embedding) in list)
        {
            // Embedding (float[])
            bw.Write(true);
            bw.Write(embedding.Length);
            bw.Write(System.Runtime.InteropServices.MemoryMarshal.AsBytes(embedding.AsSpan()));

            // Id
            bw.Write(true);
            bw.Write(id);

            // Legacy
            bw.Write(true);
            bw.Write(legacy);

            // OldTitle
            bw.Write(true);
            bw.Write(oldTitle);

            // Score (int)
            bw.Write(true);
            bw.Write(score);
        }
    }

    public static async Task RunAsync()
    {
        await Test_RenameProperty();
        await Test_TypeCoercion();
        await Test_RemovedField();
        await Test_AddedField();
    }

    // ==================== 场景 1：属性重命名 ====================
    private static async Task Test_RenameProperty()
    {
        Console.WriteLine("\n═══ Migration-1. 属性重命名（OldTitle → Title）═══");

        var path = Path.GetTempFileName() + ".vdb";
        try
        {
            var rng = new Random(1);
            WriteLegacyQdb(path,
            [
                ("R00", "标题_0", 0,  "lg0", RandomVector(rng, 32)),
                ("R01", "标题_1", 10, "lg1", RandomVector(rng, 32)),
                ("R05", "标题_5", 50, "lg5", RandomVector(rng, 32)),
            ]);

            await MigrateLegacyAsync(path);

            var db = new MigrationDb(path);
            await db.LoadAsync();

            Assert(db.Items.Count == 3, "Migration-1: 加载实体数量正确（3 条）");
            var item = db.Items.Find("R05");
            Assert(item != null, "Migration-1: Find(R05) 成功");
            Assert(item?.Title == "标题_5",
                $"Migration-1: OldTitle 已重命名为 Title（实际：'{item?.Title}'）");
        }
        finally { Cleanup(path); }
    }

    // ==================== 场景 2：值类型自动强转（int → double）====================
    private static async Task Test_TypeCoercion()
    {
        Console.WriteLine("\n═══ Migration-2. 值类型自动强转（int Score → double Score）═══");

        var path = Path.GetTempFileName() + ".vdb";
        try
        {
            var rng = new Random(2);
            WriteLegacyQdb(path,
            [
                ("T00", "类型测试_0", 100, "x", RandomVector(rng, 32)),
                ("T01", "类型测试_1", 200, "x", RandomVector(rng, 32)),
                ("T02", "类型测试_2", 300, "x", RandomVector(rng, 32)),
            ]);

            await MigrateLegacyAsync(path);

            var db = new MigrationDb(path);
            await db.LoadAsync();

            var item = db.Items.Find("T02");
            Assert(item != null, "Migration-2: Find(T02) 成功");
            Assert(item?.Score == 300.0,
                $"Migration-2: int Score 自动强转为 double（实际：{item?.Score}）");
        }
        finally { Cleanup(path); }
    }

    // ==================== 场景 3：字段删除（加载时自动跳过）====================
    private static async Task Test_RemovedField()
    {
        Console.WriteLine("\n═══ Migration-3. 字段删除（Legacy 字段加载时自动跳过）═══");

        var path = Path.GetTempFileName() + ".vdb";
        try
        {
            var rng = new Random(3);
            WriteLegacyQdb(path,
            [
                ("D01", "删除测试", 42, "这个字段在新版中不存在", RandomVector(rng, 32)),
            ]);

            await MigrateLegacyAsync(path);

            var db = new MigrationDb(path);
            Exception? ex = null;
            try { await db.LoadAsync(); }
            catch (Exception e) { ex = e; }

            Assert(ex == null, $"Migration-3: 含已删除字段的文件加载无异常（ex={ex?.Message}）");
            Assert(db.Items.Count == 1, "Migration-3: 加载后实体数量正确（1 条）");
            Assert(db.Items.Find("D01") != null, "Migration-3: Find(D01) 成功");
        }
        finally { Cleanup(path); }
    }

    // ==================== 场景 4：字段新增（加载后取默认值）====================
    private static async Task Test_AddedField()
    {
        Console.WriteLine("\n═══ Migration-4. 字段新增（NewField 取默认值 \"default\"）═══");

        var path = Path.GetTempFileName() + ".vdb";
        try
        {
            var rng = new Random(4);
            WriteLegacyQdb(path,
            [
                ("N01", "新增字段测试", 7, "old", RandomVector(rng, 32)),
            ]);

            await MigrateLegacyAsync(path);

            var db = new MigrationDb(path);
            await db.LoadAsync();

            var item = db.Items.Find("N01");
            Assert(item != null, "Migration-4: Find(N01) 成功");
            Assert(item?.NewField == "default",
                $"Migration-4: NewField 取默认值（实际：'{item?.NewField}'）");
        }
        finally { Cleanup(path); }
    }

    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 4.0 起运行时不再接受 v1/v2/v3，本辅助方法原地升级为 v4，
    /// 使后续的 <c>MigrationDb.LoadAsync()</c> 走正常运行时 schema 迁移路径。
    /// </summary>
    private static async Task MigrateLegacyAsync(string path)
    {
        var typeMap = new Dictionary<string, Type>
        {
            [typeof(MigrationEntity).FullName!] = typeof(MigrationEntity),
        };
        var rule = MigrationBuilder<MigrationEntity>.Build(m => m
            .RenameProperty("OldTitle", "Title"));
        var migrationRules = new Dictionary<string, SchemaMigrationRule>
        {
            [typeof(MigrationEntity).FullName!] = rule,
        };
        await Vorcyc.Quiver.Migration.QuiverMigrator.MigrateAsync(
            path, path, typeMap,
            migrationRules: migrationRules,
            options: new Vorcyc.Quiver.Migration.MigrateOptions { Overwrite = true, AllowNoop = true });
    }

    private static void Cleanup(string path)
    {
        if (File.Exists(path)) File.Delete(path);
    }
}
