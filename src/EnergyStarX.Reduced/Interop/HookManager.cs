// Copyright 2022 Bingxing Wang
// Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated documentation files (the "Software"), to deal in the Software without restriction, including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so, subject to the following conditions:
// The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
// If you are Microsoft (and/or its affiliates) employee, vendor or contractor who is working on Windows-specific integration projects, you may use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so without the restriction above.

using System.ComponentModel;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Accessibility;

namespace EnergyStarX.Reduced.Interop;

public static class HookManager
{
    private static UnhookWinEventSafeHandle? windowEventHook;

    internal static WINEVENTPROC WinEventProc = WindowEventCallback;

    internal static event EventHandler<HWND>? SystemForegroundWindowChanged;

    public static void SubscribeToWindowEvents()
    {
        if (windowEventHook is null || windowEventHook.IsInvalid)
        {
            windowEventHook = PInvoke.SetWinEventHook(
                PInvoke.EVENT_SYSTEM_FOREGROUND, // eventMin
                PInvoke.EVENT_SYSTEM_FOREGROUND, // eventMax
                null, // hmodWinEventProc
                WinEventProc, // lpfnWinEventProc
                0, // idProcess
                0, // idThread
                PInvoke.WINEVENT_OUTOFCONTEXT
            );

            if (windowEventHook.IsInvalid)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
        }
    }

    public static void UnsubscribeWindowEvents()
    {
        if (windowEventHook is not null && !windowEventHook.IsInvalid)
        {
            windowEventHook.Dispose();
            windowEventHook = null;
        }
    }

    private static void WindowEventCallback(
        HWINEVENTHOOK hWinEventHook,
        uint eventType,
        HWND hwnd,
        int idObject,
        int idChild,
        uint dwEventThread,
        uint dwmsEventTime
    )
    {
        SystemForegroundWindowChanged?.Invoke(null, hwnd);
    }
}
