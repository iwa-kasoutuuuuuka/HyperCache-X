param(
	[switch]$InstallIfMissing
)

$ErrorActionPreference = "Stop"
$RepoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $RepoRoot

$dllPaths = @(
	(Join-Path ${env:ProgramFiles(x86)} "WinFsp\bin\winfsp-x64.dll"),
	(Join-Path $env:ProgramFiles "WinFsp\bin\winfsp-x64.dll")
)

$found = $dllPaths | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
if (-not $found) {
	if ($InstallIfMissing) {
		Write-Host "==> Installing WinFsp.WinFsp via winget (requires UAC approval)..." -ForegroundColor Cyan
		$proc = Start-Process -FilePath "winget" -ArgumentList "install --id WinFsp.WinFsp -e --accept-package-agreements --accept-source-agreements" -Verb RunAs -Wait -PassThru
		if ($proc.ExitCode -ne 0) {
			throw "WinFsp installation exited with code $($proc.ExitCode)"
		}
		$found = $dllPaths | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
	}
}

if (-not $found) {
	Write-Warning "WinFsp is not installed on this host. Run 'winget install WinFsp.WinFsp' or pass -InstallIfMissing, then re-run this script."
	exit 2
}

Write-Host "==> WinFsp detected: $found" -ForegroundColor Green
Write-Host "==> Running live WinFsp mount E2E tests..." -ForegroundColor Cyan
dotnet test tests\HCX.Tests\HCX.Tests.csproj -c Release --filter "FullyQualifiedName~WinFsp_Live" --verbosity normal
if ($LASTEXITCODE -ne 0) { throw "WinFsp E2E tests failed with exit code $LASTEXITCODE" }
Write-Host "==> WinFsp live E2E verification PASSED." -ForegroundColor Green
