namespace Vorcyc.Quiver.Payloads;

/// <summary>
/// 保存开始前捕获的大字段切片快照，按字段名和当前行号提供可复用切片查询。
/// </summary>
internal sealed class LargeFieldSliceSnapshot : ILargeFieldSliceSource
{
    private readonly Dictionary<string, LargeFieldSlice?[]> _slicesByField = new(StringComparer.Ordinal);

    /// <summary>
    /// 记录一个字段在指定行号上的原文件切片。
    /// </summary>
    /// <param name="fieldName">大字段字段名。</param>
    /// <param name="rowIndex">当前保存快照中的行号。</param>
    /// <param name="slice">原文件切片。</param>
    public void Set(string fieldName, int rowIndex, LargeFieldSlice slice)
    {
        if (!_slicesByField.TryGetValue(fieldName, out var slices))
        {
            slices = new LargeFieldSlice?[rowIndex + 1];
            _slicesByField[fieldName] = slices;
        }
        else if (rowIndex >= slices.Length)
        {
            Array.Resize(ref slices, rowIndex + 1);
            _slicesByField[fieldName] = slices;
        }

        slices[rowIndex] = slice;
    }

    /// <summary>
    /// 按行号和字段名查找保存时可直接复用的原文件切片。
    /// </summary>
    /// <param name="rowIndex">当前保存快照中的行号。</param>
    /// <param name="fieldName">大字段字段名。</param>
    /// <param name="slice">找到时返回切片元数据。</param>
    public bool TryGetLargeFieldSlice(int rowIndex, string fieldName, out LargeFieldSlice slice)
    {
        if (_slicesByField.TryGetValue(fieldName, out var slices)
            && (uint)rowIndex < (uint)slices.Length
            && slices[rowIndex] is { } value)
        {
            slice = value;
            return true;
        }

        slice = default;
        return false;
    }
}
