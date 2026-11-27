using System.Windows;

namespace OriginalReview;

public partial class App : Application
{
    private Mutex? _instance;
    private bool _ownsInstance;

    protected override void OnStartup(StartupEventArgs e)
    {
        _instance = new Mutex(false, @"Local\OriginalReview_" + Environment.UserName);
        try { _ownsInstance = _instance.WaitOne(0); }
        catch (AbandonedMutexException) { _ownsInstance = true; }
        if (!_ownsInstance) { MessageBox.Show("复习工具已打开。"); Shutdown(); return; }
        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_ownsInstance) _instance?.ReleaseMutex();
        _instance?.Dispose();
        base.OnExit(e);
    }
}
