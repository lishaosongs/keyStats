# Runs an isolated tray integration test without building or restarting KeyStats/Explorer.
# Requires Windows with Explorer running, .NET Framework 4.8, and a .NET SDK.
$ErrorActionPreference = 'Stop'

$traySdkLine = & dotnet --list-sdks | Select-Object -Last 1
if ($traySdkLine -notmatch '^(\S+) \[(.+)\]$') {
    throw 'A .NET SDK is required to compile the isolated tray test.'
}
$trayCompiler = Join-Path $Matches[2] ($Matches[1] + '/Roslyn/bincore/csc.dll')
$trayFrameworkKind = if ([IntPtr]::Size -eq 8) { 'Framework64' } else { 'Framework' }
$trayFramework = Join-Path $env:WINDIR "Microsoft.NET/$trayFrameworkKind/v4.0.30319"
$trayTestExecutable = Join-Path ([IO.Path]::GetTempPath()) ('KeyStatsTrayTest-' + [Guid]::NewGuid().ToString('N') + '.exe')

try {
    & dotnet $trayCompiler /nologo /target:exe /langversion:10 /nullable:enable `
        "/out:$trayTestExecutable" `
        "/reference:$trayFramework/mscorlib.dll" `
        "/reference:$trayFramework/System.dll" `
        "/reference:$trayFramework/System.Drawing.dll" `
        "/reference:$trayFramework/System.Windows.Forms.dll" `
        (Join-Path $PSScriptRoot '../KeyStats/Helpers/TrayIconHost.cs') `
        (Join-Path $PSScriptRoot 'tests/TrayIconHostSmokeTest.cs')
    if ($LASTEXITCODE -ne 0) {
        throw 'Isolated tray test compilation failed.'
    }

    & $trayTestExecutable
    if ($LASTEXITCODE -ne 0) {
        throw 'Tray recovery smoke test failed.'
    }
}
finally {
    if (Test-Path -LiteralPath $trayTestExecutable) {
        Remove-Item -LiteralPath $trayTestExecutable
    }
}
