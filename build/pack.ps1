#Requires -Version 5.1
<#
.SYNOPSIS
    릴리스 패키지를 만들고 내용물을 검증한다.

.DESCRIPTION
    CI 가 없는 저장소라 pack 은 사람이 손으로 돌린다. 손으로 돌리는 단계일수록
    "analyzer 가 빠진 채로 배포됐다" 같은 조용한 사고가 나기 쉬우므로,
    이 스크립트가 만들고 나서 곧바로 nupkg 안을 열어 확인한다.

    버전은 MinVer 가 git 태그에서 가져온다. 태그가 없으면 -Version 으로 강제할 수 있지만,
    실제 배포용 산출물은 태그를 찍고 만들어야 한다.

.EXAMPLE
    ./build/pack.ps1
    ./build/pack.ps1 -Version 0.10.0        # 태그를 찍기 전 예행 연습
#>
[CmdletBinding()]
param(
    [string] $Version,
    [string] $Configuration = 'Release',
    [string] $Output = 'artifacts'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Push-Location $root

try {
    $versionArgs = @()
    if ($Version) {
        Write-Host "버전을 $Version 으로 강제합니다 (git 태그를 무시)." -ForegroundColor Yellow
        $versionArgs = @("-p:MinVerVersionOverride=$Version")
    }

    if (Test-Path $Output) { Remove-Item $Output -Recurse -Force }

    # ContinuousIntegrationBuild: 소스 경로를 정규화해 재현 가능한 빌드 + 동작하는 SourceLink 를 만든다.
    Write-Host "`n== build ==" -ForegroundColor Cyan
    dotnet build DisruptorCS.slnx -c $Configuration -p:ContinuousIntegrationBuild=true @versionArgs
    if ($LASTEXITCODE -ne 0) { throw "build 실패" }

    Write-Host "`n== pack ==" -ForegroundColor Cyan
    foreach ($project in @('Disruptor/Disruptor.csproj', 'Disruptor.Extensions.Hosting/Disruptor.Extensions.Hosting.csproj')) {
        dotnet pack $project -c $Configuration --no-build -o $Output -p:ContinuousIntegrationBuild=true @versionArgs
        if ($LASTEXITCODE -ne 0) { throw "pack 실패: $project" }
    }

    Write-Host "`n== verify ==" -ForegroundColor Cyan
    Add-Type -AssemblyName System.IO.Compression.FileSystem

    # 패키지마다 반드시 들어 있어야 하는 항목. 하나라도 없으면 배포하면 안 된다.
    $required = @{
        'DisruptorCS' = @(
            'analyzers/dotnet/cs/Disruptor.Analyzers.dll',
            'PACKAGE.md', 'icon.png',
            'lib/net8.0/DisruptorCS.dll', 'lib/net8.0/DisruptorCS.xml',
            'lib/net10.0/DisruptorCS.dll', 'lib/net10.0/DisruptorCS.xml')
        'DisruptorCS.Extensions.Hosting' = @(
            'PACKAGE.md', 'icon.png',
            'lib/net8.0/DisruptorCS.Extensions.Hosting.dll',
            'lib/net10.0/DisruptorCS.Extensions.Hosting.dll')
    }

    $failed = $false
    foreach ($id in $required.Keys) {
        $package = Get-ChildItem "$Output/$id.*.nupkg" | Where-Object { $_.Name -notlike '*.symbols.nupkg' } | Select-Object -First 1
        if (-not $package) { Write-Host "  [없음] $id nupkg" -ForegroundColor Red; $failed = $true; continue }

        $zip = [System.IO.Compression.ZipFile]::OpenRead($package.FullName)
        try   { $entries = $zip.Entries | ForEach-Object { $_.FullName } }
        finally { $zip.Dispose() }

        Write-Host "  $($package.Name)"
        foreach ($entry in $required[$id]) {
            if ($entries -contains $entry) {
                Write-Host "    [OK]   $entry" -ForegroundColor Green
            } else {
                Write-Host "    [누락] $entry" -ForegroundColor Red
                $failed = $true
            }
        }

        $symbols = Join-Path $Output ($package.BaseName + '.snupkg')
        if (Test-Path $symbols) {
            Write-Host "    [OK]   $(Split-Path -Leaf $symbols)" -ForegroundColor Green
        } else {
            Write-Host "    [누락] 심볼 패키지 (.snupkg)" -ForegroundColor Red
            $failed = $true
        }
    }

    if ($failed) { throw "패키지 검증 실패 — 배포하지 마세요." }

    Write-Host "`n검증 통과. 배포 명령:" -ForegroundColor Green
    Get-ChildItem "$Output/*.nupkg" | ForEach-Object {
        Write-Host "  dotnet nuget push $Output/$($_.Name) --source https://api.nuget.org/v3/index.json --api-key `$env:NUGET_API_KEY"
    }
}
finally {
    Pop-Location
}
