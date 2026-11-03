[CmdletBinding()]
param(
    [string[]] $Versions = @('11.0.0', '11.1.0', '11.2.0', '11.3.0', '12.0.0', '12.1.4'),
    [string] $CurrentAvaloniaRoot,
    [switch] $FullPublish
)
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
function Run-Dotnet {
    & dotnet @args
    if ($LASTEXITCODE -ne 0) { throw "dotnet failed ($LASTEXITCODE): $args" }
}
Push-Location $repo
try {
    Run-Dotnet build Hintsmith.slnx '-v:q'
    Run-Dotnet build src/Hintsmith.Tasks -c Release '-v:q'
    & (Join-Path $PSScriptRoot 'verify-symbols.ps1') -CurrentAvaloniaRoot $CurrentAvaloniaRoot
    $assemblies = [Collections.Generic.List[string]]::new()
    foreach ($version in $Versions) {
        Run-Dotnet run --project tests/Probe "-p:AvaloniaVersion=$version" '-v:q'
        $assets = Get-Content tests/Probe/obj/project.assets.json -Raw | ConvertFrom-Json
        $packagePath = $assets.libraries."Avalonia.Skia/$version".path
        $packageRoot = $assets.packageFolders.PSObject.Properties.Name |
            ForEach-Object { Join-Path $_ $packagePath } | Where-Object { Test-Path $_ } | Select-Object -First 1
        Get-ChildItem (Join-Path $packageRoot 'lib') -Recurse -Filter Avalonia.Skia.dll |
            ForEach-Object { $assemblies.Add($_.FullName) }
    }
    if ($CurrentAvaloniaRoot) {
        Run-Dotnet run --project tests/Probe "-p:CurrentAvaloniaRoot=$CurrentAvaloniaRoot" '-v:q'
        $assemblies.Add((Join-Path $CurrentAvaloniaRoot 'Avalonia.Skia.dll'))
    }
    Run-Dotnet tests/Hintsmith.Tests/bin/Debug/net10.0/Hintsmith.Tests.dll @assemblies

    # Every smoke package gets a fresh version: never clear or edit the user's
    # global NuGet cache to replace an already restored package of the same version.
    $baseVersion = ([xml](Get-Content Directory.Build.props -Raw)).Project.PropertyGroup.Version
    $testVersion = ($baseVersion -split '-')[0] + '-verify.' + [DateTime]::UtcNow.ToString('yyyyMMddHHmmssfff')
    Run-Dotnet pack src/Hintsmith -o artifacts/feed "-p:Version=$testVersion" '-v:q'
    $package = [IO.Compression.ZipFile]::OpenRead((Join-Path $repo "artifacts/feed/Hintsmith.$testVersion.nupkg"))
    try {
        $entries = @($package.Entries.FullName)
        foreach ($required in @('build/Hintsmith.props', 'build/Hintsmith.targets', 'lib/netstandard2.0/Hintsmith.dll',
                                'tasks/net10.0/Hintsmith.Tasks.dll', 'tasks/net472/Hintsmith.Tasks.dll')) {
            if ($required -notin $entries) { throw "Package asset missing: $required" }
        }
        if ($entries -match '^(Sdk|buildTransitive)/') { throw 'Package contains an SDK or transitive build entry point.' }
    }
    finally { $package.Dispose() }
    $smoke = Join-Path $repo "artifacts/package-smoke/$testVersion"
    New-Item -ItemType Directory -Force $smoke | Out-Null
    $project = (Get-Content tests/PackageProbe/PackageProbe.csproj -Raw).
        Replace('$(Version)', $testVersion).
        Replace('../Probe/Program.cs', 'Program.cs').
        Replace('<Compile Include="Program.cs" Link="Program.cs" />', '')
    Set-Content (Join-Path $smoke 'PackageProbe.csproj') $project
    Copy-Item tests/Probe/Program.cs (Join-Path $smoke 'Program.cs')
    $feed = [Security.SecurityElement]::Escape((Join-Path $repo 'artifacts/feed'))
    Set-Content (Join-Path $smoke 'NuGet.Config') "<configuration><packageSources><clear/><add key='local' value='$feed'/><add key='nuget.org' value='https://api.nuget.org/v3/index.json'/></packageSources></configuration>"
    Run-Dotnet run --project $smoke '-v:q'
    $deps = Get-Content (Join-Path $smoke 'bin/Debug/net10.0/PackageProbe.deps.json') -Raw
    if ($deps -match 'Hintsmith.Tasks|AsmResolver|System.Reflection.Metadata') {
        throw 'Build task dependencies leaked into the application runtime graph.'
    }
    $woven = Join-Path $smoke 'bin/Debug/net10.0/Avalonia.Skia.dll'
    $hash = (Get-FileHash $woven).Hash
    $time = (Get-Item $woven).LastWriteTimeUtc
    Run-Dotnet build $smoke --no-restore '-v:q'
    if ((Get-FileHash $woven).Hash -ne $hash -or (Get-Item $woven).LastWriteTimeUtc -ne $time) {
        throw 'An unchanged build rewrote the output assembly.'
    }
    Run-Dotnet build $smoke --no-restore '-p:HintsmithEnabled=false' '-v:q'
    if ((Get-FileHash $woven).Hash -eq $hash) { throw 'Disabling weaving did not restore the original.' }
    Run-Dotnet run --project $smoke --no-restore '-v:q'
    $publish = Join-Path $smoke 'publish'
    Run-Dotnet publish $smoke -c Debug --no-build -o $publish '-v:q'
    Run-Dotnet (Join-Path $publish 'PackageProbe.dll')
    Run-Dotnet tests/Hintsmith.Tests/bin/Debug/net10.0/Hintsmith.Tests.dll assert-woven (Join-Path $publish 'Avalonia.Skia.dll')

    # Exercise PDB rewriting through the installed package, not just local imports.
    $symbolSmoke = Join-Path $repo "artifacts/package-symbol-smoke/$testVersion"
    New-Item -ItemType Directory -Force $symbolSmoke | Out-Null
    $fixturePath = [Security.SecurityElement]::Escape((Join-Path $repo 'artifacts/fixtures/modern/Avalonia.Skia.dll'))
    Set-Content (Join-Path $symbolSmoke 'Symbols.csproj') @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>Exe</OutputType></PropertyGroup>
  <ItemGroup>
    <Reference Include="$fixturePath" />
    <PackageReference Include="Hintsmith" Version="$testVersion" />
  </ItemGroup>
</Project>
"@
    Copy-Item tests/SymbolConsumer/Program.cs (Join-Path $symbolSmoke 'Program.cs')
    Copy-Item (Join-Path $smoke 'NuGet.Config') (Join-Path $symbolSmoke 'NuGet.Config')
    Run-Dotnet run --project $symbolSmoke '-v:q'
    Run-Dotnet publish $symbolSmoke -c Debug --no-build -o (Join-Path $symbolSmoke 'publish') '-v:q'
    Run-Dotnet (Join-Path $symbolSmoke 'publish/Symbols.dll')

    if ($FullPublish) {
        if (!$IsWindows) { throw 'The full native publish matrix currently uses win-x64.' }
        foreach ($feature in @('PublishSingleFile', 'PublishReadyToRun', 'PublishTrimmed', 'PublishAot')) {
            $destination = Join-Path $smoke $feature
            $selfContained = if ($feature -in @('PublishTrimmed', 'PublishAot')) { 'true' } else { 'false' }
            Run-Dotnet publish $smoke -c Release -r win-x64 "-p:$feature=true" --self-contained $selfContained -o $destination '-v:q'
            & (Join-Path $destination 'PackageProbe.exe')
            if ($LASTEXITCODE -ne 0) { throw "$feature executable failed." }
        }
    }
    Run-Dotnet pack src/Hintsmith -o artifacts/feed '-v:q'
    Write-Output 'PASS: compatibility, runtime, IL guards, PackageReference restore, incremental build, disable/enable and publish.'
}
finally { Pop-Location }



