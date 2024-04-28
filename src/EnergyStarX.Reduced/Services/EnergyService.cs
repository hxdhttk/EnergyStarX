// Copyright 2022 Bingxing Wang
// Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated documentation files (the "Software"), to deal in the Software without restriction, including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so, subject to the following conditions:
// The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
// If you are Microsoft (and/or its affiliates) employee, vendor or contractor who is working on Windows-specific integration projects, you may use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so without the restriction above.

using System.Diagnostics;
using System.IO.Enumeration;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using EnergyStarX.Reduced.Interop;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Threading;

namespace EnergyStarX.Reduced.Services;

public class EnergyService
{
    private readonly object lockObject = new();
    private CancellationTokenSource houseKeepingCancellationTokenSource = new();

    // Speical handling needs for UWP to get the child window process
    private const string UWPFrameHostApp = "ApplicationFrameHost.exe";

    private readonly nint pThrottleOn;
    private readonly nint pThrottleOff;
    private readonly int szControlBlock;

    private uint pendingProcPid = 0;
    private string pendingProcName = "";

    public IReadOnlySet<string> ProcessWhiteList { get; private set; } = new HashSet<string>();

    private IReadOnlySet<string> WildcardProcessWhiteList { get; set; } = new HashSet<string>();

    public IReadOnlySet<string> ProcessBlackList { get; private set; } = new HashSet<string>();

    private IReadOnlySet<string> WildcardProcessBlackList { get; set; } = new HashSet<string>();

    public EnergyService()
    {
        szControlBlock = Unsafe.SizeOf<PROCESS_POWER_THROTTLING_STATE>();
        pThrottleOn = Marshal.AllocHGlobal(szControlBlock);
        pThrottleOff = Marshal.AllocHGlobal(szControlBlock);

        PROCESS_POWER_THROTTLING_STATE throttleState =
            new()
            {
                Version = PInvoke.PROCESS_POWER_THROTTLING_CURRENT_VERSION,
                ControlMask = PInvoke.PROCESS_POWER_THROTTLING_EXECUTION_SPEED,
                StateMask = PInvoke.PROCESS_POWER_THROTTLING_EXECUTION_SPEED,
            };

        PROCESS_POWER_THROTTLING_STATE unthrottleState =
            new()
            {
                Version = PInvoke.PROCESS_POWER_THROTTLING_CURRENT_VERSION,
                ControlMask = PInvoke.PROCESS_POWER_THROTTLING_EXECUTION_SPEED,
                StateMask = 0U,
            };

        Marshal.StructureToPtr(throttleState, pThrottleOn, false);
        Marshal.StructureToPtr(unthrottleState, pThrottleOff, false);
    }

    public void Initialize()
    {
        lock (lockObject)
        {
            HookManager.SubscribeToWindowEvents();
            HookManager.SystemForegroundWindowChanged += HookManager_SystemForegroundWindowChanged;

            ApplyProcessWhiteList(Utility.GetListContent(ListKind.WhiteList));
            ApplyProcessBlackList(Utility.GetListContent(ListKind.BlackList));
        }
    }

    public void AppExiting()
    {
        lock (lockObject)
        {
            StopThrottling();

            HookManager.SystemForegroundWindowChanged -= HookManager_SystemForegroundWindowChanged;
            HookManager.UnsubscribeWindowEvents();
        }
        ;
    }

    public void ApplyAndSaveProcessWhiteList(string processWhiteListString)
    {
        lock (lockObject)
        {
            ApplyProcessWhiteList(processWhiteListString);
            Utility.SaveListContent(ListKind.WhiteList, processWhiteListString);
            Logger.Info("ProcessWhiteList saved");
        }
    }

    public void ApplyProcessWhiteList(string processWhiteListString)
    {
        lock (lockObject)
        {
            StopThrottling();

            (HashSet<string> processWhiteList, HashSet<string> wildcardProcessWhiteList) =
                ParseProcessList(processWhiteListString);
#if DEBUG
            processWhiteList.Add("devenv.exe"); // Visual Studio
#endif
            ProcessWhiteList = processWhiteList;
            WildcardProcessWhiteList = wildcardProcessWhiteList;

            if (processWhiteList.Any())
            {
                Logger.Info(
                    $"Apply ProcessWhiteList:\n{string.Join(Environment.NewLine, processWhiteList)}"
                );
            }

            StartThrottling();
        }
    }

    public void ApplyAndSaveProcessBlackList(string processBlackListString)
    {
        lock (lockObject)
        {
            ApplyProcessBlackList(processBlackListString);
            Utility.SaveListContent(ListKind.BlackList, processBlackListString);
            Logger.Info("ProcessBlackList saved");
        }
    }

