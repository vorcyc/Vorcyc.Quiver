using System.Runtime.CompilerServices;

namespace Vorcyc.Quiver.Runtime;

/// <summary>
/// Runtime entry point: the <c>partial</c> large-field property getter emitted by the source generator calls
/// <see cref="Materialize"/> when the backing field is <c>null</c>, reading the <c>byte[]</c> payload
/// on demand from the bound <see cref="QuiverSet{TEntity}"/>.
/// <para>
/// Bindings are stored in a <see cref="ConditionalWeakTable{TKey,TValue}"/> and do not extend the
/// entity's lifetime; the binding is automatically removed when the entity is garbage-collected.
/// </para>
/// </summary>
public static class LazyLargeFieldAccessor
{
    /// <summary>Entity binding: holds the owning set source and internal row ID.</summary>
    internal sealed class Binding
    {
        /// <summary>The source used to read large-field payloads on demand.</summary>
        public ILazyLargeFieldSource Source { get; }

        /// <summary>The internal row ID of the entity within its owning set.</summary>
        public int InternalId { get; }

        /// <summary>Creates a new entity binding.</summary>
        public Binding(ILazyLargeFieldSource source, int internalId)
        {
            Source = source;
            InternalId = internalId;
        }
    }

    private static readonly ConditionalWeakTable<object, Binding> _bindings = [];

    /// <summary>Called by <c>QuiverSet</c> to bind an entity object to the source that can provide its large-field payloads on demand.</summary>
    internal static void Bind(object entity, ILazyLargeFieldSource source, int internalId)
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(source);
        // ConditionalWeakTable does not allow duplicate keys; remove the existing entry before adding.
        _bindings.Remove(entity);
        _bindings.Add(entity, new Binding(source, internalId));
    }

    /// <summary>Removes the binding for the given entity. Normally not needed; bindings are cleaned up automatically when the entity is GC'd.</summary>
    internal static void Unbind(object entity)
    {
        if (entity is null) return;
        _bindings.Remove(entity);
    }

    /// <summary>
    /// Called by the source-generator-emitted property getter: returns the large-field payload on demand
    /// when the backing field is <c>null</c>.
    /// Returns <c>null</c> when the entity is not bound to any set (e.g. a detached <c>new Entity()</c>
    /// that has not been added to a set).
    /// </summary>
    /// <param name="entity">The entity object itself (<c>this</c>).</param>
    /// <param name="fieldName">The large-field name (i.e. the property name).</param>
    public static byte[]? Materialize(object entity, string fieldName)
    {
        if (entity is null) return null;
        if (_bindings.TryGetValue(entity, out var b))
            return b.Source.GetLargeField(b.InternalId, fieldName);
        return null;
    }
}

/// <summary>
/// Abstraction targeted by <see cref="LazyLargeFieldAccessor"/>: any implementation that can return
/// a <c>byte[]?</c> payload by (internalId, fieldName) qualifies as a source.
/// Implemented by <c>QuiverSet&lt;TEntity&gt;</c>.
/// </summary>
public interface ILazyLargeFieldSource
{
    /// <summary>
    /// Reads the large-field payload by internal row ID and field name; returns a newly allocated copy
    /// that the caller may freely modify.
    /// <para>
    /// Returns <c>null</c> when: the field name is invalid, the set has been disposed, the field is
    /// marked nullable and the stored value is null, or the entity is not yet bound to a file backend.
    /// </para>
    /// </summary>
    /// <param name="internalId">The internal row ID of the entity within its owning set.</param>
    /// <param name="fieldName">The large-field name.</param>
    byte[]? GetLargeField(int internalId, string fieldName);
}
