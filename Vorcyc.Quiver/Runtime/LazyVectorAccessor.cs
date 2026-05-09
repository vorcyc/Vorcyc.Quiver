using System.Runtime.CompilerServices;

namespace Vorcyc.Quiver.Runtime;

/// <summary>
/// Runtime entry point: the <c>partial</c> vector property getter emitted by the source generator calls
/// <see cref="Materialize"/> when the backing field is <c>null</c>, reading the vector on demand from
/// the bound <see cref="QuiverSet{TEntity}"/>.
/// <para>
/// Bindings are stored in a <see cref="ConditionalWeakTable{TKey,TValue}"/> and do not extend the
/// entity's lifetime; the binding is automatically removed when the entity is garbage-collected.
/// </para>
/// </summary>
public static class LazyVectorAccessor
{
    /// <summary>Entity binding: holds the owning set source and internal row ID.</summary>
    internal sealed class Binding
    {
        public ILazyVectorSource Source { get; }
        public int InternalId { get; }
        public Binding(ILazyVectorSource source, int internalId)
        {
            Source = source;
            InternalId = internalId;
        }
    }

    private static readonly ConditionalWeakTable<object, Binding> _bindings = [];

    /// <summary>Called by <c>QuiverSet.LoadEntities</c> to bind an entity object to the source that can provide its vectors on demand.</summary>
    internal static void Bind(object entity, ILazyVectorSource source, int internalId)
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
    /// Called by the source-generator-emitted property getter: returns the vector on demand when the
    /// backing field is <c>null</c>.
    /// Returns <c>null</c> when the entity is not bound to any set (e.g. a detached <c>new Entity()</c>
    /// that has not been added to a set).
    /// </summary>
    /// <param name="entity">The entity object itself (<c>this</c>).</param>
    /// <param name="fieldName">The vector field name (i.e. the property name).</param>
    public static float[]? Materialize(object entity, string fieldName)
    {
        if (entity is null) return null;
        if (_bindings.TryGetValue(entity, out var b))
            return b.Source.GetVector(b.InternalId, fieldName);
        return null;
    }

    /// <summary>
    /// The <c>Half[]</c> variant of <see cref="Materialize"/>: called by the source-generator-emitted
    /// getter for <c>Half[]?</c> lazy vector properties to read an fp16 vector copy on demand from the
    /// bound source. Returns <c>null</c> when the entity is not bound.
    /// </summary>
    public static Half[]? MaterializeHalf(object entity, string fieldName)
    {
        if (entity is null) return null;
        if (_bindings.TryGetValue(entity, out var b))
            return b.Source.GetVectorHalf(b.InternalId, fieldName);
        return null;
    }
}

/// <summary>
/// Abstraction targeted by <see cref="LazyVectorAccessor"/>: any implementation that can return a
/// <c>float[]?</c> payload by (internalId, fieldName) qualifies as a source.
/// Implemented by <c>QuiverSet&lt;TEntity&gt;</c>.
/// </summary>
public interface ILazyVectorSource
{
    /// <summary>
    /// Reads the vector by internal row ID and field name; returns a newly allocated copy that the
    /// caller may freely modify.
    /// <para>
    /// Returns <c>null</c> in the following cases (this is the external contract for
    /// <c>[QuiverVector(Nullable = true)]</c> fields):
    /// <list type="bullet">
    ///   <item>The field name is invalid or the set has been disposed.</item>
    ///   <item>The entity identified by <paramref name="internalId"/> has no vector stored for this
    ///         field (a nullable field written as <c>null</c>, or a row flagged as null in the mmap
    ///         NullBitmap at load time).</item>
    /// </list>
    /// This contract propagates directly to the user's
    /// <c>public partial float[]? Vec { get; set; }</c> property via the source-generated getter
    /// <c>backing ?? Materialize(...)</c>.
    /// </para>
    /// </summary>
    float[]? GetVector(int internalId, string fieldName);

    /// <summary>
    /// The <c>Half[]</c> variant of <see cref="GetVector"/>, used for lazy materialization of fp16
    /// vector fields. The default implementation narrows the float view returned by the underlying
    /// storage to <c>Half[]</c>; <c>QuiverSet&lt;TEntity&gt;</c> may override this to return a direct
    /// physical fp16 copy and avoid a precision round-trip.
    /// </summary>
    Half[]? GetVectorHalf(int internalId, string fieldName)
    {
        var f = GetVector(internalId, fieldName);
        if (f is null) return null;
        return Vorcyc.Quiver.Numerics.VectorMath.NarrowFloatToHalf(f);
    }
}
