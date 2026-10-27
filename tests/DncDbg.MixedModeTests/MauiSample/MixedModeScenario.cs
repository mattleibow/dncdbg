using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace DncDbg.MixedMode.Maui;

internal static class MixedModeScenario
{
    private static readonly NativeCallback Callback = ManagedCallback;

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static int Run(int left, int right)
    {
        int result = NativeCallbackRoundtrip(left, right, Callback); // MAUI_MANAGED_BEFORE
        GC.KeepAlive(Callback);
        return result; // MAUI_MANAGED_AFTER
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int ManagedCallback(int value)
    {
        NativeCheckpoint(3, value);
        int result = value * 2; // MAUI_MANAGED_CALLBACK
        return result;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NativeCallback(int value);

    [DllImport("mixedfixture", EntryPoint = "native_callback_roundtrip", CallingConvention = CallingConvention.Cdecl)]
    private static extern int NativeCallbackRoundtrip(int left, int right, NativeCallback callback);

    [DllImport("mixedfixture", EntryPoint = "native_checkpoint", CallingConvention = CallingConvention.Cdecl)]
    private static extern void NativeCheckpoint(int phase, int result);
}
