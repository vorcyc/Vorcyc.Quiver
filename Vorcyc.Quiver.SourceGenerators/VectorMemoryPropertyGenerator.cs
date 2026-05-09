using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Vorcyc.Quiver.SourceGenerators;

/// <summary>
/// Generates <c>partial</c> implementations for <c>[QuiverVector]</c> properties declared as
/// <c>public partial float[]? Name { get; set; }</c>:
/// <list type="bullet">
///   <item>Introduces a private backing field <c>__{name}_backing</c>.</item>
///   <item>Getter: returns the backing field; if <c>null</c>, calls <c>LazyVectorAccessor.Materialize(this, "Name")</c>.</item>
///   <item>Setter: writes back to the backing field.</item>
/// </list>
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class VectorMemoryPropertyGenerator : IIncrementalGenerator
{
    private const string AttributeFullName = "Vorcyc.Quiver.QuiverVectorAttribute";
    private const string LargeFieldAttributeFullName = "Vorcyc.Quiver.QuiverLargeFieldAttribute";

    internal static readonly DiagnosticDescriptor QVR001_NotPartialProperty = new(
        id: "QVR001",
        title: "Memory-mode property must be partial",
        messageFormat: "Property '{0}' uses a non-InMemory payload memory mode but is not declared 'partial'; source generation is skipped",
        category: "Vorcyc.Quiver.SourceGenerators",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    internal static readonly DiagnosticDescriptor QVR004_InvalidLargeFieldPropertyType = new(
        id: "QVR004",
        title: "Large-field memory-mode property type must be byte[] or byte[]?",
        messageFormat: "Property '{0}' has type '{1}' but non-InMemory large-field memory mode requires 'byte[]' or 'byte[]?'",
        category: "Vorcyc.Quiver.SourceGenerators",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    internal static readonly DiagnosticDescriptor QVR002_NotPartialType = new(
        id: "QVR002",
        title: "Containing type of a memory-mode property must be partial",
        messageFormat: "Type '{0}' contains a non-InMemory payload memory property '{1}' but is not declared 'partial'; source generation is skipped",
        category: "Vorcyc.Quiver.SourceGenerators",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    internal static readonly DiagnosticDescriptor QVR003_InvalidPropertyType = new(
        id: "QVR003",
        title: "Vector memory-mode property type must be float[] or float[]?",
        messageFormat: "Property '{0}' has type '{1}' but non-InMemory vector memory mode requires 'float[]' or 'float[]?'",
        category: "Vorcyc.Quiver.SourceGenerators",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var candidates = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                AttributeFullName,
                predicate: static (node, _) => node is PropertyDeclarationSyntax,
                transform: static (ctx, _) => Transform(ctx))
            .Where(static r => r is not null)!
            .Collect();

        var largeFieldCandidates = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                LargeFieldAttributeFullName,
                predicate: static (node, _) => node is PropertyDeclarationSyntax,
                transform: static (ctx, _) => Transform(ctx))
            .Where(static r => r is not null)!
            .Collect();

        context.RegisterSourceOutput(candidates, static (spc, items) =>
            EmitProperties(spc, items, "VectorMemoryProperties", "float[]?", "LazyVectorAccessor"));

        context.RegisterSourceOutput(largeFieldCandidates, static (spc, items) =>
            EmitProperties(spc, items, "LargeFieldMemoryProperties", "byte[]?", "LazyLargeFieldAccessor"));
    }

    private static void EmitProperties(SourceProductionContext spc, ImmutableArray<TransformResult?> items, string hintSuffix, string backingType, string accessorType)
    {
        {
            if (items.IsDefaultOrEmpty) return;

            // 先派发所有诊断
            foreach (var r in items)
            {
                if (r!.Diag is { } diag)
                    spc.ReportDiagnostic(diag);
            }

            // 仅对有效结果按 (namespace + containing type chain) 分组生成
            var valid = items.Where(r => r!.Info is not null).ToList();
            if (valid.Count == 0) return;

            foreach (var group in valid.GroupBy(r => r!.Info!.TypeKey))
            {
                var sample = group.First()!.Info!;
                var source = Emit(sample, group.Select(r => r!.Info!).ToList(), backingType, accessorType);
                var hint = $"{group.Key}.{hintSuffix}.g.cs"
                    .Replace('<', '_').Replace('>', '_').Replace(',', '_').Replace(' ', '_');
                spc.AddSource(hint, source);
            }
        }
    }

    private static TransformResult? Transform(GeneratorAttributeSyntaxContext ctx)
    {
        if (ctx.TargetSymbol is not IPropertySymbol prop) return null;
        if (prop.ContainingType is not INamedTypeSymbol containingType) return null;

        // 生成器不能读取运行时 QuiverDbOptions，因此对所有 partial payload 属性
        // 生成 lazy-capable 访问器；显式非 InMemory 的字段仍在编译期强制 partial。
        var attr = prop.GetAttributes().FirstOrDefault(a =>
            a.AttributeClass?.ToDisplayString() is AttributeFullName or LargeFieldAttributeFullName);
        if (attr is null) return null;
        var isLargeField = attr.AttributeClass?.ToDisplayString() == LargeFieldAttributeFullName;

        var memoryMode = attr.NamedArguments
            .FirstOrDefault(kv => kv.Key == "MemoryMode").Value;
        var explicitMode = memoryMode.Value is int mode ? mode : 0;
        var explicitNonInMemory = explicitMode != 0;

        var syntax = (PropertyDeclarationSyntax)ctx.TargetNode;
        var propLocation = syntax.Identifier.GetLocation();

        // QVR001：属性必须 partial
        if (!syntax.Modifiers.Any(m => m.ValueText == "partial"))
        {
            if (!explicitNonInMemory) return null;
            return new TransformResult(
                Info: null,
                Diag: Diagnostic.Create(QVR001_NotPartialProperty, propLocation, prop.Name));
        }

        // QVR002：包含类型链必须全部 partial（嵌套类型生成时会重新打开整条链）
        var nonPartialType = GetFirstNonPartialType(containingType);
        if (nonPartialType is not null)
        {
            return new TransformResult(
                Info: null,
                Diag: Diagnostic.Create(QVR002_NotPartialType, propLocation,
                    nonPartialType.ToDisplayString(), prop.Name));
        }

        // QVR003/QVR004：属性类型必须匹配载荷类型
        var propType = prop.Type.ToDisplayString();
        if (!isLargeField && propType != "float[]" && propType != "float[]?")
        {
            return new TransformResult(
                Info: null,
                Diag: Diagnostic.Create(QVR003_InvalidPropertyType, propLocation,
                    prop.Name, propType));
        }
        if (isLargeField && propType != "byte[]" && propType != "byte[]?")
        {
            return new TransformResult(
                Info: null,
                Diag: Diagnostic.Create(QVR004_InvalidLargeFieldPropertyType, propLocation,
                    prop.Name, propType));
        }

        // 计算 namespace 和嵌套类型链
        var ns = containingType.ContainingNamespace.IsGlobalNamespace
            ? null
            : containingType.ContainingNamespace.ToDisplayString();

        var typeChain = new List<INamedTypeSymbol>();
        for (var t = containingType; t is not null; t = t.ContainingType)
            typeChain.Add(t);
        typeChain.Reverse();

        var typeKey = (ns is null ? "" : ns + ".") +
                      string.Join(".", typeChain.Select(t => t.Name +
                          (t.TypeParameters.Length > 0 ? "`" + t.TypeParameters.Length : "")));

        return new TransformResult(
            Info: new PropertyInfo(
                Namespace: ns,
                TypeChain: typeChain.Select(t => new TypeRef(
                    Name: t.Name,
                    Keyword: t.TypeKind == TypeKind.Struct ? "struct" : "class",
                    TypeParameters: t.TypeParameters.Select(p => p.Name).ToArray())).ToArray(),
                PropertyName: prop.Name,
                PropertyType: propType,
                TypeKey: typeKey),
            Diag: null);
    }

    private static INamedTypeSymbol? GetFirstNonPartialType(INamedTypeSymbol containingType)
    {
        var typeChain = new List<INamedTypeSymbol>();
        for (var t = containingType; t is not null; t = t.ContainingType)
            typeChain.Add(t);
        typeChain.Reverse();

        foreach (var type in typeChain)
        {
            var isPartial = type.DeclaringSyntaxReferences
                .Select(r => r.GetSyntax())
                .OfType<TypeDeclarationSyntax>()
                .Any(t => t.Modifiers.Any(m => m.ValueText == "partial"));
            if (!isPartial) return type;
        }

        return null;
    }

    private static string Emit(PropertyInfo sample, List<PropertyInfo> props, string backingType, string accessorType)
    {
        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated/>");
        sb.AppendLine("#nullable enable");
        sb.AppendLine();
        if (sample.Namespace is not null)
        {
            sb.Append("namespace ").Append(sample.Namespace).AppendLine(";");
            sb.AppendLine();
        }

        // 打开嵌套类型
        int indent = 0;
        foreach (var t in sample.TypeChain)
        {
            Indent(sb, indent).Append("partial ").Append(t.Keyword).Append(' ').Append(t.Name);
            if (t.TypeParameters.Length > 0)
                sb.Append('<').Append(string.Join(", ", t.TypeParameters)).Append('>');
            sb.AppendLine();
            Indent(sb, indent).AppendLine("{");
            indent++;
        }

        foreach (var p in props)
        {
            var backing = "__" + p.PropertyName + "_backing";
            Indent(sb, indent).Append("private ").Append(backingType).Append(' ').Append(backing).AppendLine(";");
            sb.AppendLine();
            Indent(sb, indent).Append("public partial ").Append(p.PropertyType).Append(' ').Append(p.PropertyName).AppendLine();
            Indent(sb, indent).AppendLine("{");
            Indent(sb, indent + 1).Append("get => ").Append(backing)
                .Append(" ?? global::Vorcyc.Quiver.Runtime.").Append(accessorType).Append(".Materialize(this, \"")
                .Append(p.PropertyName).AppendLine("\");");
            Indent(sb, indent + 1).Append("set => ").Append(backing).AppendLine(" = value;");
            Indent(sb, indent).AppendLine("}");
            sb.AppendLine();
        }

        // 关闭嵌套类型
        for (int i = sample.TypeChain.Length - 1; i >= 0; i--)
        {
            indent--;
            Indent(sb, indent).AppendLine("}");
        }

        return sb.ToString();
    }

    private static StringBuilder Indent(StringBuilder sb, int level)
    {
        for (int i = 0; i < level; i++) sb.Append("    ");
        return sb;
    }

    internal sealed record TypeRef(string Name, string Keyword, string[] TypeParameters);

    internal sealed record PropertyInfo(
        string? Namespace,
        TypeRef[] TypeChain,
        string PropertyName,
        string PropertyType,
        string TypeKey);

    internal sealed record TransformResult(PropertyInfo? Info, Diagnostic? Diag);
}
