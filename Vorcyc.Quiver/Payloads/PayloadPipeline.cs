using System.Reflection;
using Vorcyc.Quiver.Storage;

namespace Vorcyc.Quiver.Payloads;

/// <summary>
/// 统一负载管线入口，负责把属性特性解析成 <see cref="PayloadDescriptor"/> 并执行通用读写校验。
/// </summary>
internal static class PayloadPipeline
{
    /// <summary>
    /// 从 <see cref="QuiverVectorAttribute"/> 属性创建向量负载描述。
    /// </summary>
    /// <param name="typeName">持久化使用的实体类型名。</param>
    /// <param name="clrType">实体 CLR 类型。</param>
    /// <param name="property">向量属性。</param>
    /// <param name="memoryMode">解析后的向量内存模式。</param>
    public static PayloadDescriptor CreateVectorDescriptor(string typeName, Type clrType, PropertyInfo property, PayloadMemoryMode memoryMode = PayloadMemoryMode.InMemory)
    {
        var attr = property.GetCustomAttribute<QuiverVectorAttribute>()
            ?? throw new InvalidOperationException($"Property '{property.Name}' is not marked with [QuiverVector].");

        bool isHalf = property.PropertyType == typeof(Half[]);
        if (property.PropertyType != typeof(float[]) && !isHalf)
            throw new InvalidOperationException($"[QuiverVector] property '{property.Name}' on {clrType.Name} must be of type float[] or Half[].");

        var declaredDim = attr.Dimensions;
        var storageDim = attr.EffectiveDimensions > 0 && attr.EffectiveDimensions < declaredDim
            ? attr.EffectiveDimensions
            : declaredDim;
        // Half[] 字段以 fp16 物理落盘（Float16 编码）；其余按是否启用 SQ8 标量量化决定。
        var encoding = isHalf
            ? VectorBlobEncoding.Float16
            : attr.Quantization == VectorQuantization.Sq8
                ? VectorBlobEncoding.Sq8
                : VectorBlobEncoding.Float32;
        var normFlags = attr.CustomSimilarity is null && attr.Metric == DistanceMetric.Cosine
            ? VectorBlobNormFlags.L2Normalized
            : VectorBlobNormFlags.None;

        return new PayloadDescriptor(
            typeName,
            property.Name,
            PayloadKind.Vector,
            memoryMode,
            property.PropertyType,
            property,
            attr.Nullable,
            declaredDim,
            storageDim,
            storageDim,
            encoding,
            normFlags);
    }

    /// <summary>
    /// 从 <see cref="QuiverLargeFieldAttribute"/> 属性创建大字段负载描述。
    /// </summary>
    /// <param name="typeName">持久化使用的实体类型名。</param>
    /// <param name="clrType">实体 CLR 类型。</param>
    /// <param name="property">大字段属性。</param>
    /// <param name="memoryMode">解析后的大字段内存模式。</param>
    public static PayloadDescriptor CreateLargeFieldDescriptor(string typeName, Type clrType, PropertyInfo property, PayloadMemoryMode memoryMode = PayloadMemoryMode.InMemory)
    {
        var attr = property.GetCustomAttribute<QuiverLargeFieldAttribute>()
            ?? throw new InvalidOperationException($"Property '{property.Name}' is not marked with [QuiverLargeField].");

        if (property.PropertyType != typeof(byte[]))
            throw new InvalidOperationException($"[QuiverLargeField] property '{property.Name}' on {clrType.Name} must be of type byte[].");

        return new PayloadDescriptor(
            typeName,
            property.Name,
            PayloadKind.LargeField,
            memoryMode,
            typeof(byte[]),
            property,
            attr.Nullable);
    }

    /// <summary>
    /// 为读取路径创建负载描述；当当前 CLR 类型已不存在对应属性时，生成宽松描述以便跳过未知字段。
    /// </summary>
    /// <param name="typeName">持久化使用的实体类型名。</param>
    /// <param name="clrType">实体 CLR 类型。</param>
    /// <param name="fieldName">负载字段名。</param>
    /// <param name="kind">负载类型。</param>
    /// <param name="memoryMode">解析后的内存模式。</param>
    public static PayloadDescriptor CreateReadDescriptor(string typeName, Type clrType, string fieldName, PayloadKind kind, PayloadMemoryMode memoryMode = PayloadMemoryMode.InMemory)
    {
        var property = clrType.GetProperty(fieldName);
        if (property is null)
        {
            return new PayloadDescriptor(typeName, fieldName, kind, memoryMode, kind == PayloadKind.Vector ? typeof(float[]) : typeof(byte[]), null!, true);
        }

        return kind switch
        {
            PayloadKind.Vector => CreateVectorDescriptor(typeName, clrType, property, memoryMode),
            PayloadKind.LargeField => CreateLargeFieldDescriptor(typeName, clrType, property, memoryMode),
            _ => throw new InvalidOperationException($"Unsupported payload kind '{kind}'."),
        };
    }

    /// <summary>
    /// 校验写入值是否满足负载类型和可空约束。
    /// </summary>
    /// <param name="descriptor">负载描述。</param>
    /// <param name="value">待写入的属性值。</param>
    /// <param name="row">当前行号，用于错误信息。</param>
    public static void ValidateWriteValue(in PayloadDescriptor descriptor, object? value, int row)
    {
        if (value is null)
        {
            if (!descriptor.Nullable)
            {
                var attributeName = descriptor.Kind == PayloadKind.Vector ? "QuiverVector" : "QuiverLargeField";
                throw new InvalidOperationException(
                    $"{PayloadLabel(descriptor)} '{descriptor.TypeName}.{descriptor.FieldName}' is required but was null at row {row}. " +
                    $"Mark [{attributeName}(Nullable = true)] to allow null.");
            }
            return;
        }

        if (!descriptor.ClrType.IsInstanceOfType(value))
            throw new InvalidOperationException(
                $"{PayloadLabel(descriptor)} '{descriptor.TypeName}.{descriptor.FieldName}' expected value of type {descriptor.ClrType.Name} at row {row}.");
    }

    /// <summary>
    /// 校验读取到的 null 是否满足字段可空约束。
    /// </summary>
    /// <param name="descriptor">负载描述。</param>
    /// <param name="row">当前行号，用于错误信息。</param>
    public static void ValidateReadNull(in PayloadDescriptor descriptor, int row)
    {
        if (!descriptor.Nullable)
        {
            throw new InvalidOperationException(
                $"{PayloadLabel(descriptor)} '{descriptor.TypeName}.{descriptor.FieldName}' is required but was null at row {row} during load.");
        }
    }

    private static string PayloadLabel(in PayloadDescriptor descriptor)
        => descriptor.Kind == PayloadKind.Vector ? "Vector payload" : "Large field";
}
