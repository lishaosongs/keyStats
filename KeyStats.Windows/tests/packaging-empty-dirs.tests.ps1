# 回归检查：build.ps1 的"移除 publish 中无文件目录"逻辑
# 用 TEMP 中的临时输入验证：空目录（含隐藏空目录）不进输出，非空目录（含仅隐藏文件的目录）完整保留。
# 运行：powershell -NoProfile -ExecutionPolicy Bypass -File .\tests\packaging-empty-dirs.tests.ps1
$ErrorActionPreference = "Stop"
$root = Join-Path ([System.IO.Path]::GetTempPath()) ("keystats-packtest-" + [guid]::NewGuid().ToString("N"))
$src = Join-Path $root "src"
$out = Join-Path $root "out"
# build.ps1 中 dotnet build -o $OutputDir 会先创建输出目录
New-Item -ItemType Directory -Path $out -Force | Out-Null

foreach ($d in @(
    "src\Assets\Pet\core",
    "src\runtimes\win-x86\native",
    "src\runtimes\win-x64\native",
    "src\zh-Hans",
    "src\hidden-only",
    "src\hidden-empty"
)) { New-Item -ItemType Directory -Path (Join-Path $root $d) -Force | Out-Null }

Set-Content -Path (Join-Path $root "src\runtimes\win-x64\native\real.dll") -Value "x"
Set-Content -Path (Join-Path $root "src\zh-Hans\KeyStats.resources.dll") -Value "x"
Set-Content -Path (Join-Path $root "src\zh-Hans\hidden.cfg") -Value "x"
attrib +h (Join-Path $root "src\zh-Hans\hidden.cfg")
# 目录内只有隐藏文件：不能被当作空目录删除
Set-Content -Path (Join-Path $root "src\hidden-only\hidden.cfg") -Value "x"
attrib +h (Join-Path $root "src\hidden-only\hidden.cfg")
# 隐藏的空目录：应被清理
attrib +h (Join-Path $root "src\hidden-empty")

Get-ChildItem -Path $src -Force | Copy-Item -Destination $out -Recurse -Force

# 执行 build.ps1 中的实际清理片段（按 AST 取管道，而非复制代码）
$buildScript = Join-Path $PSScriptRoot "..\build.ps1"
$tokens = $null; $errors = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile($buildScript, [ref]$tokens, [ref]$errors)
if ($errors) { Write-Host "FAIL: build.ps1 解析失败" -ForegroundColor Red; exit 1 }
$cleanup = $ast.FindAll({
        param($n)
        $n -is [System.Management.Automation.Language.PipelineAst] -and
        $n.Extent.Text -match 'Get-ChildItem -Path \$OutputDir -Recurse -Directory'
    }, $true) | Select-Object -First 1
if (-not $cleanup) { Write-Host "FAIL: 未在 build.ps1 中找到清理片段" -ForegroundColor Red; exit 1 }

$OutputDir = $out
& ([scriptblock]::Create($cleanup.Extent.Text))

$fail = @()
foreach ($gone in @("Assets", "runtimes\win-x86", "hidden-empty")) {
    if (Test-Path (Join-Path $out $gone)) { $fail += "空目录 $gone 未被移除" }
}
foreach ($keep in @("runtimes\win-x64\native\real.dll", "zh-Hans\KeyStats.resources.dll", "zh-Hans\hidden.cfg", "hidden-only\hidden.cfg")) {
    if (-not (Test-Path (Join-Path $out $keep))) { $fail += "文件 $keep 被误删" }
}

if ($fail.Count) { $fail | ForEach-Object { Write-Host "FAIL: $_" -ForegroundColor Red }; exit 1 }
Write-Host "PASS: 空目录（含隐藏空目录）被移除，非空目录（含仅隐藏文件的目录）完整保留" -ForegroundColor Green
Write-Host "测试输入保留于: $root"
