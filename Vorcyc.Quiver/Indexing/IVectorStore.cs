namespace Vorcyc.Quiver.Indexing;

/// <summary>
/// Storage abstraction for vector data. Separates vector ownership from index implementations,
/// allowing indices to manage only topological structure (graph/tree/inverted list)
/// while vector data is managed uniformly by implementations of this interface.
/// <para>
/// Current implementation: <see cref="HeapVectorStore"/> — vectors reside on the GC managed heap.
/// </para>
/// </summary>
/// <seealso cref="IVectorIndex"/>
internal interface IVectorStore : IDisposable
{
    /// <summary>Current number of stored vectors.</summary>
    int Count { get; }

    /// <summary>
    /// Stores a vector associated with the specified internal ID.
    /// <para>The implementation decides the physical storage location (GC heap / mmap region).</para>
    /// </summary>
    /// <param name="id">Internal ID, allocated by <c>QuiverSet._nextId</c>.</param>
    /// <param name="vector">Vector data. The caller guarantees the correct dimension and normalization (if required).</param>
    void Store(int id, ReadOnlySpan<float> vector);

    /// <summary>
    /// Returns a read-only view of the vector for the specified internal ID.
    /// <para>
    /// The lifetime of the returned <see cref="ReadOnlySpan{T}"/> is determined by the implementation:
    /// it points to a <c>float[]</c> on the GC heap and is valid until the GC collects it.
    /// Callers should use it synchronously within a read lock and must not hold it long-term.
    /// </para>
    /// </summary>
    /// <param name="id">Internal ID.</param>
    /// <returns>A read-only view of the vector data.</returns>
    ReadOnlySpan<float> Get(int id);

    /// <summary>Returns whether a vector with the specified ID exists.</summary>
    bool Contains(int id);

    /// <summary>Removes the vector with the specified ID. Silently returns if the ID does not exist.</summary>
    void Remove(int id);

    /// <summary>Clears all vector data.</summary>
    void Clear();

    /// <summary>
    /// Gets all stored internal IDs.
    /// <para>Used by indices for traversal (e.g., FlatIndex brute-force search needs to iterate all IDs).</para>
    /// </summary>
    IEnumerable<int> Ids { get; }
}
