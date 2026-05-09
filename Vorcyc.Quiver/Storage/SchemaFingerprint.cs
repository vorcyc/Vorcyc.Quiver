using System.Reflection;
using System.Text;

namespace Vorcyc.Quiver.Storage;

/// <summary>
/// 用 FNV1a64 计算实体 schema 指纹，便于检测「文件中保存的实体形状」与「当前 CLR 实体声明」是否一致。
/// <para>
/// 当前版本仅在内存中使用此指纹（用于潜在的 schema 漂移诊断）；
/// 后续版本会把指纹写入 v4 文件头实现持久化的"防呆"保护。
/// </para>
/// <para>
/// 指纹仅依赖 (PropertyName + PropertyTypeFullName + IsQuiverVector + VectorDimensions) 序列；
/// 不依赖属性顺序在 reflection 中的呈现以避免误报：写入前会按属性名排序。
/// </para>
/// </summary>
internal static class SchemaFingerprint
{
    public static ulong Compute(Type entityType)
    {
        var props = entityType
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.CanWrite)
            .OrderBy(p => p.Name, StringComparer.Ordinal)
            .ToArray();

        const ulong FnvOffset = 14695981039346656037UL;
        const ulong FnvPrime = 1099511628211UL;
        ulong h = FnvOffset;

        var sb = new StringBuilder(128);
        foreach (var p in props)
        {
            sb.Clear();
            sb.Append(p.Name).Append('|').Append(p.PropertyType.FullName ?? p.PropertyType.Name);

            var vec = p.GetCustomAttribute<QuiverVectorAttribute>();
            if (vec is not null)
                sb.Append("|V:").Append(vec.Dimensions);

            if (p.GetCustomAttribute<QuiverKeyAttribute>() is not null)
                sb.Append("|K");

            var bytes = Encoding.UTF8.GetBytes(sb.ToString());
            foreach (var b in bytes)
            {
                h ^= b;
                h *= FnvPrime;
            }
            // separator between fields
            h ^= 0x0A;
            h *= FnvPrime;
        }
        return h;
    }
}
