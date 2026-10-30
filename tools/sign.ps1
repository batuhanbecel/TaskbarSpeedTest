# Signs files with a code-signing certificate given as a base64 PFX in $env:SIGN_CERT_PFX_BASE64
# (password in $env:SIGN_CERT_PASSWORD). Does nothing when no certificate is configured.
param([Parameter(Mandatory)][string[]]$Files)

if (-not $env:SIGN_CERT_PFX_BASE64) {
    Write-Host "No signing certificate configured; skipping signing."
    exit 0
}

$pfx = Join-Path ([IO.Path]::GetTempPath()) "taskbarstatus-sign.pfx"
[IO.File]::WriteAllBytes($pfx, [Convert]::FromBase64String($env:SIGN_CERT_PFX_BASE64))
try {
    $signtool = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\signtool.exe" |
        Sort-Object FullName -Descending | Select-Object -First 1 -ExpandProperty FullName
    if (-not $signtool) { throw "signtool.exe not found (Windows SDK)" }
    foreach ($f in $Files) {
        & $signtool sign /f $pfx /p $env:SIGN_CERT_PASSWORD /fd sha256 /tr http://timestamp.digicert.com /td sha256 /d TaskbarSpeedTest $f
        if ($LASTEXITCODE -ne 0) { throw "signing failed: $f" }
    }
}
finally {
    Remove-Item $pfx -ErrorAction SilentlyContinue
}
