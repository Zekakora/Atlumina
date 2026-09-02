using System.Runtime.InteropServices;
using System.Text;
using Microsoft.ML.OnnxRuntime;

namespace MyAlbum.Core.Services;

/// <summary>Which compute device an ONNX session should run on.</summary>
public enum AiDeviceKind
{
    None = 0,
    Cpu = 1,
    Gpu = 2,
    Npu = 3,
}

/// <summary>Result of probing the machine for usable AI compute devices.</summary>
public sealed record AiDeviceProbe(
    bool NpuAvailable,
    bool GpuAvailable,
    AiDeviceKind Best,
    string BestName);

/// <summary>One compute adapter discovered during the probe.</summary>
public sealed record AiAdapterInfo(string Name, string Description, bool IsSoftware, bool IsNpu);

/// <summary>
/// Local AI inference engine built on ONNX Runtime with the DirectML execution provider.
/// Probes the machine for an NPU first via DXCore's NPU hardware-type attribute (Intel AI
/// Boost, Qualcomm Hexagon, AMD XDNA all register under DXCORE_HARDWARE_TYPE_ATTRIBUTE_NPU),
/// then falls back to GPU (DXCore GPU attribute) and finally CPU, so a missing NPU never
/// blocks the app. No model files are bundled; sessions are only created once the user
/// drops an .onnx file into the models directory.
/// </summary>
public static class AiEngine
{
    /// <summary>Subdirectory under the app data folder where ONNX models are expected.</summary>
    public static string ModelsDirectory => Path.Combine(MyAlbum.Core.Infrastructure.AppPaths.AppDataDirectory, "models");

