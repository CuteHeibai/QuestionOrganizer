param(
    [string]$InstallDir = "$env:LOCALAPPDATA\Programs\QuestionOrganizer"
)

$ErrorActionPreference = "Stop"
$packageRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$appSource = Join-Path $packageRoot "app"
$exePath = Join-Path $InstallDir "QuestionOrganizer.exe"

if (-not (Test-Path $appSource)) {
    throw "安装包缺少 app 目录。"
}

New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
Copy-Item -Path (Join-Path $appSource "*") -Destination $InstallDir -Recurse -Force

$shell = New-Object -ComObject WScript.Shell
$desktopShortcut = Join-Path ([Environment]::GetFolderPath("DesktopDirectory")) "题目整理.lnk"
$startMenuDir = Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs\题目整理"
$startMenuShortcut = Join-Path $startMenuDir "题目整理.lnk"
New-Item -ItemType Directory -Force -Path $startMenuDir | Out-Null

foreach ($shortcutPath in @($desktopShortcut, $startMenuShortcut)) {
    $shortcut = $shell.CreateShortcut($shortcutPath)
    $shortcut.TargetPath = $exePath
    $shortcut.WorkingDirectory = $InstallDir
    $shortcut.Description = "题目整理"
    $shortcut.Save()
}

Write-Host "题目整理已安装到：$InstallDir"
Write-Host "用户项目、输出文件和 AI 配置不会由安装脚本修改。"
