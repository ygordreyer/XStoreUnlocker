param(
    [string]$OutputDirectory,
    [switch]$SkipTests,
    [switch]$FrameworkDependent
)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $repoRoot 'dist/XStoreManager' }
$OutputDirectory = [System.IO.Path]::GetFullPath($OutputDirectory)
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
$vsInstall = if (Test-Path -LiteralPath $vswhere) {
    & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
}
if (-not $vsInstall) { throw 'Install Visual Studio with the Desktop development with C++ workload.' }
$cmake = Join-Path $vsInstall 'Common7/IDE/CommonExtensions/Microsoft/CMake/CMake/bin/cmake.exe'
if (-not (Test-Path -LiteralPath $cmake)) { $cmake = (Get-Command cmake -ErrorAction Stop).Source }
$ctest = Join-Path (Split-Path -Parent $cmake) 'ctest.exe'
$generator = if ((Split-Path (Split-Path $vsInstall -Parent) -Leaf) -eq '18') { 'Visual Studio 18 2026' } else { 'Visual Studio 17 2022' }
$nativeBuild = Join-Path $repoRoot 'build/native'
& $cmake -S $repoRoot -B $nativeBuild -G $generator -A x64 -DBUILD_TESTING=ON
if ($LASTEXITCODE) { throw 'Native configuration failed.' }
& $cmake --build $nativeBuild --config Release --parallel
if ($LASTEXITCODE) { throw 'Native build failed.' }
if (-not $SkipTests) {
    & $ctest --test-dir $nativeBuild -C Release --output-on-failure
    if ($LASTEXITCODE) { throw 'Native tests failed.' }
    & dotnet run --project (Join-Path $repoRoot 'tests/managed/XStoreManager.Tests.csproj') -c Release
    if ($LASTEXITCODE) { throw 'Manager tests failed.' }
}
$publishArgs = @('publish', (Join-Path $repoRoot 'manager/XStoreManager.App/XStoreManager.App.csproj'), '-c', 'Release', '-r', 'win-x64', '-o', $OutputDirectory, '-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true')
if ($FrameworkDependent) { $publishArgs += @('--self-contained', 'false') } else { $publishArgs += @('--self-contained', 'true') }
& dotnet @publishArgs
if ($LASTEXITCODE) { throw 'Desktop publish failed.' }
$engineDirectory = Join-Path $OutputDirectory 'engine'
New-Item -ItemType Directory -Path $engineDirectory -Force | Out-Null
$nativeDll = Join-Path $nativeBuild 'Release/XGameRuntime.dll'
if (-not (Test-Path -LiteralPath $nativeDll)) { throw "Native payload not found: $nativeDll" }
$payload = Join-Path $engineDirectory 'XGameRuntime.dll'
Copy-Item -LiteralPath $nativeDll -Destination $payload -Force
(Get-FileHash -LiteralPath $payload -Algorithm SHA256).Hash.ToLowerInvariant() | Set-Content -LiteralPath "$payload.sha256" -Encoding ascii
Copy-Item -LiteralPath (Join-Path $repoRoot 'LICENSE') -Destination (Join-Path $OutputDirectory 'LICENSE') -Force
Copy-Item -LiteralPath (Join-Path $repoRoot 'README.md') -Destination (Join-Path $OutputDirectory 'README.md') -Force
New-Item -ItemType Directory -Path (Join-Path $OutputDirectory 'docs') -Force | Out-Null
Copy-Item -Path (Join-Path $repoRoot 'docs/*') -Destination (Join-Path $OutputDirectory 'docs') -Recurse -Force
$runtimeConfigPath = Join-Path $repoRoot 'manager/XStoreManager.App/bin/Release/net8.0-windows/win-x64/XStoreManager.runtimeconfig.json'
if ((Test-Path -LiteralPath $runtimeConfigPath) -and -not $FrameworkDependent) {
    $runtimeConfig = Get-Content -LiteralPath $runtimeConfigPath -Raw | ConvertFrom-Json
    $packageRoot = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path ([Environment]::GetFolderPath('UserProfile')) '.nuget/packages' }
    $noticeRoot = Join-Path $OutputDirectory 'third-party-notices'
    New-Item -ItemType Directory -Path $noticeRoot -Force | Out-Null
    foreach ($framework in $runtimeConfig.runtimeOptions.includedFrameworks) {
        $packageDirectory = Join-Path $packageRoot ($framework.name.ToLowerInvariant() + '.runtime.win-x64/' + $framework.version)
        foreach ($notice in @('LICENSE', 'LICENSE.TXT', 'THIRD-PARTY-NOTICES.TXT')) {
            $noticePath = Join-Path $packageDirectory $notice
            if (Test-Path -LiteralPath $noticePath) {
                Copy-Item -LiteralPath $noticePath -Destination (Join-Path $noticeRoot ($framework.name + '-' + $notice)) -Force
            }
        }
    }
}
$source = (& git -C $repoRoot rev-parse HEAD 2>$null)
$dirty = [bool](& git -C $repoRoot status --porcelain 2>$null)
[ordered]@{
    product = 'XStore Manager'; version = '1.0.0'; architecture = 'win-x64'
    builtUtc = [DateTime]::UtcNow.ToString('O'); sourceCommit = "$source"; sourceDirty = $dirty
    runtimeSha256 = (Get-FileHash -LiteralPath $payload -Algorithm SHA256).Hash.ToLowerInvariant()
    selfContained = -not $FrameworkDependent.IsPresent
} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $OutputDirectory 'build-info.json') -Encoding utf8
Write-Output "Published: $OutputDirectory"

