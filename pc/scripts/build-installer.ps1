param(
  [string]$Configuration = "Release",
  [string]$Runtime = "win-x64",
  [string]$Version = "1.0.0",
  [string]$PublishDir = "",
  [string]$InstallerScript = "",
  [switch]$Sign,
  [switch]$SelfSign,
  [string]$SelfSignPassword = "",
  [string]$SelfSignSubject = "CN=PhoneShell Dev Code Signing",
  [string]$CertPath = $env:PHONESHELL_CERT_PFX,
  [string]$CertPassword = $env:PHONESHELL_CERT_PASSWORD,
  [string]$TimeStampUrl = $env:PHONESHELL_TIMESTAMP_URL
)

$ErrorActionPreference = "Stop"

function Find-Iscc {
  $cmd = Get-Command iscc -ErrorAction SilentlyContinue
  if ($cmd) { return $cmd.Source }

  $candidates = @(
    (Join-Path $env:LOCALAPPDATA "Programs\\Inno Setup 6\\ISCC.exe"),
    (Join-Path ${env:ProgramFiles(x86)} "Inno Setup 6\\ISCC.exe"),
    (Join-Path $env:ProgramFiles "Inno Setup 6\\ISCC.exe")
  )

  foreach ($path in $candidates) {
    if ($path -and (Test-Path $path)) { return $path }
  }

  return $null
}

function Find-Signtool {
  $cmd = Get-Command signtool -ErrorAction SilentlyContinue
  if ($cmd) { return $cmd.Source }

  $binRoot = Join-Path ${env:ProgramFiles(x86)} "Windows Kits\\10\\bin"
  if (Test-Path $binRoot) {
    $candidates = Get-ChildItem $binRoot -Filter signtool.exe -Recurse -ErrorAction SilentlyContinue |
      Where-Object { $_.FullName -match "\\\\x64\\\\signtool\\.exe$" } |
      Sort-Object FullName -Descending
    if ($candidates.Count -gt 0) { return $candidates[0].FullName }
  }

  $ack = Join-Path ${env:ProgramFiles(x86)} "Windows Kits\\10\\App Certification Kit\\signtool.exe"
  if (Test-Path $ack) { return $ack }

  return $null
}

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$project = Join-Path $repoRoot "src\PhoneShell.App\PhoneShell.App.csproj"
$installerScriptPath = if ($InstallerScript) { $InstallerScript } else { Join-Path $repoRoot "installer\PhoneShell.iss" }
$publishRoot = if ($PublishDir) { $PublishDir } else { Join-Path $repoRoot "installer\publish" }
$publishRoot = (New-Item -ItemType Directory -Force $publishRoot).FullName

$version4 = if ($Version -match '^[0-9]+\.[0-9]+\.[0-9]+$') { "$Version.0" } else { $Version }
$selfSignThumbprint = $null

$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
if (-not $dotnet) {
  throw "dotnet not found. Please install .NET SDK 8.0+"
}

Write-Host "Publishing to $publishRoot"
& $dotnet.Source publish $project -c $Configuration -r $Runtime --self-contained true -p:PublishSingleFile=false -p:DebugType=None -p:Version=$Version -p:InformationalVersion=$Version -p:AssemblyVersion=$version4 -p:FileVersion=$version4 -o $publishRoot

# Scrub runtime data/logs to avoid shipping local secrets or device info.
$scrubTargets = @(
  (Join-Path $publishRoot "data")
)
foreach ($target in $scrubTargets) {
  if (Test-Path $target) {
    Remove-Item -Recurse -Force $target
  }
}
Get-ChildItem $publishRoot -Filter *.log -Recurse -ErrorAction SilentlyContinue | Remove-Item -Force

