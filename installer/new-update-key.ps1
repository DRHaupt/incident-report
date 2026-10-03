<#
  Makes the Field Suite release signing key (run once, on any Windows PC; no .NET SDK needed).

  The private key is copied to the clipboard. Paste it into GitHub:
    repository > Settings > Secrets and variables > Actions > New repository secret
    Name: UPDATE_SIGNING_KEY   Secret: (paste)
  Then don't keep it anywhere else (clear the clipboard). From the next build on, every release is signed and
  every installed copy only accepts updates signed with this key.

  Keep the key secret: whoever has it can sign updates the apps will accept. If it is lost, make a new one the
  same way; installations then need one manual install of a release signed with the new key.
#>
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSVersion.Major -ge 7) {
    $key = [System.Security.Cryptography.ECDsa]::Create([System.Security.Cryptography.ECCurve+NamedCurves]::nistP256)
    $pkcs8 = $key.ExportPkcs8PrivateKey()
} else {
    $params = New-Object System.Security.Cryptography.CngKeyCreationParameters
    $params.ExportPolicy = [System.Security.Cryptography.CngExportPolicies]::AllowPlaintextExport
    $key = [System.Security.Cryptography.CngKey]::Create([System.Security.Cryptography.CngAlgorithm]::ECDsaP256, $null, $params)
    $pkcs8 = $key.Export([System.Security.Cryptography.CngKeyBlobFormat]::Pkcs8PrivateBlob)
}
[Convert]::ToBase64String($pkcs8) | Set-Clipboard
Write-Host "New signing key copied to the clipboard." -ForegroundColor Green
Write-Host "Paste it as the GitHub Actions secret UPDATE_SIGNING_KEY, then clear the clipboard."
