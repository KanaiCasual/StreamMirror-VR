# Builds dist\StreamMirror-VR-<version>.msi: the gaze mirror layer, the SteamVR helper, the OBS plugin, the mirror
# window and the settings app (all from v2\), plus the official Quad-Views-Foveated installer as an optional extra.
# Prerequisites: Visual Studio 2022 (C++), the .NET 10 SDK, the GitHub CLI for v2\Get-External.ps1 (first time only),
# and internet access the first time (the WiX toolset comes from nuget.org as part of the build).
#
#   .\Build-Installer.ps1 -Version 2.2.1                 a normal build
#   .\Build-Installer.ps1 -Version 2.2.1 -Label beta.1   a beta: the app calls itself 2.2.1-beta.1, the MSI is 2.2.1
#   .\Build-Installer.ps1 -ListSigningCertificates       the code-signing certificates Windows holds, with thumbprints
#   .\Build-Installer.ps1 -Version 2.2.1 -SignThumbprint <thumbprint>   signed with one of them (Certum: sign in to
#                                                        SimplySign Desktop first)
#
# Every published build - beta or not - needs its own x.y.z: Windows Installer only upgrades to a higher number, and the
# app's update check compares those numbers. Whether a release counts as a beta is decided by GitHub's "pre-release" tick.
param(
    [string]$Version = '3.0.0',
    [string]$Label = '',
    # owner/name of the repository whose releases the app's update check looks at. '' = build without update checks.
    [string]$GitHubRepository = 'KanaiCasual/StreamMirror-VR',
    # A .pfx to sign with (with its password): every file of ours and the MSI. '' = unsigned. For a test certificate, see
    # tools\New-TestSigningCertificate.ps1.
    [string]$SignCertificate = '',
    [string]$SignPassword = '',
    # Or a certificate Windows holds for this user (Cert:\CurrentUser\My), by its thumbprint: a Certum certificate through
    # SimplySign Desktop, whose key never leaves Certum, shows up there while SimplySign Desktop is signed in.
    [string]$SignThumbprint = '',
    # The RFC 3161 timestamp server. '' = Certum's with -SignThumbprint, DigiCert's with -SignCertificate.
    [string]$TimestampUrl = '',
    # Lists the code-signing certificates Windows holds for this user (thumbprint, name, expiry), and builds nothing.
    [switch]$ListSigningCertificates,
    # Also writes the same MSI as VR-Gaze-Mirror-<version>.msi (with its .sha256): the name the update button of the 2.x
    # versions looks for, so they can update to 3.x in one click. Upload both to the release.
    [switch]$AlsoOldName
)

$ErrorActionPreference = 'Stop'

# Code-signing certificates of this user: valid now, for code signing, with their key at hand.
function Get-SigningCertificates {
    Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert | Where-Object { $_.HasPrivateKey -and $_.NotAfter -gt (Get-Date) }
}
if ($ListSigningCertificates) {
    $found = @(Get-SigningCertificates)
    if ($found.Count -eq 0) { 'No code-signing certificate with its key in Cert:\CurrentUser\My. (Certum: is SimplySign Desktop signed in?)' }
    $found | ForEach-Object { '{0}  {1}  (until {2:yyyy-MM-dd})' -f $_.Thumbprint, $_.Subject, $_.NotAfter }
    return
}
if ($SignThumbprint -and $SignCertificate) { throw 'Sign with -SignThumbprint or with -SignCertificate, not both.' }
if ($SignThumbprint) {
    # Checked now, not after a five-minute build.
    $SignThumbprint = $SignThumbprint -replace '[^0-9A-Fa-f]', ''
    if (-not (Get-SigningCertificates | Where-Object Thumbprint -eq $SignThumbprint)) {
        throw "No usable code-signing certificate with thumbprint $SignThumbprint in Cert:\CurrentUser\My (it must be valid now and have its key at hand). Certum: sign in to SimplySign Desktop. -ListSigningCertificates shows what there is."
    }
}
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "-Version must be x.y.z (got '$Version'). Put 'beta.1' etc. in -Label." }
$root = $PSScriptRoot
$dist = Join-Path $root 'dist'
$appOut = Join-Path $dist 'app'
$appVersion = if ($Label) { "$Version-$Label" } else { $Version }

# 1) Third-party files (fetched once) and every 2.0 binary, with the offline layer test.
& (Join-Path $root 'v2\Get-External.ps1') | Out-Null
& (Join-Path $root 'v2\Build-Dev.ps1') | Out-Null

# 2) Layer, OBS plugin, Quad-Views-Foveated installer and licences -> StreamMirrorApp\Payload
& (Join-Path $root 'StreamMirrorApp\Collect-Payload.ps1') | Out-Null