if ($SelfSign.IsPresent) {
  $certDir = Join-Path $repoRoot "installer\certs"
  New-Item -ItemType Directory -Force $certDir | Out-Null

  if ([string]::IsNullOrWhiteSpace($CertPath)) {
    $CertPath = Join-Path $certDir "PhoneShell-Dev-CodeSign.pfx"
  }

  if ([string]::IsNullOrWhiteSpace($CertPassword)) {
    if ([string]::IsNullOrWhiteSpace($SelfSignPassword)) {
      $SelfSignPassword = [Guid]::NewGuid().ToString("N")
      Write-Host "Self-sign password (generated): $SelfSignPassword"
    }
    $CertPassword = $SelfSignPassword
  }

  $cerPath = Join-Path $certDir "PhoneShell-Dev-CodeSign.cer"
  $cert = Get-ChildItem Cert:\CurrentUser\My | Where-Object { $_.Subject -eq $SelfSignSubject } |
    Sort-Object NotAfter -Descending | Select-Object -First 1

  if (-not $cert) {
    $newSelfSigned = Get-Command New-SelfSignedCertificate -ErrorAction SilentlyContinue
    if (-not $newSelfSigned) {
      throw "New-SelfSignedCertificate not found. Run on Windows PowerShell 5.1+."
    }

    Write-Host "Creating self-signed code signing certificate..."
    $cert = New-SelfSignedCertificate -Type CodeSigningCert -Subject $SelfSignSubject -KeyAlgorithm RSA -KeyLength 2048 -HashAlgorithm SHA256 -CertStoreLocation "Cert:\CurrentUser\My"
  }

  if (-not (Test-Path $CertPath)) {
    $securePassword = ConvertTo-SecureString -String $CertPassword -AsPlainText -Force
    Export-PfxCertificate -Cert $cert -FilePath $CertPath -Password $securePassword | Out-Null
  }

  if (-not (Test-Path $cerPath)) {
    Export-Certificate -Cert $cert -FilePath $cerPath | Out-Null
  }

  $selfSignThumbprint = $cert.Thumbprint

  Write-Host "Self-signed PFX: $CertPath"
  Write-Host "Self-signed CER: $cerPath"
}

$shouldSign = $Sign.IsPresent -or $SelfSign.IsPresent -or [string]::IsNullOrWhiteSpace($CertPath) -eq $false
if ($shouldSign) {
  $signtoolPath = Find-Signtool
  if (-not $signtoolPath) {
    throw "signtool not found. Install Windows SDK or provide signtool in PATH."
  }
  if (-not $SelfSign.IsPresent -and ([string]::IsNullOrWhiteSpace($CertPath) -or [string]::IsNullOrWhiteSpace($CertPassword))) {
    throw "Signing requested but PHONESHELL_CERT_PFX / PHONESHELL_CERT_PASSWORD not set."
  }

  $appExe = Join-Path $publishRoot "PhoneShell.App.exe"
  if (-not (Test-Path $appExe)) {
    throw "Expected app exe not found: $appExe"
  }

  $timestampArgs = @()
  if (-not [string]::IsNullOrWhiteSpace($TimeStampUrl)) {
    $timestampArgs = @("/tr", $TimeStampUrl, "/td", "sha256")
  }

  $signArgs = @("/fd", "sha256")
  if ($timestampArgs.Count -gt 0) { $signArgs += $timestampArgs }
  if ($SelfSign.IsPresent) {
    if ([string]::IsNullOrWhiteSpace($selfSignThumbprint)) {
      throw "Self-sign enabled but certificate thumbprint not found."
    }
    $signArgs += @("/sha1", $selfSignThumbprint)
  } else {
    $signArgs += @("/f", $CertPath, "/p", $CertPassword)
  }

  Write-Host "Signing app: $appExe"
  & $signtoolPath sign @signArgs $appExe
  if ($LASTEXITCODE -ne 0) { throw "signtool failed when signing app (exit $LASTEXITCODE)." }
}

$isccPath = Find-Iscc
if (-not $isccPath) {
  throw "iscc not found. Please install Inno Setup 6 and add it to PATH."
}

Write-Host "Building installer"
& $isccPath "/DAppVersion=$Version" "/DPublishDir=$publishRoot" $installerScriptPath

$installerOutDir = Join-Path (Split-Path -Parent $installerScriptPath) "out"
$installerPath = Join-Path $installerOutDir "PhoneShell-Setup-$Version.exe"

if ($shouldSign) {
  if (-not (Test-Path $installerPath)) {
    throw "Installer not found: $installerPath"
  }

  Write-Host "Signing installer: $installerPath"
  $signtoolPath = Find-Signtool
  $timestampArgs = @()
  if (-not [string]::IsNullOrWhiteSpace($TimeStampUrl)) {
    $timestampArgs = @("/tr", $TimeStampUrl, "/td", "sha256")
  }
  $signArgs = @("/fd", "sha256")
  if ($timestampArgs.Count -gt 0) { $signArgs += $timestampArgs }
  if ($SelfSign.IsPresent) {
    if ([string]::IsNullOrWhiteSpace($selfSignThumbprint)) {
      throw "Self-sign enabled but certificate thumbprint not found."
    }
    $signArgs += @("/sha1", $selfSignThumbprint)
  } else {
    $signArgs += @("/f", $CertPath, "/p", $CertPassword)
  }
  & $signtoolPath sign @signArgs $installerPath
  if ($LASTEXITCODE -ne 0) { throw "signtool failed when signing installer (exit $LASTEXITCODE)." }
}

Write-Host "Done. Output: $installerPath"
