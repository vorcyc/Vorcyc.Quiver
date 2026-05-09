## 5. Index Types

### 5.1 Flat (Brute-Force Search)

Traverses all vectors computing similarity, results are **100% exact**, and is the default index type.

| Property | Value |
|----------|-------|
| Implementation | `FlatIndex` |
| Time Complexity | O(n * d) |
| Space Complexity | O(n * d) |
| Accuracy | 100% |
| Suitable Data Size | < 10,000 |
| Parallel Threshold | Automatically enables `Parallel.ForEach` when > 10,000 entries |

```mermaid
flowchart TD
    Q["Query vector q"] --> CHECK{"Vector count > 10,000?"}
    CHECK -- "No" --> SEQ["Sequential search<br/>Traverse all vectors computing sim(q, v)"]
    CHECK -- "Yes" --> PAR["Parallel search<br/>Parallel.ForEach + ConcurrentBag"]
    SEQ --> SORT["OrderByDescending(sim)<br/>.Take(topK)"]
    PAR --> SORT
    SORT --> RES["Top-K results"]
```

**Search Strategy Switching**:

```csharp
// Small data (<=10K): sequential traversal is faster, avoids thread scheduling overhead
private List<(int Id, float Similarity)> SequentialSearchCore(float[] query, int topK)
{
    var results = new List<(int Id, float Sim)>(_vectors.Count);
    foreach (var (id, vector) in _vectors)
        results.Add((id, similarityFunc(query, vector)));
    return results.OrderByDescending(r => r.Sim).Take(topK).ToList();
}

// Large data (>10K): Parallel.ForEach for multi-threaded parallel computation
private List<(int Id, float Similarity)> ParallelSearchCore(float[] query, int topK)
{
    var results = new ConcurrentBag<(int Id, float Similarity)>();
    Parallel.ForEach(_vectors, kvp =>
    {
        results.Add((kvp.Key, similarityFunc(query, kvp.Value)));
    });
    return results.OrderByDescending(r => r.Similarity).Take(topK).ToList();
}
```

```csharp
// Usage: default index, no [QuiverIndex] annotation needed
[QuiverVector(128)]
public float[] Embedding { get; set; } = [];
```

### 5.2 HNSW (Hierarchical Navigable Small World Graph)

Multi-layer proximity graph structure, the **universal preferred choice for approximate search**. Similar to "highway -> regional road -> local road" layered navigation.

| Property | Value |
|----------|-------|
| Implementation | `HnswIndex` |
| Search Complexity | O(log n) |
| Insert Complexity | O(log n) * efConstruction |
| Space Complexity | O(n * M) |
| Suitable Data Size | 10K ~ 10M |
| Deletion Strategy | Lazy deletion (residual references auto-cleaned) |
| Persistence Optimization | `SaveAsync` writes `IndexSnapshot`; load restores graph topology first |

#### HNSW Snapshot Persistence

Building the HNSW graph is often more expensive than reading entity and vector bytes. Quiver writes HNSW topology into a `SegmentKind.IndexSnapshot` segment during full saves. The snapshot contains the entry point, max level, per-node level, per-layer neighbor lists, and the covered `NextId`. On the next `LoadAsync()`, if the snapshot fingerprint matches the current similarity type, parameters, and effective dimension, Quiver restores the graph directly and skips `Add(id)` rebuild for covered ids.

This is automatic and requires no additional configuration. Old files, corrupted snapshots, or parameter mismatches safely fall back to a full rebuild. The snapshot stores topology only, not entities or vector copies, so mmap vector reads, non-InMemory vector properties, and `[QuiverLargeField]` large-object loading keep the same behavior.

#### HNSW Layered Structure

