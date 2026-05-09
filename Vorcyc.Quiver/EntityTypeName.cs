using System.Collections.Concurrent;
using System.Reflection;

namespace Vorcyc.Quiver;

/// <summary>
/// 统一解析实体类型在持久化层使用的<b>稳定名</b>（"stored TypeName"）。
/// <para>
/// 解析规则：
/// <list type="number">
///   <item>若类型标注了 <see cref="QuiverEntityAttribute"/>，使用 <c>QuiverEntityAttribute.Name</c>。</item>
///   <item>否则回退到 <see cref="Type.FullName"/>。</item>
/// </list>
/// </para>
/// <para>
/// 结果按 <see cref="Type"/> 缓存。所有 Save / Append / Export / Migrate / RebindMmap 等需要
/// 把 <see cref="Type"/> 落到磁盘标识的代码路径都应该经过这里，避免与默认 <see cref="Type.FullName"/>
/// 出现不一致。
/// </para>
/// </summary>
internal static class EntityTypeName
{
    private static readonly ConcurrentDictionary<Type, string> Cache = new();

    /// <summary>
    /// 返回 <paramref name="type"/> 在持久化层使用的稳定名。
    /// </summary>
    public static string Resolve(Type type)
    {
        if (type is null) throw new ArgumentNullException(nameof(type));
        return Cache.GetOrAdd(type, static t =>
        {
            var attr = t.GetCustomAttribute<QuiverEntityAttribute>(inherit: false);
            if (attr is not null) return attr.Name;
            return t.FullName ?? t.Name;
        });
    }

    /// <summary>
    /// 当 <paramref name="type"/> 标注了 <see cref="QuiverEntityAttribute"/> 且其 <c>Name</c>
    /// 与 <see cref="Type.FullName"/> 不同，返回 <c>FullName</c>（用于向后兼容旧文件）；否则返回 <see langword="null"/>。
    /// </summary>
    public static string? ResolveLegacyAliasOrNull(Type type)
    {
        if (type is null) throw new ArgumentNullException(nameof(type));
        var resolved = Resolve(type);
        var full = type.FullName;
        return (!string.IsNullOrEmpty(full) && !string.Equals(full, resolved, StringComparison.Ordinal))
            ? full
            : null;
    }
}
