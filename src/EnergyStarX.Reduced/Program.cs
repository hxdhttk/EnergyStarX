using EnergyStarX.Reduced.Services;
using Windows.Win32;

namespace EnergyStarX.Reduced;

internal class Program
{
    public static EnergyService EnergyService { get; } = new();

    public static ManualResetEvent Blocker { get; } = new(false);

    [STAThread]
    private static void Main()
    {
        Logger.Info("Starting EnergyStarX.Reduced");

        PInvoke.CoInitialize();

        AppDomain.CurrentDomain.UnhandledException += (sender, e) =>
        {
            EnergyService.AppExiting();

            PInvoke.CoUninitialize();

            Logger.Error((Exception)e.ExceptionObject, "Unhandled exception");
        };

        EnergyService.Initialize();

        Blocker.WaitOne();
    }
}
