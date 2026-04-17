using Vorcyc.Quiver;
using Vorcyc.Quiver.Similarity;
using static AllBasicTests.TestHelper;

namespace AllBasicTests;

/// <summary>
/// 测试 10：相似度量测试。
/// 覆盖全部 9 种内置度量的数学正确性、端到端搜索排名、CustomSimilarity 属性路径。
/// </summary>
public static class SimilarityTests
{
    private const int Dim = 64;
    private const int N = 200;
    private const float Eps = 1e-5f;

    public static async Task RunAsync()
    {
        Console.WriteLine("\n═══ 10. 相似度量测试 ═══");

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
        float[] a = [1, 2, 3, 4];
        float[] b = [2, 3, 4, 5];
        float[] zero = [0, 0, 0, 0];

        // Cosine: (1*2+2*3+3*4+4*5) / (sqrt(30) * sqrt(54)) = 40 / sqrt(1620)
        var cos = CosineSimilarity.Compute(a, b);
        var expected = 40f / MathF.Sqrt(30f * 54f);
        Assert(MathF.Abs(cos - expected) < Eps, "Cosine 数学正确性");

        // DotProduct: 1*2+2*3+3*4+4*5 = 40
        var dot = DotProductSimilarity.Compute(a, b);
        Assert(MathF.Abs(dot - 40f) < Eps, "DotProduct 数学正确性");

        // Euclidean: 1/(1+sqrt(4)) = 1/3
        var euc = EuclideanSimilarity.Compute(a, b);
        Assert(MathF.Abs(euc - 1f / (1f + 2f)) < Eps, "Euclidean 数学正确性");

        // Manhattan: 1/(1+(1+1+1+1)) = 1/5
        var man = ManhattanSimilarity.Compute(a, b);
        Assert(MathF.Abs(man - 1f / 5f) < Eps, "Manhattan 数学正确性");

        // Chebyshev: 1/(1+max(1,1,1,1)) = 1/2
        var cheb = ChebyshevSimilarity.Compute(a, b);
        Assert(MathF.Abs(cheb - 0.5f) < Eps, "Chebyshev 数学正确性");

        // Pearson: a=[1,2,3,4] b=[2,3,4,5] 等差数列，完美线性相关 -> r=1.0
        var pear = PearsonCorrelationSimilarity.Compute(a, b);
        Assert(MathF.Abs(pear - 1.0f) < Eps, "Pearson 完美正相关 = 1.0");

        float[] rev = [4, 3, 2, 1];
        var pearNeg = PearsonCorrelationSimilarity.Compute(a, rev);
        Assert(MathF.Abs(pearNeg - (-1.0f)) < Eps, "Pearson 完美负相关 = -1.0");

        // Hamming: self = 1.0, all-diff = 0.0
        Assert(MathF.Abs(HammingSimilarity.Compute(a, a) - 1.0f) < Eps, "Hamming 自身 = 1.0");
        Assert(MathF.Abs(HammingSimilarity.Compute(a, b) - 0.0f) < Eps, "Hamming 完全不同 = 0.0");
        float[] c = [1, 2, 99, 4]; // 1 element mismatch
        Assert(MathF.Abs(HammingSimilarity.Compute(a, c) - 0.75f) < Eps, "Hamming 3/4 匹配 = 0.75");

        // Jaccard: sum_min/sum_max = (1+2+3+4)/(2+3+4+5) = 10/14
        var jac = JaccardSimilarity.Compute(a, b);
        Assert(MathF.Abs(jac - 10f / 14f) < Eps, "Jaccard 数学正确性");
        Assert(MathF.Abs(JaccardSimilarity.Compute(zero, zero) - 1.0f) < Eps, "Jaccard 全零 = 1.0");

        // Canberra
        var canExpected = 1f - (1f / 3f + 1f / 5f + 1f / 7f + 1f / 9f) / 4f;
        Assert(MathF.Abs(CanberraSimilarity.Compute(a, b) - canExpected) < Eps, "Canberra 数学正确性");
        Assert(MathF.Abs(CanberraSimilarity.Compute(zero, zero) - 1.0f) < Eps, "Canberra 全零 = 1.0");
    }

    // ────────────────────────────────────────────────────────────
    // 2. 自身相似度应为最大值
    // ────────────────────────────────────────────────────────────

    private static void TestSelfSimilarityIsMax()
    {
        var rng = new Random(123);
        var v = RandomVector(rng, Dim);

        Assert(MathF.Abs(EuclideanSimilarity.Compute(v, v) - 1f) < Eps, "Euclidean 自身 = 1.0");
        Assert(MathF.Abs(ManhattanSimilarity.Compute(v, v) - 1f) < Eps, "Manhattan 自身 = 1.0");
        Assert(MathF.Abs(ChebyshevSimilarity.Compute(v, v) - 1f) < Eps, "Chebyshev 自身 = 1.0");
        Assert(MathF.Abs(HammingSimilarity.Compute(v, v) - 1f) < Eps, "Hamming 自身 = 1.0");
        Assert(MathF.Abs(CanberraSimilarity.Compute(v, v) - 1f) < Eps, "Canberra 自身 = 1.0");
        Assert(MathF.Abs(CosineSimilarity.Compute(v, v) - 1f) < Eps, "Cosine 自身 = 1.0");
        Assert(MathF.Abs(PearsonCorrelationSimilarity.Compute(v, v) - 1f) < Eps, "Pearson 自身 = 1.0");

        var vPos = new float[Dim];
        for (int i = 0; i < Dim; i++) vPos[i] = MathF.Abs(v[i]);
        Assert(MathF.Abs(JaccardSimilarity.Compute(vPos, vPos) - 1f) < Eps, "Jaccard 自身 = 1.0");
    }

