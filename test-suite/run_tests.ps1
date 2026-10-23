[CmdletBinding()]
param(
    [Parameter(Position = 0, ValueFromRemainingArguments = $true)]
    [string[]] $Tests
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$utf8 = [System.Text.UTF8Encoding]::new($false)
$OutputEncoding = $utf8
[Console]::InputEncoding = $utf8
[Console]::OutputEncoding = $utf8

$isWindowsHost = [OperatingSystem]::IsWindows()
$suiteRoot = $PSScriptRoot
$repoRoot = Split-Path -Parent $suiteRoot

function Test-IsWindowsOnly([string] $ProjectPath) {
    return (Get-Content -Raw $ProjectPath) -match "<TargetFramework[^>]*>[^<]*-windows</TargetFramework>"
}

function Invoke-DotNet([string[]] $Arguments) {
    & dotnet @Arguments | Out-Host
    return $LASTEXITCODE
}

Push-Location $suiteRoot
try {
    $projects = @(
        Get-ChildItem -Path $suiteRoot -Directory -Filter "Test*" |
            ForEach-Object {
                $projectPath = Join-Path $_.FullName "$($_.Name).csproj"
                if (Test-Path -Path $projectPath -PathType Leaf) {
                    [pscustomobject]@{
                        Name = $_.Name
                        Directory = $_.FullName
                        Project = $projectPath
                        WindowsOnly = Test-IsWindowsOnly $projectPath
                    }
                }
            } |
            Sort-Object Name
    )

    $projectsByName = [System.Collections.Generic.Dictionary[string, object]]::new(
        [StringComparer]::OrdinalIgnoreCase)
    foreach ($project in $projects) {
        $projectsByName.Add($project.Name, $project)
    }

    if ($Tests -and $Tests.Count -gt 0) {
        $selectedProjects = foreach ($name in $Tests) {
            $project = $null
            if (-not $projectsByName.TryGetValue($name, [ref] $project)) {
                throw "Unknown test '$name'. Test projects must be named Test*/Test*.csproj."
            }
            if ($project.WindowsOnly -and -not $isWindowsHost) {
                throw "Test '$name' targets Windows and cannot run on this operating system."
            }
            $project
        }
    }
    else {
        $selectedProjects = @($projects | Where-Object { $isWindowsHost -or -not $_.WindowsOnly })
        $skipped = @($projects | Where-Object { -not $isWindowsHost -and $_.WindowsOnly })
        if ($skipped.Count -gt 0) {
            Write-Host "Skipping Windows-only tests: $($skipped.Name -join ', ')"
        }
    }

    $debuggerName = if ($isWindowsHost) { "dncdbg.exe" } else { "dncdbg" }
    $debugger = if ([string]::IsNullOrWhiteSpace($env:DNCDBG)) {
        Join-Path $repoRoot "bin/$debuggerName"
    }
    else {
        $env:DNCDBG
    }
    $debugger = [IO.Path]::GetFullPath($debugger)
    if (-not (Test-Path -Path $debugger -PathType Leaf)) {
        throw "Debugger apphost not found at '$debugger'. Publish it first or set DNCDBG."
    }

    if ((Invoke-DotNet @("build", "Runner/Runner.csproj", "--nologo")) -ne 0) {
        throw "Failed to build the test Runner."
    }

    $passed = 0
    $failed = 0
    $results = [System.Collections.Generic.List[string]]::new()
    $targetFramework = if ([string]::IsNullOrWhiteSpace($env:DNCDBG_TARGET_FRAMEWORK)) {
        "net10.0"
    }
    else {
        $env:DNCDBG_TARGET_FRAMEWORK
    }

    foreach ($project in $selectedProjects) {
        $configuration = if ($project.Name.EndsWith("_Release", [StringComparison]::Ordinal)) {
            "Release"
        }
        else {
            "Debug"
        }

        if ((Invoke-DotNet @("build", $project.Project, "-c", $configuration, "--nologo")) -ne 0) {
            $failed++
            $results.Add("$($project.Name) ... failed: build error")
            continue
        }

        $sources = @(
            Get-ChildItem -Path $project.Directory -Recurse -File -Filter "*.cs" |
                Where-Object { $_.FullName -notmatch "[\\/]obj[\\/]" } |
                ForEach-Object { [IO.Path]::GetRelativePath($suiteRoot, $_.FullName) }
        )
        $sourceArgument = $sources -join ";"
        $framework = if ($project.WindowsOnly -and -not $targetFramework.EndsWith("-windows", [StringComparison]::OrdinalIgnoreCase)) {
            "$targetFramework-windows"
        }
        else {
            $targetFramework
        }
        $assembly = Join-Path $project.Directory "bin/$configuration/$framework/$($project.Name).dll"

        $exitCode = Invoke-DotNet @(
            "run", "--project", "Runner/Runner.csproj", "--no-build", "--",
            "--local", $debugger,
            "--test", $project.Name,
            "--sources", $sourceArgument,
            "--assembly", $assembly
        )

        if ($exitCode -eq 0) {
            $passed++
            $results.Add("$($project.Name) ... passed")
        }
        else {
            $failed++
            $results.Add("$($project.Name) ... failed ($exitCode)")
        }
    }

    Write-Output ""
    $results | Write-Output
    Write-Output "Total tests: $($passed + $failed). Passed: $passed. Failed: $failed."

    if ($failed -ne 0) {
        exit 1
    }
}
finally {
    Pop-Location
}
