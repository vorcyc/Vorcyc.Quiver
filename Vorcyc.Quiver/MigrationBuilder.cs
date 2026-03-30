namespace Vorcyc.Quiver;

/// <summary>
/// 实体 Schema 迁移构建器，提供流式 API 声明属性重命名和值转换规则。
/// <para>
/// 在 <see cref="QuiverDbContext"/> 子类的构造函数中通过 
/// <see cref="QuiverDbContext.ConfigureMigration{TEntity}"/> 使用：
/// </para>
/// <example>
/// <code>
/// public class MyDb : QuiverDbContext
/// {
///     public QuiverSet&lt;Document&gt; Documents { get; set; }
///
///     public MyDb() : base(new QuiverDbOptions { DatabasePath = "my.db" })
///     {
///         ConfigureMigration&lt;Document&gt;(m => m
///             .RenameProperty("OldTitle", "Title")
///             .TransformValue("Score", v => v is int i ? (double)i : v));
///     }
/// }
/// </code>
/// </example>
/// </summary>
/// <typeparam name="TEntity">要迁移的实体类型。</typeparam>
public class MigrationBuilder<TEntity> where TEntity : class, new()
{
    internal SchemaMigrationRule Rule { get; } = new();

    /// <summary>
    /// 声明一个属性重命名映射。加载时文件中名为 <paramref name="oldName"/> 的属性
    /// 会被映射到当前类型的 <paramref name="newName"/> 属性。
    /// </summary>
    /// <param name="oldName">文件中（旧版本）的属性名称。</param>
    /// <param name="newName">当前 CLR 类型中的属性名称。</param>
    /// <returns>当前构建器实例，支持链式调用。</returns>
    public MigrationBuilder<TEntity> RenameProperty(string oldName, string newName)
    {
        Rule.PropertyRenames[oldName] = newName;
        return this;
    }

    /// <summary>
    /// 声明一个值转换规则。加载完成后，对指定属性的值执行 <paramref name="transform"/> 转换。
    /// <para>
    /// 适用于类型变更（如 <c>int → double</c>）、格式迁移、计算派生值等场景。
    /// </para>
    /// </summary>
    /// <param name="propertyName">当前 CLR 类型中的属性名称。</param>
    /// <param name="transform">值转换函数，输入旧值，输出新值。</param>
    /// <returns>当前构建器实例，支持链式调用。</returns>
    public MigrationBuilder<TEntity> TransformValue(string propertyName, Func<object?, object?> transform)
    {
        Rule.ValueTransforms[propertyName] = transform;
        return this;
    }
}
