using System.Numerics.Tensors;
using Vorcyc.Quiver.Similarity;

namespace Vorcyc.Quiver.Indexing;

/// <summary>
/// IVF (Inverted File Index): cluster-based approximate nearest-neighbor search using K-Means.
/// <para>
/// <b>Core idea</b>: divides the vector space into K Voronoi cells (clusters) using K-Means;
/// each cluster maintains an inverted list (the IDs of vectors assigned to that cluster).
/// At search time, only the nProbe clusters closest to the query vector are scanned, greatly reducing computation.
/// </para>
/// <para>
/// <b>Performance characteristics</b>:
/// <list type="bullet">
///   <item>Build complexity: O(n × k × d × iter), n=vectors, k=clusters, d=dimensions, iter=iterations</item>
///   <item>Search complexity: O(k × d + nProbe × n/k × d), far less than brute-force O(n × d)</item>
///   <item>Space complexity: O(n × d + k × d), original vectors + cluster centroids</item>
/// </list>
/// </para>
/// <para>
/// <b>Parameter tuning guide</b>:
/// <list type="bullet">
///   <item><c>numClusters</c>: number of clusters. When 0, defaults to √n automatically. Larger values reduce per-cluster vector count but increase centroid comparison overhead.</item>
///   <item><c>numProbes</c>: number of clusters probed during search. Larger values improve recall but slow search. Recommended: 1–20.</item>
/// </list>
/// </para>
/// <para>
/// <b>Lazy build + auto-rebuild</b>: the index is built on the first search (lazily).
/// When the dataset grows beyond <see cref="RebuildRatio"/> × (1.5×) the size at the last build, the index is automatically marked for rebuild.
/// </para>
/// </summary>
internal sealed class IvfIndex<TSim> : IVectorIndex
    where TSim : struct, ISimilarity<float>
{
    // ──────────────────────────────────────────────────────────────
    // Algorithm parameters
    // ──────────────────────────────────────────────────────────────

    /// <summary>Vector data store, injected externally.</summary>
    private readonly IVectorStore _vectorStore;

    /// <summary>
    /// Number of clusters to probe during search. Larger values improve recall but slow search.
    /// Setting this equal to k (total clusters) is equivalent to brute-force search.
    /// </summary>
    private readonly int _numProbes;

    /// <summary>
    /// Number of clusters. When 0, automatically set to <c>√n</c> in <see cref="Build"/>.
    /// Fixed to the actual value used after construction.
    /// </summary>
    private int _numClusters;

    // ──────────────────────────────────────────────────────────────
    // ID tracking
    // ──────────────────────────────────────────────────────────────

    /// <summary>Set of internal IDs registered in the index. Vector data is managed by <see cref="_vectorStore"/>.</summary>
    private readonly HashSet<int> _ids = [];

    // ──────────────────────────────────────────────────────────────
    // Cluster structure (built by Build())
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// K-Means centroid array. <c>_centroids[i]</c> is the centroid vector for cluster i.
    /// At search time, the query vector's similarity to all centroids is computed; the nProbe nearest clusters are probed.
    /// </summary>
    private float[][] _centroids = [];

    /// <summary>
    /// Inverted list array. <c>_invertedLists[i]</c> stores the internal IDs of all vectors assigned to cluster i.
    /// During search, the selected clusters' inverted lists are traversed to compute exact similarity.
    /// </summary>
    private List<int>[] _invertedLists = [];

    /// <summary>Whether the index has been built. Triggered on the first search; set to false when data grows too large, forcing a rebuild.</summary>
    private bool _isBuilt;

    // ──────────────────────────────────────────────────────────────
    // Auto-rebuild control
    // ──────────────────────────────────────────────────────────────

    /// <summary>Number of vectors at the time of the last build. Used to compare against the current count to decide whether a rebuild is needed.</summary>
    private int _lastBuildCount;

    /// <summary>
    /// Auto-rebuild ratio. When <c>current vector count &gt; last build count × RebuildRatio</c>,
    /// the index is marked for rebuild. 1.5 means a rebuild is triggered after 50% data growth.
    /// </summary>
    private const double RebuildRatio = 1.5;

    /// <summary>
    /// Creates an IVF index instance.
    /// </summary>
    /// <param name="vectorStore">Vector data store.</param>
    /// <param name="numClusters">
    /// Number of clusters. When 0, automatically set to <c>√n</c> (n = vector count at first build time).
    /// Explicitly specifying a value is recommended for large datasets.
    /// </param>
    /// <param name="numProbes">
    /// Number of clusters to probe during search. Default: 10. Larger values improve recall; smaller values improve speed.
    /// </param>
    public IvfIndex(IVectorStore vectorStore, int numClusters = 0, int numProbes = 10)
    {
        _vectorStore = vectorStore;
        _numClusters = numClusters;
        _numProbes = numProbes;
    }

    /// <inheritdoc />
    public int Count => _ids.Count;

    /// <summary>
    /// Registers the specified ID in the index. After adding, checks whether a rebuild is needed
    /// (triggered when the data size exceeds <see cref="RebuildRatio"/> times the count at the last build).
    /// </summary>
    /// <param name="id">Internal ID; the vector has already been stored in <see cref="IVectorStore"/>.</param>
    public void Add(int id)
    {
        _ids.Add(id);

        // Mark for rebuild when data growth exceeds the threshold
        if (_isBuilt && _ids.Count > _lastBuildCount * RebuildRatio)
            _isBuilt = false;
    }

    /// <inheritdoc />
    public void Remove(int id) => _ids.Remove(id);

    /// <inheritdoc />
    /// <remarks>Clears the ID set, cluster centroids, and inverted lists; resets the build flag.</remarks>
    public void Clear()
    {
        _ids.Clear();
        _centroids = [];
        _invertedLists = [];
        _isBuilt = false;
    }

    /// <summary>
    /// Searches for the Top-K results most similar to the query vector. Algorithm steps:
    /// <list type="number">
    ///   <item>Compute the similarity between the query vector and all K cluster centroids</item>
    ///   <item>Select the nProbe clusters with the highest similarity</item>
    ///   <item>Traverse the inverted lists of the selected clusters and compute exact similarity</item>
    ///   <item>Select the Top-K results from the candidates and return</item>
    /// </list>
    /// </summary>
    /// <param name="query">The query vector.</param>
    /// <param name="topK">Maximum number of results to return.</param>
    /// <returns>A list of (internal ID, similarity) pairs sorted by similarity in descending order.</returns>
    public List<(int Id, float Similarity)> Search(float[] query, int topK)
    {
        if (_ids.Count == 0) return [];

        // Ensure the cluster index is built (lazy build / auto-rebuild after data growth)
        EnsureBuilt();

        // Step 1: compute similarity between the query vector and all cluster centroids
        var clusterSims = new (int Index, float Similarity)[_centroids.Length];
        for (int i = 0; i < _centroids.Length; i++)
            clusterSims[i] = (i, TSim.Compute(query, _centroids[i]));

        // Step 2: select the nProbe most similar clusters to probe
        var probeClusters = clusterSims
            .OrderByDescending(c => c.Similarity)
            .Take(Math.Min(_numProbes, _centroids.Length));

        // Step 3: traverse the inverted lists of the selected clusters and compute exact similarity
        var results = new List<(int Id, float Similarity)>();
        foreach (var (clusterIdx, _) in probeClusters)
        {
            foreach (var id in _invertedLists[clusterIdx])
            {
                // Skip IDs that may have been deleted but still remain in the inverted list
                if (!_vectorStore.Contains(id)) continue;
                results.Add((id, TSim.Compute(query, _vectorStore.Get(id))));
            }
        }

        // Step 4: select Top-K from the candidates
        return results.OrderByDescending(r => r.Similarity).Take(topK).ToList();
    }

    /// <summary>
    /// Searches for all vectors whose similarity is at or above the given threshold.
    /// The number of probed clusters is doubled to <c>nProbe × 2</c> to improve recall
    /// (threshold searches need to cover a wider area to avoid missing results).
    /// </summary>
    /// <param name="query">The query vector.</param>
    /// <param name="threshold">Similarity lower bound (inclusive).</param>
    /// <returns>A list of (internal ID, similarity) pairs meeting the threshold condition, in no particular order.</returns>
    public List<(int Id, float Similarity)> SearchByThreshold(float[] query, float threshold)
    {
        if (_ids.Count == 0) return [];
        EnsureBuilt();

        var results = new List<(int Id, float Similarity)>();

        // Threshold search doubles the probe count (2× nProbe) to reduce missed results from cluster boundary effects
        var probeClusters = Math.Min(_numProbes * 2, _centroids.Length);

        // Compute similarity between query and all centroids
        var clusterSims = new (int Index, float Similarity)[_centroids.Length];
        for (int i = 0; i < _centroids.Length; i++)
            clusterSims[i] = (i, TSim.Compute(query, _centroids[i]));

        // Probe the nearest clusters; collect only results exceeding the threshold
        foreach (var (clusterIdx, _) in clusterSims
            .OrderByDescending(c => c.Similarity).Take(probeClusters))
        {
            foreach (var id in _invertedLists[clusterIdx])
            {
                if (!_vectorStore.Contains(id)) continue;
                var sim = TSim.Compute(query, _vectorStore.Get(id));
                if (sim >= threshold) results.Add((id, sim));
            }
        }
        return results;
    }

    #region K-Means clustering (SIMD-accelerated)

    /// <summary>
    /// Ensures the cluster index is built. Calls <see cref="Build"/> when triggered by the first search
    /// or after a data-growth rebuild flag is set.
    /// </summary>
    private void EnsureBuilt()
    {
        if (_isBuilt) return;
        Build();
    }

    /// <summary>
    /// Builds the K-Means cluster index. Full steps:
    /// <list type="number">
    ///   <item>Determine K (explicitly specified or auto = √n)</item>
    ///   <item>Initialize centroids using K-Means++ (converges faster and produces higher-quality clusters than random initialization)</item>
    ///   <item>Iterate Lloyd's algorithm: assign → update centroids, until convergence or max iterations</item>
    ///   <item>Build inverted lists: assign each vector to its nearest cluster</item>
    /// </list>
    /// <para>
    /// Centroid updates use <see cref="TensorPrimitives.Add"/> and <see cref="TensorPrimitives.Divide"/>
    /// for SIMD-accelerated vector accumulation and mean computation.
    /// </para>
    /// </summary>
    private void Build()
    {
        if (_ids.Count == 0) return;

        // ── Step 1: determine K ──
        // When not explicitly specified (0), default to √n to balance search speed and cluster granularity
        var k = _numClusters > 0
            ? _numClusters
            : Math.Max(1, (int)Math.Sqrt(_ids.Count));
        _numClusters = k;

        // Extract all IDs and vectors (materialized from the store for K-Means iteration)
        var allIds = _ids.ToList();
        var allVectors = allIds.Select(id => _vectorStore.Get(id).ToArray()).ToList();
        var dim = allVectors[0].Length;

        // ── Step 2: K-Means++ centroid initialization ──
        _centroids = KMeansPlusPlusInit(allVectors, k, dim);

        // ── Step 3: Lloyd iterations (assign + update centroids) ──
        var assignments = new int[allVectors.Count]; // assignments[i] = cluster index for vector i
        const int maxIterations = 50;

        for (int iter = 0; iter < maxIterations; iter++)
        {
            // Assignment phase: assign each vector to its nearest centroid
            bool changed = false;
            for (int i = 0; i < allVectors.Count; i++)
            {
                var bestCluster = FindNearestCentroid(allVectors[i]);
                if (bestCluster != assignments[i])
                {
                    assignments[i] = bestCluster;
                    changed = true;
                }
            }

            // All assignments unchanged → converged; stop early
            if (!changed) break;

            // Update phase: recompute each cluster's centroid (mean of all member vectors)
            var sums = new float[k][];   // Cumulative vector sums per cluster
            var counts = new int[k];     // Member count per cluster
            for (int c = 0; c < k; c++)
                sums[c] = new float[dim];

            // Accumulation phase: SIMD-accelerated vector addition via TensorPrimitives.Add
            for (int i = 0; i < allVectors.Count; i++)
            {
                var c = assignments[i];
                counts[c]++;
                TensorPrimitives.Add(sums[c], allVectors[i], sums[c]);
            }

            // Divide by member count to get the mean: SIMD-accelerated via TensorPrimitives.Divide
            for (int c = 0; c < k; c++)
            {
                // Leave empty clusters' centroids unchanged (avoid division by zero)
                if (counts[c] == 0) continue;
                TensorPrimitives.Divide(sums[c], (float)counts[c], _centroids[c]);
            }
        }

        // ── Step 4: build inverted lists ──
        _invertedLists = new List<int>[k];
        for (int c = 0; c < k; c++)
            _invertedLists[c] = [];

        // Add each vector's internal ID to its cluster's inverted list
        for (int i = 0; i < allIds.Count; i++)
            _invertedLists[assignments[i]].Add(allIds[i]);

        _isBuilt = true;
        _lastBuildCount = _ids.Count; // Record the current count for future rebuild comparisons
    }

    /// <summary>
    /// Finds the cluster centroid most similar to the given vector and returns its index.
    /// Linear scan over all centroids (K is usually small, so no additional acceleration structure is needed).
    /// </summary>
    /// <param name="vector">The vector to assign.</param>
    /// <returns>The index (0-based) of the nearest cluster centroid.</returns>
    private int FindNearestCentroid(float[] vector)
    {
        int best = 0;
        float bestSim = float.MinValue;
        for (int i = 0; i < _centroids.Length; i++)
        {
            var sim = TSim.Compute(vector, _centroids[i]);
            if (sim > bestSim) { bestSim = sim; best = i; }
        }
        return best;
    }

    /// <summary>
    /// K-Means++ centroid initialization. Produces more spread-out initial centroids than random initialization,
    /// accelerating convergence and reducing the chance of falling into a local optimum.
    /// <para>
    /// Algorithm steps:
    /// <list type="number">
    ///   <item>Randomly select the first centroid</item>
    ///   <item>For each data point, compute its squared distance to the nearest already-selected centroid</item>
    ///   <item>Use squared distance as a weight for roulette-wheel sampling: points farther away have a higher probability of being selected</item>
    ///   <item>Repeat steps 2–3 until K centroids have been selected</item>
    /// </list>
    /// </para>
    /// <para>
    /// Uses a fixed seed (42) for reproducibility. Distance computation uses <see cref="TensorPrimitives.Distance"/> for SIMD acceleration.
    /// </para>
    /// </summary>
    /// <param name="vectors">All vector data.</param>
    /// <param name="k">Number of centroids to select.</param>
    /// <param name="dim">Vector dimension.</param>
    /// <returns>The initialized centroid array (cloned; does not reference the original data).</returns>
    private static float[][] KMeansPlusPlusInit(List<float[]> vectors, int k, int dim)
    {
        var rng = new Random(42);  // Fixed seed for reproducibility
        var centroids = new float[k][];

        // Step 1: randomly select the first centroid
        centroids[0] = (float[])vectors[rng.Next(vectors.Count)].Clone();

        // distances[i] = squared distance from vector i to its nearest already-selected centroid
        var distances = new float[vectors.Count];

        // Steps 2–3: select the remaining k-1 centroids
        for (int c = 1; c < k; c++)
        {
            // Compute each data point's squared distance to the nearest already-selected centroid
            float totalDist = 0;
            for (int i = 0; i < vectors.Count; i++)
            {
                float minDist = float.MaxValue;
                for (int j = 0; j < c; j++)
                {
                    // Use TensorPrimitives.Distance for SIMD-accelerated Euclidean distance
                    var d = TensorPrimitives.Distance(vectors[i], centroids[j]);
                    minDist = Math.Min(minDist, d * d);  // Squared distance (avoid unnecessary sqrt then re-square)
                }
                distances[i] = minDist;
                totalDist += minDist;
            }

            // Roulette-wheel sampling: larger squared distance → higher probability of being selected as the next centroid
            // This ensures new centroids are as far as possible from existing ones, improving cluster spread
            var threshold = rng.NextSingle() * totalDist;
            float cumulative = 0;
            for (int i = 0; i < vectors.Count; i++)
            {
                cumulative += distances[i];
                if (cumulative >= threshold)
                {
                    centroids[c] = (float[])vectors[i].Clone();
                    break;
                }
            }

            // Fallback for edge cases: when all distances are 0 (e.g., duplicate vectors), select randomly
            centroids[c] ??= (float[])vectors[rng.Next(vectors.Count)].Clone();
        }
        return centroids;
    }

    #endregion
}