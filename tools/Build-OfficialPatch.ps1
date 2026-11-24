param(
    [Parameter(Mandatory = $true)][string]$OfficialExe,
    [switch]$MakeSample,
    [switch]$SkipTests
)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$official = [IO.Path]::GetFullPath($OfficialExe)
if (-not (Test-Path -LiteralPath $official -PathType Leaf)) { throw 'Official PCL EXE does not exist.' }
Push-Location -LiteralPath $root
try {
    $sourceHash = (Get-FileHash -LiteralPath $official -Algorithm SHA256).Hash
    if (-not (Test-Path -LiteralPath 'PCLCS\bin\MeloongCore.dll')) {
        & dotnet build 'PCLCS\PCLCS.csproj' -c Release
        if ($LASTEXITCODE -ne 0) { throw 'Compile-time public dependency build failed.' }
    }
    $generated = Join-Path $root 'PCL.Link.UI\obj\Adapter'
    New-Item -ItemType Directory -Path $generated -Force | Out-Null
    $xaml = Get-Content -LiteralPath 'Plain Craft Launcher 2\Pages\PageLink\PageLinkMain.xaml' -Encoding UTF8 -Raw
    $xaml = $xaml.Replace('<local:MyPageRight', '<UserControl').Replace('</local:MyPageRight>', '</UserControl>')
    $xaml = $xaml.Replace('clr-namespace:PCL"', 'urn:pcl-link-controls"')
    $xaml = $xaml.Replace('x:Class="PageLinkMain" PanScroll="PanBack"', '')
    $xaml = $xaml.Replace('不修改默认路由或 DNS。', '不修改默认路由或 DNS。此页由第三方联机补丁提供，与 PCL 官方无关。')
    $code = Get-Content -LiteralPath 'Plain Craft Launcher 2\Pages\PageLink\PageLinkMain.xaml.vb' -Encoding UTF8 -Raw
    $code = $code.Replace('Public Class PageLinkMain', 'Public Class LinkPage').Replace('Private Shared AutoJoinCode As String', 'Private AutoJoinCode As String')
    $end = $code.IndexOf('    Public Shared Sub Join()')
    if ($end -lt 0) { throw 'Native page adaptation anchor is missing.' }
    $code = $code.Substring(0, $end) + @'
    Public Function GetScroll() As MyScrollViewer
        Return PanBack
    End Function

    Public Sub QueueJoin(Code As String)
        If LinkState = LinkStates.Finished OrElse LinkState = LinkStates.Loading Then
            Hint("已经在联机网络中，请先离开再加入其他网络。", HintType.Blue)
            Return
        End If
        AutoJoinCode = Code
        If IsLoaded Then EnterPage()
    End Sub
End Class
'@
    [IO.File]::WriteAllText((Join-Path $generated 'LinkPage.xaml'), $xaml, (New-Object Text.UTF8Encoding $false))
    [IO.File]::WriteAllText((Join-Path $generated 'LinkPage.xaml.vb'), $code, (New-Object Text.UTF8Encoding $false))
    # PCL's assembly name contains spaces. Runtime namespace mapping avoids an
    # invalid clr-namespace URI while still using the real official controls.
    [xml]$document = $xaml
    $bindings = New-Object Text.StringBuilder
    [void]$bindings.AppendLine('Partial Public Class LinkPage')
    [void]$bindings.AppendLine('    Inherits UserControl')
    $names = @()
    foreach ($element in $document.SelectNodes('//*')) {
        $name = $element.GetAttribute('Name', 'http://schemas.microsoft.com/winfx/2006/xaml')
        if ($name) {
            $names += [pscustomobject]@{ Name = $name; Type = $element.LocalName }
            [void]$bindings.AppendLine("    Private WithEvents $name As $($element.LocalName)")
        }
    }
    [void]$bindings.AppendLine(@'
    Private Sub InitializeComponent()
        Dim Context As New Markup.ParserContext()
        Context.XamlTypeMapper = New Markup.XamlTypeMapper(New String() {})
        Context.XamlTypeMapper.AddMappingProcessingInstruction("urn:pcl-link-controls", "PCL", GetType(MyCard).Assembly.FullName)
        Using Stream = GetType(LinkPage).Assembly.GetManifestResourceStream("PclLink.Ui.LinkPage.xaml")
            Dim Root = CType(Markup.XamlReader.Load(Stream, Context), UserControl)
'@)
    foreach ($name in $names) {
        [void]$bindings.AppendLine("            Me.$($name.Name) = CType(Root.FindName(""$($name.Name)""), $($name.Type))")
    }
    [void]$bindings.AppendLine(@'
            Dim Body = Root.Content
            Root.Content = Nothing
            Me.Content = Body
            NameScope.SetNameScope(Me, NameScope.GetNameScope(Root))
        End Using
    End Sub
End Class
'@)
    [IO.File]::WriteAllText((Join-Path $generated 'LinkPage.bindings.vb'), $bindings.ToString(), (New-Object Text.UTF8Encoding $false))
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    $msbuild = & $vswhere -latest -products '*' -find 'MSBuild\**\Bin\amd64\MSBuild.exe' | Select-Object -First 1
    if (-not $msbuild) { throw 'Visual Studio MSBuild with .NET desktop tools is required.' }
    $args = @('PCL.Link.UI\PCL.Link.UI.vbproj', '/nologo', '/v:minimal', "/p:PclReferencePath=$official")
    $packages = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $HOME '.nuget\packages' }
    $reference = Join-Path $packages 'microsoft.netframework.referenceassemblies.net48\1.0.3\build\.NETFramework\v4.8'
    if (Test-Path -LiteralPath $reference) { $args += "/p:FrameworkPathOverride=$reference" }
    & $msbuild @args
    if ($LASTEXITCODE -ne 0) { throw 'Native official-version UI adapter build failed.' }

    & dotnet run --project 'PCL.Link.Patcher\PCL.Link.Patcher.csproj' -c Release -- profile $official 'PCL.Link.Patcher\Profiles\pcl-2.99.0.1.json'
    if ($LASTEXITCODE -ne 0) { throw 'Official-version fingerprint generation failed.' }
    $tool = Join-Path $root 'dist\PCL-Link-PatchTool'
    $payload = Join-Path $tool 'Payload'
    New-Item -ItemType Directory -Path $payload -Force | Out-Null
    Copy-Item -LiteralPath 'PCL.Link.UI\bin\PCL.Link.UI.dll' -Destination $payload -Force
    & dotnet publish 'PCL.Link\PCL.Link.csproj' -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o (Join-Path $payload 'Link')
    if ($LASTEXITCODE -ne 0) { throw 'Networking backend publish failed.' }
    Copy-Item -LiteralPath 'PCL.Link\kernel\PROVENANCE.md' -Destination (Join-Path $payload 'Link\kernel') -Force
    $files = [ordered]@{}
    Get-ChildItem -LiteralPath $payload -File -Recurse | Where-Object { $_.Extension -ne '.pdb' -and $_.Name -ne 'payload.json' } |
        ForEach-Object { $relative = $_.FullName.Substring($payload.Length + 1).Replace('\', '/'); $files[$relative] = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
    $manifest = [ordered]@{ Schema = 1; PclVersion = '2.99.0.1'; Files = $files }
    [IO.File]::WriteAllText((Join-Path $payload 'payload.json'), ($manifest | ConvertTo-Json -Depth 5), (New-Object Text.UTF8Encoding $false))
    & dotnet publish 'PCL.Link.Patcher\PCL.Link.Patcher.csproj' -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o $tool
    if ($LASTEXITCODE -ne 0) { throw 'Patch tool publish failed.' }
    Copy-Item -LiteralPath 'docs\OFFICIAL-LINK-PATCH.md' -Destination (Join-Path $tool 'README.md') -Force
    Copy-Item -LiteralPath 'LICENCE' -Destination $tool -Force
    Copy-Item -LiteralPath 'PCL.Link.Patcher\COPYING' -Destination $tool -Force
    Copy-Item -LiteralPath 'PCL.Link.Patcher\LICENSE.md' -Destination $tool -Force
    $source = Join-Path $tool 'Source'
    if (Test-Path -LiteralPath $source) {
        $resolvedSource = [IO.Path]::GetFullPath($source)
        $resolvedDist = [IO.Path]::GetFullPath((Join-Path $root 'dist')) + [IO.Path]::DirectorySeparatorChar
        if (-not $resolvedSource.StartsWith($resolvedDist, [StringComparison]::OrdinalIgnoreCase) -or
            [IO.Path]::GetFileName($resolvedSource) -ne 'Source') {
            throw 'Refusing to clear an unexpected source directory.'
        }
        Remove-Item -LiteralPath $resolvedSource -Recurse -Force
    }
    New-Item -ItemType Directory -Path $source -Force | Out-Null
    foreach ($name in @('PCL.Link.Patcher.csproj', 'PatchEngine.cs', 'PatchModels.cs', 'Program.cs', 'PatchWindow.cs',
        'EmbeddedAssemblyResolver.cs', 'COPYING', 'LICENSE.md')) {
        Copy-Item -LiteralPath (Join-Path 'PCL.Link.Patcher' $name) -Destination $source -Force
    }
    Copy-Item -LiteralPath 'PCL.Link.Patcher\Profiles' -Destination $source -Recurse -Force
    $patchSource = Join-Path $source 'third_party\pcl2-patch'
    New-Item -ItemType Directory -Path $patchSource -Force | Out-Null
    foreach ($name in @('README.md', 'LICENSE')) { Copy-Item -LiteralPath (Join-Path 'third_party\pcl2-patch' $name) -Destination $patchSource -Force }
    $patchCore = Join-Path $patchSource 'Pcl2Patch.Core'
    New-Item -ItemType Directory -Path $patchCore -Force | Out-Null
    foreach ($name in @('Pcl2Patch.Core.csproj', 'Ctx.cs', 'Patches.cs')) {
        Copy-Item -LiteralPath (Join-Path 'third_party\pcl2-patch\Pcl2Patch.Core' $name) -Destination $patchCore -Force
    }
    $uiSource = Join-Path $source 'PCL.Link.UI'
    New-Item -ItemType Directory -Path $uiSource -Force | Out-Null
    foreach ($name in @('PCL.Link.UI.vbproj', 'Bridge.vb', 'AssemblyInfo.vb')) {
        Copy-Item -LiteralPath (Join-Path 'PCL.Link.UI' $name) -Destination $uiSource -Force
    }
    Copy-Item -LiteralPath 'tools\Build-OfficialPatch.ps1' -Destination $source -Force
    if (-not $SkipTests) {
        & dotnet run --project 'PCL.Link.Patcher.Tests\PCL.Link.Patcher.Tests.csproj' -c Release -- $official $payload
        if ($LASTEXITCODE -ne 0) { throw 'Official patch preservation/rollback tests failed.' }
    }
    if ($MakeSample) {
        $sampleDirectory = Join-Path $root 'dist\PCL-Official-Link'
        $sample = Join-Path $sampleDirectory 'Plain Craft Launcher 2.exe'
        if (Test-Path -LiteralPath $sample) {
            & (Join-Path $tool 'PCL.Link.Patcher.exe') restore $sample | Out-Host
            if ($LASTEXITCODE -ne 0) { throw 'Existing sample cannot be safely restored for rebuilding.' }
        }
        & (Join-Path $tool 'PCL.Link.Patcher.exe') patch $official $sample $payload --download --protect-updates | Out-Host
        if ($LASTEXITCODE -ne 0) { throw 'Official PCL test copy patch failed.' }
        Copy-Item -LiteralPath 'docs\OFFICIAL-LINK-PATCH.md' -Destination (Join-Path $sampleDirectory 'README.md') -Force
        if (-not $SkipTests) {
            $windowsPowerShell = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
            & $windowsPowerShell -NoProfile -STA -ExecutionPolicy Bypass -File 'tools\Test-OfficialLinkUI.ps1' `
                -PatchedExe $sample -OutputPng (Join-Path $root 'dist\official-link-page.png')
            if ($LASTEXITCODE -ne 0) { throw 'Isolated official-page UI smoke test failed.' }
        }
    }
    if ((Get-FileHash -LiteralPath $official -Algorithm SHA256).Hash -ne $sourceHash) { throw 'Original input changed during build.' }
    $zip = Join-Path $root 'dist\PCL-Link-PatchTool-win-x64.zip'
    # Do not accidentally package a user's generated official copy, settings,
    # backups, or game folders inside the tool directory on subsequent builds.
    Compress-Archive -LiteralPath @((Join-Path $tool 'PCL.Link.Patcher.exe'), (Join-Path $tool 'README.md'),
        (Join-Path $tool 'LICENCE'), (Join-Path $tool 'COPYING'), (Join-Path $tool 'LICENSE.md'), $source, $payload) `
        -DestinationPath $zip -Force
    Write-Output "Patch tool: $tool"
    Write-Output "Original EXE unchanged: $sourceHash"
}
finally { Pop-Location }
