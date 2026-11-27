using System;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace HCX.App;

public partial class App : Application
{
	private const string SingleInstanceMutexName = @"Local\HyperCacheX_SingleInstance_Mutex";
	private const string ActivateEventName = @"Local\HyperCacheX_ActivateWindow_Event";

	private Mutex? _singleInstanceMutex;
	private EventWaitHandle? _activateEvent;
	private ManualResetEventSlim? _exitSignal;
	private Thread? _activationListenerThread;

	private void OnStartup(object sender, StartupEventArgs e)
	{
		bool createdNew;
		_singleInstanceMutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out createdNew);
		if (!createdNew)
		{
			try
			{
				if (EventWaitHandle.TryOpenExisting(ActivateEventName, out EventWaitHandle? existing))
				{
					using (existing)
					{
						existing.Set();
					}
				}
			}
			catch
			{
			}
			_singleInstanceMutex.Dispose();
			_singleInstanceMutex = null;
			Shutdown(0);
			return;
		}

		_activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
		_exitSignal = new ManualResetEventSlim(false);
		_activationListenerThread = new Thread(ListenForActivation)
		{
			IsBackground = true,
			Name = "HCX.ActivationListener"
		};
		_activationListenerThread.Start();

		bool startMinimized = e.Args.Any(a => string.Equals(a, "--minimized", StringComparison.OrdinalIgnoreCase));

		DispatcherUnhandledException += OnDispatcherUnhandledException;
		AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
		SessionEnding += OnSessionEnding;
		Exit += OnAppExit;

		MainWindow window = new MainWindow(startMinimized);
		MainWindow = window;
		if (!startMinimized)
		{
			window.Show();
		}
	}

	private void ListenForActivation()
	{
		if (_activateEvent == null || _exitSignal == null)
		{
			return;
		}
		WaitHandle[] handles = { _activateEvent, _exitSignal.WaitHandle };
		while (!_exitSignal.IsSet)
		{
			int signaled = WaitHandle.WaitAny(handles);
			if (signaled != 0)
			{
				break;
			}
			Dispatcher? disp = Current?.Dispatcher;
			if (disp != null && !disp.HasShutdownStarted && !disp.HasShutdownFinished)
			{
				disp.BeginInvoke(() => (Current?.MainWindow as MainWindow)?.RestoreFromTray());
			}
		}
	}

	private void OnAppExit(object sender, ExitEventArgs e)
	{
		_exitSignal?.Set();
		_activateEvent?.Dispose();
		_activateEvent = null;
		_exitSignal?.Dispose();
		_exitSignal = null;
		if (_singleInstanceMutex != null)
		{
			try { _singleInstanceMutex.ReleaseMutex(); } catch { }
			_singleInstanceMutex.Dispose();
			_singleInstanceMutex = null;
		}
	}

	private void OnSessionEnding(object sender, SessionEndingCancelEventArgs e)
	{
		(MainWindow as MainWindow)?.PerformSafeShutdown();
	}

	private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
	{
		try
		{
			(MainWindow as MainWindow)?.EmergencySafeShutdown();
		}
		catch
		{
		}
		MessageBox.Show($"予期しないエラーが発生しました (キャッシュは安全に同期されました):\n{e.Exception.Message}", "HyperCache X エラー", MessageBoxButton.OK, MessageBoxImage.Error);
		e.Handled = true;
	}

	private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
	{
		try
		{
			(MainWindow as MainWindow)?.EmergencySafeShutdown();
		}
		catch
		{
		}
	}
}