```mermaid
graph TD
    subgraph "Layer 2 (Sparse, Highway)"
        L2A((A)) --- L2D((D))
    end

    subgraph "Layer 1 (Medium Density, Regional Road)"
        L1A((A)) --- L1B((B))
        L1B --- L1D((D))
        L1A --- L1D
    end

    subgraph "Layer 0 (Densest, Local Road - All Nodes)"
        L0A((A)) --- L0B((B))
        L0B --- L0C((C))
        L0C --- L0D((D))
        L0D --- L0E((E))
        L0A --- L0C
        L0B --- L0E
        L0A --- L0E
    end

    L2A -.->|Same node| L1A
    L2D -.->|Same node| L1D
    L1A -.->|Same node| L0A
    L1B -.->|Same node| L0B
    L1D -.->|Same node| L0D
```

#### Insertion Algorithm Flow

```mermaid
flowchart TD
    START["Add(id, vector)"] --> RL["RandomLevel()<br/>Exponential decay random level l"]
    RL --> CREATE["Create HnswNode(id, vector, l)"]
    CREATE --> EMPTY{"Graph empty?"}
    EMPTY -- "Yes" --> EP["Set as entry point<br/>_entryPointId = id"]
    EMPTY -- "No" --> GREEDY["Start from entry point<br/>Greedy search from maxLevel to l+1<br/>(ef=1)<br/>Quickly locate target region"]
    GREEDY --> LAYER["From min(l, maxLevel) to layer 0<br/>Build bidirectional connections layer by layer"]
    LAYER --> SRCH["SearchLayer(ef=efConstruction)<br/>Search best neighbors at current layer"]
    SRCH --> SELECT["Select Top-mMax neighbors<br/>Layer 0: mMax = M×2<br/>Other layers: mMax = M"]
    SELECT --> CONNECT["Establish bidirectional connections<br/>node ↔ neighbor"]
    CONNECT --> PRUNE{"Neighbor connection count > mMax?"}
    PRUNE -- "Yes" --> TRIM["PruneConnections()<br/>Keep the mMax with highest similarity"]
    PRUNE -- "No" --> NEXT{"More layers remaining?"}
    TRIM --> NEXT
    NEXT -- "Yes" --> LAYER
    NEXT -- "No" --> UPEP{"l > maxLevel?"}
    UPEP -- "Yes" --> NEWEP["Update entry point to new node"]
    UPEP -- "No" --> DONE["Done"]
    NEWEP --> DONE
```

#### Search Algorithm Flow

```mermaid
flowchart TD
    START["Search(query, topK)"] --> GREEDY["Start from entry point<br/>Greedy search from maxLevel to layer 1<br/>(ef=1)<br/>Quickly approach target region"]
    GREEDY --> FINE["At layer 0<br/>ef = max(efSearch, topK)<br/>Fine-grained search"]
    FINE --> TOPK["Take top topK by similarity"]
    TOPK --> RES["Return (id, similarity) list"]
```

**Parameter Tuning Guide**:

| Parameter | Default | Recommended Range | Increase Effect | Decrease Effect |
|-----------|---------|-------------------|-----------------|-----------------|
| `M` | 16 | 12 ~ 48 | Higher recall, more memory, longer build time | Less memory, lower recall |
| `EfConstruction` | 200 | 100 ~ 500 | Better graph quality, slower insertion | Faster insertion, lower graph quality |
| `EfSearch` | 50 | 50 ~ 500 | Higher recall, slower search | Faster search, lower recall |

> **`EfSearch` can be dynamically adjusted at runtime** without rebuilding the index: `hnswIndex.EfSearch = 200;`

```csharp
[QuiverVector(768, DistanceMetric.Cosine)]
[QuiverIndex(VectorIndexType.HNSW, M = 32, EfConstruction = 300, EfSearch = 100)]
public float[] Embedding { get; set; } = [];
```

### 5.3 IVF (Inverted File Index)

Partitions vector space based on **K-Means clustering**, only probing the nearest clusters during search.

