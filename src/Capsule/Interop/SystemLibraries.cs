using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Capsule.Interop;

/// <summary>
/// Every Windows library Capsule calls is loaded by its full System32 path. The search-path attribute alone may let the
/// runtime look in the app's own folder first (for example Downloads, where the setup file runs), so this resolver
/// removes any search: a same-named DLL placed next to Capsule.exe is never loaded.
/// </summary>
internal static class SystemLibraries
{
    private static readonly HashSet<string> Allowed = new(StringComparer.OrdinalIgnoreCase)
    {
        "advapi32.dll", "gdi32.dll", "kernel32.dll", "ntdll.dll", "ole32.dll", "pdh.dll", "psapi.dll", "shcore.dll", "user32.dll",
    };

#pragma warning disable CA2255 // a module initializer is exactly right here: it must run before the first P/Invoke
    [ModuleInitializer]
#pragma warning restore CA2255
    internal static void Register() => NativeLibrary.SetDllImportResolver(typeof(SystemLibraries).Assembly, Resolve);

    internal static IntPtr Resolve(string name, Assembly assembly, DllImportSearchPath? searchPath)
    {
        var file = name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? name : name + ".dll";
        if (!Allowed.Contains(file) || file.IndexOfAny(['\\', '/', ':']) >= 0)
        {
            throw new DllNotFoundException($"{name} is not a Windows system library Capsule uses");
        }

        return NativeLibrary.Load(Path.Combine(Environment.SystemDirectory, file));
    }
}
