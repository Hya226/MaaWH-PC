# PC 端清单同步：whmx/interface.json -> interface.json（相对脚本目录）
# 与 scripts/sync_interface.py 同逻辑：resource path 改写 + tools 组名转换
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$src = Join-Path $root "whmx\interface.json"
$dst = Join-Path $root "interface.json"
$data = Get-Content $src -Raw -Encoding UTF8 | ConvertFrom-Json
foreach ($r in $data.resource) { $r.path = @("{PROJECT_DIR}/whmx") }
foreach ($g in $data.group) { if ($g.name -eq "tools") { $g.label = "额外队列" } }
$data | ConvertTo-Json -Depth 100 | Set-Content $dst -Encoding UTF8
Write-Host "synced -> $dst"
