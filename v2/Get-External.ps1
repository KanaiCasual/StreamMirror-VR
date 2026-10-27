# Fetches the third-party files the 2.0 build needs but does not keep in the repository (only their licences are kept):
#   external\libobs\      OBS Studio's public headers, tag 32.1.2 (GPL-2)   - to build the OBS plugin
#   external\libobs-lib\  an import library made from the INSTALLED obs.dll  - no download
#   external\openxr-sdk\  the OpenXR headers, release 1.1.43 (Apache-2.0) - for the layer and its test
#   external\openvr\      Valve's OpenVR SDK v2.15.6: header, import library, redistributable DLL (BSD-3) - for the OpenVR helper
#   external\qvf-deps\    what the quad views layer is built against: fmt, OpenXR SDK headers, XrUtility, WIL
# Needs the GitHub CLI (gh) and Visual Studio 2022. Nothing is executed or installed.
$ErrorActionPreference = 'Stop'
$root = Join-Path $PSScriptRoot 'external'
New-Item -ItemType Directory -Force "$root\libobs", "$root\libobs-lib", "$root\openvr", "$root\openxr-sdk\openxr" | Out-Null

function Fetch($repo, $ref, $path, $target) {
    New-Item -ItemType Directory -Force (Split-Path $target) | Out-Null
    gh api "repos/$repo/contents/$path`?ref=$ref" -H 'Accept: application/vnd.github.raw' > $target
    if ($LASTEXITCODE -ne 0) { throw "could not fetch $repo $ref $path" }
}

if (-not (Test-Path "$root\openxr-sdk\openxr\openxr.h")) {
    # The OpenXR headers (Apache-2.0 / MIT), release 1.1.43: for the layer and its offline test.
    foreach ($file in 'openxr.h', 'openxr_platform.h', 'openxr_platform_defines.h', 'openxr_loader_negotiation.h', 'openxr_reflection.h', 'openxr_reflection_structs.h', 'openxr_reflection_parent_structs.h') {
        Fetch 'KhronosGroup/OpenXR-SDK' 'release-1.1.43' "include/openxr/$file" (Join-Path "$root\openxr-sdk\openxr" $file)
    }
    Fetch 'KhronosGroup/OpenXR-SDK' 'release-1.1.43' 'LICENSE' "$root\openxr-sdk\LICENSE"
    'OpenXR headers fetched.'
}
if (-not (Test-Path "$root\openvr\openvr.h")) {
    foreach ($file in 'headers/openvr.h', 'lib/win64/openvr_api.lib', 'bin/win64/openvr_api.dll', 'LICENSE') {
        Fetch 'ValveSoftware/openvr' 'v2.15.6' $file (Join-Path "$root\openvr" (Split-Path $file -Leaf))
    }
    'OpenVR SDK fetched.'
}

if (-not (Test-Path "$root\libobs\obs-module.h")) {
    $tree = gh api 'repos/obsproject/obs-studio/git/trees/32.1.2?recursive=1' --jq '.tree[] | select(.path | startswith("libobs/")) | select((.path | endswith(".h")) or (.path | endswith(".hpp"))) | .path'
    foreach ($path in $tree) { Fetch 'obsproject/obs-studio' '32.1.2' $path (Join-Path $root ($path -replace '/', '\')) }
    Fetch 'obsproject/obs-studio' '32.1.2' 'COPYING' "$root\libobs\COPYING"
    "libobs headers fetched ($($tree.Count) files)."
}

if (-not (Test-Path "$root\libobs-lib\obs.lib")) {
    $obs = 'C:\Program Files\obs-studio\bin\64bit\obs.dll'
    if (-not (Test-Path $obs)) { throw "OBS Studio is not installed at $obs" }
    $vs = & "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe" -latest -property installationPath
    $bin = (Get-ChildItem "$vs\VC\Tools\MSVC" -Recurse -Filter dumpbin.exe | Where-Object { $_.FullName -match 'Hostx64\\x64' } | Select-Object -First 1).DirectoryName
    $names = & "$bin\dumpbin.exe" /exports $obs | Where-Object { $_ -match '^\s+\d+\s+[0-9A-F]+\s+[0-9A-F]{8}\s+(\S+)' } | ForEach-Object { $Matches[1] }
    "LIBRARY obs.dll`r`nEXPORTS`r`n" + ($names -join "`r`n") | Set-Content "$root\libobs-lib\obs.def" -Encoding ascii
    & "$bin\lib.exe" /nologo /machine:x64 /def:"$root\libobs-lib\obs.def" /out:"$root\libobs-lib\obs.lib" | Out-Null
    "obs.lib made from the installed OBS ($($names.Count) exports)."
}

if (-not (Test-Path "$root\qvf-deps\wil\include\wil\resource.h")) {
    # What the quad views layer (v2\quad-views, built on Quad-Views-Foveated) is built against, at the versions upstream
    # pins: fmt (MIT), the OpenXR SDK headers and loader_interfaces.h (Apache-2.0), Microsoft's XrUtility headers (MIT),
    # and WIL (MIT) from its NuGet package. Downloaded straight to files: PowerShell's > would turn them into UTF-16.
    $deps = Join-Path $root 'qvf-deps'
    function Raw($repo, $sha, $path, $target) {
        New-Item -ItemType Directory -Force (Split-Path $target) | Out-Null
        Invoke-WebRequest "https://raw.githubusercontent.com/$repo/$sha/$path" -OutFile $target -UseBasicParsing
    }
    function Tree($repo, $sha, $prefix) {
        gh api "repos/$repo/git/trees/$sha`?recursive=1" --jq '.tree[] | select(.type == \"blob\") | .path' | Where-Object { $_.StartsWith($prefix) }
    }
    $fmt = 'f5e54359df4c26b6230fc61d38aa294581393084'
    foreach ($path in (Tree 'fmtlib/fmt' $fmt 'include/fmt/') + 'src/format.cc' + 'LICENSE.rst') { Raw 'fmtlib/fmt' $fmt $path "$deps\fmt\$path" }
    $sdk = '58a00cf85c39ad5ec4dc43a769624e420c06179a'
    foreach ($path in (Tree 'KhronosGroup/OpenXR-SDK' $sdk 'include/openxr/') + 'src/common/loader_interfaces.h' + 'LICENSE') { Raw 'KhronosGroup/OpenXR-SDK' $sdk $path "$deps\openxr-sdk\$path" }
    $mr = 'ef759465acd79e117c2650ea8eb32a4d31a6b675'
    foreach ($path in (Tree 'microsoft/OpenXR-MixedReality' $mr 'shared/XrUtility/') + 'shared/SampleShared/ScopeGuard.h' + 'LICENSE') { Raw 'microsoft/OpenXR-MixedReality' $mr $path "$deps\xr-utility\$path" }
    $package = Join-Path $env:TEMP 'wil.1.0.220201.1.zip'
    Invoke-WebRequest 'https://www.nuget.org/api/v2/package/Microsoft.Windows.ImplementationLibrary/1.0.220201.1' -OutFile $package -UseBasicParsing
    $unpacked = Join-Path $env:TEMP 'wil.1.0.220201.1'
    Expand-Archive $package $unpacked -Force
    New-Item -ItemType Directory -Force "$deps\wil" | Out-Null
    Copy-Item "$unpacked\include" "$deps\wil\include" -Recurse -Force
    Copy-Item "$unpacked\LICENSE" "$deps\wil\LICENSE" -Force
    Remove-Item $package, $unpacked -Recurse -Force
    'Quad views layer dependencies fetched.'
}
'External files are in place.'
