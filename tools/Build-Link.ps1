param(
    [ValidateSet('Debug', 'Release', 'Snapshot')][string]$Configuration = 'Release',
    [switch]$SkipTests
)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
Push-Location -LiteralPath $root
try {
    if (-not (Test-Path -LiteralPath 'MeloongCore\Shared\MeloongCore.csproj')) {
        & git submodule update --init --depth 1
        if ($LASTEXITCODE -ne 0) { throw 'Cannot initialize MeloongCore.' }
    }
    & dotnet build 'PCLCS\PCLCS.csproj' -c $Configuration
    if ($LASTEXITCODE -ne 0) { throw 'PCLCS build failed.' }

    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (-not (Test-Path -LiteralPath $vswhere)) { throw 'Install Visual Studio Build Tools with the .NET desktop build tools workload.' }
    $msbuild = & $vswhere -latest -products '*' -find 'MSBuild\**\Bin\amd64\MSBuild.exe' | Select-Object -First 1
    if (-not $msbuild) { throw 'MSBuild.exe was not found.' }
    $version = (& dotnet --version).Trim()
    if (-not $version.StartsWith('10.')) { throw 'This build requires .NET SDK 10.' }
    $sdk = & dotnet --list-sdks | Where-Object { $_.StartsWith("$version ") } | Select-Object -First 1
    $sdkRoot = $sdk.Substring($sdk.IndexOf('[') + 1).TrimEnd(']')
    $oldSdk = $env:MSBuildSDKsPath
    $oldResolver = $env:MSBuildEnableWorkloadResolver
    $env:MSBuildSDKsPath = Join-Path $sdkRoot "$version\Sdks"
    $env:MSBuildEnableWorkloadResolver = 'false'
    try {
        $args = @('Plain Craft Launcher 2\Plain Craft Launcher 2.vbproj', '/nologo', '/v:minimal',
            "/p:Configuration=$Configuration", '/p:BuildProjectReferences=false')
        $packages = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $HOME '.nuget\packages' }
        $reference = Join-Path $packages 'microsoft.netframework.referenceassemblies.net48\1.0.3\build\.NETFramework\v4.8'
        if (Test-Path -LiteralPath $reference) { $args += "/p:FrameworkPathOverride=$reference" }
        & $msbuild @args
        if ($LASTEXITCODE -ne 0) { throw 'PCL WPF build failed.' }
    }
    finally {
        $env:MSBuildSDKsPath = $oldSdk
        $env:MSBuildEnableWorkloadResolver = $oldResolver
    }
    if (-not $SkipTests) {
        & dotnet run --project 'PCL.Link.Tests\PCL.Link.Tests.csproj' -c Release -- 'Plain Craft Launcher 2\bin\Plain Craft Launcher 2.exe'
        if ($LASTEXITCODE -ne 0) { throw 'Link or restoration tests failed.' }
    }

    $bundle = Join-Path $root 'dist\PCL-Link'
    New-Item -ItemType Directory -Path $bundle -Force | Out-Null
    # Package only build products, not the test launcher's game/account/cache data.
    Get-ChildItem -LiteralPath 'Plain Craft Launcher 2\bin' -File |
        Where-Object { $_.Extension -in '.exe', '.dll', '.config' } |
        ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $bundle -Force }
    & dotnet publish 'PCL.Link\PCL.Link.csproj' -c Release -r win-x64 --self-contained true `
        -p:PublishSingleFile=true -o (Join-Path $bundle 'Link')
    if ($LASTEXITCODE -ne 0) { throw 'Link publish failed.' }
    Copy-Item -LiteralPath 'LICENCE' -Destination $bundle -Force
    Copy-Item -LiteralPath 'docs\INTEGRATED-LINK.md' -Destination (Join-Path $bundle 'README.md') -Force
    Copy-Item -LiteralPath 'docs\RESTORED-FEATURES.md' -Destination $bundle -Force
    Copy-Item -LiteralPath 'PCL.Link\kernel\PROVENANCE.md' -Destination (Join-Path $bundle 'Link\kernel') -Force
    $zip = Join-Path $root 'dist\PCL-Link-2.99.0.1-win-x64.zip'
    Compress-Archive -LiteralPath $bundle -DestinationPath $zip -Force
    Write-Output "Build ready: $bundle"
    Write-Output "Bundle ready: $zip"
}
finally { Pop-Location }
