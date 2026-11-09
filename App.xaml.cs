using System.Windows;
namespace DeskRelay;
public partial class App : Application
{
    Mutex? instance;
    bool ownsInstance;
    protected override void OnStartup(StartupEventArgs e)
    {
        string user = System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value;
        instance = new Mutex(false, @"Local\CrossDesk-" + user);
        try { ownsInstance = instance.WaitOne(0); }
        catch (AbandonedMutexException) { ownsInstance = true; }
        if (!ownsInstance)
        {
            MessageBox.Show("CrossDesk is already open. Use its existing window.", "CrossDesk");
            Shutdown();
            return;
        }
        base.OnStartup(e);
    }
    protected override void OnExit(ExitEventArgs e)
    {
        if (ownsInstance) instance?.ReleaseMutex();
        instance?.Dispose();
        base.OnExit(e);
    }
}
