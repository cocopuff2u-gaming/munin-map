using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace System.Runtime.CompilerServices
{
    // C# 9 module initializers on .NET Framework: the compiler only needs the attribute to exist
    [AttributeUsage(AttributeTargets.Method, Inherited = false)]
    internal sealed class ModuleInitializerAttribute : Attribute { }
}

namespace MuninMap.Util
{
    // The libraries Munin Map needs (websocket-sharp, ImageSharp and the System DLLs it uses) ride
    // inside MuninMap.dll as resources (deps/<name>.dll, see MuninMap.csproj), so the mod is one file.
    // When the runtime asks for one, it's loaded from there under its own name, so the game's own
    // libraries (System.Memory...) find it as if it were a file next to the DLL. One already loaded
    // (another mod brought it, or an old copy sits in the folder) is used as it is.
    internal static class Deps
    {
        private static readonly Dictionary<string, Assembly> loaded = new Dictionary<string, Assembly>(StringComparer.OrdinalIgnoreCase);
        private static bool hooked;

        // before any type that uses them is touched: at module load, and again from Awake in case
        // the runtime skipped the module initializer
        [System.Runtime.CompilerServices.ModuleInitializer]
        internal static void Init()
        {
            lock (loaded)
            {
                if (hooked) return;
                hooked = true;
            }
            AppDomain.CurrentDomain.AssemblyResolve += Resolve;
        }

        private static Assembly Resolve(object sender, ResolveEventArgs args)
        {
            string name;
            try { name = new AssemblyName(args.Name).Name; } catch { return null; }
            lock (loaded)
            {
                if (loaded.TryGetValue(name, out var have)) return have;
                foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
                    if (string.Equals(a.GetName().Name, name, StringComparison.OrdinalIgnoreCase)) return loaded[name] = a;
                using (var st = typeof(Deps).Assembly.GetManifestResourceStream("deps/" + name + ".dll"))
                {
                    if (st == null) return null;
                    using (var ms = new MemoryStream())
                    {
                        st.CopyTo(ms);
                        var asm = Assembly.Load(ms.ToArray());
                        loaded[name] = asm;
                        try { ZLog.Log("MuninMap: " + name + " " + asm.GetName().Version + " loaded from MuninMap.dll"); } catch { }
                        return asm;
                    }
                }
            }
        }
    }
}
