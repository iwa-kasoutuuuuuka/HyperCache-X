param(
	[string]$Version = "2.1.0",
	[string]$Runtime = "win-x64"
)

$ErrorActionPreference = "Stop"
$RepoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $RepoRoot

Write-Host "==> Running Release unit tests..." -ForegroundColor Cyan
dotnet test HCX.sln -c Release --verbosity minimal
if ($LASTEXITCODE -ne 0) { throw "Tests failed with exit code $LASTEXITCODE" }

$DistRoot = Join-Path $RepoRoot "dist"
$PackageName = "HyperCacheX-v$Version-$Runtime"
$PublishDir = Join-Path $DistRoot $PackageName
$ZipPath = Join-Path $DistRoot "$PackageName.zip"
$SumsPath = Join-Path $DistRoot "SHA256SUMS.txt"

if (Test-Path $PublishDir) { Remove-Item $PublishDir -Recurse -Force }
if (Test-Path $ZipPath) { Remove-Item $ZipPath -Force }
New-Item -ItemType Directory -Path $PublishDir -Force | Out-Null

Write-Host "==> Publishing self-contained $PackageName..." -ForegroundColor Cyan
dotnet publish src\HCX.App\HCX.App.csproj `
	-c Release `
	-r $Runtime `
	--self-contained true `
	-p:PublishSingleFile=false `
	-o $PublishDir
if ($LASTEXITCODE -ne 0) { throw "Publish failed with exit code $LASTEXITCODE" }

Copy-Item (Join-Path $RepoRoot "README.md") -Destination (Join-Path $PublishDir "README.md") -Force

Write-Host "==> Creating ZIP archive: $ZipPath" -ForegroundColor Cyan
Compress-Archive -Path "$PublishDir\*" -DestinationPath $ZipPath -CompressionLevel Optimal -Force

$zipHash = (Get-FileHash -Path $ZipPath -Algorithm SHA256).Hash.ToLowerInvariant()
$exeHash = (Get-FileHash -Path (Join-Path $PublishDir "HCX.App.exe") -Algorithm SHA256).Hash.ToLowerInvariant()
@(
	"$zipHash  $PackageName.zip"
	"$exeHash  $PackageName/HCX.App.exe"
) | Set-Content -Path $SumsPath -Encoding UTF8

Write-Host "==> Release package ready:" -ForegroundColor Green
Write-Host "    ZIP:  $ZipPath ($zipHash)"
Write-Host "    SUMS: $SumsPath"
