param(
    [string]$InstallDir = "$env:LOCALAPPDATA\Programs\QuestionOrganizer"
)

$ErrorActionPreference = "Stop"
$desktopShortcut = Join-Path ([Environment]::GetFolderPath("DesktopDirectory")) "题目整理.lnk"
$startMenuDir = Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs\题目整理"

Remove-Item -LiteralPath $desktopShortcut -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath $startMenuDir -Recurse -Force -ErrorAction SilentlyContinue

if (Test-Path $InstallDir) {
    Remove-Item -LiteralPath $InstallDir -Recurse -Force
}

Write-Host "题目整理已卸载。"
Write-Host "已保留用户项目、输出文件和 AI 配置："
Write-Host "  $env:LOCALAPPDATA\QuestionOrganizer"
Write-Host "  文档目录中的题目整理输出文件夹"
