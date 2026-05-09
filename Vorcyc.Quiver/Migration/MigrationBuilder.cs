namespace Vorcyc.Quiver.Migration;

/// <summary>
/// Fluent builder for declaring entity schema migration rules (property renames and value transforms).
/// <para>
/// Use inside a <see cref="QuiverDbContext"/> subclass constructor via
/// <see cref="QuiverDbContext.ConfigureMigration{TEntity}"/> for runtime migration applied during
/// <c>LoadAsync</c>, or call <see cref="Build"/> to construct an offline rule for
/// <see cref="QuiverMigrator.MigrateAsync"/>.
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
/// <typeparam name="TEntity">The entity type to configure migration rules for.</typeparam>
public class MigrationBuilder<TEntity> where TEntity : class, new()
{
    internal SchemaMigrationRule Rule { get; } = new();

    /// <summary>
    /// Builds a <see cref="SchemaMigrationRule"/> for scenarios that require an explicit rule object,
    /// such as offline file-format migration via <see cref="QuiverMigrator.MigrateAsync"/>.
    /// </summary>
    /// <param name="configure">Delegate that configures the migration rules on the builder.</param>
    /// <returns>The fully configured <see cref="SchemaMigrationRule"/>.</returns>
    public static SchemaMigrationRule Build(Action<MigrationBuilder<TEntity>> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var builder = new MigrationBuilder<TEntity>();
        configure(builder);
        return builder.Rule;
    }

    /// <summary>
    /// Declares a property rename mapping. During load, a property stored under <paramref name="oldName"/>
    /// in the file is mapped to the <paramref name="newName"/> property on the current CLR type.
    /// </summary>
    /// <param name="oldName">The property name as it appears in the file (old/previous version).</param>
    /// <param name="newName">The property name on the current CLR type.</param>
    /// <returns>The current builder instance to support method chaining.</returns>
    public MigrationBuilder<TEntity> RenameProperty(string oldName, string newName)
    {
        Rule.PropertyRenames[oldName] = newName;
        return this;
    }

    /// <summary>
    /// Declares a value transform rule. After loading, the <paramref name="transform"/> function is
    /// applied to the specified property's value.
    /// <para>
    /// Suitable for type changes (e.g. <c>int → double</c>), format migrations, and computed derived values.
    /// </para>
    /// </summary>
    /// <param name="propertyName">The property name on the current CLR type.</param>
    /// <param name="transform">Transform function that receives the old value and returns the new value.</param>
    /// <returns>The current builder instance to support method chaining.</returns>
    public MigrationBuilder<TEntity> TransformValue(string propertyName, Func<object?, object?> transform)
    {
        Rule.ValueTransforms[propertyName] = transform;
        return this;
    }
}
