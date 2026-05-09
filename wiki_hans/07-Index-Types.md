## 5. 索引类型

### 5.1 Flat（暴力搜索）

遍历所有向量计算相似度，结果 **100% 精确**，是默认索引类型。

| 属性 | 值 |
|------|-----|
| 实现类 | `FlatIndex` |
| 时间复杂度 | O(n × d) |
| 空间复杂度 | O(n × d) |
| 精确度 | 100% |
| 适合数据量 | < 10,000 |
| 并行阈值 | > 10,000 条时自动启用 `Parallel.ForEach` |

```mermaid
flowchart TD
    Q["查询向量 q"] --> CHECK{"向量数 > 10,000?"}
    CHECK -- "否" --> SEQ["顺序搜索<br/>遍历所有向量计算 sim(q, v)"]
    CHECK -- "是" --> PAR["并行搜索<br/>Parallel.ForEach + ConcurrentBag"]
    SEQ --> SORT["OrderByDescending(sim)<br/>.Take(topK)"]
    PAR --> SORT
    SORT --> RES["Top-K 结果"]
```

**搜索策略切换**：

```csharp
// 小数据量（≤ 10K）：顺序遍历更快，避免线程调度开销
private List<(int Id, float Similarity)> SequentialSearchCore(float[] query, int topK)
{
    var results = new List<(int Id, float Sim)>(_vectors.Count);
    foreach (var (id, vector) in _vectors)
        results.Add((id, similarityFunc(query, vector)));
    return results.OrderByDescending(r => r.Sim).Take(topK).ToList();
}

// 大数据量（> 10K）：Parallel.ForEach 多线程并行计算
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
// 使用方式：默认索引，无需标记 [QuiverIndex]
[QuiverVector(128)]
public float[] Embedding { get; set; } = [];
```

### 5.2 HNSW（分层可导航小世界图）

多层近邻图结构，**近似搜索的通用首选**。类似"高速公路 → 省道 → 乡道"的分层导航。

| 属性 | 值 |
|------|-----|
| 实现类 | `HnswIndex` |
| 搜索复杂度 | O(log n) |
| 插入复杂度 | O(log n) × efConstruction |
| 空间复杂度 | O(n × M) |
| 适合数据量 | 10K ~ 10M |
| 删除策略 | 惰性删除（残留引用自动清理） |
| 持久化优化 | `SaveAsync` 写入 `IndexSnapshot`，加载时优先恢复图拓扑 |

#### HNSW 快照持久化

HNSW 图构建成本高于实体和向量的二进制读取成本。Quiver 会在全量保存时把 HNSW 拓扑写入 `SegmentKind.IndexSnapshot` 段，包含入口点、最大层级、节点层数、每层邻居列表和快照覆盖的 `NextId`。下一次 `LoadAsync()` 时，如果快照指纹与当前相似度、参数和有效维度匹配，就直接恢复图结构，并跳过已覆盖 id 的 `Add(id)` 重建。

该机制是自动的，无需额外配置。旧文件、损坏快照或参数不匹配时会安全回退到完整重建。快照只保存索引拓扑，不保存实体或向量副本，因此不会破坏 mmap 向量读取、非 InMemory 向量属性或 `[QuiverLargeField]` 大对象加载。

#### HNSW 分层结构

```mermaid
graph TD
    subgraph "Layer 2 (稀疏，高速公路)"
        L2A((A)) --- L2D((D))
    end

    subgraph "Layer 1 (中等密度，省道)"
        L1A((A)) --- L1B((B))
        L1B --- L1D((D))
        L1A --- L1D
    end

    subgraph "Layer 0 (最稠密，乡道 — 所有节点)"
        L0A((A)) --- L0B((B))
        L0B --- L0C((C))
        L0C --- L0D((D))
        L0D --- L0E((E))
        L0A --- L0C
        L0B --- L0E
        L0A --- L0E
    end

    L2A -.->|同一节点| L1A
    L2D -.->|同一节点| L1D
    L1A -.->|同一节点| L0A
    L1B -.->|同一节点| L0B
    L1D -.->|同一节点| L0D
```

#### 插入算法流程

```mermaid
flowchart TD
    START["Add(id, vector)"] --> RL["RandomLevel()<br/>指数衰减随机层级 l"]
    RL --> CREATE["创建 HnswNode(id, vector, l)"]
    CREATE --> EMPTY{"图为空?"}
    EMPTY -- "是" --> EP["设为入口点<br/>_entryPointId = id"]
    EMPTY -- "否" --> GREEDY["从入口点开始<br/>在 maxLevel → l+1 层<br/>贪心搜索 (ef=1)<br/>快速定位目标区域"]
    GREEDY --> LAYER["在 min(l, maxLevel) → 0 层<br/>逐层建立双向连接"]
    LAYER --> SRCH["SearchLayer(ef=efConstruction)<br/>搜索当前层最佳邻居"]
    SRCH --> SELECT["选择 Top-mMax 个邻居<br/>第 0 层: mMax = M×2<br/>其他层: mMax = M"]
    SELECT --> CONNECT["建立双向连接<br/>node ↔ neighbor"]
    CONNECT --> PRUNE{"邻居连接数 > mMax?"}
    PRUNE -- "是" --> TRIM["PruneConnections()<br/>保留相似度最高的 mMax 个"]
    PRUNE -- "否" --> NEXT{"还有下一层?"}
    TRIM --> NEXT
    NEXT -- "是" --> LAYER
    NEXT -- "否" --> UPEP{"l > maxLevel?"}
    UPEP -- "是" --> NEWEP["更新入口点为新节点"]
    UPEP -- "否" --> DONE["完成"]
    NEWEP --> DONE
```

