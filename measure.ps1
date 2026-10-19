# The measurement this repository has been unable to make.
#
# Two questions have been open since M8 and M21, and both need a Windows
# toolchain that nothing here has had:
#
#   1. Do the conversions produce a solution that restores and compiles?
#      `git apply` takes the patches. Nobody has built the result, and
#      "converted" has meant "has a patch that applies" for want of a machine.
#
#   2. Is our conversion worth having at all?
#      `upgrade-assistant` is alive and does this work. It is run here as a
#      competitor and never as a component: nothing in Legacy Lens calls it,
#      and if it turns out to do this better the answer is to stop doing it and
#      say so, not to depend on it. The single file you double-click stays
#      offline either way.
#
#   3. On a real .NET Framework estate, what does characterization reach?
#      On modern code it examined 402 members and could call 11. Whether that
#      ratio inverts on Framework code was untested until M30, and half the
#      product's claim rests on it.
#
# Needs, on the machine: .NET Framework and its targeting packs, MSBuild,
# nuget.exe on the PATH for the packages.config restore, the .NET SDK for the
# restore after conversion, and git.
#
#   .\measure.ps1 -Repo C:\work\nop-3.90 -Solution src\NopCommerce.sln
#
# It has run, on a hosted Windows runner, through
# .github/workflows/measure-windows.yml, which clones Orchard and nopCommerce
# 3.90 at the commits the figures were taken from. It was written on Linux
# without PowerShell, and the first run on Windows did fail in places: the
# image carries a v4.5.1 folder with no reference assemblies in it, and the
# characterization looked only in bin folders where nopCommerce writes its
# plugins elsewhere. The workflow supplies the first and the script fixes the
# second, and the figures are M30 in docs/ROADMAP.md. Read the report rather than the console: each step records
# its own failure and the steps after it still say something.
#
# The competitor step still names upgrade-assistant, which has since been
# replaced twice; the run that matters for it is ticket #2, and the workflow
# skips it.
#
# It writes one report and changes nothing outside the repository copy it was
# given. It is deliberately loud about each step, because the first run of this
# is the interesting one and a silent failure would waste the machine.

param(
    [Parameter(Mandatory = $true)][string] $Repo,

    # Relative to the repository. Without it the first solution found is used,
    # which on a tree with more than one is a guess, and the report names it.
    [string] $Solution,

    [string] $Lens = "$PSScriptRoot\build\desktop\win-x64\LegacyLens.exe",
    [string] $Out  = "$PSScriptRoot\build\measurement",

    # Reference assemblies for a target whose targeting pack the machine does
    # not have, handed to MSBuild as TargetFrameworkRootPath on both builds so
    # the before and the after are built against the same thing. Said in the
    # report when used.
    [string] $FrameworkRoot,

    # The competitor needs a tool that may not install on the day, and a run
    # that is only about our own conversions should not wait on it.
    [switch] $SkipCompetitor,

    # How many of the solution's own assemblies get characterized, and how long
    # each may take before it is stopped and recorded as stopped.
    [int] $Assemblies = 200,
    [int] $MinutesPerAssembly = 5
)

$ErrorActionPreference = 'Stop'

$Repo = (Resolve-Path $Repo).Path
$Lens = (Resolve-Path $Lens).Path
New-Item -ItemType Directory -Force -Path $Out | Out-Null
$Out = (Resolve-Path $Out).Path
$log = Join-Path $Out 'measurement.md'

function Say([string] $text) {
    Write-Host $text
    Add-Content -Path $log -Value $text
}

function Step([string] $name, [scriptblock] $work) {
    Say ""
    Say "## $name"
    Say ""

    $started = Get-Date

    try {
        & $work
        Say ""
        Say "*took $([int]((Get-Date) - $started).TotalSeconds)s*"
    }
    catch {
        # Reported and never swallowed. A step that failed is a finding, and
        # the steps after it still say something.
        Say ""
        Say "**FAILED.** $($_.Exception.Message)"
        Say ""
        Say "*after $([int]((Get-Date) - $started).TotalSeconds)s*"
    }
}

