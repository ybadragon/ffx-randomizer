<#
.SYNOPSIS
    Runs Karifean's FFXDataParser against an FFX data folder and saves its output to a file.

.DESCRIPTION
    -Root is a folder that contains ffx_ps2 (for example the randomizer's input folder).
    -Mods is optional: a folder that contains a sparse ffx_ps2 tree of changed files (for example
    the randomizer's output folder). Files found there are read instead of the matching files
    under -Root; everything else comes from -Root. This uses the parser's own "mods/" overlay
    by building a temporary root out of two directory junctions, so neither folder is modified.

    stdout goes to -Out; stderr (if any) goes to <Out>.err.txt.
    The parser is run from its repo folder (it writes target/text/... relative to the current
    directory) and from target/classes rather than the jar (inside the jar it cannot load its
    enum CSVs, so elements print as b0002 instead of Ice).

.EXAMPLE
    .\ffxdataparser.ps1 -Root "$env:LOCALAPPDATA\FFX Randomizer\FFXRando\input" -Mode PARSE_MONSTER -Arguments 1,2,3 -Out mon.txt

.EXAMPLE
    # Randomized output over the original data
    .\ffxdataparser.ps1 -Root '...\FFXRando\input' -Mods '...\FFXRando\output' -Mode READ_TREASURES -Out treasures.txt
#>
#Requires -Version 7
[CmdletBinding(PositionalBinding = $false)]
param(
    [Parameter(Mandatory)] [string] $Root,
    [Parameter(Mandatory)] [string] $Mode,
    # Mode arguments: either -Arguments 1,2,3 or bare trailing values (... -Mode PARSE_MONSTER 1 2 3).
    [Parameter(ValueFromRemainingArguments)] [string[]] $Arguments = @(),
    [string] $Out = "$Mode.txt",
    [string] $Mods,
    [string] $ParserDir = 'C:\Github\Personal\FFXDataParser'
)
$ErrorActionPreference = 'Stop'
# pwsh -File passes "1,2,3" as one string, so split on commas.
$Arguments = @($Arguments | ForEach-Object { $_ -split ',' } | Where-Object { $_ -ne '' })

# These modes write files into <root>/mods, which with -Mods would be the randomizer's output folder.
$writingModes = 'MAKE_EDITS', 'RECOMPILE', 'ADD_ATEL_SPACE', 'REMAKE_SIZE_TABLE', 'MAKE_AUTOHASTE_MOD', 'GUI'
if ($writingModes -contains $Mode.ToUpper()) { throw "Mode $Mode writes files or opens the GUI; not supported by this wrapper." }

$Root = (Resolve-Path $Root).Path
if (-not (Test-Path (Join-Path $Root 'ffx_ps2'))) { throw "No ffx_ps2 folder under $Root" }
$classes = Join-Path $ParserDir 'target\classes'
if (-not (Test-Path $classes)) { throw "Parser not built: run 'mvn package' in $ParserDir" }
$Out = [IO.Path]::GetFullPath($Out, (Get-Location).Path)
$errFile = [IO.Path]::ChangeExtension($Out, '.err.txt')

$tempRoot = $null
$runRoot = $Root
if ($Mods) {
    $Mods = (Resolve-Path $Mods).Path
    if (-not (Test-Path (Join-Path $Mods 'ffx_ps2'))) { throw "No ffx_ps2 folder under $Mods" }
    $tempRoot = Join-Path ([IO.Path]::GetTempPath()) ("ffxdataparser-" + [guid]::NewGuid().ToString('N').Substring(0, 8))
    New-Item -ItemType Directory $tempRoot | Out-Null
    New-Item -ItemType Junction -Path (Join-Path $tempRoot 'ffx_ps2') -Target (Join-Path $Root 'ffx_ps2') | Out-Null
    New-Item -ItemType Junction -Path (Join-Path $tempRoot 'mods') -Target $Mods | Out-Null
    $runRoot = $tempRoot
}

# The parser wants forward slashes and a trailing slash.
$rootArg = $runRoot.Replace('\', '/').TrimEnd('/') + '/'

Push-Location $ParserDir
$prevEncoding = [Console]::OutputEncoding
try {
    [Console]::OutputEncoding = [Text.Encoding]::UTF8
    $sw = [Diagnostics.Stopwatch]::StartNew()
    & java '-Dstdout.encoding=UTF-8' '-Dstderr.encoding=UTF-8' -cp $classes main.Main $rootArg $Mode @Arguments 1> $Out 2> $errFile
    $code = $LASTEXITCODE
    $sw.Stop()
}
finally {
    [Console]::OutputEncoding = $prevEncoding
    Pop-Location
    if ($tempRoot) {
        # Delete only the junctions themselves (non-recursive), never what they point to.
        foreach ($link in 'ffx_ps2', 'mods') {
            $p = Join-Path $tempRoot $link
            if (Test-Path $p) { [IO.Directory]::Delete($p, $false) }
        }
        [IO.Directory]::Delete($tempRoot, $false)
    }
}

if ((Test-Path $errFile) -and (Get-Item $errFile).Length -eq 0) { Remove-Item $errFile }
Write-Host ("{0} finished in {1:N1}s (exit {2}) -> {3}" -f $Mode, $sw.Elapsed.TotalSeconds, $code, $Out)
if (Test-Path $errFile) { Write-Host "stderr written to $errFile" }
exit $code
