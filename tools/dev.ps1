#Requires -Version 7.3
param([string]$Action = 'build')

$ErrorActionPreference = 'Stop'
$PSNativeCommandArgumentPassing = 'Standard'
$PSNativeCommandUseErrorActionPreference = $false
$forwarded = $args

function Invoke-Native([string]$Command, [string[]]$Arguments) {
    & $Command @Arguments
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

function Resolve-Godot {
    $path = $env:OPENRTS_GODOT
    if ($IsWindows -and [string]::IsNullOrWhiteSpace($path)) {
        throw 'Set OPENRTS_GODOT to the full path of the Godot .NET executable (e.g. Godot_v4.7.2-stable_mono_win64.exe).'
    }
    if ($path) {
        if (Test-Path -LiteralPath $path -PathType Leaf) {
            $path = (Resolve-Path -LiteralPath $path).ProviderPath
        } else {
            $path = (Get-Command $path -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1).Source
        }
    } else {
        $path = (Get-Command godot-mono, godot4-mono, godot_console, godot, godot4 -CommandType Application -ErrorAction SilentlyContinue |
                 Select-Object -First 1).Source
    }
    if (!$path) { throw 'Godot .NET executable not found. Check the executable path in OPENRTS_GODOT.' }
    if ($IsWindows -and $path -like '*.exe') {
        $console = $path -replace '\.exe$', '_console.exe'
        if (Test-Path -LiteralPath $console -PathType Leaf) { $path = $console }
    }
    if ($IsWindows) {
        $version = (& $path --version 2>&1 | Out-String).Trim()
        if ($LASTEXITCODE -ne 0 -or $version -notmatch '\.mono(\.|$)') {
            throw "'$path' did not report a Godot .NET version. Set OPENRTS_GODOT to the original .NET executable. $version"
        }
    }
    return $path
}

function Invoke-Python([string[]]$Arguments) {
    if (!$IsWindows) {
        Invoke-Native python3 $Arguments
    } elseif (Get-Command py.exe -CommandType Application -ErrorAction SilentlyContinue) {
        Invoke-Native py.exe (@('-3') + $Arguments)
    } else {
        Invoke-Native python.exe $Arguments
    }
}

function Build-Project([string]$Target = 'OpenRTS.sln', [string]$Configuration = 'Debug') {
    $restoreArgs = @('restore', $Target, '--disable-build-servers', '-m:1')
    if ($env:OPENRTS_NUGET_SOURCE) { $restoreArgs += @('--source', $env:OPENRTS_NUGET_SOURCE) }
    Invoke-Native dotnet $restoreArgs
    Invoke-Native dotnet @('build', $Target, '--configuration', $Configuration, '--no-restore',
                           '--disable-build-servers', '-m:1', '-p:UseSharedCompilation=false')
}

function Invoke-Project([string]$Project, [string[]]$Arguments, [string]$Configuration = 'Debug') {
    Invoke-Native dotnet (@('run', '--project', $Project, '--configuration', $Configuration,
                            '--no-build', '--no-launch-profile', '--') + $Arguments)
}

function Invoke-Tests([string[]]$Arguments) {
    Build-Project
    Invoke-Project 'tests/OpenRTS.Network.Tests'
    Invoke-Project 'tests/DeclarativeContent.Tests'
    Invoke-Project 'tests/OpenRTS.Tests' $Arguments
}

function Import-GodotAssets([string]$Project) {
    Invoke-Native $godot @('--headless', '--editor', '--path', $Project, '--import')
}

Push-Location (Join-Path $PSScriptRoot '..')
$settings = @{
    DOTNET_CLI_HOME = Join-Path $PWD.Path '.dotnet-home'
    DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    DOTNET_NOLOGO = '1'
    DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
    NUGET_PACKAGES = Join-Path $PWD.Path '.packages'
}
$previous = @{}
try {
    foreach ($key in $settings.Keys) {
        $previous[$key] = [Environment]::GetEnvironmentVariable($key)
        [Environment]::SetEnvironmentVariable($key, $settings[$key])
    }
    $sandbox = Join-Path $PWD.Path 'sandbox'
    $sandbox3d = Join-Path $PWD.Path 'sandbox3d'
    if ($Action -match '^(run|editor|run3d|editor3d|verify3d|map-build|scene-map|scene-map-pack|verify-map-export|verify-authoring|verify-directory|verify-replay|verify-menu|verify-network|verify)$') {
        $godot = Resolve-Godot
    }

    switch -Regex ($Action) {
        '^build$' { Build-Project }
        '^test$' { Invoke-Tests $forwarded }
        '^(match-directory|replay)$' {
            $project = if ($Action -eq 'replay') { 'OpenRTS.Replay' } else { 'OpenRTS.MatchDirectory' }
            Build-Project "tools/$project/$project.csproj"
            Invoke-Project "tools/$project" $forwarded
        }
        '^headless$' {
            Build-Project
            Invoke-Project 'tools/OpenRTS.Headless' $forwarded
        }
        '^benchmark$' {
            Build-Project 'tools/OpenRTS.Headless/OpenRTS.Headless.csproj' Release
            $previous.DOTNET_TieredCompilation = $env:DOTNET_TieredCompilation
            $env:DOTNET_TieredCompilation = '0'
            Invoke-Project 'tools/OpenRTS.Headless' (@('--benchmark') + $forwarded) Release
        }
        '^run$' {
            Build-Project
            Import-GodotAssets $sandbox
            Invoke-Native $godot (@('--path', $sandbox, '--') + $forwarded)
        }
        '^editor$' {
            Build-Project
            Invoke-Native $godot @('--path', $sandbox, '--editor')
        }
        '^(run3d|editor3d|verify3d)$' {
            Build-Project 'sandbox3d/OpenRTS.Sandbox3D.csproj'
            if ($Action -eq 'editor3d') {
                Invoke-Native $godot (@('--path', $sandbox3d, '--editor') + $forwarded)
            } else {
                Import-GodotAssets $sandbox3d
                if ($Action -eq 'verify3d') {
                    $output = Invoke-Native $godot @('--headless', '--max-fps', '60', '--quit-after', '600',
                                                     '--path', $sandbox3d, '--', '--verify')
                    $output
                    if (!($output -match '^Sandbox3D verified:')) {
                        throw 'Sandbox3D did not report a completed verification.'
                    }
                } else {
                    Invoke-Native $godot (@('--path', $sandbox3d, '--') + $forwarded)
                }
            }
        }
        '^(map-build|scene-map|scene-map-pack|verify-map-export|verify-authoring)$' {
            Invoke-Python (@('tools/dev_maps.py', $godot, $Action) + $forwarded)
        }
        '^verify-wan$' {
            Build-Project
            if ($forwarded[0] -ceq '--netem') {
                Invoke-Native docker @('build', '-t', 'openrts-netem:net2', '-f', 'tools/network/Dockerfile', 'tools/network')
                Invoke-Python (@('tools/verify_wan_netem.py') + ($forwarded | Select-Object -Skip 1))
            } else {
                Invoke-Project 'tests/OpenRTS.Network.Tests' (@('--wan') + $forwarded)
            }
        }
        '^verify-menu$' {
            Build-Project
            Import-GodotAssets $sandbox
            $options = @('--headless')
            if ($forwarded[0] -ceq '--visible') {
                $options = @()
                $forwarded = @($forwarded | Select-Object -Skip 1)
            }
            Invoke-Native $godot ($options + @('--path', $sandbox, '--', '--verify-menu') + $forwarded)
        }
        '^verify-(directory|replay|network)$' {
            Build-Project
            Import-GodotAssets $sandbox
            $script = "tools/$($Action.Replace('-', '_')).py"
            Invoke-Python (@($script, $godot) + $forwarded)
        }
        '^verify$' {
            Invoke-Tests
            Import-GodotAssets $sandbox
            Invoke-Python @('tools/verify.py', $godot)
        }
        default {
            [Console]::Error.WriteLine('Usage: tools/dev.ps1 [build|test|headless|replay|benchmark|run|editor|run3d|editor3d|verify3d|map-build|scene-map|scene-map-pack|verify|verify-authoring|verify-map-export|verify-network|verify-menu|verify-replay|verify-wan|match-directory|verify-directory]')
            exit 2
        }
    }
} finally {
    foreach ($key in $previous.Keys) {
        [Environment]::SetEnvironmentVariable($key, $previous[$key])
    }
    Pop-Location
}
