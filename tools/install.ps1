# requires -Version 5.1
<#
.SYNOPSIS
    把 XAssistant 部署到固定目录并注册开机自启。

.DESCRIPTION
    自用安装脚本。做三件事：
      1. 复制 Release 发布产物到固定安装目录（默认 C:\Program Files\XAssistant）
      2. 把开机自启注册表项指向安装目录下的 exe
      3. 可选：立即启动

    为什么需要它：程序在开发时会把自启指向
    bin\Release\net10.0-windows\win-x64\publish\XAssistant.exe —— 一旦清理构建产物
    或移动项目目录，自启就会失效。个人长期使用必须装到稳定路径。

.PARAMETER InstallDir
    安装目录。默认 C:\Program Files\XAssistant。

.PARAMETER SkipStart
    加此参数则部署后不立即启动程序。

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\install.ps1

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\install.ps1 -InstallDir "D:\Apps\XAssistant"
#>

[CmdletBinding()]
param(
    [string]$InstallDir = "C:\Program Files\XAssistant",
    [switch]$SkipStart
)

$ErrorActionPreference = "Stop"

# ---- 定位发布产物 ----
# 脚本位于 <仓库根>/tools/，发布产物位于
# <仓库根>/bin/Release/net10.0-windows/win-x64/publish/
$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot   = Split-Path -Parent $scriptRoot
$publishDir = Join-Path $repoRoot "bin\Release\net10.0-windows\win-x64\publish"

if (-not (Test-Path (Join-Path $publishDir "XAssistant.exe"))) {
    Write-Host "找不到 XAssistant.exe（期望路径：$publishDir）" -ForegroundColor Red
    Write-Host "请先执行：dotnet publish XAssistant.csproj -c Release -r win-x64 --self-contained true" -ForegroundColor Yellow
    exit 1
}

Write-Host "源目录  : $publishDir" -ForegroundColor Cyan
Write-Host "安装到  : $InstallDir" -ForegroundColor Cyan

# ---- 停止正在运行的实例 ----
$running = Get-Process -Name "XAssistant" -ErrorAction SilentlyContinue
if ($running) {
    Write-Host "正在停止运行中的 XAssistant（PID: $($running.Id -join ', ')）..." -ForegroundColor Yellow
    $running | Stop-Process -Force
    Start-Sleep -Milliseconds 800
}

# ---- 复制到安装目录 ----
if (-not (Test-Path $InstallDir)) {
    New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null
}
# 排除可能存在的旧配置目录（正常不会在发布目录里）
Get-ChildItem -Path $publishDir -Force | ForEach-Object {
    Copy-Item -Path $_.FullName -Destination $InstallDir -Recurse -Force
}
$exePath = Join-Path $InstallDir "XAssistant.exe"
if (-not (Test-Path $exePath)) {
    Write-Host "复制失败：$exePath 不存在" -ForegroundColor Red
    exit 1
}
Write-Host "已复制到 $InstallDir" -ForegroundColor Green

# ---- 注册开机自启 ----
$runKey = "HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run"
New-ItemProperty -Path $runKey -Name "XAssistant" -Value $exePath -PropertyType String -Force | Out-Null
Write-Host "已注册开机自启 → $exePath" -ForegroundColor Green

# ---- 提示数据位置 ----
$appData = Join-Path $env:APPDATA "XAssistant"
Write-Host ""
Write-Host "数据目录：$appData" -ForegroundColor DarkGray
Write-Host "（首次启动后自动创建：统计数据库、appsettings.json、日志）" -ForegroundColor DarkGray

# ---- 启动 ----
if (-not $SkipStart) {
    Start-Process -FilePath $exePath
    Write-Host "已启动（启动后自动最小化到托盘，双击托盘图标可显示界面）" -ForegroundColor Green
}

Write-Host ""
Write-Host "部署完成。" -ForegroundColor Cyan
Write-Host "卸载：删除 '$InstallDir' 并删除注册表项 $runKey 下的 XAssistant" -ForegroundColor DarkGray
