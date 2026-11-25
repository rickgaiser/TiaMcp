namespace System.Runtime.CompilerServices;

/// <summary>
/// Polyfill required for C# 9 `record`/init-only properties on net48, which doesn't ship this type.
/// </summary>
internal static class IsExternalInit
{
}
