# Creates the self-signed Imvello code-signing certificate (run once per machine).
$ErrorActionPreference = 'Stop'

$existing = Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert |
    Where-Object { $_.Subject -eq 'CN=Imvello' -and $_.NotAfter -gt (Get-Date) } |
    Sort-Object NotAfter -Descending | Select-Object -First 1

if ($existing) {
    $cert = $existing
    Write-Host "Reusing existing code-signing cert: $($cert.Thumbprint)"
} else {
    $cert = New-SelfSignedCertificate -Type CodeSigningCert -Subject 'CN=Imvello' `
        -HashAlgorithm SHA256 -KeyAlgorithm RSA -KeyLength 3072 `
        -KeyUsage DigitalSignature -KeyExportPolicy Exportable `
        -NotAfter (Get-Date).AddYears(5) -CertStoreLocation 'Cert:\CurrentUser\My'
    Write-Host "Created code-signing cert: $($cert.Thumbprint)"
}

$cerPath = Join-Path $PSScriptRoot 'ImvelloCodeSigning.cer'
Export-Certificate -Cert $cert -FilePath $cerPath | Out-Null
Write-Host "Public cert exported to: $cerPath"
Write-Host "Thumbprint: $($cert.Thumbprint)"
