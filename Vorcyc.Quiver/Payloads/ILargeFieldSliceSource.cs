namespace Vorcyc.Quiver.Payloads;

/// <summary>
/// 保存路径用于查询旧文件大字段切片的内部抽象。
/// <para>
/// 当 lazy 大字段尚未被物化且属性 backing field 仍为 <c>null</c> 时，存储层可直接复用原文件切片，
/// 避免把大字段读入托管内存再写回。
/// </para>
/// </summary>
internal interface ILargeFieldSliceSource
{
    /// <summary>
    /// 按写入快照中的行号和字段名查找可复用的大字段切片。
    /// </summary>
    /// <param name="rowIndex">当前保存快照中的行号。</param>
    /// <param name="fieldName">大字段字段名。</param>
    /// <param name="slice">找到时返回旧文件中的切片位置。</param>
    bool TryGetLargeFieldSlice(int rowIndex, string fieldName, out LargeFieldSlice slice);
}
