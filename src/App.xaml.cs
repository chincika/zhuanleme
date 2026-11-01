using System;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows;

namespace ZhuanLeMe;

public partial class App : Application
{
    private Mutex? _mutex;
    private EventWaitHandle? _wake;
    private bool _ownsMutex;
    private volatile bool _stopping;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var overridePath = Environment.GetEnvironmentVariable("ZHUANLEME_DATA_DIR");
        var suffix = string.IsNullOrEmpty(overridePath) ? "default" :
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(overridePath))).Substring(0, 12);
        var key = "Local\\ZhuanLeMe-v1-" + suffix;
        _wake = new EventWaitHandle(false, EventResetMode.AutoReset, key + "-wake");
        _mutex = new Mutex(true, key + "-mutex", out _ownsMutex);
        if (!_ownsMutex)
        {
            _wake.Set();
            Shutdown();
            return;
        }

        var window = new MainWindow();
        MainWindow = window;
        window.Show();
        var waiter = new Thread(() =>
        {
            while (!_stopping)
            {
                try { _wake.WaitOne(); }
                catch (ObjectDisposedException) { return; }
                if (!_stopping) Dispatcher.BeginInvoke(new Action(window.RestoreFromTray));
            }
        }) { IsBackground = true, Name = "ZhuanLeMe activate" };
        waiter.Start();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _stopping = true;
        if (_ownsMutex)
        {
            _wake?.Set();
            _mutex?.ReleaseMutex();
        }
        _wake?.Dispose();
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
