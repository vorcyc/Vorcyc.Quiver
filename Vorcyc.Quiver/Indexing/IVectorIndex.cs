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
    /// 可选：批量构建索引拓扑。用于把一个字段从 Flat 切换到 HNSW（或其它需要重建图的索引）后，
    /// 一次性导入全部 id 时加速构建。默认实现退化为逐个 <see cref="Add"/>，保持与单条插入完全一致的行为；
    /// 支持并行构建的实现（如 <see cref="HnswIndex{TSim}"/>）会用多线程加速。
    /// <para>
    /// 调用前所有 id 对应的向量必须已写入 <see cref="IVectorStore"/>。调用方需保证当前没有其它线程
    /// 并发改写本索引（上层 <c>QuiverSet</c> 的写锁已提供该保证）。
    /// </para>
    /// </summary>
    /// <param name="ids">待批量插入的内部 ID 列表（其向量已存入 <see cref="IVectorStore"/>）。</param>
    /// <param name="degreeOfParallelism">
    /// 期望的最大并行度。<see langword="null"/> 表示由实现自行决定（通常为 <see cref="Environment.ProcessorCount"/>）。
    /// 小于等于 1 时退化为串行。对不支持并行的实现该参数被忽略。
    /// </param>
    void BuildBulk(IReadOnlyList<int> ids, int? degreeOfParallelism = null)
    {
        ArgumentNullException.ThrowIfNull(ids);
        for (int i = 0; i < ids.Count; i++)
            Add(ids[i]);
    }

    /// <summary>
    /// 可选：与底层 <see cref="IVectorStore"/> 对账，移除拓扑中引用了 store 内已不存在向量的悬空条目。
    /// 默认空实现。图索引（如 <see cref="HnswIndex{TSim}"/>）在从快照恢复后调用：非正常退出可能导致磁盘上的
    /// 快照与向量数据不一致（快照引用了 store 中缺失的 id），若不清理，后续搜索 / 插入在解引用这些邻居时会抛
    /// <see cref="System.Collections.Generic.KeyNotFoundException"/>（例如 "Vector id N not found in mmap store."）。
    /// </summary>
    void ReconcileWithStore() { }

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

    /// <summary>
    /// 可选：把索引的内部拓扑序列化到 <paramref name="writer"/>。返回 <see langword="true"/> 表示
    /// 写入了快照；<see langword="false"/> 表示该索引类型不支持快照（调用方应继续走在线重建路径）。
    /// <para>
    /// 实现需在快照内嵌入自验证指纹（维度、参数、相似度类型、当前 ID 上界等），以便 <see cref="TryLoadSnapshot"/>
    /// 阶段在指纹不一致时安全拒绝。
    /// </para>
    /// </summary>
    bool TrySaveSnapshot(System.IO.BinaryWriter writer) => false;

    /// <summary>
    /// 可选：从 <paramref name="reader"/> 反序列化索引拓扑。返回 <see langword="true"/> 表示成功恢复，
    /// 调用方可跳过对快照覆盖区间内 id 的 <see cref="Add"/>；返回 <see langword="false"/> 表示快照无效
    /// 或不兼容（指纹不匹配 / 版本不支持 / 数据损坏），调用方应走在线重建路径。
    /// </summary>
    /// <param name="reader">指向快照数据起点的二进制读取器。</param>
    /// <param name="snapshotCoveredNextId">输出：快照覆盖的 id 上界（不含）。调用方对 id &gt;= 该值的实体仍需 <see cref="Add"/>。</param>
    bool TryLoadSnapshot(System.IO.BinaryReader reader, out int snapshotCoveredNextId)
    {
        snapshotCoveredNextId = 0;
        return false;
    }
}