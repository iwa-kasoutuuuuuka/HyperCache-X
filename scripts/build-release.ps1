param(
	[string]$Version = "2.4.0",
	[string]$Runtime = "win-x64",
	[string]$CertThumbprint = "",
	[string]$TimestampUrl = "http://timestamp.digicert.com"
)

$ErrorActionPreference = "Stop"
$RepoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $RepoRoot

Write-Host "==> Running Release unit tests..." -ForegroundColor Cyan
dotnet test HCX.sln -c Release --verbosity minimal
if ($LASTEXITCODE -ne 0) { throw "Tests failed with exit code $LASTEXITCODE" }

$DistRoot = Join-Path $RepoRoot "dist"
$SelfName = "HyperCacheX-v$Version-$Runtime-selfcontained"
$PortName = "HyperCacheX-v$Version-$Runtime-portable"
$SelfDir = Join-Path $DistRoot $SelfName
$PortDir = Join-Path $DistRoot $PortName
$SelfZip = Join-Path $DistRoot "$SelfName.zip"
$PortZip = Join-Path $DistRoot "$PortName.zip"
$SumsPath = Join-Path $DistRoot "SHA256SUMS.txt"

foreach ($p in @($SelfDir, $PortDir, $SelfZip, $PortZip)) {
	if (Test-Path $p) { Remove-Item $p -Recurse -Force }
}

function Invoke-CodeSignIfConfigured([string]$ExePath) {
	if ([string]::IsNullOrWhiteSpace($CertThumbprint)) {
		return
	}
	$signtool = Get-ChildItem -Path "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\signtool.exe" -ErrorAction SilentlyContinue |
		Sort-Object FullName -Descending | Select-Object -First 1 -ExpandProperty FullName
	if (-not $signtool) {
		throw "signtool.exe not found under Windows Kits 10."
	}
	Write-Host "==> Signing $ExePath with cert $CertThumbprint..." -ForegroundColor Cyan
	& $signtool sign /sha1 $CertThumbprint /fd SHA256 /tr $TimestampUrl /td SHA256 $ExePath
	if ($LASTEXITCODE -ne 0) { throw "signtool failed with exit code $LASTEXITCODE" }
}

New-Item -ItemType Directory -Path $SelfDir -Force | Out-Null
New-Item -ItemType Directory -Path $PortDir -Force | Out-Null

Write-Host "==> Publishing self-contained $SelfName..." -ForegroundColor Cyan
dotnet publish src\HCX.App\HCX.App.csproj `
	-c Release `
	-r $Runtime `
	--self-contained true `
	-p:PublishSingleFile=false `
	-o $SelfDir
if ($LASTEXITCODE -ne 0) { throw "Self-contained publish failed with exit code $LASTEXITCODE" }
dotnet publish src\HCX.Mcp\HCX.Mcp.csproj `
	-c Release `
	-r $Runtime `
	--self-contained true `
	-p:PublishSingleFile=false `
	-o $SelfDir
if ($LASTEXITCODE -ne 0) { throw "Self-contained MCP publish failed with exit code $LASTEXITCODE" }
Copy-Item (Join-Path $RepoRoot "README.md") -Destination (Join-Path $SelfDir "README.md") -Force
Invoke-CodeSignIfConfigured (Join-Path $SelfDir "HCX.App.exe")
Invoke-CodeSignIfConfigured (Join-Path $SelfDir "HCX.Mcp.exe")

Write-Host "==> Publishing framework-dependent portable $PortName..." -ForegroundColor Cyan
dotnet publish src\HCX.App\HCX.App.csproj `
	-c Release `
	-r $Runtime `
	--self-contained false `
	-p:PublishSingleFile=false `
	-o $PortDir
if ($LASTEXITCODE -ne 0) { throw "Portable publish failed with exit code $LASTEXITCODE" }
dotnet publish src\HCX.Mcp\HCX.Mcp.csproj `
	-c Release `
	-r $Runtime `
	--self-contained false `
	-p:PublishSingleFile=false `
	-o $PortDir
if ($LASTEXITCODE -ne 0) { throw "Portable MCP publish failed with exit code $LASTEXITCODE" }
Copy-Item (Join-Path $RepoRoot "README.md") -Destination (Join-Path $PortDir "README.md") -Force
Invoke-CodeSignIfConfigured (Join-Path $PortDir "HCX.App.exe")
Invoke-CodeSignIfConfigured (Join-Path $PortDir "HCX.Mcp.exe")

if ([string]::IsNullOrWhiteSpace($CertThumbprint)) {
	Write-Host "==> Notice: CertThumbprint not set; packages are unsigned (pass -CertThumbprint <SHA1> to sign)." -ForegroundColor Yellow
}

Write-Host "==> Creating ZIP archives..." -ForegroundColor Cyan
Compress-Archive -Path "$SelfDir\*" -DestinationPath $SelfZip -CompressionLevel Optimal -Force
Compress-Archive -Path "$PortDir\*" -DestinationPath $PortZip -CompressionLevel Optimal -Force

$selfZipHash = (Get-FileHash -Path $SelfZip -Algorithm SHA256).Hash.ToLowerInvariant()
$portZipHash = (Get-FileHash -Path $PortZip -Algorithm SHA256).Hash.ToLowerInvariant()
$selfExeHash = (Get-FileHash -Path (Join-Path $SelfDir "HCX.App.exe") -Algorithm SHA256).Hash.ToLowerInvariant()
$portExeHash = (Get-FileHash -Path (Join-Path $PortDir "HCX.App.exe") -Algorithm SHA256).Hash.ToLowerInvariant()

@(
	"$selfZipHash  $SelfName.zip"
	"$portZipHash  $PortName.zip"
	"$selfExeHash  $SelfName/HCX.App.exe"
	"$portExeHash  $PortName/HCX.App.exe"
) | Set-Content -Path $SumsPath -Encoding UTF8

Write-Host "==> Release packages ready:" -ForegroundColor Green
Write-Host "    Self-contained: $SelfZip ($selfZipHash)"
Write-Host "    Portable:       $PortZip ($portZipHash)"
Write-Host "    SUMS:           $SumsPath"
