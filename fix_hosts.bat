@echo off
:: Скрипт для исправления файла hosts
:: Удаляет блокировку files.minecraftforge.net (нужен для Forge)
:: Запусти от имени администратора!

powershell -Command "
$hostsPath = 'C:\Windows\System32\drivers\etc\hosts'
$lines = Get-Content $hostsPath
$filtered = $lines | Where-Object { $_ -notmatch 'files\.minecraftforge\.net' }
$filtered | Set-Content $hostsPath -Encoding UTF8
Write-Host 'Done! files.minecraftforge.net unblocked.' -ForegroundColor Green
"
pause
