[CmdletBinding()]
param([string] $CurrentAvaloniaRoot)
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
function Run-Dotnet {
    & dotnet @args
    if ($LASTEXITCODE -ne 0) { throw "dotnet failed ($LASTEXITCODE): $args" }
}
Push-Location $repo
try {
    Run-Dotnet build src/Hintsmith.Tasks '-v:q'
    Run-Dotnet build tests/Hintsmith.Tests '-v:q'
    $fixtures = @()
    foreach ($profile in @('modern', 'legacy', 'embedded')) {
        $format = if ($profile -eq 'embedded') { 'embedded' } else { 'portable' }
        $legacy = if ($profile -eq 'legacy') { 'true' } else { 'false' }
        Run-Dotnet build tests/SymbolFixture '-t:Rebuild' "-p:DebugType=$format" "-p:FixtureLegacy=$legacy" -o "artifacts/fixtures/$profile" '-v:q'
        $fixture = Join-Path $repo "artifacts/fixtures/$profile/Avalonia.Skia.dll"
        $fixtures += $fixture
        Run-Dotnet run --project tests/SymbolConsumer "-p:FixturePath=$fixture" '-v:q'
        Run-Dotnet publish tests/SymbolConsumer -c Debug --no-build "-p:FixturePath=$fixture" -o "artifacts/symbol-publish/$profile" '-v:q'
        Run-Dotnet "artifacts/symbol-publish/$profile/SymbolConsumer.dll"
    }
    if ($CurrentAvaloniaRoot) { $fixtures += Join-Path $CurrentAvaloniaRoot 'Avalonia.Skia.dll' }
    Run-Dotnet tests/Hintsmith.Tests/bin/Debug/net10.0/Hintsmith.Tests.dll symbols @fixtures

    $fixture = $fixtures[0]
    Run-Dotnet build tests/SymbolConsumer "-p:FixturePath=$fixture" '-v:q'
    $output = Join-Path $repo 'tests/SymbolConsumer/bin/Debug/net10.0/Avalonia.Skia.pdb'
    $hash = (Get-FileHash $output).Hash
    $time = (Get-Item $output).LastWriteTimeUtc
    Run-Dotnet build tests/SymbolConsumer --no-restore "-p:FixturePath=$fixture" '-v:q'
    if ((Get-FileHash $output).Hash -ne $hash -or (Get-Item $output).LastWriteTimeUtc -ne $time) { throw 'Incremental build rewrote unchanged symbols.' }

    # Only PDB bytes change: same ID, invalid checksum. A cache hit must NOT hide it.
    $inputPdb = [IO.Path]::ChangeExtension($fixture, '.pdb')
    $bytes = [IO.File]::ReadAllBytes($inputPdb)
    try {
        $damaged = $bytes.Clone()
        $damaged[$damaged.Length - 1] = $damaged[$damaged.Length - 1] -bxor 1
        [IO.File]::WriteAllBytes($inputPdb, $damaged)
        & dotnet build tests/SymbolConsumer --no-restore "-p:FixturePath=$fixture" '-v:q' > artifacts/symbol-cache-error.log 2>&1
        if ($LASTEXITCODE -eq 0 -or !(Select-String -Path artifacts/symbol-cache-error.log -Pattern 'checksum')) { throw 'Cache ignored changed PDB input.' }
    }
    finally { [IO.File]::WriteAllBytes($inputPdb, $bytes) }
    # Missing generated sidecars are regenerated even when the DLL is still cached.
    $cacheRoot = Join-Path $repo 'tests/SymbolConsumer/obj/Debug/net10.0/hintsmith'
    Get-ChildItem $cacheRoot -Recurse -Filter external-symbols | ForEach-Object {
        $sidecar = Join-Path $_.DirectoryName 'Avalonia.Skia.pdb'
        if (Test-Path -LiteralPath $sidecar) { [IO.File]::Delete($sidecar) }
    }
    Run-Dotnet run --project tests/SymbolConsumer --no-restore "-p:FixturePath=$fixture" '-v:q'
    Run-Dotnet build tests/SymbolConsumer --no-restore "-p:FixturePath=$fixture" '-p:HintsmithSymbols=Strip' '-v:q'
    if (Test-Path -LiteralPath $output) { throw 'Strip left a stale PDB in bin.' }
    Run-Dotnet publish tests/SymbolConsumer -c Debug --no-build "-p:FixturePath=$fixture" '-p:HintsmithSymbols=Strip' -o artifacts/symbol-publish/modern '-v:q'
    if (Test-Path artifacts/symbol-publish/modern/Avalonia.Skia.pdb) { throw 'Strip left a stale PDB in publish.' }
    Run-Dotnet run --project tests/SymbolConsumer --no-restore "-p:FixturePath=$fixture" '-v:q'
    Write-Output 'PASS Portable PDB: external/embedded, runtime lines, scopes, imports, checksum/ID guards, no-symbols/strip, cache and publish.'
}
finally { Pop-Location }
