namespace Vorcyc.Quiver.Indexing;

/// <summary>
/// Unified interface for vector indices. Defines the operations that all index implementations
/// (Flat, HNSW, IVF, KDTree) must provide.
/// <para>
/// <b>Responsibility</b>: manages the topological structure (graph/tree/inverted list) of internal IDs
/// and provides similarity-based search. Physical storage of vector data is handled by <see cref="IVectorStore"/>;
/// the index reads vectors through that reference.
/// </para>
/// <para>
/// <b>Thread safety</b>: guaranteed by the read-write lock in the upper-layer <c>QuiverSet&lt;TEntity&gt;</c>;
/// index implementations do not need to handle concurrency themselves.
/// </para>
/// </summary>
/// <seealso cref="FlatIndex{TSim}"/>
/// <seealso cref="HnswIndex{TSim}"/>
/// <seealso cref="IvfIndex{TSim}"/>
/// <seealso cref="KDTreeIndex{TSim}"/>
internal interface IVectorIndex
{
    /// <summary>Current number of vectors in the index.</summary>
    int Count { get; }

    /// <summary>
    /// Registers the specified ID in the index topology.
    /// <para>
    /// The vector data has already been written to <see cref="IVectorStore"/> by the caller;
    /// the index implementation reads the vector via <c>IVectorStore.Get(id)</c> to build the graph/tree.
    /// </para>
    /// </summary>
    /// <param name="id">Internal ID; the vector has already been stored in <see cref="IVectorStore"/>.</param>
    void Add(int id);

    /// <summary>
    /// Removes the specified ID from the index topology. Silently returns if the ID does not exist.
    /// <para>The corresponding vector data is managed separately by <see cref="IVectorStore"/>; this method does not delete vectors.</para>
    /// </summary>
    /// <param name="id">The internal ID to remove.</param>
    void Remove(int id);

    /// <summary>Clears the index topology.</summary>
    void Clear();

    /// <summary>
    /// Searches for the Top-K results most similar to the query vector.
    /// </summary>
    /// <param name="query">The query vector.</param>
    /// <param name="topK">Maximum number of results to return.</param>
    /// <returns>A list of (internal ID, similarity) pairs sorted by similarity in descending order.</returns>
    List<(int Id, float Similarity)> Search(float[] query, int topK);

    /// <summary>
    /// Searches for all vectors whose similarity is at or above the given threshold.
    /// </summary>
    /// <param name="query">The query vector.</param>
    /// <param name="threshold">Similarity lower bound (inclusive).</param>
    /// <returns>A list of (internal ID, similarity) pairs in no particular order.</returns>
    List<(int Id, float Similarity)> SearchByThreshold(float[] query, float threshold);
}