| Property | Value |
|----------|-------|
| Implementation | `IvfIndex` |
| Build Complexity | O(n * k * d * iter) |
| Search Complexity | O(k * d + nProbe * n/k * d) |
| Suitable Data Size | 100K+ |
| Build Method | Lazy (triggered on first search) |
| Auto-Rebuild | Flagged for rebuild after 50% data growth |
| Centroid Initialization | K-Means++ |
| Iteration Algorithm | Lloyd (max 50 rounds) |
| SIMD Acceleration | internal `VectorMath.Add` / `VectorMath.Divide` |

#### IVF Search Flow

```mermaid
flowchart TD
    Q["Query vector q"] --> ENSURE["EnsureBuilt()<br/>Build on first search or data growth"]
    ENSURE --> BUILD{"Need to build?"}
    BUILD -- "Yes" --> KMEANS["K-Means clustering<br/>1. K-Means++ initialize centroids<br/>2. Lloyd iteration (max 50 rounds)<br/>3. Build inverted lists"]
    BUILD -- "No" --> CENT["Compute similarity of q against all K centroids"]
    KMEANS --> CENT
    CENT --> PROBE["Select the nProbe most similar clusters"]
    PROBE --> SCAN["Traverse inverted lists of selected clusters<br/>Compute exact similarity"]
    SCAN --> TOPK["OrderByDescending(sim)<br/>.Take(topK)"]
    TOPK --> RES["Return Top-K results"]
```

#### K-Means Clustering Build

```mermaid
flowchart TD
    START["Build()"] --> K["Determine cluster count K<br/>Explicitly specified or auto √n"]
    K --> INIT["K-Means++ initialize centroids<br/>Probability proportional to distance²"]
    INIT --> ITER["Lloyd iteration loop"]
    ITER --> ASSIGN["Assignment phase: each vector → nearest centroid"]
    ASSIGN --> CHK{"Assignments changed?"}
    CHK -- "No (converged)" --> IL["Build inverted lists"]
    CHK -- "Yes" --> UPDATE["Update phase:<br/>Centroid = mean of member vectors<br/>VectorMath.Add (SIMD accumulation)<br/>VectorMath.Divide (SIMD division)"]
    UPDATE --> MAX{"Reached 50 rounds?"}
    MAX -- "No" --> ASSIGN
    MAX -- "Yes" --> IL
    IL --> DONE["Record _lastBuildCount<br/>_isBuilt = true"]
```

**Parameter Tuning**:

| Parameter | Default | Recommended Range | Description |
|-----------|---------|-------------------|-------------|
| `NumClusters` | 0 (auto sqrt(n)) | sqrt(n) ~ 4*sqrt(n) | Cluster count. Larger -> smaller clusters -> faster search but more centroid comparisons |
| `NumProbes` | 10 | 1 ~ 20 | Probe count. When = total clusters, degrades to brute-force search |

> **Threshold search** automatically expands probe range to `nProbe * 2`, reducing missed results from cluster partitioning.

```csharp
[QuiverVector(128, DistanceMetric.Cosine)]
[QuiverIndex(VectorIndexType.IVF, NumClusters = 100, NumProbes = 15)]
public float[] Feature { get; set; } = [];
```

### 5.4 KDTree

Spatial binary partition tree for **exact search**. Alternately splits space along dimensions, using pruning to skip impossible subtrees.

| Property | Value |
|----------|-------|
| Implementation | `KDTreeIndex` |
| Search Complexity | O(log n) (low dim), O(n) (high dim) |
| Accuracy | 100% |
| Suitable Dimensions | < 20 |
| Build Method | Lazy (triggered on first search, full rebuild) |
| Rebuild Trigger | Flagged for rebuild after every Add/Remove |

#### KD-Tree Structure Diagram