# A native command, with everything it printed kept in a file of its own and
# its last lines in the report. Returns the exit code, because a native command
# that fails does not throw in PowerShell and the code is the only thing that
# says it failed.
function Native([string] $name, [string] $command, [string[]] $arguments, [int] $tail = 15) {
    $file = Join-Path $Out "$name.log"
    & $command @arguments *> $file
    $code = $LASTEXITCODE

    Say '```'
    Get-Content $file -Tail $tail | ForEach-Object { Say $_ }
    Say '```'
    Say ""
    Say "``$name``: exit code $code, everything it printed is in ``$name.log``."

    return $code
}

# What an MSBuild log says, counted rather than quoted. The tail of a failed
# build is the last few errors of hundreds; the count by code is what says
# whether it is one problem or many.
function Tally([string] $name) {
    $file = Join-Path $Out "$name.log"
    $lines = Get-Content $file

    $built = @($lines | Where-Object { $_ -match '^\s*\S+ -> .+\.(dll|exe)\s*$' }).Count

    $errors = @($lines |
        Where-Object { $_ -match ':\s*error\s+([A-Z]+\d+)\s*:' } |
        Sort-Object -Unique)

    $failing = @($errors |
        ForEach-Object { if ($_ -match '\[([^\]]+\.(cs|vb|fs)proj)') { Split-Path $Matches[1] -Leaf } } |
        Sort-Object -Unique)

    Say ""
    Say "| | |"
    Say "|---|---|"
    Say "| assemblies written | $built |"
    Say "| distinct errors | $($errors.Count) |"
    Say "| projects with an error | $($failing.Count) |"

    if ($errors.Count -gt 0) {
        Say ""
        Say "| error code | count | first seen |"
        Say "|---|---|---|"

        $errors |
            ForEach-Object { if ($_ -match ':\s*error\s+([A-Z]+\d+)\s*:\s*(.*)$') { [pscustomobject]@{ Code = $Matches[1]; Text = $Matches[2] } } } |
            Group-Object Code |
            Sort-Object Count -Descending |
            ForEach-Object {
                $first = ($_.Group[0].Text -replace '\[[^\]]*\]\s*$', '' -replace '\|', '\|').Trim()
                if ($first.Length -gt 160) { $first = $first.Substring(0, 160) + '...' }
                Say "| $($_.Name) | $($_.Count) | $first |"
            }
    }

    if ($failing.Count -gt 0) {
        Say ""
        Say "Projects with an error: $($failing -join ', ')."
    }
}

function MSBuildArguments([string] $solution) {
    $arguments = @($solution, '/m', '/v:minimal', '/nologo', '/p:Configuration=Debug')
    if ($FrameworkRoot) { $arguments += "/p:TargetFrameworkRootPath=$FrameworkRoot" }
    return $arguments
}

function FindSolution {
    if ($Solution) { return (Get-Item (Join-Path $Repo $Solution)) }
    return (Get-ChildItem -Path $Repo -Filter *.sln -Recurse | Select-Object -First 1)
}

Set-Content -Path $log -Value "# Measured on $(Get-Date -Format 'yyyy-MM-dd HH:mm')"
Say ""
Say "Repository: ``$Repo``"
$head = & git -C $Repo rev-parse HEAD 2>$null
if ($LASTEXITCODE -eq 0) { Say "Commit: ``$head``" }
Say "Machine: $([System.Environment]::OSVersion.VersionString), PowerShell $($PSVersionTable.PSVersion)"

$msbuildVersion = & msbuild -version -nologo 2>$null | Select-Object -Last 1
Say "MSBuild: $msbuildVersion; .NET SDK: $(& dotnet --version 2>$null)"

$packs = 'C:\Program Files (x86)\Reference Assemblies\Microsoft\Framework\.NETFramework'
if (Test-Path $packs) {
    Say "Targeting packs installed: $((Get-ChildItem $packs -Directory | ForEach-Object Name) -join ', ')"
}
if ($FrameworkRoot) {
    Say "Reference assemblies handed to MSBuild: ``$FrameworkRoot`` ($((Get-ChildItem (Join-Path $FrameworkRoot '.NETFramework') -Directory | ForEach-Object Name) -join ', '))"
}

# ---------------------------------------------------------------------------