# 3) Settings app, self-contained, as a plain folder (the MSI installs it).
if (Test-Path $appOut) { Remove-Item $appOut -Recurse -Force }
dotnet publish (Join-Path $root 'StreamMirrorApp\StreamMirrorApp.csproj') -c Release -o $appOut -v q --nologo `
    "-p:Version=$appVersion" "-p:GitHubRepository=$GitHubRepository"
if ($LASTEXITCODE -ne 0) { throw "Publishing the settings app failed." }

# 3b) The mirror window and the SteamVR helper, next to the settings app (the app finds them there).
Copy-Item (Join-Path $root 'v2\bin\mirror-window\MirrorWindow.exe') $appOut -Force
Copy-Item (Join-Path $root 'v2\bin\helper\StreamMirrorHelper.exe') $appOut -Force
Copy-Item (Join-Path $root 'v2\bin\helper\openvr_api.dll') $appOut -Force
# The helper's SteamVR manifest (relative binary path): shipped next to the exe, so an uninstall takes it away too.
Copy-Item (Join-Path $root 'v2\openvr-helper\StreamMirrorHelper.vrmanifest') $appOut -Force

# 3c) Signing: every program file with no signature yet - ours - with SHA-256 and a timestamp. Files that come signed
# (.NET, Microsoft, Valve, the Quad-Views-Foveated installer) keep their own signature.
$signWith = if ($SignThumbprint) { @('/sha1', $SignThumbprint) } elseif ($SignCertificate) { @('/f', $SignCertificate, '/p', $SignPassword) } else { @() }
$timestamp = if ($TimestampUrl) { $TimestampUrl } elseif ($SignThumbprint) { 'http://time.certum.pl' } else { 'http://timestamp.digicert.com' }
function Sign([string[]]$files) {
    # /d and /du: the name and link Windows' permission prompt shows. Without them it shows the file's name, and for an
    # installer that is the random name of Windows Installer's own copy (ec5b44.msi).
    & $signtool sign /q /fd SHA256 @signWith /tr $timestamp /td SHA256 /d 'StreamMirror VR' /du 'https://github.com/KanaiCasual/StreamMirror-VR' $files
    if ($LASTEXITCODE -ne 0) { throw "Signing failed: $files" }
    # What Windows makes of each: Valid with a certificate it trusts; signed but untrusted (a test certificate) warns.
    foreach ($file in $files) {
        $signature = Get-AuthenticodeSignature $file
        if ($signature.Status -eq 'Valid') { "signed: $(Split-Path $file -Leaf) - valid, timestamped $([bool]$signature.TimeStamperCertificate)" }
        elseif ($signature.SignerCertificate) { Write-Warning "signed, but this PC does not trust the certificate ($($signature.Status)): $(Split-Path $file -Leaf)" }
        else { throw "Not signed after signing: $file" }
    }
}
if ($signWith) {
    $signtool = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\signtool.exe" | Sort-Object FullName -Descending | Select-Object -First 1
    if (-not $signtool) { throw 'No signtool.exe: install the Windows SDK.' }
    $unsigned = Get-ChildItem (Join-Path $root 'StreamMirrorApp\Payload'), $appOut -Recurse -Include *.exe, *.dll |
        Where-Object { (Get-AuthenticodeSignature $_.FullName).Status -eq 'NotSigned' }
    Sign $unsigned.FullName
}

# 4) The MSI.
$payload = Join-Path $root 'StreamMirrorApp\Payload'
dotnet build (Join-Path $root 'Installer\StreamMirror.Installer.wixproj') -c Release -v q --nologo `
    "-p:ProductVersion=$Version" "-p:PayloadDir=$payload" "-p:AppDir=$appOut"
if ($LASTEXITCODE -ne 0) { throw "Building the MSI failed." }

$msi = Get-ChildItem (Join-Path $root 'Installer\bin') -Recurse -Filter 'StreamMirror-VR.msi' |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1
$target = Join-Path $dist "StreamMirror-VR-$appVersion.msi"
Copy-Item $msi.FullName $target -Force
if ($signWith) { Sign $target }
# The checksum file the app's updater checks a download against. Upload it with the .msi to every release
# (gh release create vX.Y.Z dist\<msi> dist\<msi>.sha256 ...); without it the app only links to the release page.
$hash = (Get-FileHash $target -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText("$target.sha256", "$hash  $(Split-Path $target -Leaf)`n")
'{0}  ({1:N1} MB)  SHA-256 {2}' -f $target, ((Get-Item $target).Length / 1MB), $hash
if ($AlsoOldName) {
    $oldName = Join-Path $dist "VR-Gaze-Mirror-$appVersion.msi"
    Copy-Item $target $oldName -Force
    [IO.File]::WriteAllText("$oldName.sha256", "$hash  $(Split-Path $oldName -Leaf)`n")
    '{0}  (the same file, for the 2.x update button)' -f $oldName
}
