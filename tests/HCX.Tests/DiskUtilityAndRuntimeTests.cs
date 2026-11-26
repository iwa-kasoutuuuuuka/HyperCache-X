using System;
using System.Collections.Generic;
using System.IO;
using HCX.Core;
using HCX.VirtualFs;
using Xunit;

namespace HCX.Tests;

public sealed class DiskUtilityAndRuntimeTests
{
	[Fact]
	public void DiskUtility_EnumeratesReadyDrivesAndAvailableLetters()
	{
		List<DiskInfo> disks = DiskUtility.GetDisks();
		Assert.NotEmpty(disks);
		foreach (DiskInfo d in disks)
		{
			Assert.False(string.IsNullOrWhiteSpace(d.DriveLetter));
			Assert.False(string.IsNullOrWhiteSpace(d.DisplayName));
		}

		List<string> freeLetters = DiskUtility.GetAvailableDriveLetters();
		foreach (DiskInfo d in disks)
		{
			Assert.DoesNotContain(d.DriveLetter, freeLetters, StringComparer.OrdinalIgnoreCase);
		}

		Assert.True(DiskUtility.GetTotalPhysicalMemoryBytes() > 0);
	}

	[Fact]
	public void WinFspRuntime_GetStatusDoesNotThrow()
	{
		WinFspStatusInfo status = WinFspRuntime.GetStatus();
		Assert.NotNull(status.Summary);
	}

	[Fact]
	public void CacheOptions_RejectsRawDevicePathForSsdCacheDirectory()
	{
		string existingDir = Path.GetTempPath();
		CacheOptions opts = new CacheOptions
		{
			TargetRootPath = existingDir,
			SsdCacheDirectory = @"\\?\Volume{00000000-0000-0000-0000-000000000000}\",
			SsdCacheSizeBytes = 1024 * 1024
		};

		Assert.Throws<ArgumentException>(() => opts.Validate());
	}
}