Step 'What the tool says before anything is applied' {
    & $Lens report $Repo 2>$null | Out-File (Join-Path $Out 'before.md') -Encoding utf8
    Say "Written to ``before.md``."

    $surface = & $Lens surface $Repo 2>$null | Select-Object -First 12
    Say '```'
    $surface | ForEach-Object { Say $_ }
    Say '```'

    Say ""
    Say '```'
    & $Lens convert $Repo 2>&1 | ForEach-Object { Say "$_" }
    Say '```'
}

Step 'Does it restore and build as it stands' {
    # The baseline, and it matters as much as the after. A solution that does
    # not build before the patches cannot be said to have been broken by them.
    $sln = FindSolution
    Say "Solution: ``$($sln.FullName)``"
    Say ""

    Push-Location $sln.DirectoryName
    try {
        Native 'before-restore' 'nuget' @('restore', $sln.FullName, '-NonInteractive') 5 | Out-Null
        Say ""
        $code = Native 'before-build' 'msbuild' (MSBuildArguments $sln.FullName) 20
        Tally 'before-build'
        Say ""
        Say $(if ($code -eq 0) { "**It builds as it stands.**" } else { "**It does not build as it stands.**" })
    }
    finally { Pop-Location }
}

Step 'Apply the two conversions, in the order the tool prints' {
    Push-Location $Repo
    try {
        if (-not (Test-Path (Join-Path $Repo '.git'))) {
            & git init 2>&1 | Out-Null
            & git add -A 2>&1 | Out-Null
            & git -c user.email=m@m -c user.name=m commit -qm baseline 2>&1 | Out-Null
            Say "No history here, so a baseline commit was made to diff against."
        }

        foreach ($kind in @('packages', 'sdk')) {
            $patch = Join-Path $Out "$kind.patch"
            $notes = Join-Path $Out "$kind.notes.txt"

            # Through cmd, so the patch reaches the file byte for byte. A
            # PowerShell pipeline splits it into lines and joins them again with
            # its own line ending, and git applies what it was given.
            & cmd /c "`"$Lens`" convert `"$Repo`" $kind > `"$patch`" 2> `"$notes`""

            $files = @(Select-String -Path $patch -Pattern '^diff --git ').Count
            Say "``$kind``: the patch touches $files file(s); its notes are in ``$kind.notes.txt``."

            & git apply --check $patch *> (Join-Path $Out "$kind.apply.log")
            if ($LASTEXITCODE -ne 0) {
                Say "``$kind``: **git apply refused it**, see ``$kind.apply.log``."
                Get-Content (Join-Path $Out "$kind.apply.log") -Tail 5 | ForEach-Object { Say "    $_" }
                continue
            }

            & git apply $patch *> (Join-Path $Out "$kind.apply.log")

            # Announcing "applied" on the strength of having run git is exactly
            # the kind of claim this repository spent a milestone removing from
            # its own wording.
            if ($LASTEXITCODE -ne 0) { throw "git apply refused the $kind patch after accepting it on --check" }

            Say "``$kind``: applied."
        }

        Say ""
        Say "What the conversions left for a person, as the tool printed it:"
        Say ""
        Say '```'
        foreach ($kind in @('packages', 'sdk')) {
            Get-Content (Join-Path $Out "$kind.notes.txt") -ErrorAction SilentlyContinue |
                Where-Object { $_ -match '\S' } | Select-Object -First 15 | ForEach-Object { Say $(if ($_.Length -gt 240) { $_.Substring(0, 240) + '...' } else { $_ }) }
        }
        Say '```'

        $all  = @(Get-ChildItem -Path $Repo -Recurse -Filter *.csproj).Count
        $sdk  = @(Get-ChildItem -Path $Repo -Recurse -Filter *.csproj | Where-Object { (Get-Content $_.FullName -Raw) -match '<Project\s+Sdk=' }).Count
        $left = @(Get-ChildItem -Path $Repo -Recurse -Filter packages.config).Count
        Say ""
        Say "After: $sdk of $all project file(s) in the SDK format, $left packages.config left."
    }
    finally { Pop-Location }
}

$afterStarted = Get-Date

Step 'Does it restore and build after' {
    # The answer to the first open question. Everything the repository says
    # about twenty-nine projects out of thirty-one turns on this one exit code.
    $sln = FindSolution

    Push-Location $sln.DirectoryName
    try {
        $restore = @('restore', $sln.FullName)
        if ($FrameworkRoot) { $restore += "-p:TargetFrameworkRootPath=$FrameworkRoot" }
        Native 'after-restore' 'dotnet' $restore 10 | Out-Null
        Say ""
        $code = Native 'after-build' 'msbuild' (MSBuildArguments $sln.FullName) 30
        Tally 'after-build'

        Say ""
        Say $(if ($code -eq 0)
              { "**It builds.** The word *converted* has been earned." }
              else
              { "**It does not build.** That is the finding, and it is worth more than a green tick would have been." })
    }
    finally { Pop-Location }
}

if (-not $SkipCompetitor) {
Step 'What the tool everybody already has would have done' {
    # A competitor, not a component. Three milestones went into the conversions
    # while upgrade-assistant was alive and doing the same work, and the honest
    # way to find out whether that was a mistake is to run both on the same
    # solution on the same day.
    #
    # Run against a fresh copy, because it writes into the tree rather than
    # printing a patch, and comparing two tools means neither goes first.
    $theirs = Join-Path $Out 'upgrade-assistant-copy'

    if (Test-Path $theirs) { Remove-Item -Recurse -Force $theirs }
    Copy-Item -Recurse $Repo $theirs

    Native 'upgrade-assistant-install' 'dotnet' @('tool', 'install', '--global', 'upgrade-assistant') 1 | Out-Null

    Push-Location $theirs
    try {
        $solution = Get-ChildItem -Filter *.sln -Recurse | Select-Object -First 1
        Native 'upgrade-assistant' 'upgrade-assistant' @('upgrade', $solution.FullName, '--non-interactive', '--operation', 'Inplace') 30 | Out-Null
    }
    finally { Pop-Location }

    Say ""
    Say "### Side by side"
    Say ""

    $ours   = (Get-ChildItem -Path $Repo   -Recurse -Filter *.csproj | Where-Object { (Get-Content $_.FullName -Raw) -match 'Sdk=' }).Count
    $theirsCount = (Get-ChildItem -Path $theirs -Recurse -Filter *.csproj | Where-Object { (Get-Content $_.FullName -Raw) -match 'Sdk=' }).Count

    Say "| | project files in the SDK format |"
    Say "|---|---|"
    Say "| Legacy Lens | $ours |"
    Say "| upgrade-assistant | $theirsCount |"
    Say ""
    Say "A count is not a verdict. What matters is which of the two produced"
    Say "something that builds, and the step above answered that for ours."
}
}
else {
    Say ""
    Say "## What the tool everybody already has would have done"
    Say ""
    Say "Not run on this occasion (``-SkipCompetitor``)."
}

Step 'What characterization reaches on a real Framework estate' {
    # The second open question. On modern code: 402 members examined, 11
    # callable. Nobody knows what that is on Framework code, and half the
    # product's claim rests on the answer.
    #
    # The solution's own assemblies, by the names its project files give them,
    # rather than every dll under a bin folder: a web application's bin holds
    # its whole dependency tree, and characterizing log4net says nothing about
    # this codebase. Found wherever the build put them, which is not always
    # a bin folder: nopCommerce writes its plugins into the web project.
    $names = @{}
    foreach ($project in Get-ChildItem -Path $Repo -Recurse -Filter *.csproj) {
        $text = Get-Content $project.FullName -Raw
        $name = if ($text -match '<AssemblyName>\s*([^<\s]+)\s*</AssemblyName>') { $Matches[1] } else { $project.BaseName }
        $names[$name] = $true
    }

    $found = Get-ChildItem -Path $Repo -Recurse -Filter *.dll |
        Where-Object { $_.FullName -notmatch '\\(obj|packages)\\' -and $names.ContainsKey($_.BaseName) } |
        Group-Object BaseName |
        ForEach-Object { $_.Group | Sort-Object LastWriteTime -Descending | Select-Object -First 1 } |
        Sort-Object Name

    $assemblies = @($found | Select-Object -First $Assemblies)

    Say "$($names.Count) project(s); $(@($found).Count) of their assemblies are on disk after the builds; $($assemblies.Count) characterized, $MinutesPerAssembly minute(s) each at most."
    Say ""
    Say "An assembly written before the after-build started is marked *before*: it is what the solution built as it stood, because the converted project did not produce one."
    Say ""

    $rows = @()

    foreach ($assembly in $assemblies) {
        $stem = Join-Path $Out "characterize-$($assembly.BaseName)"
        $process = Start-Process -FilePath $Lens `
            -ArgumentList @('characterize', "`"$($assembly.FullName)`"") `
            -RedirectStandardOutput "$stem.out.txt" -RedirectStandardError "$stem.err.txt" `
            -NoNewWindow -PassThru

        $finished = $process.WaitForExit($MinutesPerAssembly * 60 * 1000)
        if (-not $finished) {
            try { $process.Kill($true) } catch { }
        }

        $text = (Get-Content "$stem.out.txt" -Raw -ErrorAction SilentlyContinue) + ''
        $errorText = (Get-Content "$stem.err.txt" -Raw -ErrorAction SilentlyContinue) + ''

        $number = { param($label) if ($text -match "$label\s+(\d+)") { [int]$Matches[1] } else { $null } }

        $row = [pscustomobject]@{
            Name     = $assembly.Name
            Built    = $(if ($assembly.LastWriteTime -ge $afterStarted) { 'after' } else { 'before' })
            Callable = & $number 'methods callable'
            Calls    = & $number 'calls made'
            Tests    = & $number 'tests kept'
            Outcome  = $(if (-not $finished) { "stopped after $MinutesPerAssembly min" }
                         elseif ($errorText -match 'Could not load') { 'did not load' }
                         elseif ($text -match 'methods callable') { 'ran' }
                         else { "exit $($process.ExitCode), nothing reported" })
        }
        $rows += $row
        Say "- ``$($row.Name)`` ($($row.Built)): $($row.Outcome); callable $($row.Callable), calls $($row.Calls), tests kept $($row.Tests)"
    }

    $ran = @($rows | Where-Object Outcome -eq 'ran')
    $sum = { param($property) ($ran | Measure-Object -Property $property -Sum).Sum }

    Say ""
    Say "| | |"
    Say "|---|---|"
    Say "| assemblies characterized | $($rows.Count) |"
    Say "| ran to the end | $($ran.Count) |"
    Say "| did not load | $(@($rows | Where-Object Outcome -eq 'did not load').Count) |"
    Say "| stopped for time | $(@($rows | Where-Object Outcome -like 'stopped*').Count) |"
    Say "| methods callable | $(& $sum 'Callable') |"
    Say "| calls made | $(& $sum 'Calls') |"
    Say "| tests kept | $(& $sum 'Tests') |"

    # What stopped the rest, summed across every assembly that ran. The
    # refusals are the finding, on modern code and here.
    $reasons = @{}
    foreach ($row in $ran) {
        $stem = Join-Path $Out "characterize-$([System.IO.Path]::GetFileNameWithoutExtension($row.Name))"
        $inside = $false
        foreach ($line in Get-Content "$stem.out.txt") {
            if ($line -match '^Not characterized') { $inside = $true; continue }
            if ($inside -and $line -match '^\s+(\d+)\s+(.+)$') {
                $reasons[$Matches[2].Trim()] = [int]$reasons[$Matches[2].Trim()] + [int]$Matches[1]
            }
        }
    }

    if ($reasons.Count -gt 0) {
        Say ""
        Say "| not characterized, summed | count |"
        Say "|---|---|"
        $reasons.GetEnumerator() | Sort-Object Value -Descending | Select-Object -First 15 |
            ForEach-Object { Say "| $($_.Key -replace '\|', '\|') | $($_.Value) |" }
    }

    $rows | ConvertTo-Json | Out-File (Join-Path $Out 'characterization.json') -Encoding utf8
}

Step 'And whether behaviour can be compared at all here' {
    Say "Behaviour comparison needs two versions of one file that both compile"
    Say "on this runtime. On a Framework estate the original usually does not,"
    Say "and the honest answer is *not checked*. Recorded so the next run knows"
    Say "it was asked rather than forgotten."
}

Say ""
Say "---"
Say ""
Say "Written by ``measure.ps1``. Nothing outside ``$Repo`` and ``$Out`` was touched."

Write-Host ""
Write-Host "Report: $log"
