$ErrorActionPreference = "Stop"
$RootDir = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $RootDir

Write-Host "==================================================================" -ForegroundColor Cyan
Write-Host "  Roblox Background and Guarded Attack Macro Suite - Build Script" -ForegroundColor Cyan
Write-Host "==================================================================" -ForegroundColor Cyan

Write-Host "`n[1/4] Building Solution (Release)..." -ForegroundColor Yellow
dotnet build -c Release

Write-Host "`n[2/4] Running Phase 2 Proof-of-Concept 3-Scenario Self-Test..." -ForegroundColor Yellow
dotnet run --project .\src\RobloxMacro.PoC\RobloxMacro.PoC.csproj -c Release -- --self-test

Write-Host "`n[3/4] Running Phase 4 Automated Unit and Integration Test Suite..." -ForegroundColor Yellow
dotnet run --project .\tests\RobloxMacro.Tests\RobloxMacro.Tests.csproj -c Release

Write-Host "`n[4/4] Packaging Release Executables into dist\..." -ForegroundColor Yellow
dotnet publish .\src\RobloxMacro.App\RobloxMacro.App.csproj -c Release -o .\dist\App
dotnet publish .\src\RobloxMacro.PoC\RobloxMacro.PoC.csproj -c Release -o .\dist\PoC

Write-Host "`nRunning Smoke Test on Packaged WPF Application (dist\App\RobloxMacro.App.exe)..." -ForegroundColor Yellow
& .\dist\App\RobloxMacro.App.exe --smoke-test

Write-Host "`n==================================================================" -ForegroundColor Green
Write-Host "  BUILD, VERIFICATION AND PACKAGING COMPLETE!" -ForegroundColor Green
Write-Host "  Main Desktop GUI : $RootDir\dist\App\RobloxMacro.App.exe" -ForegroundColor Green
Write-Host "  PoC CLI Tool     : $RootDir\dist\PoC\RobloxMacro.PoC.exe" -ForegroundColor Green
Write-Host "==================================================================" -ForegroundColor Green