    public void ApplyProcessBlackList(string processBlackListString)
    {
        lock (lockObject)
        {
            StopThrottling();

            (HashSet<string> processBlackList, HashSet<string> wildcardProcessBlackList) =
                ParseProcessList(processBlackListString);
            ProcessBlackList = processBlackList;
            WildcardProcessBlackList = wildcardProcessBlackList;

            if (processBlackList.Any())
            {
                Logger.Info(
                    $"Apply ProcessBlackList:\n{string.Join(Environment.NewLine, processBlackList)}"
                );
            }

            StartThrottling();
        }
    }

    private (HashSet<string> fullProcessList, HashSet<string> wildcardProcessList) ParseProcessList(
        string processListString
    )
    {
        HashSet<string> fullProcessList = new();
        HashSet<string> wildcardProcessList = new();

        Regex doubleSlashRegex = new("//");

        using StringReader stringReader = new(processListString);
        while (stringReader.ReadLine() is string line)
        {
            Match doubleSlashMatch = doubleSlashRegex.Match(line);
            string processName = (doubleSlashMatch.Success ? line[..doubleSlashMatch.Index] : line)
                .Trim()
                .ToLowerInvariant();
            if (!string.IsNullOrEmpty(processName))
            {
                fullProcessList.Add(processName);

                if (processName.Contains("?") || processName.Contains("*"))
                {
                    wildcardProcessList.Add(processName);
                }
            }
        }

        return (fullProcessList, wildcardProcessList);
    }

    private bool StartThrottling()
    {
        lock (lockObject)
        {
            Logger.Info("Start throttling");
            ThrottleUserBackgroundProcesses();
            houseKeepingCancellationTokenSource = new CancellationTokenSource();
            _ = HouseKeeping(houseKeepingCancellationTokenSource.Token);

            return true;
        }
    }

    private bool StopThrottling()
    {
        lock (lockObject)
        {
            Logger.Info("Stop throttling");
            houseKeepingCancellationTokenSource.Cancel();
            RecoverUserProcesses();

            return true;
        }
    }

    private bool ThrottleUserBackgroundProcesses()
    {
        lock (lockObject)
        {
            Process[] runningProcesses = Process.GetProcesses();
            int currentSessionID = Process.GetCurrentProcess().SessionId;

            IEnumerable<Process> sameAsThisSession = runningProcesses.Where(p =>
                p.SessionId == currentSessionID
            );
            foreach (Process proc in sameAsThisSession)
            {
                if (proc.Id == pendingProcPid)
                {
                    continue;
                }
                if (ShouldBypassProcess($"{proc.ProcessName}.exe".ToLowerInvariant()))
                {
                    continue;
                }
                HANDLE hProcess = PInvoke.OpenProcess(
                    PROCESS_ACCESS_RIGHTS.PROCESS_SET_INFORMATION,
                    false,
                    (uint)proc.Id
                );
                ToggleEfficiencyMode(hProcess, true);
                PInvoke.CloseHandle(hProcess);
            }

            return true;
        }
    }

    private bool RecoverUserProcesses()
    {
        lock (lockObject)
        {
            Process[] runningProcesses = Process.GetProcesses();
            int currentSessionID = Process.GetCurrentProcess().SessionId;

            IEnumerable<Process> sameAsThisSession = runningProcesses.Where(p =>
                p.SessionId == currentSessionID
            );
            foreach (Process proc in sameAsThisSession)
            {
                if (ShouldBypassProcess($"{proc.ProcessName}.exe".ToLowerInvariant()))
                {
                    continue;
                }
                HANDLE hProcess = PInvoke.OpenProcess(
                    PROCESS_ACCESS_RIGHTS.PROCESS_SET_INFORMATION,
                    false,
                    (uint)proc.Id
                );
                ToggleEfficiencyMode(hProcess, false);
                PInvoke.CloseHandle(hProcess);
            }

            return true;
        }
    }

