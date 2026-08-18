Write-Host ">>> [1/3] Downloading cloudflared..." -ForegroundColor Cyan
New-Item -ItemType Directory -Force -Path "C:\cloudflared" | Out-Null
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
Invoke-WebRequest -Uri "https://github.com/cloudflare/cloudflared/releases/latest/download/cloudflared-windows-amd64.exe" -OutFile "C:\cloudflared\cloudflared.exe"

Write-Host ">>> [2/3] Installing Cloudflare Tunnel Windows Service..." -ForegroundColor Cyan
$token = "eyJhIjoiMDI1NjRjZmRkNzk4NjkwZDRiODlmN2FmYWM3MTY4M2IiLCJ0IjoiOTAwYzcwZDgtOWVlZi00MjY3LWEzNTAtMGM3OTM1OWU5YTdhIiwicyI6InRFOWIzbXJ1ZElCNnIxTEtHVGVqem95MzJ5SXBJcWE4OHhDanVNbWhyTk09In0="
& "C:\cloudflared\cloudflared.exe" service install $token

Write-Host ">>> [3/3] Starting Tunnel Service..." -ForegroundColor Cyan
Start-Service cloudflared -ErrorAction SilentlyContinue

Write-Host "`n>>> SUCCESS! Tunnel is active at https://broadcast.glztech.com" -ForegroundColor Green
