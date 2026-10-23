# Builds artifacts\Imvello-Setup.exe
#   powershell -NoProfile -ExecutionPolicy Bypass -File installer\build-installer.ps1
$ErrorActionPreference = 'Stop'
$root      = Split-Path -Parent $PSScriptRoot
$project   = Join-Path $root 'src\Imvello\Imvello.csproj'
$publish   = Join-Path $root 'artifacts\Imvello-win-x64'
$buildDir  = Join-Path $PSScriptRoot 'build'
$iss       = Join-Path $PSScriptRoot 'Imvello-Setup.iss'
$cerPath   = Join-Path $PSScriptRoot 'ImvelloCodeSigning.cer'

# Authenticode signing (removes "Unknown publisher"). Uses the CN=Imvello
# code-signing cert in the user store; run installer\create-signing-cert.ps1 once to create it.
$sdkBin = "${env:ProgramFiles(x86)}\Windows Kits\10\bin"
$signTool = Get-ChildItem $sdkBin -Recurse -Filter 'signtool.exe' -ErrorAction SilentlyContinue |
    Where-Object { $_.Directory.Name -eq 'x64' } |
    Sort-Object FullName | Select-Object -Last 1 -ExpandProperty FullName
$signCert = Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert |
    Where-Object { $_.Subject -eq 'CN=Imvello' -and $_.NotAfter -gt (Get-Date) } |
    Sort-Object NotAfter -Descending | Select-Object -First 1
function Sign-File([string]$path) {
    if (-not $signTool -or -not $signCert) {
        Write-Warning "Skipping signing of '$(Split-Path $path -Leaf)': signtool or CN=Imvello cert not found (run installer\create-signing-cert.ps1)."
        return
    }
    & $signTool sign /fd SHA256 /sha1 $signCert.Thumbprint /tr http://timestamp.digicert.com /td SHA256 $path
    if ($LASTEXITCODE -ne 0) { throw "signtool failed for $path ($LASTEXITCODE)" }
}

# 1. Publish self-contained single-file exe (settings come from the Release group in Imvello.csproj)
if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
dotnet publish $project -c Release -o $publish
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }

# 1b. Sign the app exe before the installer packs it
Sign-File (Join-Path $publish 'Imvello.exe')

# 2. Generate wizard images (BMP) from the app logo
Add-Type -AssemblyName System.Drawing
New-Item -ItemType Directory -Force $buildDir | Out-Null
$logo = [System.Drawing.Image]::FromFile((Join-Path $root 'src\Imvello\Assets\Imvello.png'))
function New-WizardImage([int]$w, [int]$h, [string]$name, [bool]$large) {
    $bmp = New-Object System.Drawing.Bitmap $w, $h, ([System.Drawing.Imaging.PixelFormat]::Format24bppRgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'; $g.InterpolationMode = 'HighQualityBicubic'; $g.TextRenderingHint = 'AntiAliasGridFit'
    if ($large) {
        $rect = New-Object System.Drawing.Rectangle 0, 0, $w, $h
        $grad = New-Object System.Drawing.Drawing2D.LinearGradientBrush $rect, ([System.Drawing.Color]::FromArgb(0x0F,0x6C,0xBD)), ([System.Drawing.Color]::FromArgb(0x0A,0x3D,0x73)), 90
        $g.FillRectangle($grad, $rect)
        $s = [int]($w * 0.55)
        $g.DrawImage($logo, [int](($w - $s) / 2), [int]($h * 0.22), $s, $s)
        $font = New-Object System.Drawing.Font 'Segoe UI Semibold', ([single]($w * 0.13)), ([System.Drawing.FontStyle]::Regular), ([System.Drawing.GraphicsUnit]::Pixel)
        $fmt = New-Object System.Drawing.StringFormat; $fmt.Alignment = 'Center'
        $g.DrawString('Imvello', $font, [System.Drawing.Brushes]::White, (New-Object System.Drawing.RectangleF 0, ([single]($h * 0.22 + $s + $h * 0.04)), $w, ([single]($h * 0.2))), $fmt)
    } else {
        $g.Clear([System.Drawing.Color]::White)
        $pad = [int]($w * 0.06)
        $g.DrawImage($logo, $pad, $pad, $w - 2 * $pad, $h - 2 * $pad)
    }
    $g.Dispose()
    $bmp.Save((Join-Path $buildDir $name), [System.Drawing.Imaging.ImageFormat]::Bmp)
    $bmp.Dispose()
}
New-WizardImage 164 314 'wizard-large-100.bmp' $true
New-WizardImage 328 628 'wizard-large-200.bmp' $true
New-WizardImage 55 55 'wizard-small-100.bmp' $false
New-WizardImage 110 110 'wizard-small-200.bmp' $false
$logo.Dispose()

# 3. Compile the installer
$iscc = @(
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw 'Inno Setup 6 (ISCC.exe) not found. Install it from https://jrsoftware.org/isdl.php' }
& $iscc $iss
if ($LASTEXITCODE -ne 0) { throw "ISCC failed ($LASTEXITCODE)" }

# 4. Sign the installer
Sign-File (Join-Path $root 'artifacts\Imvello-Setup.exe')

Write-Host "`nInstaller: $(Join-Path $root 'artifacts\Imvello-Setup.exe')" -ForegroundColor Green