#### 搜索算法流程

```mermaid
flowchart TD
    START["Search(query, topK)"] --> GREEDY["从入口点出发<br/>在 maxLevel → 1 层<br/>贪心搜索 (ef=1)<br/>快速靠近目标区域"]
    GREEDY --> FINE["在第 0 层<br/>ef = max(efSearch, topK)<br/>精细搜索"]
    FINE --> TOPK["取相似度最高的 topK 个"]
    TOPK --> RES["返回 (id, similarity) 列表"]
```

**参数调优指南**：

| 参数 | 默认值 | 推荐范围 | 增大效果 | 减小效果 |
|------|--------|---------|---------|---------|
| `M` | 16 | 12 ~ 48 | ↑召回率 ↑内存 ↑构建时间 | ↓内存 ↓召回率 |
| `EfConstruction` | 200 | 100 ~ 500 | ↑图质量 ↓插入速度 | ↑插入速度 ↓图质量 |
| `EfSearch` | 50 | 50 ~ 500 | ↑召回率 ↓搜索速度 | ↑搜索速度 ↓召回率 |

> **`EfSearch` 可运行时动态调整**，无需重建索引：`hnswIndex.EfSearch = 200;`

```csharp
[QuiverVector(768, DistanceMetric.Cosine)]
[QuiverIndex(VectorIndexType.HNSW, M = 32, EfConstruction = 300, EfSearch = 100)]
public float[] Embedding { get; set; } = [];
```

### 5.3 IVF（倒排文件索引）

基于 **K-Means 聚类**划分向量空间，搜索时只探测最近的几个聚类。

| 属性 | 值 |
|------|-----|
| 实现类 | `IvfIndex` |
| 构建复杂度 | O(n × k × d × iter) |
| 搜索复杂度 | O(k × d + nProbe × n/k × d) |
| 适合数据量 | 100K+ |
| 构建方式 | 惰性（首次搜索时触发） |
| 自动重建 | 数据量增长 50% 后标记重建 |
| 质心初始化 | K-Means++ |
| 迭代算法 | Lloyd（最大 50 轮） |
| SIMD 加速 | 内部 `VectorMath.Add` / `VectorMath.Divide` |

#### IVF 搜索流程

```mermaid
flowchart TD
    Q["查询向量 q"] --> ENSURE["EnsureBuilt()<br/>首次搜索或数据增长时构建"]
    ENSURE --> BUILD{"需要构建?"}
    BUILD -- "是" --> KMEANS["K-Means 聚类<br/>1. K-Means++ 初始化质心<br/>2. Lloyd 迭代（max 50 轮）<br/>3. 构建倒排列表"]
    BUILD -- "否" --> CENT["计算 q 与所有 K 个质心的相似度"]
    KMEANS --> CENT
    CENT --> PROBE["选取最相似的 nProbe 个聚类"]
    PROBE --> SCAN["遍历选中聚类的倒排列表<br/>计算精确相似度"]
    SCAN --> TOPK["OrderByDescending(sim)<br/>.Take(topK)"]
    TOPK --> RES["返回 Top-K 结果"]
```

#### K-Means 聚类构建

```mermaid
flowchart TD
    START["Build()"] --> K["确定聚类数 K<br/>显式指定 or 自动 √n"]
    K --> INIT["K-Means++ 初始化质心<br/>概率正比于距离²"]
    INIT --> ITER["Lloyd 迭代循环"]
    ITER --> ASSIGN["分配阶段：每个向量 → 最近质心"]
    ASSIGN --> CHK{"分配是否变化?"}
    CHK -- "否 (收敛)" --> IL["构建倒排列表"]
    CHK -- "是" --> UPDATE["更新阶段：<br/>质心 = 成员向量均值<br/>VectorMath.Add (SIMD 累加)<br/>VectorMath.Divide (SIMD 除法)"]
    UPDATE --> MAX{"达到 50 轮?"}
    MAX -- "否" --> ASSIGN
    MAX -- "是" --> IL
    IL --> DONE["记录 _lastBuildCount<br/>_isBuilt = true"]
```

**参数调优**：

| 参数 | 默认值 | 推荐范围 | 说明 |
|------|--------|---------|------|
| `NumClusters` | 0（自动 √n） | √n ~ 4√n | 聚类数。增大 → 每个聚类更小 → 搜索更快但质心比较增多 |
| `NumProbes` | 10 | 1 ~ 20 | 探测聚类数。= 聚类总数时退化为暴力搜索 |

