// Polyfill: enables C# 9 record / init-only properties on netstandard2.0.
namespace System.Runtime.CompilerServices
{
    internal static class IsExternalInit { }
}
