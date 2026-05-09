using System.Reflection;
using Vorcyc.Quiver.Storage;

namespace Vorcyc.Quiver.Payloads;

/// <summary>
/// 统一描述向量负载和大字段负载的内部元数据。
/// </summary>
/// <param name="TypeName">持久化使用的实体类型名。</param>
/// <param name="FieldName">负载字段名。</param>
/// <param name="Kind">负载类型。</param>
/// <param name="MemoryMode">该负载字段解析后的内存模式。</param>
/// <param name="ClrType">属性对应的 CLR 类型。</param>
/// <param name="Property">反射属性信息。</param>
/// <param name="Nullable">该负载是否允许为 null。</param>
/// <param name="DeclaredDimensions">向量声明维度；非向量负载为 0。</param>
/// <param name="StorageDimensions">向量磁盘存储维度；非向量负载为 0。</param>
/// <param name="EffectiveDimensions">运行时索引/搜索使用的有效维度；非向量负载为 0。</param>
/// <param name="VectorEncoding">向量磁盘编码；非向量负载保留默认值。</param>
/// <param name="VectorNormFlags">向量写入时的归一化标志；非向量负载保留默认值。</param>
internal readonly record struct PayloadDescriptor(
    string TypeName,
    string FieldName,
    PayloadKind Kind,
    PayloadMemoryMode MemoryMode,
    Type ClrType,
    PropertyInfo Property,
    bool Nullable,
    int DeclaredDimensions = 0,
    int StorageDimensions = 0,
    int EffectiveDimensions = 0,
    VectorBlobEncoding VectorEncoding = VectorBlobEncoding.Float32,
    VectorBlobNormFlags VectorNormFlags = VectorBlobNormFlags.None)
{
    /// <summary>该描述是否表示向量负载。</summary>
    public bool IsVector => Kind == PayloadKind.Vector;

    /// <summary>该描述是否表示大字段负载。</summary>
    public bool IsLargeField => Kind == PayloadKind.LargeField;
}
