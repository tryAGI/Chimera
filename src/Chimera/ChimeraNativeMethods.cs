using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Chimera;

/// <summary>Native C ABI exported by upstream chimera-wasm (layout version 1).</summary>
internal static unsafe partial class ChimeraNativeMethods
{
    private const string LibraryName = "chimera_wasm";

    static ChimeraNativeMethods()
    {
        NativeLibrary.SetDllImportResolver(typeof(ChimeraNativeMethods).Assembly, ResolveLibrary);
    }

    private static IntPtr ResolveLibrary(string name, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (name != LibraryName)
        {
            return IntPtr.Zero;
        }

        // Useful for source builds and native smoke tests; normal NuGet RID assets resolve automatically.
        var explicitPath = Environment.GetEnvironmentVariable("CHIMERA_NATIVE_PATH");
        return string.IsNullOrWhiteSpace(explicitPath)
            ? IntPtr.Zero
            : NativeLibrary.Load(Path.GetFullPath(explicitPath));
    }

    [LibraryImport(LibraryName, EntryPoint = "chimera_layout_version")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial uint LayoutVersion();

    [LibraryImport(LibraryName, EntryPoint = "chimera_gesture_record_size")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial uint GestureRecordSize();

    [LibraryImport(LibraryName, EntryPoint = "chimera_call")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial uint Call(byte* request, nuint length);

    [LibraryImport(LibraryName, EntryPoint = "chimera_result_ptr")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial nint ResultPointer();

    [LibraryImport(LibraryName, EntryPoint = "chimera_result_len")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial nuint ResultLength();

    [LibraryImport(LibraryName, EntryPoint = "engine_new")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial nint EngineNew(double sampleRate);

    [LibraryImport(LibraryName, EntryPoint = "engine_free")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void EngineFree(nint engine);

    [LibraryImport(LibraryName, EntryPoint = "engine_set_body")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void EngineSetBody(nint engine, double female, double child);

    [LibraryImport(LibraryName, EntryPoint = "engine_records")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial nint EngineRecords(nint engine);

    [LibraryImport(LibraryName, EntryPoint = "engine_schedule")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial double EngineSchedule(nint engine, nuint count, double lead, uint replace);

    [LibraryImport(LibraryName, EntryPoint = "engine_render")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial nint EngineRender(nint engine, nuint frames);
}
