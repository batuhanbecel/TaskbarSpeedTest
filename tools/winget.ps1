# Generates winget manifests for a released installer into dist\winget and optionally submits
# them to microsoft/winget-pkgs (needs a GitHub token with public_repo scope in $env:WINGET_TOKEN).
param(
    [Parameter(Mandatory)][string]$Version,
    [Parameter(Mandatory)][string]$InstallerPath,
    [switch]$Submit
)

$id = "batuhanbecel.TaskbarSpeedTest"
$url = "https://github.com/batuhanbecel/TaskbarSpeedTest/releases/download/v$Version/TaskbarSpeedTest-Setup-$Version.exe"
$sha = (Get-FileHash $InstallerPath -Algorithm SHA256).Hash
$date = Get-Date -Format "yyyy-MM-dd"

$root = Split-Path $PSScriptRoot
$out = Join-Path $root "dist\winget\manifests\b\batuhanbecel\TaskbarSpeedTest\$Version"
New-Item -ItemType Directory -Force $out | Out-Null

foreach ($template in Get-ChildItem (Join-Path $root "winget\*.yaml")) {
    $text = (Get-Content $template.FullName -Raw).
        Replace("{VERSION}", $Version).Replace("{URL}", $url).Replace("{SHA256}", $sha).Replace("{DATE}", $date)
    [IO.File]::WriteAllText((Join-Path $out ($template.Name -replace "^manifest", $id)), $text)
}
Write-Host "Manifests written to $out"

if ($Submit) {
    if (-not $env:WINGET_TOKEN) { Write-Host "WINGET_TOKEN not set; not submitting."; exit 0 }
    $wc = Join-Path ([IO.Path]::GetTempPath()) "wingetcreate.exe"
    Invoke-WebRequest https://aka.ms/wingetcreate/latest -OutFile $wc
    & $wc submit --token $env:WINGET_TOKEN --prtitle "New version: $id version $Version" $out
    exit $LASTEXITCODE
}
