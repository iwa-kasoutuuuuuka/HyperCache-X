using System;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace HCX.VirtualFs;

public enum WinFspInstallState
{
	NotInstalled = 0,
	Installed = 1
}

public sealed class WinFspStatusInfo
{
	public WinFspInstallState State { get; init; }
	public string DllPath { get; init; } = string.Empty;
	public string Version { get; init; } = string.Empty;
	public string Summary => State == WinFspInstallState.Installed
		? $"インストール済み (v{Version})"
		: "未インストール";
}

/// <summary>
/// Detects WinFsp user-mode filesystem runtime availability and provides setup invocation.
/// </summary>
public static class WinFspRuntime
{
	public static WinFspStatusInfo GetStatus()
	{
		string[] candidateDlls =
		{
			Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "WinFsp", "bin", "winfsp-x64.dll"),
			Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WinFsp", "bin", "winfsp-x64.dll")
		};

		foreach (string path in candidateDlls)
		{
			if (File.Exists(path))
			{
				FileVersionInfo info = FileVersionInfo.GetVersionInfo(path);
				return new WinFspStatusInfo
				{
					State = WinFspInstallState.Installed,
					DllPath = path,
					Version = info.ProductVersion ?? info.FileVersion ?? "Unknown"
				};
			}
		}

		try
		{
			using RegistryKey? key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\WinFsp")
				?? Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WinFsp");
			string? installDir = key?.GetValue("InstallDir") as string;
			if (!string.IsNullOrWhiteSpace(installDir))
			{
				string dll = Path.Combine(installDir, "bin", "winfsp-x64.dll");
				if (File.Exists(dll))
				{
					FileVersionInfo info = FileVersionInfo.GetVersionInfo(dll);
					return new WinFspStatusInfo
					{
						State = WinFspInstallState.Installed,
						DllPath = dll,
						Version = info.ProductVersion ?? info.FileVersion ?? "Unknown"
					};
				}
			}
		}
		catch
		{
		}

		return new WinFspStatusInfo
		{
			State = WinFspInstallState.NotInstalled
		};
	}

	/// <summary>
	/// Launches winget in an elevated command window so the user can review and complete WinFsp installation.
	/// </summary>
	public static bool LaunchWinGetInstall()
	{
		try
		{
			ProcessStartInfo psi = new ProcessStartInfo("winget", "install --id WinFsp.WinFsp -e --accept-package-agreements --accept-source-agreements")
			{
				UseShellExecute = true,
				Verb = "runas"
			};
			Process.Start(psi);
			return true;
		}
		catch
		{
			return false;
		}
	}
}