> **阈值搜索**时探测范围自动扩大为 `nProbe × 2`，降低因聚类划分导致的漏检。

```csharp
[QuiverVector(128, DistanceMetric.Cosine)]
[QuiverIndex(VectorIndexType.IVF, NumClusters = 100, NumProbes = 15)]
public float[] Feature { get; set; } = [];
```

### 5.4 KDTree（KD 树）

空间二叉划分树，**精确搜索**。沿各维度交替切分空间，利用剪枝跳过不可能的子树。

| 属性 | 值 |
|------|-----|
| 实现类 | `KDTreeIndex` |
| 搜索复杂度 | O(log n)（低维），O(n)（高维） |
| 精确度 | 100% |
| 适合维度 | < 20 维 |
| 构建方式 | 惰性（首次搜索触发全量重建） |
| 重建触发 | 每次 Add/Remove 后标记重建 |

#### KD-Tree 结构示意

```mermaid
graph TD
    ROOT["根节点<br/>SplitDim=X, SplitVal=5"]
    L1["左子树<br/>SplitDim=Y, SplitVal=3<br/>(X ≤ 5)"]
    R1["右子树<br/>SplitDim=Y, SplitVal=7<br/>(X > 5)"]
    LL["左左<br/>SplitDim=Z, SplitVal=1"]
    LR["左右<br/>SplitDim=Z, SplitVal=4"]
    RL["右左<br/>SplitDim=Z, SplitVal=6"]
    RR["右右<br/>SplitDim=Z, SplitVal=9"]

    ROOT --> L1
    ROOT --> R1
    L1 --> LL
    L1 --> LR
    R1 --> RL
    R1 --> RR
```

#### 搜索剪枝策略

```mermaid
flowchart TD
    START["SearchNode(node, query, topK)"] --> CALC["计算 sim(query, node.Vector)"]
    CALC --> HEAP["最小堆更新<br/>堆 &lt; topK → 入堆<br/>堆已满且 sim > 堆顶 → 替换"]
    HEAP --> DIFF["diff = query[splitDim] - node.splitValue"]
    DIFF --> FIRST["搜索查询点所在侧的子树<br/>(diff ≤ 0 ? Left : Right)"]
    FIRST --> CHECK{"堆 &lt; topK<br/>OR |diff| &lt; 搜索半径?"}
    CHECK -- "是" --> SECOND["搜索另一侧子树<br/>(可能包含更优结果)"]
    CHECK -- "否 (剪枝)" --> SKIP["跳过另一侧<br/>不可能有更优结果 ✂️"]
    SECOND --> DONE["返回"]
    SKIP --> DONE
```

> ⚠️ **维度诅咒**：维度超过约 20 时，几乎每个子树都需要访问（剪枝失效），退化为 O(n)。高维场景应使用 HNSW。  
> ⚠️ **阈值搜索**退化为暴力遍历（KD-Tree 的剪枝难以直接应用于阈值搜索）。

```csharp
[QuiverVector(16, DistanceMetric.Euclidean)]
[QuiverIndex(VectorIndexType.KDTree)]
public float[] LowDimFeature { get; set; } = [];
```

### 5.5 索引选择决策指南

```mermaid
flowchart TD
    START["选择索引类型"] --> Q1{"数据量 &lt; 10K?"}
    Q1 -- "是" --> FLAT["✅ Flat<br/>暴力搜索，100% 精确<br/>简单可靠"]
    Q1 -- "否" --> Q2{"维度 &lt; 20?"}
    Q2 -- "是" --> KDT["✅ KDTree<br/>精确搜索，O(log n)<br/>低维最优"]
    Q2 -- "否" --> Q3{"数据量 &gt; 100K<br/>且需要批量查询?"}
    Q3 -- "是" --> IVF["✅ IVF<br/>聚类搜索，高吞吐<br/>可调精度"]
    Q3 -- "否" --> HNSW["✅ HNSW<br/>通用首选<br/>O(log n)，高召回率"]

    style FLAT fill:#d4edda
    style HNSW fill:#cce5ff
    style IVF fill:#fff3cd
    style KDT fill:#f8d7da
```

**综合对比表**：

| 维度 | Flat | HNSW | IVF | KDTree |
|------|------|------|-----|--------|
| 搜索速度 | O(n×d) | O(log n) | O(n/k×d) | O(log n) ~ O(n) |
| 精确度 | 100% | ~95-99%+ | ~90-99% | 100% |
| 插入速度 | O(1) | O(log n) | O(1)* | O(1)** |
| 内存 | n×d | n×(d+M) | n×d + k×d | n×d + 树结构 |
| 适合数据量 | <10K | 10K~10M | 100K+ | <10K (低维) |
| 适合维度 | 任意 | 任意 | 任意 | <20 |
| 构建方式 | 即时 | 即时 | 惰性 | 惰性 |
| 并行化 | ✅ >10K | ❌ | ❌ | ❌ |

> \* IVF 插入即时，但索引需重建  
> \*\* KDTree 插入即时，但树需重建

---

