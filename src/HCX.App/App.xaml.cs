using System;
using System.Linq;
using System.Windows;
using System.Windows.Threading;

namespace HCX.App;

public partial class App : Application
{
	private void OnStartup(object sender, StartupEventArgs e)
	{
		bool startMinimized = e.Args.Any(a => string.Equals(a, "--minimized", StringComparison.OrdinalIgnoreCase));

		DispatcherUnhandledException += OnDispatcherUnhandledException;
		AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
		SessionEnding += OnSessionEnding;

		MainWindow window = new MainWindow(startMinimized);
		MainWindow = window;
		if (!startMinimized)
		{
			window.Show();
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