    /// <summary>Returns true when the DirectML native library is loadable on this machine.</summary>
    public static bool IsDirectMlAvailable
    {
        get
        {
            try
            {
                var opts = new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_DISABLE_ALL };
                opts.AppendExecutionProvider_DML(0);
                opts.Dispose();
                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>
    /// Probes the machine via DXCore: NPUs are exposed as adapters filtered by the
    /// <c>DXCORE_HARDWARE_TYPE_ATTRIBUTE_NPU</c> attribute (they do NOT appear in the DXGI
    /// adapter list), GPUs by the GPU attribute. Returns the best available device.
    /// </summary>
    public static AiDeviceProbe Probe()
    {
        bool dml = IsDirectMlAvailable;
        var npus = EnumerateByType(NpuAttribute);
        var gpus = EnumerateByType(GpuAttribute);

        bool npu = npus.Count > 0 && dml;
        bool gpu = gpus.Count > 0 && dml;

        if (npu)
        {
            return new AiDeviceProbe(true, gpu, AiDeviceKind.Npu, "NPU (DirectML)");
        }
        if (gpu)
        {
            return new AiDeviceProbe(false, true, AiDeviceKind.Gpu, "GPU (DirectML)");
        }
        return new AiDeviceProbe(false, false, AiDeviceKind.Cpu, "CPU");
    }

    /// <summary>
    /// Builds session options targeting the best available provider. If no model files are
    /// present yet this still validates that the provider is usable.
    /// </summary>
    public static SessionOptions CreateSessionOptions()
    {
        var options = new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL };
        var probe = Probe();
        if (probe.Best is AiDeviceKind.Npu or AiDeviceKind.Gpu)
        {
            try
            {
                options.AppendExecutionProvider_DML(0);
            }
            catch
            {
                options.AppendExecutionProvider_CPU();
            }
        }
        else
        {
            options.AppendExecutionProvider_CPU();
        }
        return options;
    }

    /// <summary>Lists the .onnx model files currently present in the models directory.</summary>
    public static string[] DiscoverModels()
    {
        try
        {
            if (!Directory.Exists(ModelsDirectory))
            {
                return [];
            }
            return Directory.GetFiles(ModelsDirectory, "*.onnx", SearchOption.TopDirectoryOnly)
                .Select(Path.GetFileNameWithoutExtension)
                .Where(n => n is not null)
                .Select(n => n!)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch
        {
            return [];
        }
    }

    /// <summary>Enumerates every DXCore adapter (NPU + GPU), tagged by hardware type.</summary>
    public static List<AiAdapterInfo> EnumerateAdapters()
    {
        var result = new List<AiAdapterInfo>();
        foreach (var adapter in EnumerateByType(NpuAttribute))
        {
            result.Add(adapter);
        }
        foreach (var adapter in EnumerateByType(GpuAttribute))
        {
            result.Add(adapter);
        }
        return result;
    }

    /// <summary>
    /// Enumerates DXCore adapters carrying <paramref name="filterAttribute"/>. vtable
    /// function pointers are used instead of RCW casting (which is unreliable here).
    /// </summary>
    private static List<AiAdapterInfo> EnumerateByType(Guid filterAttribute)
    {
        var result = new List<AiAdapterInfo>();
        try
        {
            var factoryIid = IID_IDXCoreAdapterFactory;
            if (DXCoreCreateAdapterFactory(in factoryIid, out var factory) != 0 || factory == IntPtr.Zero)
            {
                return result;
            }
            try
            {
                var listIid = IID_IDXCoreAdapterList;
                var createList = Marshal.GetDelegateForFunctionPointer<CreateAdapterListProc>(ReadVtbl(factory, 3));
                var filter = filterAttribute;
                if (createList(factory, 1, ref filter, in listIid, out var list) != 0 || list == IntPtr.Zero)
                {
                    return result;
                }
                try
                {
                    var getCount = Marshal.GetDelegateForFunctionPointer<GetAdapterCountProc>(ReadVtbl(list, 4));
                    var getAdapter = Marshal.GetDelegateForFunctionPointer<GetAdapterProc>(ReadVtbl(list, 3));
                    uint count = getCount(list);
                    for (uint i = 0; i < count; i++)
                    {
                        var adapterIid = IID_IDXCoreAdapter;
                        if (getAdapter(list, i, in adapterIid, out var adapter) != 0 || adapter == IntPtr.Zero)
                        {
                            continue;
                        }
                        try
                        {
                            string desc = ReadDriverDescription(adapter);
                            result.Add(new AiAdapterInfo(desc, desc, false, filterAttribute == NpuAttribute));
                        }
                        finally
                        {
                            Marshal.Release(adapter);
                        }
                    }
                }
                finally
                {
                    Marshal.Release(list);
                }
            }
            finally
            {
                Marshal.Release(factory);
            }
        }
        catch
        {
            // DXCore unavailable → no accelerated device
        }
        return result;
    }

    /// <summary>Reads the DXCoreAdapterProperty.DriverDescription (UTF-8) of an adapter.</summary>
    private static string ReadDriverDescription(IntPtr adapter)
    {
        try
        {
            var getPropSize = Marshal.GetDelegateForFunctionPointer<GetPropertySizeProc>(ReadVtbl(adapter, 7));
            var getProp = Marshal.GetDelegateForFunctionPointer<GetPropertyProc>(ReadVtbl(adapter, 6));
            nuint size = 0;
            if (getPropSize(adapter, (uint)DXCoreAdapterProperty.DriverDescription, out size) != 0 || size == 0)
            {
                return "";
            }
            var buffer = Marshal.AllocHGlobal((int)size);
            try
            {
                if (getProp(adapter, (uint)DXCoreAdapterProperty.DriverDescription, size, buffer) != 0)
                {
                    return "";
                }
                var bytes = new byte[size];
                Marshal.Copy(buffer, bytes, 0, (int)size);
                return Encoding.UTF8.GetString(bytes).TrimEnd('\0');
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        catch
        {
            return "";
        }
    }

    // ---- DXCore interop (via vtable function pointers) ----
    // GUIDs from dxcore_interface.h.

    private const string DxCore = "dxcore.dll";

    /// <summary>DXCORE_HARDWARE_TYPE_ATTRIBUTE_NPU</summary>
    private static readonly Guid NpuAttribute = new("d46140c4-add7-451b-9e56-06fe8c3b58ed");

    /// <summary>DXCORE_HARDWARE_TYPE_ATTRIBUTE_GPU</summary>
    private static readonly Guid GpuAttribute = new("b69eb219-3ded-4464-979f-a00bd4687006");

    private static readonly Guid IID_IDXCoreAdapterFactory = new("78ee5945-c36e-4b13-a669-005dd11c0f06");
    private static readonly Guid IID_IDXCoreAdapterList = new("526c7776-40e9-459b-b711-f32ad76dfc28");
    private static readonly Guid IID_IDXCoreAdapter = new("f0db4c7f-fe5a-42a2-bd62-f2a6cf6fc83e");

    private enum DXCoreAdapterProperty : uint
    {
        InstanceLuid = 0,
        DriverVersion = 1,
        DriverDescription = 2,
        HardwareID = 3,
        IsHardware = 11,
        IsIntegrated = 12,
    }

    [DllImport(DxCore, SetLastError = true)]
    private static extern int DXCoreCreateAdapterFactory(in Guid riid, out IntPtr ppFactory);

    // IDXCoreAdapterFactory: IUnknown(0-2) + CreateAdapterList(3) + GetAdapterByLuid(4) + ...
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CreateAdapterListProc(IntPtr self, uint numAttributes, [In] ref Guid filterAttributes, in Guid riid, out IntPtr ppAdapterList);

    // IDXCoreAdapterList: IUnknown(0-2) + GetAdapter(3) + GetAdapterCount(4) + IsStale(5) + GetFactory(6)
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetAdapterProc(IntPtr self, uint index, in Guid riid, out IntPtr ppAdapter);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate uint GetAdapterCountProc(IntPtr self);

    // IDXCoreAdapter: IUnknown(0-2) + IsValid(3) + IsAttributeSupported(4) + IsPropertySupported(5)
    // + GetProperty(6) + GetPropertySize(7) + IsQueryStateSupported(8) + QueryState(9) + SetState(10) + GetFactory(11)
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetPropertyProc(IntPtr self, uint property, nuint bufferSize, IntPtr propertyData);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetPropertySizeProc(IntPtr self, uint property, out nuint bufferSize);

    /// <summary>Reads the function pointer at the given vtable slot of a COM object.</summary>
    private static IntPtr ReadVtbl(IntPtr comObject, int slot)
    {
        IntPtr vtable = Marshal.ReadIntPtr(comObject);
        return Marshal.ReadIntPtr(vtable, slot * IntPtr.Size);
    }
}
