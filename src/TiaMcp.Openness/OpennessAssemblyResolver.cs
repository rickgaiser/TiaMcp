using System;
using System.IO;
using System.Reflection;

namespace TiaMcp.Openness;

/// <summary>
/// Resolves Siemens Openness assemblies from the local TIA Portal install at runtime,
/// since they are not in the GAC and are not otherwise discoverable via normal probing.
/// Must be registered (via <see cref="Register"/>) before any code path touches a
/// Siemens.Engineering.* type - the CLR resolves a method's type tokens as a whole when
/// that method is JIT-compiled, not lazily per-instruction, so the registering method
/// itself must not reference any Openness type or the registration comes too late.
/// </summary>
public static class OpennessAssemblyResolver
{
    private static readonly string[] ProbeDirs =
    {
        @"C:\Program Files\Siemens\Automation\Portal V21\PublicAPI\V21\net48",
        @"C:\Program Files\Siemens\Automation\Portal V21\Bin\PublicAPI",
        @"C:\Program Files\Siemens\Automation\Portal V21\Bin",
    };

    private static bool _registered;

    public static void Register()
    {
        if (_registered) return;
        _registered = true;
        AppDomain.CurrentDomain.AssemblyResolve += Resolve;
    }

    private static Assembly? Resolve(object? sender, ResolveEventArgs args)
    {
        var name = new AssemblyName(args.Name).Name;
        foreach (var dir in ProbeDirs)
        {
            var path = Path.Combine(dir, name + ".dll");
            if (File.Exists(path))
            {
                return Assembly.LoadFrom(path);
            }
        }
        return null;
    }
}