```mermaid
graph TD
    ROOT["Root Node<br/>SplitDim=X, SplitVal=5"]
    L1["Left Subtree<br/>SplitDim=Y, SplitVal=3<br/>(X ≤ 5)"]
    R1["Right Subtree<br/>SplitDim=Y, SplitVal=7<br/>(X > 5)"]
    LL["Left-Left<br/>SplitDim=Z, SplitVal=1"]
    LR["Left-Right<br/>SplitDim=Z, SplitVal=4"]
    RL["Right-Left<br/>SplitDim=Z, SplitVal=6"]
    RR["Right-Right<br/>SplitDim=Z, SplitVal=9"]

    ROOT --> L1
    ROOT --> R1
    L1 --> LL
    L1 --> LR
    R1 --> RL
    R1 --> RR
```

#### Search Pruning Strategy

```mermaid
flowchart TD
    START["SearchNode(node, query, topK)"] --> CALC["Compute sim(query, node.Vector)"]
    CALC --> HEAP["Min-heap update<br/>heap &lt; topK → push<br/>heap full and sim > heap top → replace"]
    HEAP --> DIFF["diff = query[splitDim] - node.splitValue"]
    DIFF --> FIRST["Search subtree on query point's side<br/>(diff ≤ 0 ? Left : Right)"]
    FIRST --> CHECK{"heap &lt; topK<br/>OR |diff| &lt; search radius?"}
    CHECK -- "Yes" --> SECOND["Search the other subtree<br/>(may contain better results)"]
    CHECK -- "No (prune)" --> SKIP["Skip the other side<br/>Cannot have better results ✂️"]
    SECOND --> DONE["Return"]
    SKIP --> DONE
```

> ⚠️ **Curse of Dimensionality**: When dimensions exceed ~20, nearly every subtree must be visited (pruning fails), degrading to O(n). Use HNSW for high-dimensional scenarios.  
> ⚠️ **Threshold search** degrades to brute-force traversal (KD-Tree pruning cannot be directly applied to threshold search).

```csharp
[QuiverVector(16, DistanceMetric.Euclidean)]
[QuiverIndex(VectorIndexType.KDTree)]
public float[] LowDimFeature { get; set; } = [];
```

### 5.5 Index Selection Decision Guide

```mermaid
flowchart TD
    START["Choose Index Type"] --> Q1{"Data size < 10K?"}
    Q1 -- "Yes" --> FLAT["Flat<br/>Brute-force, 100% exact<br/>Simple and reliable"]
    Q1 -- "No" --> Q2{"Dimensions < 20?"}
    Q2 -- "Yes" --> KDT["KDTree<br/>Exact search, O(log n)<br/>Best for low dimensions"]
    Q2 -- "No" --> Q3{"Data size > 100K<br/>and batch queries needed?"}
    Q3 -- "Yes" --> IVF["IVF<br/>Cluster search, high throughput<br/>Adjustable accuracy"]
    Q3 -- "No" --> HNSW["HNSW<br/>Universal preferred<br/>O(log n), high recall"]

    style FLAT fill:#d4edda
    style HNSW fill:#cce5ff
    style IVF fill:#fff3cd
    style KDT fill:#f8d7da
```

**Comprehensive Comparison Table**:

| Dimension | Flat | HNSW | IVF | KDTree |
|-----------|------|------|-----|--------|
| Search Speed | O(n*d) | O(log n) | O(n/k*d) | O(log n) ~ O(n) |
| Accuracy | 100% | ~95-99%+ | ~90-99% | 100% |
| Insert Speed | O(1) | O(log n) | O(1)* | O(1)** |
| Memory | n*d | n*(d+M) | n*d + k*d | n*d + tree structure |
| Suitable Data Size | <10K | 10K~10M | 100K+ | <10K (low dim) |
| Suitable Dimensions | Any | Any | Any | <20 |
| Build Method | Immediate | Immediate | Lazy | Lazy |
| Parallelization | Yes >10K | No | No | No |

> \* IVF insertion is immediate, but index needs rebuilding
> \*\* KDTree insertion is immediate, but tree needs rebuilding

---

