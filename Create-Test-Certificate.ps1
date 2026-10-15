$ErrorActionPreference = 'Stop'
$subject = 'CN=WifiGuardian.Dev'
$pfxPath = Join-Path $PSScriptRoot 'WifiGuardian.Package\WifiGuardian_TemporaryKey.pfx'
$cerPath = Join-Path $PSScriptRoot 'WifiGuardian.Package\WifiGuardian_TemporaryKey.cer'
$packageDir = Split-Path $pfxPath -Parent
New-Item -ItemType Directory -Force -Path $packageDir | Out-Null

$password = ConvertTo-SecureString -String 'DevOnly-WifiGuardian-2026' -Force -AsPlainText
$cert = New-SelfSignedCertificate -Type Custom -Subject $subject -FriendlyName 'WiFi Guardian local development certificate' `
    -CertStoreLocation 'Cert:\CurrentUser\My' -KeyAlgorithm RSA -KeyLength 2048 -HashAlgorithm SHA256 `
    -KeyUsage DigitalSignature -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3')
Export-PfxCertificate -Cert $cert -FilePath $pfxPath -Password $password | Out-Null
Export-Certificate -Cert $cert -FilePath $cerPath | Out-Null
Import-Certificate -FilePath $cerPath -CertStoreLocation 'Cert:\CurrentUser\TrustedPeople' | Out-Null
Write-Host 'Certificado de desarrollo creado e instalado para el usuario actual.' -ForegroundColor Green
Write-Host "PFX: $pfxPath"
Write-Host 'Se usa solo para compilar e instalar esta app localmente; no es un certificado comercial.'
