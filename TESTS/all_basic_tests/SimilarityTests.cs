using Vorcyc.Quiver;
using Vorcyc.Quiver.Similarity;
using static AllBasicTests.TestHelper;

namespace AllBasicTests;

/// <summary>
/// 测试 10：相似度度量测试。
/// 覆盖全部 9 种内置度量的数学正确性、端到端搜索排名、CustomSimilarity 属性路径。
/// </summary>
public static class SimilarityTests
{
    private const int Dim = 64;
    private const int N = 200;
    private const float Eps = 1e-5f;

    public static async Task RunAsync()
    {
        Console.WriteLine("\n═══ 10. 相似度度量测试 ═══");

        TestMathCorrectness();
        TestSelfSimilarityIsMax();
        await TestEndToEndSearchAsync();
        await TestCustomSimilarityAsync();
    }

    // ────────────────────────────────────────────────────────────
    // 1. 数学正确性：用手工计算的已知值验证每个 Compute 方法
    // ────────────────────────────────────────────────────────────

    private static void TestMathCorrectness()
    {
        // 准备已知向量
        float[] a = [1, 2, 3, 4];
        float[] b = [2, 3, 4, 5];
        float[] zero = [0, 0, 0, 0];

        // Cosine: (1*2+2*3+3*4+4*5) / (√30 × √54) = 40 / √1620
        var cos = CosineSimilarity.Compute(a, b);
        var expected = 40f / MathF.Sqrt(30f * 54f);
        Assert(MathF.Abs(cos - expected) < Eps, "Cosine 数学正确性");

        // DotProduct: 1*2+2*3+3*4+4*5 = 40
        var dot = DotProductSimilarity.Compute(a, b);
        Assert(MathF.Abs(dot - 40f) < Eps, "DotProduct 数学正确性");

        // Euclidean: 1/(1+√((1-2)²+(2-3)²+(3-4)²+(4-5)²)) = 1/(1+2)
        var euc = EuclideanSimilarity.Compute(a, b);
        Assert(MathF.Abs(euc - 1f / (1f + 2f)) < Eps, "Euclidean 数学正确性");

        // Manhattan: 1/(1+(1+1+1+1)) = 1/5
        var man = ManhattanSimilarity.Compute(a, b);
        Assert(MathF.Abs(man - 1f / 5f) < Eps, "Manhattan 数学正确性");

        // Chebyshev: 1/(1+max(1,1,1,1)) = 1/2
        var cheb = ChebyshevSimilarity.Compute(a, b);
        Assert(MathF.Abs(cheb - 0.5f) < Eps, "Chebyshev 数学正确性");

        // Pearson: 对 a=[1,2,3,4] b=[2,3,4,5] (均为等差数列，完美线性相关 → r=1.0)
        var pear = PearsonCorrelationSimilarity.Compute(a, b);
        Assert(MathF.Abs(pear - 1.0f) < Eps, "Pearson 完美线性相关 = 1.0");

        // Pearson: a=[1,2,3,4] vs 反向 b=[4,3,2,1] → r=-1.0
        float[] rev = [4, 3, 2, 1];
        var pearNeg = PearsonCorrelationSimilarity.Compute(a, rev);
        Assert(MathF.Abs(pearNeg - (-1.0f)) < Eps, "Pearson 完美负相关 = -1.0");

        // Hamming: a==a → 1.0, a vs b → 0/4 match = 0.0
        var hamSelf = HammingSimilarity.Compute(a, a);
        Assert(MathF.Abs(hamSelf - 1.0f) < Eps, "Hamming 自身 = 1.0");
        var hamDiff = HammingSimilarity.Compute(a, b);
        Assert(MathF.Abs(hamDiff - 0.0f) < Eps, "Hamming 完全不同 = 0.0");

        // Hamming: 部分匹配
        float[] c = [1, 2, 99, 4]; // 1/4 不匹配
        var hamPartial = HammingSimilarity.Compute(a, c);
        Assert(MathF.Abs(hamPartial - 0.75f) < Eps, "Hamming 3/4 匹配 = 0.75");

        // Jaccard: Σmin/Σmax = min(1,2)+min(2,3)+min(3,4)+min(4,5) / max(1,2)+max(2,3)+max(3,4)+max(4,5)
        //        = (1+2+3+4)/(2+3+4+5) = 10/14
        var jac = JaccardSimilarity.Compute(a, b);
        Assert(MathF.Abs(jac - 10f / 14f) < Eps, "Jaccard 数学正确性");

        // Jaccard: 全零 → 1.0 (定义)
        var jacZero = JaccardSimilarity.Compute(zero, zero);
        Assert(MathF.Abs(jacZero - 1.0f) < Eps, "Jaccard 全零 = 1.0");

        // Canberra: Σ|ai-bi|/(|ai|+|bi|)/n = (1/3+1/5+1/7+1/9)/4
        var canExpected = 1f - (1f / 3f + 1f / 5f + 1f / 7f + 1f / 9f) / 4f;
        var can = CanberraSimilarity.Compute(a, b);
        Assert(MathF.Abs(can - canExpected) < Eps, "Canberra 数学正确性");

        // Canberra: 全零 → 1.0
        var canZero = CanberraSimilarity.Compute(zero, zero);
        Assert(MathF.Abs(canZero - 1.0f) < Eps, "Canberra 全零 = 1.0");
    }

