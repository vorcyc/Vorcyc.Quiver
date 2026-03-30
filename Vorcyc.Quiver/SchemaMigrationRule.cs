namespace Vorcyc.Quiver;

/// <summary>
/// 存储单个实体类型的 Schema 迁移规则：属性重命名映射和值转换函数。
/// <para>
/// 由 <see cref="MigrationBuilder{TEntity}"/> 构建，在 <see cref="QuiverDbContext.LoadAsync"/> 
/// 时传递给存储提供者，实现透明的 Schema 迁移。
/// </para>
/// </summary>
internal class SchemaMigrationRule
{
    /// <summary>
    /// 属性重命名映射：旧属性名 → 新属性名。
    /// <para>
    /// 在加载时，存储提供者会将文件中的旧属性名映射到当前 CLR 类型的新属性名。
    /// </para>
    /// </summary>
    public Dictionary<string, string> PropertyRenames { get; } = [];

    /// <summary>
    /// 值转换函数映射：新属性名 → 转换函数。
    /// <para>
    /// 在加载完成后，对指定属性的值执行转换（如类型转换、格式变更等）。
    /// </para>
    /// </summary>
    public Dictionary<string, Func<object?, object?>> ValueTransforms { get; } = [];

    /// <summary>
    /// 反向重命名映射（延迟计算）：新属性名 → 旧属性名。
    /// <para>
    /// 用于 XML 加载时按当前属性名反查文件中的旧元素名称。
    /// </para>
    /// </summary>
    private Dictionary<string, string>? _reverseRenames;

    /// <summary>
    /// 获取反向重命名映射：新属性名 → 旧属性名。
    /// </summary>
    public Dictionary<string, string> ReverseRenames =>
        _reverseRenames ??= PropertyRenames.ToDictionary(kv => kv.Value, kv => kv.Key);
}
