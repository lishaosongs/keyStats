# 回归检查：build.ps1 版本解析（显式 -Version / 最近可达 tag / csproj 回退 / 非法输入）
# 按 AST 抽取 build.ps1 中的 Resolve-BuildVersion 执行，另静态断言两处 dotnet build 共享版本参数。
# 运行：powershell -NoProfile -ExecutionPolicy Bypass -File .\tests\packaging-version.tests.ps1
$ErrorActionPreference = "Stop"
$buildScript = Join-Path $PSScriptRoot "..\build.ps1"

$tokens = $null; $errors = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile($buildScript, [ref]$tokens, [ref]$errors)
if ($errors) { Write-Host "FAIL: build.ps1 解析失败" -ForegroundColor Red; exit 1 }
$fn = $ast.FindAll({
        param($n)
        $n -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq 'Resolve-BuildVersion'
    }, $true) | Select-Object -First 1
if (-not $fn) { Write-Host "FAIL: 未在 build.ps1 中找到 Resolve-BuildVersion" -ForegroundColor Red; exit 1 }
. ([scriptblock]::Create($fn.Extent.Text))

$root = Join-Path ([System.IO.Path]::GetTempPath()) ("keystats-versiontest-" + [guid]::NewGuid().ToString("N"))
$fail = @()

function New-Repo {
    param([string]$Name, [string]$Tag)
    $dir = Join-Path $root $Name
    New-Item -ItemType Directory -Path $dir -Force | Out-Null
    $csproj = Join-Path $dir "KeyStats.csproj"
    Set-Content -Path $csproj -Value '<Project><PropertyGroup><Version>1.54</Version></PropertyGroup></Project>'
    Push-Location $dir
    try {
        git init -q
        git -c user.email=test@example.com -c user.name=test commit -q --allow-empty -m init
        if ($Tag) { git tag $Tag }
    }
    finally { Pop-Location }
    return [pscustomobject]@{ Dir = $dir; Project = $csproj }
}

# 正常解析：tag 自动取版本、无 tag 回退 csproj、显式 -Version 覆盖、v 前缀剥离
$cases = @(
    @{ Name = "tag"; Tag = "v1.55"; Explicit = ""; Expected = "1.55" },
    @{ Name = "no-tag"; Tag = ""; Explicit = ""; Expected = "1.54" },
    @{ Name = "explicit"; Tag = "v1.55"; Explicit = "2.0.0"; Expected = "2.0.0" },
    @{ Name = "explicit-v"; Tag = "v1.55"; Explicit = "v2.0.0"; Expected = "2.0.0" }
)
foreach ($case in $cases) {
    $repo = New-Repo $case.Name $case.Tag
    $actual = Resolve-BuildVersion -ExplicitVersion $case.Explicit -ProjectFile $repo.Project -RepoDir $repo.Dir
    if ($actual -ne $case.Expected) { $fail += "$($case.Name): 期望 $($case.Expected)，实际 $actual" }
}

# 非 git 源码快照：回退 csproj
$snapshot = Join-Path $root "snapshot"
New-Item -ItemType Directory -Path $snapshot -Force | Out-Null
$snapshotProject = Join-Path $snapshot "KeyStats.csproj"
Set-Content -Path $snapshotProject -Value '<Project><PropertyGroup><Version>1.54</Version></PropertyGroup></Project>'
$actual = Resolve-BuildVersion -ExplicitVersion "" -ProjectFile $snapshotProject -RepoDir $snapshot
if ($actual -ne "1.54") { $fail += "非 git 快照: 期望 1.54，实际 $actual" }

# 非法版本：自动 tag 或显式传入都应报错，而不是生成错包
foreach ($bad in @(
        @{ Tag = "v1.2.3.4.5"; Explicit = "" },
        @{ Tag = "v1.55"; Explicit = "1.2.3.4.5" }
    )) {
    $repo = New-Repo ("bad-" + [guid]::NewGuid().ToString("N")) $bad.Tag
    $threw = $false
    try { Resolve-BuildVersion -ExplicitVersion $bad.Explicit -ProjectFile $repo.Project -RepoDir $repo.Dir | Out-Null }
    catch { $threw = $true }
    if (-not $threw) { $fail += "非法版本未报错: tag=$($bad.Tag) explicit=$($bad.Explicit)" }
}

# 静态断言：两处 dotnet build 都传 BuildProperties，ZIP/README/EXE 校验复用 $Version
$text = Get-Content $buildScript -Raw
$buildLines = @($text -split "`n" | Where-Object { $_ -match 'dotnet build ' })
if ($buildLines.Count -ne 2) { $fail += "dotnet build 调用数不是 2: $($buildLines.Count)" }
if (@($buildLines | Where-Object { $_ -notmatch '@BuildProperties' }).Count) { $fail += "存在未传 @BuildProperties 的 dotnet build" }
if (-not $text.Contains('@("/p:Version=$Version")')) { $fail += "BuildProperties 未包含 /p:Version" }
if (-not $text.Contains('"KeyStats-Windows-$Version.zip"')) { $fail += "ZIP 名称未复用解析后的版本" }
if (-not $text.Contains('"Version: $Version"')) { $fail += "README 版本未复用解析后的版本" }
if (-not $text.Contains("ProductVersion") -or -not $text.Contains("-split '\+'")) { $fail += "缺少 publish EXE ProductVersion 校验" }

if ($fail.Count) {
    $fail | ForEach-Object { Write-Host "FAIL: $_" -ForegroundColor Red }
    Write-Host "测试输入保留于: $root"
    exit 1
}
Write-Host "PASS: 显式/tag/无tag/非git 回退解析正确，非法版本报错，两处 build 与 ZIP/README/EXE 校验复用版本" -ForegroundColor Green
Write-Host "测试输入保留于: $root"