    // ────────────────────────────────────────────────────────────
    // 2. 自身相似度应为最大值（对所有返回 (0,1] 或 [0,1] 的度量）
    // ────────────────────────────────────────────────────────────

    private static void TestSelfSimilarityIsMax()
    {
        var rng = new Random(123);
        var v = RandomVector(rng, Dim);

        // 对所有距离转换型度量：self-similarity = 1.0
        Assert(MathF.Abs(EuclideanSimilarity.Compute(v, v) - 1f) < Eps, "Euclidean 自身 = 1.0");
        Assert(MathF.Abs(ManhattanSimilarity.Compute(v, v) - 1f) < Eps, "Manhattan 自身 = 1.0");
        Assert(MathF.Abs(ChebyshevSimilarity.Compute(v, v) - 1f) < Eps, "Chebyshev 自身 = 1.0");
        Assert(MathF.Abs(HammingSimilarity.Compute(v, v) - 1f) < Eps, "Hamming 自身 = 1.0");
        Assert(MathF.Abs(CanberraSimilarity.Compute(v, v) - 1f) < Eps, "Canberra 自身 = 1.0");

        // Cosine: self = 1.0
        Assert(MathF.Abs(CosineSimilarity.Compute(v, v) - 1f) < Eps, "Cosine 自身 = 1.0");

        // Pearson: self = 1.0
        Assert(MathF.Abs(PearsonCorrelationSimilarity.Compute(v, v) - 1f) < Eps, "Pearson 自身 = 1.0");

        // Jaccard: 非负向量 self = 1.0
        var vPos = new float[Dim];
        for (int i = 0; i < Dim; i++) vPos[i] = MathF.Abs(v[i]);
        Assert(MathF.Abs(JaccardSimilarity.Compute(vPos, vPos) - 1f) < Eps, "Jaccard 自身 = 1.0");
    }

    // ────────────────────────────────────────────────────────────
    // 3. 端到端搜索：每种内置度量都能正确完成 add + search + 排名稳定
    // ────────────────────────────────────────────────────────────

    private static async Task TestEndToEndSearchAsync()
    {
        var format = StorageFormat.Binary;

        await TestMetricSearch<ManhattanEntity, ManhattanDb>(
            "Manhattan", format, MakeVec, (db, q) => db.Items.Search(e => e.Vec, q, 5));

        await TestMetricSearch<ChebyshevEntity, ChebyshevDb>(
            "Chebyshev", format, MakeVec, (db, q) => db.Items.Search(e => e.Vec, q, 5));

        await TestMetricSearch<PearsonEntity, PearsonDb>(
            "Pearson", format, MakeVec, (db, q) => db.Items.Search(e => e.Vec, q, 5));

        await TestMetricSearch<CanberraEntity, CanberraDb>(
            "Canberra", format, MakeVec, (db, q) => db.Items.Search(e => e.Vec, q, 5));

        // Hamming: 二值向量
        await TestMetricSearch<HammingEntity, HammingDb>(
            "Hamming", format, MakeBinaryVec, (db, q) => db.Items.Search(e => e.Vec, q, 5));

        // Jaccard: 非负向量
        await TestMetricSearch<JaccardEntity, JaccardDb>(
            "Jaccard", format, MakeNonNegVec, (db, q) => db.Items.Search(e => e.Vec, q, 5));
    }

