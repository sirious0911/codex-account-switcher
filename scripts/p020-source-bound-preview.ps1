param(
    [Parameter(Mandatory=$true)][string]$SourceRoot,
    [Parameter(Mandatory=$true)][string]$ExpectedCommit,
    [Parameter(Mandatory=$true)][string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path -LiteralPath $SourceRoot).Path
$head = (& git -C $root rev-parse HEAD).Trim().ToLowerInvariant()
if ($LASTEXITCODE -ne 0 -or $ExpectedCommit -cne $head) {
    throw 'HOLD_P020_SOURCE_COMMIT_MISMATCH'
}
# Build inputs extend beyond the Swift/WPF paths: the candidate also
# consumes Directory.Build.props, project references and this script.
# Guard the ENTIRE checkout to prevent source_commit claiming a binary
# built from uncommitted edits to another build input. CI's normal bin/obj
# and Windows artifacts remain covered by the repository's .gitignore.
$dirty = @(& git -C $root status --porcelain --untracked-files=all)
if ($LASTEXITCODE -ne 0) { throw 'HOLD_P020_GIT_STATUS_UNAVAILABLE' }
# Windows SwiftPM may remove the macOS-only Sparkle Package.resolved
# after tests; the existing package-windows.ps1 already documents that
# behavior. Recover ONLY an exact, sole tracked deletion from the
# committed index. Never accept a modified lockfile or any other dirty
# source, and never run this repair outside hosted CI.
if ($env:GITHUB_ACTIONS -ceq 'true' -and
    $dirty.Count -eq 1 -and $dirty[0] -ceq ' D Package.resolved') {
    $headLockBlob = (& git -C $root rev-parse 'HEAD:Package.resolved').Trim()
    if ($LASTEXITCODE -ne 0) { throw 'HOLD_P020_LOCKFILE_HEAD_MISSING' }
    $indexLockBlob = (& git -C $root rev-parse ':Package.resolved').Trim()
    if ($LASTEXITCODE -ne 0 -or $headLockBlob -cne $indexLockBlob) {
        throw 'HOLD_P020_LOCKFILE_INDEX_MISMATCH'
    }
    $lockPath = Join-Path $root 'Package.resolved'
    if (Test-Path -LiteralPath $lockPath) {
        throw 'HOLD_P020_LOCKFILE_NOT_ACTUALLY_MISSING'
    }
    & git -C $root checkout-index --force -- Package.resolved
    if ($LASTEXITCODE -ne 0) { throw 'HOLD_P020_LOCKFILE_RESTORE_FAILED' }
    $dirty = @(& git -C $root status --porcelain --untracked-files=all)
    if ($LASTEXITCODE -ne 0 -or $dirty.Count -ne 0) {
        throw 'HOLD_P020_LOCKFILE_RESTORE_NOT_CLEAN'
    }
    Write-Output "P020_EXACT_LOCKFILE_RESTORED_FROM_HEAD=$headLockBlob"
}
if ($dirty.Count -ne 0) {
    # CI runs several build/test steps before this provenance gate. Print
    # only the Git status codes and relative file names (never file
    # contents or environment secrets) to identify generated artifacts
    # without weakening the fail-closed source check.
    Write-Output "P020_DIRTY_ENTRY_COUNT=$($dirty.Count)"
    foreach ($line in @($dirty | Select-Object -First 20)) {
        Write-Output "P020_DIRTY_ENTRY=$line"
    }
    throw 'HOLD_P020_SOURCE_NOT_COMMITTED'
}
$artifacts = Join-Path $root 'windows/artifacts'
$zip = Join-Path $artifacts 'SwiftRuntime.zip'
$zipReceipt = Join-Path $artifacts 'SwiftRuntime.zip.sha256'
$hostReceipt = Join-Path $artifacts 'SwiftHost-source-commit.txt'
foreach ($file in @($zip, $zipReceipt, $hostReceipt)) {
    if (-not (Test-Path -LiteralPath $file -PathType Leaf)) {
        throw 'HOLD_P020_FRESH_CI_RUNTIME_RECEIPT_MISSING'
    }
}
$zipLine = (Get-Content -LiteralPath $zipReceipt -Raw).Trim()
$match = [regex]::Match($zipLine, '^([0-9a-f]{64})  SwiftRuntime\.zip$')
if (-not $match.Success) { throw 'HOLD_P020_ZIP_RECEIPT_INVALID' }
$zipSha = $match.Groups[1].Value
if ((Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant() -cne $zipSha) {
    throw 'HOLD_P020_RUNTIME_ZIP_SHA_MISMATCH'
}
$values = @{}
foreach ($line in @(Get-Content -LiteralPath $hostReceipt)) {
    $parsed = [regex]::Match($line, '^(source_commit|swifthost_sha256)=([0-9a-f]+)$')
    if (-not $parsed.Success -or $values.ContainsKey($parsed.Groups[1].Value)) {
        throw 'HOLD_P020_HOST_RECEIPT_INVALID'
    }
    $values[$parsed.Groups[1].Value] = $parsed.Groups[2].Value
}
if ($values.Count -ne 2 -or $values['source_commit'] -cne $head -or
    $values['swifthost_sha256'] -cnotmatch '^[0-9a-f]{64}$') {
    throw 'HOLD_P020_HOST_COMMIT_OR_HASH_INVALID'
}
Add-Type -AssemblyName System.IO.Compression
$archive = [IO.Compression.ZipFile]::OpenRead($zip)
try {
    $hostEntries = @($archive.Entries | Where-Object { $_.FullName -ceq 'SwitcherHost.exe' })
    if ($hostEntries.Count -ne 1) { throw 'HOLD_P020_SWIFTHOST_NOT_UNIQUE' }
    $stream = $hostEntries[0].Open()
    try {
        $hostSha = (Get-FileHash -InputStream $stream -Algorithm SHA256).Hash.ToLowerInvariant()
    } finally { $stream.Dispose() }
} finally { $archive.Dispose() }
if ($hostSha -cne $values['swifthost_sha256']) {
    throw 'HOLD_P020_SWIFTHOST_HASH_MISMATCH'
}
# The receipt and ZIP are produced by the same job, so matching those
# two ALONE does not prove the ZIP contains the SwiftHost compiled here.
# Independently compare the archive host bytes against the directly built
# release product from this checkout before accepting the preview.
$swift = Get-Command swift -ErrorAction SilentlyContinue | Select-Object -First 1
if ($null -eq $swift -or [string]::IsNullOrWhiteSpace($swift.Source)) {
    throw 'HOLD_P020_SWIFT_TOOLCHAIN_UNAVAILABLE'
}
$binResult = @(& $swift.Source build --package-path $root -c release --show-bin-path)
if ($LASTEXITCODE -ne 0 -or $binResult.Count -ne 1 -or
    [string]::IsNullOrWhiteSpace([string]$binResult[0])) {
    throw 'HOLD_P020_FRESH_SWIFT_BIN_PATH_UNAVAILABLE'
}
$freshHost = Join-Path ([string]$binResult[0]).Trim() 'SwitcherHost.exe'
if (-not (Test-Path -LiteralPath $freshHost -PathType Leaf)) {
    throw 'HOLD_P020_COMPILED_SWIFTHOST_MISSING'
}
$builtHostSha = (Get-FileHash -LiteralPath $freshHost -Algorithm SHA256).Hash.ToLowerInvariant()
if ($builtHostSha -cne $hostSha) {
    throw 'HOLD_P020_ZIP_HOST_DIFFERS_FROM_COMPILED_SWIFTHOST'
}
$candidate = Join-Path $root 'windows/P014WpfSource/dedicated-thread-v72-native-20261119/DedicatedThreadV72Candidate.csproj'
if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) {
    throw 'HOLD_P020_CANDIDATE_SOURCE_MISSING'
}
$output = [IO.Path]::GetFullPath($OutputDirectory)
if ($env:GITHUB_ACTIONS -cne 'true' -or
    [string]::IsNullOrWhiteSpace($env:RUNNER_TEMP)) {
    throw 'HOLD_P020_CI_ONLY_NO_LOCAL_INSTALL'
}
$runnerRoot = [IO.Path]::GetFullPath($env:RUNNER_TEMP).TrimEnd('\') + '\'
if (-not $output.StartsWith($runnerRoot, [StringComparison]::OrdinalIgnoreCase) -or
    $output.StartsWith($root.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase) -or
    $output -eq $root -or (Test-Path -LiteralPath $output)) {
    throw 'HOLD_P020_OUTPUT_NOT_FRESH_RUNNER_TEMP'
}
$pathMap = "$root=C:\P020\source"
& dotnet publish $candidate -c Release -r win-x64 --self-contained true -o $output '-p:PublishSingleFile=true' '-p:IncludeNativeLibrariesForSelfExtract=true' '-p:EnableCompressionInSingleFile=true' '-p:DebugType=None' '-p:DebugSymbols=false' '-p:ContinuousIntegrationBuild=true' '-p:Deterministic=true' "-p:PathMap=$pathMap" "-p:SwiftRuntimeArchive=$zip" "-p:SwiftRuntimeExpectedSha256=$zipSha" --nologo
if ($LASTEXITCODE -ne 0) { throw 'HOLD_P020_PUBLISH_FAILED' }
$exe = Join-Path $output 'Codex-Account-Switcher-windows-x64.exe'
# Reject unexpected directories, hidden files, and extra artifacts too.
if (-not (Test-Path -LiteralPath $exe -PathType Leaf) -or
    @(Get-ChildItem -LiteralPath $output -Force).Count -ne 1) {
    throw 'HOLD_P020_SINGLE_EXE_INVALID'
}
if ((Get-AuthenticodeSignature -LiteralPath $exe).Status -ne 'NotSigned') {
    throw 'HOLD_P020_UNEXPECTED_PREVIEW_SIGNER'
}
if ((Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant() -cne $zipSha) {
    throw 'HOLD_P020_RUNTIME_CHANGED_DURING_PUBLISH'
}
Write-Output "P020_CURRENT_COMMIT=$head"
Write-Output "P020_FRESH_RUNTIME_SHA256=$zipSha"
Write-Output "P020_BUILD_AND_ZIP_SWIFTHOST_SHA256=$builtHostSha"
Write-Output "P020_UNSIGNED_PREVIEW_SHA256=$((Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash.ToLowerInvariant())"
Write-Output 'P020_SOURCE_BOUND_CI_PREVIEW_PASS_NO_INSTALL_NO_SEND'
