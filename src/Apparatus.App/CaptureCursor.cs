using System.Runtime.InteropServices;
using Apparatus.Core;
using Apparatus.Core.Capture;
using Apparatus.Native;
using Microsoft.UI.Input;
using WinRT;

namespace Apparatus.App;

/// <summary>The capture's crosshair as a WinUI cursor, for a monitor with a given scale.</summary>
internal static class CaptureCursor
{
    // WinUI only makes the system shapes by itself; a cursor drawn by the program comes in through this interface of the
    // Windows App SDK (Microsoft.UI.Input.InputCursor.Interop.h), which has one method after the six of IInspectable.
    private static readonly Guid CursorStaticsInterop = new("ac6f5065-90c4-46ce-beb7-05e138e54117");
    private const int CreateFromHCursorSlot = 6;

    /// <summary>Null when it cannot be made: the capture then keeps the Windows crosshair.</summary>
    public static InputCursor? TryCreate(double monitorScale)
    {
        try
        {
            nint handle = Pointers.CaptureCrosshair(Crosshair.SizeFor(monitorScale, Pointers.ChosenSize()));
            if (handle != 0) return FromHandle(handle);
            AppLog.Warn("Windows did not create the capture pointer; using its own crosshair.");
        }
        catch (Exception ex)   // a pointer is never worth losing the capture for
        {
            AppLog.Warn($"The capture pointer is not available, using the Windows crosshair: {ex.Message}");
        }
        return null;
    }

    private static unsafe InputCursor FromHandle(nint handle)
    {
        using var factory = ActivationFactory.Get("Microsoft.UI.Input.InputCursor", CursorStaticsInterop);
        nint self = factory.ThisPtr, cursor = 0;
        var create = (delegate* unmanaged[Stdcall]<nint, nint, nint*, int>)(*(void***)self)[CreateFromHCursorSlot];
        Marshal.ThrowExceptionForHR(create(self, handle, &cursor));
        try { return MarshalInspectable<InputCursor>.FromAbi(cursor); }
        finally { Marshal.Release(cursor); }
    }
}