    /// <summary>
    /// 泛型端到端测试：填充 N 条 → 搜索 Top-5 → 保存 → 加载 → 再搜索 → 排名一致。
    /// </summary>
    private static async Task TestMetricSearch<TEntity, TDb>(
        string label,
        StorageFormat format,
        Func<Random, float[]> vecFactory,
        Func<TDb, float[], List<QuiverSearchResult<TEntity>>> searchFunc)
        where TEntity : class, new()
        where TDb : QuiverDbContext
    {
        var path = $"test_metric_{label.ToLower()}.vdb";
        var rng = new Random(42);

        // 通过反射创建 DbContext（所有 metric Db 共享相同的构造函数签名）
        var db = (TDb)Activator.CreateInstance(typeof(TDb), path, format)!;

        // 获取 Items 属性
        var itemsProp = typeof(TDb).GetProperty("Items")!;
        var items = itemsProp.GetValue(db)!;
        var addMethod = items.GetType().GetMethod("Add", [typeof(TEntity)])!;

        // 获取实体的 Id 和 Vec setter
        var idProp = typeof(TEntity).GetProperty("Id")!;
        var vecProp = typeof(TEntity).GetProperty("Vec")!;

        for (int i = 0; i < N; i++)
        {
            var entity = new TEntity();
            idProp.SetValue(entity, $"M{i:D4}");
            vecProp.SetValue(entity, vecFactory(rng));
            addMethod.Invoke(items, [entity]);
        }

        var query = vecFactory(new Random(777));

        // 保存前搜索
        var before = searchFunc(db, query);
        Assert(before.Count == 5, $"[{label}] 搜索返回 5 条结果");
        Assert(before[0].Similarity >= before[4].Similarity, $"[{label}] 结果按相似度降序");

        // 保存 → 加载
        await db.SaveAsync();
        var db2 = (TDb)Activator.CreateInstance(typeof(TDb), path, format)!;
        await db2.LoadAsync();

        var after = searchFunc(db2, query);

        // 排名一致
        var rankOk = before.Count == after.Count;
        for (int i = 0; i < before.Count && rankOk; i++)
        {
            var id1 = (string)idProp.GetValue(before[i].Entity)!;
            var id2 = (string)idProp.GetValue(after[i].Entity)!;
            if (id1 != id2) rankOk = false;
        }
        Assert(rankOk, $"[{label}] 持久化往返后 Top-5 排名一致");

        // 相似度数值一致
        var simOk = true;
        for (int i = 0; i < before.Count && simOk; i++)
        {
            if (MathF.Abs(before[i].Similarity - after[i].Similarity) > Eps)
                simOk = false;
        }
        Assert(simOk, $"[{label}] 持久化往返后相似度数值一致");

        File.Delete(path);
    }

    // ────────────────────────────────────────────────────────────
    // 4. CustomSimilarity 属性路径：通过 typeof(ManhattanSimilarity) 注入
    // ────────────────────────────────────────────────────────────

    private static async Task TestCustomSimilarityAsync()
    {
        var path = "test_custom_sim.vdb";
        var format = StorageFormat.Binary;
        var rng = new Random(42);

        var db = new CustomSimDb(path, format);
        for (int i = 0; i < N; i++)
            db.Items.Add(new CustomSimEntity { Id = $"CS{i:D4}", Vec = MakeVec(rng) });

        var query = MakeVec(new Random(777));
        var results = db.Items.Search(e => e.Vec, query, 5);
        Assert(results.Count == 5, "[CustomSimilarity] 搜索返回 5 条结果");

        // 验证搜索结果与手动调用 ManhattanSimilarity.Compute 一致
        var topEntity = results[0].Entity;
        var expectedSim = ManhattanSimilarity.Compute(query, topEntity.Vec);
        Assert(MathF.Abs(results[0].Similarity - expectedSim) < Eps,
            "[CustomSimilarity] 相似度与 ManhattanSimilarity.Compute 一致");

        // 持久化往返
        await db.SaveAsync();
        var db2 = new CustomSimDb(path, format);
        await db2.LoadAsync();
        var after = db2.Items.Search(e => e.Vec, query, 5);
        Assert(results[0].Entity.Id == after[0].Entity.Id,
            "[CustomSimilarity] 持久化往返后 Top-1 一致");

        File.Delete(path);
    }

    // ────────────────────────────────────────────────────────────
    // 向量生成辅助
    // ────────────────────────────────────────────────────────────

    /// <summary>随机浮点向量 [-1, 1]。</summary>
    private static float[] MakeVec(Random rng) => RandomVector(rng, Dim);

    /// <summary>二值向量 (0 或 1)，适合 Hamming。</summary>
    private static float[] MakeBinaryVec(Random rng)
    {
        var v = new float[Dim];
        for (int i = 0; i < Dim; i++) v[i] = rng.Next(2);
        return v;
    }

    /// <summary>非负向量 [0, 1]，适合 Jaccard。</summary>
    private static float[] MakeNonNegVec(Random rng)
    {
        var v = new float[Dim];
        for (int i = 0; i < Dim; i++) v[i] = rng.NextSingle();
        return v;
    }
}
