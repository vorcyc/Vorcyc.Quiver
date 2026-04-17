using System.Runtime.InteropServices;
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
///   <item>迁移 + WAL 并存（WAL 回放 + 快照加载均应用迁移规则）</item>
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
        await Test_MigrationWithWal();
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

            var db = new MigrationDb(path);
            await db.LoadAsync();

            var item = db.Items.Find("N01");
            Assert(item != null, "Migration-4: Find(N01) 成功");
            Assert(item?.NewField == "default",
                $"Migration-4: NewField 取默认值（实际：'{item?.NewField}'）");
        }
        finally { Cleanup(path); }
    }

    // ==================== 场景 5：迁移 + WAL 并存 ====================
    private static async Task Test_MigrationWithWal()
    {
        Console.WriteLine("\n═══ Migration-5. 迁移规则 + WAL 回放并存 ═══");

        var path = Path.GetTempFileName() + ".vdb";
        try
        {
            var rng = new Random(5);
            WriteLegacyQdb(path,
            [
                ("W00", "WAL迁移_0", 0,  "lg0", RandomVector(rng, 32)),
                ("W01", "WAL迁移_1", 5,  "lg1", RandomVector(rng, 32)),
                ("W02", "WAL迁移_2", 10, "lg2", RandomVector(rng, 32)),
                ("W03", "WAL迁移_3", 15, "lg3", RandomVector(rng, 32)),
                ("W04", "WAL迁移_4", 20, "lg4", RandomVector(rng, 32)),
            ]);

            // 加载旧快照，追加新实体，写 WAL
            var walDb = new MigrationWalDb(path);
            await walDb.LoadAsync();

            Assert(walDb.Items.Count == 5, "Migration-5: WAL 上下文加载旧版快照正确（5 条）");
            Assert(walDb.Items.Find("W02")?.Title == "WAL迁移_2",
                "Migration-5: WAL 加载后重命名规则已应用");

            walDb.Items.Add(new MigrationEntity
            {
                Id = "W99",
                Title = "WAL新增",
                Score = 99.5,
                NewField = "wal_new",
                Embedding = RandomVector(rng, 32)
            });
            await walDb.SaveChangesAsync();
            walDb.Dispose();

            // 重新打开，验证快照 + WAL 回放结果
            var reloadDb = new MigrationWalDb(path);
            await reloadDb.LoadAsync();

            Assert(reloadDb.Items.Count == 6, "Migration-5: 快照(5) + WAL(1) 回放后共 6 条");

            var w99 = reloadDb.Items.Find("W99");
            Assert(w99 != null, "Migration-5: WAL 回放新实体 W99 可找到");
            Assert(w99?.Title == "WAL新增", "Migration-5: WAL 回放实体 Title 正确");
            Assert(w99?.Score == 99.5, "Migration-5: WAL 回放 double Score 正确");

            var w00 = reloadDb.Items.Find("W00");
            Assert(w00?.Title == "WAL迁移_0", "Migration-5: 快照旧实体重命名规则仍正确");
            reloadDb.Dispose();
        }
        finally { Cleanup(path); }
    }

    // ──────────────────────────────────────────────────────────────

    private static void Cleanup(string path)
    {
        if (File.Exists(path)) File.Delete(path);
        var wal = path + ".wal";
        if (File.Exists(wal)) File.Delete(wal);
    }
}
