param(
    [Parameter(Mandatory = $true)][string]$PatchedExe,
    [Parameter(Mandatory = $true)][string]$OutputPng
)
$ErrorActionPreference = 'Stop'
$target = [IO.Path]::GetFullPath($PatchedExe)
$output = [IO.Path]::GetFullPath($OutputPng)
$before = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash
Add-Type -AssemblyName PresentationFramework, PresentationCore, WindowsBase
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class PclSmokeRegistry {
    public static readonly IntPtr CurrentUser = new IntPtr(unchecked((int)0x80000001));
    [DllImport("advapi32.dll")] public static extern int RegOverridePredefKey(IntPtr key, IntPtr replacement);
}
'@
$id = [Guid]::NewGuid().ToString('N')
$keyName = 'Software\PCL-Link-Smoke-' + $id
$key = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey($keyName)
$key.SetValue('SmokeOwner', $id)
$application = $null
$page = $null
try {
    if ([PclSmokeRegistry]::RegOverridePredefKey([PclSmokeRegistry]::CurrentUser, $key.Handle.DangerousGetHandle()) -ne 0) {
        throw 'Cannot isolate UI test registry. No application code was loaded.'
    }
    $assembly = [Reflection.Assembly]::LoadFrom($target)
    $uiPath = Join-Path ([IO.Path]::GetDirectoryName($target)) 'PCL.Link.UI.dll'
    $script:smokeUiPath = $uiPath
    [AppDomain]::CurrentDomain.add_AssemblyResolve({
        param($sender, $request)
        if ($request.Name.Split(',')[0] -eq 'PCL.Link.UI') { return [Reflection.Assembly]::LoadFrom($script:smokeUiPath) }
        return $null
    })
    $appType = $assembly.GetType('PCL.Application', $true)
    $application = [Activator]::CreateInstance($appType, $true)
    # Load only official styles and our page. Never run official Startup, login,
    # update/telemetry, main-window initialization, or Minecraft folder discovery.
    $startup = $appType.GetMethod('Application_Startup', [Reflection.BindingFlags]'Instance,NonPublic')
    $handler = [Delegate]::CreateDelegate([System.Windows.StartupEventHandler], $application, $startup)
    $application.remove_Startup($handler)
    $application.InitializeComponent()
    $startupUri = [System.Windows.Application].GetField('_startupUri', [Reflection.BindingFlags]'Instance,NonPublic')
    if ($null -eq $startupUri) { throw 'Cannot disable startup URI in this isolated test runtime.' }
    $startupUri.SetValue($application, $null)
    $core = [Type]::GetType('MeloongCore.Main, MeloongCore', $true)
    $core.GetMethod('Init').Invoke($null, @('PCL-Link-Smoke')) | Out-Null
    $page = [Activator]::CreateInstance($assembly.GetType('PCL.PageLinkMain', $true))
    if ($page.Child.GetType().FullName -ne 'PclLink.Ui.LinkPage') { throw 'Page did not attach the native add-on.' }
    foreach ($name in @('LabPlayer', 'BtnCreate', 'BtnJoin', 'TextCode', 'PanRooms', 'PanPeers')) {
        if ($null -eq $page.Child.FindName($name)) { throw "Missing native UI element: $name" }
    }
    if ($null -ne $page.Child.FindName('TextPassword') -or $null -ne $page.Child.FindName('BtnLogin')) { throw 'Manual networking login is still visible.' }
    $launchPage = [Activator]::CreateInstance($assembly.GetType('PCL.PageLaunchRight', $true))
    $panel = $launchPage.FindName('PanMain')
    if ($null -eq ($panel.Children | Where-Object { $_.GetType().FullName -eq 'PclLink.Ui.LinkPage' })) { throw 'Networking widget is missing from the launch homepage.' }
    $launchInit = $assembly.GetType('PCL.PageLaunchRight', $true).GetMethod('Init', [Reflection.BindingFlags]'Instance,NonPublic')
    if ($null -eq $launchInit) { throw 'Official launch page initialization anchor is missing.' }
    $launchInit.Invoke($launchPage, $null) | Out-Null
    if ($launchPage.FindName('PanLog').Visibility -ne [Windows.Visibility]::Collapsed -or
        $launchPage.FindName('PanHint').Visibility -ne [Windows.Visibility]::Collapsed) {
        throw 'Release homepage still shows debug log or snapshot-only notice.'
    }
    Write-Output 'PASS: Networking widget is embedded independently from custom homepage content.'
    Write-Output 'PASS: Manual networking login controls and redundant notices are absent.'
    Write-Output 'PASS: Release homepage hides the startup log and snapshot-only notice.'
    function InitializeCards($element) {
        if ($element -is [System.Windows.FrameworkElement]) {
            if ($element.GetType().FullName -in @('PCL.MyCard', 'PCL.MyHint')) {
                $element.RaiseEvent((New-Object System.Windows.RoutedEventArgs ([System.Windows.FrameworkElement]::LoadedEvent)))
            }
            foreach ($child in [System.Windows.LogicalTreeHelper]::GetChildren($element)) { InitializeCards $child }
        }
    }
    InitializeCards $launchPage.Child
    $launchPage.Child.Background = New-Object System.Windows.Media.SolidColorBrush ([System.Windows.Media.Color]::FromRgb(245,245,245))
    $launchPage.Width = 760
    $launchPage.Height = 1000
    $launchPage.Measure((New-Object Windows.Size 760,1000))
    $launchPage.Arrange((New-Object Windows.Rect 0,0,760,1000))
    $launchPage.UpdateLayout()
    $bitmap = New-Object Windows.Media.Imaging.RenderTargetBitmap 760,1000,96,96,([Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($launchPage)
    $encoder = New-Object Windows.Media.Imaging.PngBitmapEncoder
    $encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($output)) | Out-Null
    $file = [IO.File]::Create($output)
    try { $encoder.Save($file) } finally { $file.Dispose() }
    Write-Output 'PASS: Patched official page constructs and attaches the native .NET Framework UI.'
    Write-Output 'PASS: Required controls and runtime XAML namespace mapping resolve.'
    Write-Output 'PASS: No official startup/login/update/game discovery was invoked; registry reads are isolated.'
    Write-Output "Rendered page: $output"
}
finally {
    if ($null -ne $page) { $page.TryExit($true, $true) | Out-Null }
    if ($null -ne $application) { $application.Shutdown() }
    [PclSmokeRegistry]::RegOverridePredefKey([PclSmokeRegistry]::CurrentUser, [IntPtr]::Zero) | Out-Null
    $key.Dispose()
    $owner = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($keyName)
    $owned = $null -ne $owner -and $owner.GetValue('SmokeOwner') -eq $id
    if ($null -ne $owner) { $owner.Dispose() }
    if ($owned -and $keyName -eq ('Software\PCL-Link-Smoke-' + $id)) {
        [Microsoft.Win32.Registry]::CurrentUser.DeleteSubKeyTree($keyName, $false)
    }
    if ((Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -ne $before) { throw 'UI smoke test changed the program file.' }
}
