<#
.SYNOPSIS
    Creates a self-signed code-signing certificate for sideloading the MSIX, and optionally trusts
    it on this machine.

.DESCRIPTION
    Windows installs an MSIX only if it is signed by a certificate the machine trusts. With no
    company code-signing certificate at hand, a self-signed one is enough for an internal wall: sign
    with it here, and import it into Local Machine\Trusted People on every machine that installs the
    package.

    The subject has to match Identity/@Publisher in the package manifest exactly, so the same value
    is passed to both this script and build-msix.ps1.

.PARAMETER Publisher
    Certificate subject, e.g. 'CN=SMIC'. Pass the same value to build-msix.ps1 -Publisher.

.PARAMETER Trust
    Also import the public part into Local Machine\Trusted People. Needs an elevated shell.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File packaging\New-DevCertificate.ps1 -Password 'pw' -Trust
#>
[CmdletBinding()]
param(
    [string] $Publisher = 'CN=SMIC',
    [string] $OutputPath,
    [Parameter(Mandatory = $true)][string] $Password,
    [int] $ValidYears = 3,
    [switch] $Trust
)

$ErrorActionPreference = 'Stop'
if (-not $OutputPath) { $OutputPath = Join-Path $PSScriptRoot 'GridDisplay.pfx' }

Write-Host "Creating a code-signing certificate for $Publisher ..."
$certificate = New-SelfSignedCertificate `
    -Type Custom `
    -Subject $Publisher `
    -KeyUsage DigitalSignature `
    -KeyAlgorithm RSA `
    -KeyLength 2048 `
    -FriendlyName 'Grid Display (sideload signing)' `
    -CertStoreLocation 'Cert:\CurrentUser\My' `
    -NotAfter (Get-Date).AddYears($ValidYears) `
    -TextExtension @(
        # Extended Key Usage = Code Signing. Without it signtool refuses the certificate.
        '2.5.29.37={text}1.3.6.1.5.5.7.3.3',
        # Basic Constraints: not a CA.
        '2.5.29.19={text}'
    )

Write-Host "  thumbprint    $($certificate.Thumbprint)"

$securePassword = ConvertTo-SecureString -String $Password -Force -AsPlainText
Export-PfxCertificate -Cert $certificate -FilePath $OutputPath -Password $securePassword | Out-Null
Write-Host "  pfx           $OutputPath"

$cerPath = [IO.Path]::ChangeExtension($OutputPath, '.cer')
Export-Certificate -Cert $certificate -FilePath $cerPath | Out-Null
Write-Host "  public cert   $cerPath"

if ($Trust) {
    Write-Host 'Importing into Local Machine\Trusted People ...'
    Import-Certificate -FilePath $cerPath -CertStoreLocation 'Cert:\LocalMachine\TrustedPeople' | Out-Null
    Write-Host '  trusted on this machine.'
}

Write-Host @"

Keep the .pfx out of the repository - it is a signing key.

Sign a package with it:
  packaging\build-msix.ps1 -Publisher '$Publisher' -CertificateThumbprint $($certificate.Thumbprint)

Trust it on a wall (elevated, once per machine):
  Import-Certificate -FilePath .\$([IO.Path]::GetFileName($cerPath)) -CertStoreLocation Cert:\LocalMachine\TrustedPeople
"@