    // ────────────────────────────────────────────────────────────
    // 3. 端到端搜索：每种内置度量都能正确完成 add + search + 排名稳定
    // ────────────────────────────────────────────────────────────

    private static async Task TestEndToEndSearchAsync()
    {
        await TestMetricSearch<ManhattanEntity, ManhattanDb>(
            "Manhattan", MakeVec, (db, q) => db.Items.Search(e => e.Vec, q, 5));

        await TestMetricSearch<ChebyshevEntity, ChebyshevDb>(
            "Chebyshev", MakeVec, (db, q) => db.Items.Search(e => e.Vec, q, 5));

        await TestMetricSearch<PearsonEntity, PearsonDb>(
            "Pearson", MakeVec, (db, q) => db.Items.Search(e => e.Vec, q, 5));

        await TestMetricSearch<CanberraEntity, CanberraDb>(
            "Canberra", MakeVec, (db, q) => db.Items.Search(e => e.Vec, q, 5));

        await TestMetricSearch<HammingEntity, HammingDb>(
            "Hamming", MakeBinaryVec, (db, q) => db.Items.Search(e => e.Vec, q, 5));

        await TestMetricSearch<JaccardEntity, JaccardDb>(
            "Jaccard", MakeNonNegVec, (db, q) => db.Items.Search(e => e.Vec, q, 5));
    }

    /// <summary>
    /// 泛型端到端测试：填充 N 条 → 搜索 Top-5 → 保存 → 加载 → 再搜索 → 排名一致。
    /// </summary>
    private static async Task TestMetricSearch<TEntity, TDb>(
        string label,
        Func<Random, float[]> vecFactory,
        Func<TDb, float[], List<QuiverSearchResult<TEntity>>> searchFunc)
        where TEntity : class, new()
        where TDb : QuiverDbContext
    {
        var path = $"test_metric_{label.ToLower()}.vdb";
        var rng = new Random(42);

        var db = (TDb)Activator.CreateInstance(typeof(TDb), path)!;

        var itemsProp = typeof(TDb).GetProperty("Items")!;
        var items = itemsProp.GetValue(db)!;
        var addMethod = items.GetType().GetMethod("Add", [typeof(TEntity)])!;

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

        var before = searchFunc(db, query);
        Assert(before.Count == 5, $"[{label}] 搜索返回 5 条结果");
        Assert(before[0].Similarity >= before[4].Similarity, $"[{label}] 结果按相似度降序");

        await db.SaveAsync();
        var db2 = (TDb)Activator.CreateInstance(typeof(TDb), path)!;
        await db2.LoadAsync();

        var after = searchFunc(db2, query);

        var rankOk = before.Count == after.Count;
        for (int i = 0; i < before.Count && rankOk; i++)
        {
            var id1 = (string)idProp.GetValue(before[i].Entity)!;
            var id2 = (string)idProp.GetValue(after[i].Entity)!;
            if (id1 != id2) rankOk = false;
        }
        Assert(rankOk, $"[{label}] 持久化往返后 Top-5 排名一致");

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
        var rng = new Random(42);

        var db = new CustomSimDb(path);
        for (int i = 0; i < N; i++)
            db.Items.Add(new CustomSimEntity { Id = $"CS{i:D4}", Vec = MakeVec(rng) });

        var query = MakeVec(new Random(777));
        var results = db.Items.Search(e => e.Vec, query, 5);
        Assert(results.Count == 5, "[CustomSimilarity] 搜索返回 5 条结果");

        var topEntity = results[0].Entity;
        var expectedSim = ManhattanSimilarity.Compute(query, topEntity.Vec);
        Assert(MathF.Abs(results[0].Similarity - expectedSim) < Eps,
            "[CustomSimilarity] 相似度与 ManhattanSimilarity.Compute 一致");

        await db.SaveAsync();
        var db2 = new CustomSimDb(path);
        await db2.LoadAsync();
        var after = db2.Items.Search(e => e.Vec, query, 5);
        Assert(results[0].Entity.Id == after[0].Entity.Id,
            "[CustomSimilarity] 持久化往返后 Top-1 一致");

        File.Delete(path);
    }

    // ────────────────────────────────────────────────────────────
    // 向量生成辅助
    // ────────────────────────────────────────────────────────────

    private static float[] MakeVec(Random rng) => RandomVector(rng, Dim);

    private static float[] MakeBinaryVec(Random rng)
    {
        var v = new float[Dim];
        for (int i = 0; i < Dim; i++) v[i] = rng.Next(2);
        return v;
    }

    private static float[] MakeNonNegVec(Random rng)
    {
        var v = new float[Dim];
        for (int i = 0; i < Dim; i++) v[i] = rng.NextSingle();
        return v;
    }
}
