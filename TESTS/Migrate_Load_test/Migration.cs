using NiubiServer.Database;
using Vorcyc.Quiver.Files;
using Vorcyc.Quiver.Migration;
using Vorcyc.Quiver.Storage;

public static class MigrationRunner
{

    public async static Task Run()
    {
        const string sourceFile = @"G:\歌曲宝\niubi_media.vdb";
        const string destFile = @"G:\niubi_media_v4.vdb";

        // ── 第一步：探查文件版本与实体类型 ───────────────────────────────────────────
        Console.WriteLine("=== Inspect ===");
        var info = await QuiverDbFile.InspectAsync(sourceFile, verifyCrc: false);
        Console.WriteLine($"格式版本  : v{info.FormatVersion}");
        Console.WriteLine($"CRC 有效  : {info.CrcValid}");
        Console.WriteLine($"段数量    : {info.Segments.Count}");
        foreach (var seg in info.Segments)
        {
            Console.WriteLine(
                $"  [{seg.Kind}] TypeName={seg.TypeName}  offset={seg.Offset}" +
                $"  length={seg.Length}  entities={seg.EntityCount}" +
                $"  crc={(seg.StoredCrc == seg.ActualCrc ? "OK" : "FAIL")}");
        }
        Console.WriteLine();

        if (info.FormatVersion == 4)
        {
            Console.WriteLine("文件已经是 v4 格式，无需迁移。");
            return;
        }

        // ── 第二步：执行迁移 ──────────────────────────────────────────────────────────
        // typeMap：旧文件中记录的 TypeName → 当前 CLR 类型
        // 若旧文件用了无命名空间的类，FullName 就是 "AudioMediaEntity"
        var typeMap = new Dictionary<string, Type>
        {
            [typeof(AudioMediaEntity).FullName!] = typeof(AudioMediaEntity)
        };

        Console.WriteLine("=== MigrateAsync ===");
        await QuiverMigrator.MigrateAsync(
            sourceFile: sourceFile,
            destinationFile: destFile,
            typeMap: typeMap,
            options: new MigrateOptions
            {
                Overwrite = true,
                DeleteSourceOnSuccess = false,
                AllowNoop = true
            });
        Console.WriteLine($"迁移完成 → {destFile}");

        // ── 第三步：校验迁移结果 ──────────────────────────────────────────────────────
        Console.WriteLine();
        Console.WriteLine("=== 校验 v4 文件 ===");
        var v4Info = await QuiverDbFile.InspectAsync(destFile, verifyCrc: true);
        Console.WriteLine($"格式版本  : v{v4Info.FormatVersion}");
        Console.WriteLine($"CRC 有效  : {v4Info.CrcValid}");
        Console.WriteLine($"段数量    : {v4Info.Segments.Count}");
        foreach (var seg in v4Info.Segments)
        {
            Console.WriteLine(
                $"  [{seg.Kind}] TypeName={seg.TypeName}  entities={seg.EntityCount}" +
                $"  crc={(seg.StoredCrc == seg.ActualCrc ? "OK" : "FAIL")}");
        }

    }

}

