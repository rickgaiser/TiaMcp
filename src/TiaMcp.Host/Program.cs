using TiaMcp.Openness;

namespace TiaMcp.Host;

/// <summary>
/// Main() must not reference any Siemens.Engineering.* type (directly or transitively
/// via a type used as a generic argument etc.) - see OpennessAssemblyResolver for why.
/// It only registers the resolver and hands off to Bootstrap.
/// </summary>
internal static class Program
{
    private static void Main(string[] args)
    {
        OpennessAssemblyResolver.Register();
        Bootstrap.Run(args);
    }
}