    private async Task HouseKeeping(CancellationToken cancellationToken)
    {
        Logger.Info("House keeping task started");

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromMinutes(5), cancellationToken);
                ThrottleUserBackgroundProcesses();
                Logger.Info("House keeping task throttling background processes");
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception e)
            {
                Logger.Error(e, "House keeping task error");
            }
        }

        Logger.Info("House keeping task stopped.");
    }

    private unsafe void HookManager_SystemForegroundWindowChanged(object? sender, HWND hwnd)
    {
        lock (lockObject)
        {
            uint* procId = stackalloc uint[1];
            uint windowThreadId = PInvoke.GetWindowThreadProcessId(hwnd, procId);
            // This is invalid, likely a process is dead, or idk
            if (windowThreadId == 0 || procId[0] == 0)
            {
                return;
            }

            HANDLE procHandle = PInvoke.OpenProcess(
                PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_LIMITED_INFORMATION
                    | PROCESS_ACCESS_RIGHTS.PROCESS_SET_INFORMATION,
                false,
                procId[0]
            );
            if (procHandle == HANDLE.Null)
            {
                return;
            }

            // Get the process
            string appName = GetProcessNameFromHandle(procHandle);

            // UWP needs to be handled in a special case
            if (appName == UWPFrameHostApp)
            {
                bool found = false;
                PInvoke.EnumChildWindows(
                    hwnd,
                    (innerHwnd, lparam) =>
                    {
                        if (found)
                        {
                            return true;
                        }

                        uint* innerProcId = stackalloc uint[1];
                        if (PInvoke.GetWindowThreadProcessId(innerHwnd, innerProcId) > 0)
                        {
                            if (procId == innerProcId)
                            {
                                return true;
                            }

                            HANDLE innerProcHandle = PInvoke.OpenProcess(
                                PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_LIMITED_INFORMATION
                                    | PROCESS_ACCESS_RIGHTS.PROCESS_SET_INFORMATION,
                                false,
                                innerProcId[0]
                            );
                            if (innerProcHandle == HANDLE.Null)
                            {
                                return true;
                            }

                            // Found. Set flag, reinitialize handles and call it a day
                            found = true;
                            PInvoke.CloseHandle(innerProcHandle);
                            procHandle = innerProcHandle;
                            procId = innerProcId;
                            appName = GetProcessNameFromHandle(procHandle);
                        }

                        return true;
                    },
                    nint.Zero
                );
            }

            // Boost the current foreground app, and then impose EcoQoS for previous foreground app
            bool bypass = ShouldBypassProcess(appName);
            if (!bypass)
            {
                Logger.Info($"Boosting {appName}");
                ToggleEfficiencyMode(procHandle, false);
            }

            if (pendingProcPid != 0)
            {
                Logger.Info($"Throttle {pendingProcName}");

                HANDLE prevProcHandle = PInvoke.OpenProcess(
                    PROCESS_ACCESS_RIGHTS.PROCESS_SET_INFORMATION,
                    false,
                    pendingProcPid
                );
                if (prevProcHandle != HANDLE.Null)
                {
                    ToggleEfficiencyMode(prevProcHandle, true);
                    PInvoke.CloseHandle(prevProcHandle);
                    pendingProcPid = 0;
                    pendingProcName = "";
                }
            }

            if (!bypass)
            {
                pendingProcPid = procId[0];
                pendingProcName = appName;
            }

            PInvoke.CloseHandle(procHandle);
        }
    }

    private bool ShouldBypassProcess(string processName) => !ShouldThrottleProcess(processName);

    private bool ShouldThrottleProcess(string processName) =>
        IsProcessInBlackList(processName) || !IsProcessInWhiteList(processName);

    private bool IsProcessInWhiteList(string processName)
    {
        return IsProcessInList(processName, ProcessWhiteList, WildcardProcessWhiteList);
    }

    private bool IsProcessInBlackList(string processName)
    {
        return IsProcessInList(processName, ProcessBlackList, WildcardProcessBlackList);
    }

    private bool IsProcessInList(
        string processName,
        IReadOnlySet<string> fullProcessList,
        IReadOnlySet<string> wildcardProcessList
    )
    {
        if (fullProcessList.Contains(processName.ToLowerInvariant()))
        {
            return true;
        }

        if (
            wildcardProcessList.Any(wildcardExpression =>
                FileSystemName.MatchesSimpleExpression(wildcardExpression, processName, true)
            )
        )
        {
            return true;
        }

        return false;
    }

    private unsafe void ToggleEfficiencyMode(HANDLE hProcess, bool enable)
    {
        PInvoke.SetProcessInformation(
            hProcess,
            PROCESS_INFORMATION_CLASS.ProcessPowerThrottling,
            enable ? pThrottleOn.ToPointer() : pThrottleOff.ToPointer(),
            (uint)szControlBlock
        );
        PInvoke.SetPriorityClass(
            hProcess,
            enable
                ? PROCESS_CREATION_FLAGS.IDLE_PRIORITY_CLASS
                : PROCESS_CREATION_FLAGS.NORMAL_PRIORITY_CLASS
        );
    }

    private unsafe string GetProcessNameFromHandle(HANDLE hProcess)
    {
        uint* capacity = stackalloc uint[1];
        capacity[0] = 1024U;
        fixed (char* sb = new char[(uint)capacity])
        {
            PWSTR pwstr = new PWSTR(sb);

            if (
                PInvoke.QueryFullProcessImageName(
                    hProcess,
                    PROCESS_NAME_FORMAT.PROCESS_NAME_WIN32,
                    pwstr,
                    capacity
                )
            )
            {
                return Path.GetFileName(new string(sb));
            }
        }

        return "";
    }
}
