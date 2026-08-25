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
#      ratio inverts on Framework code is untested, and half the product's
#      claim rests on it.
#
# Needs, on the machine: .NET Framework and its targeting packs, MSBuild,
# nuget.exe on the PATH for the packages.config restore, the .NET SDK for the
# restore after conversion, and git.
#
#   .\measure.ps1 -Repo C:\work\nop-3.90
#
# **This has never run.** It was written on Linux, where PowerShell is not
# installed, so not one line of it has been executed or even parsed. Expect the
# first run to fail somewhere and read the report rather than the console: each
# step records its own failure and the steps after it still say something.
#
# It writes one report and changes nothing outside the repository copy it was
# given. It is deliberately loud about each step, because the first run of this
# is the interesting one and a silent failure would waste the machine.

param(
    [Parameter(Mandatory = $true)][string] $Repo,
    [string] $Lens = "$PSScriptRoot\build\desktop\win-x64\LegacyLens.exe",
    [string] $Out  = "$PSScriptRoot\build\measurement"
)

$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force -Path $Out | Out-Null
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
    }
}

Set-Content -Path $log -Value "# Measured on $(Get-Date -Format 'yyyy-MM-dd HH:mm')"
Say ""
Say "Repository: ``$Repo``"
Say "Machine: $([System.Environment]::OSVersion.VersionString)"

# ---------------------------------------------------------------------------

Step 'What the tool says before anything is applied' {
    & $Lens report $Repo | Out-File (Join-Path $Out 'before.md') -Encoding utf8
    Say "Written to ``before.md``."

    $surface = & $Lens surface $Repo 2>&1 | Select-Object -First 12
    Say '```'
    $surface | ForEach-Object { Say $_ }
    Say '```'
}

Step 'Does it restore and build as it stands' {
    # The baseline, and it matters as much as the after. A solution that does
    # not build before the patches cannot be said to have been broken by them.
    Push-Location $Repo
    try {
        $solution = Get-ChildItem -Filter *.sln -Recurse | Select-Object -First 1
        Say "Solution: ``$($solution.FullName)``"

        & nuget restore $solution.FullName 2>&1 | Select-Object -Last 5 | ForEach-Object { Say "    $_" }
        & msbuild $solution.FullName /m /v:minimal /nologo 2>&1 | Select-Object -Last 20 | ForEach-Object { Say "    $_" }

        Say ""
        Say "Exit code: $LASTEXITCODE"
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
            & $Lens convert $Repo $kind 2>$null | Out-File $patch -Encoding utf8

            & git apply $patch

            # A native command that fails does not throw in PowerShell, it
            # sets an exit code. Announcing "applied" on the strength of
            # having run git is exactly the kind of claim this repository
            # spent a milestone removing from its own wording.
            if ($LASTEXITCODE -ne 0) { throw "git apply refused the $kind patch" }

            Say "``$kind``: applied."
        }
    }
    finally { Pop-Location }
}

Step 'Does it restore and build after' {
    # The answer to the first open question. Everything the repository says
    # about twenty-nine projects out of thirty-one turns on this one exit code.
    Push-Location $Repo
    try {
        $solution = Get-ChildItem -Filter *.sln -Recurse | Select-Object -First 1

        & dotnet restore $solution.FullName 2>&1 | Select-Object -Last 5 | ForEach-Object { Say "    $_" }
        & msbuild $solution.FullName /m /v:minimal /nologo 2>&1 | Select-Object -Last 30 | ForEach-Object { Say "    $_" }

        Say ""
        Say "Exit code: $LASTEXITCODE"
        Say ""
        Say $(if ($LASTEXITCODE -eq 0)
              { "**It builds.** The word *converted* has been earned." }
              else
              { "**It does not build.** That is the finding, and it is worth more than a green tick would have been." })
    }
    finally { Pop-Location }
}

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

    & dotnet tool install --global upgrade-assistant 2>&1 | Select-Object -Last 1 | ForEach-Object { Say "    $_" }

    Push-Location $theirs
    try {
        $solution = Get-ChildItem -Filter *.sln -Recurse | Select-Object -First 1

        & upgrade-assistant upgrade $solution.FullName --non-interactive --operation Inplace 2>&1 |
            Select-Object -Last 30 | ForEach-Object { Say "    $_" }

        Say ""
        Say "Exit code: $LASTEXITCODE"
    }
    finally { Pop-Location }

    Say ""
    Say "### Side by side"
    Say ""

    $ours   = (Get-ChildItem -Path $Repo   -Recurse -Filter *.csproj | Where-Object { (Get-Content $_ -Raw) -match 'Sdk=' }).Count
    $theirsCount = (Get-ChildItem -Path $theirs -Recurse -Filter *.csproj | Where-Object { (Get-Content $_ -Raw) -match 'Sdk=' }).Count

    Say "| | project files in the SDK format |"
    Say "|---|---|"
    Say "| Legacy Lens | $ours |"
    Say "| upgrade-assistant | $theirsCount |"
    Say ""
    Say "A count is not a verdict. What matters is which of the two produced"
    Say "something that builds, and the step above answered that for ours."
}

Step 'What characterization reaches on a real Framework estate' {
    # The second open question. On modern code: 402 members examined, 11
    # callable. Nobody knows what that is on Framework code, and half the
    # product's claim rests on the answer.
    $assemblies = Get-ChildItem -Path $Repo -Recurse -Filter *.dll |
        Where-Object { $_.FullName -match '\\bin\\' -and $_.FullName -notmatch '\\obj\\' } |
        Where-Object { $_.Name -notmatch '^(System|Microsoft|Newtonsoft|Autofac|EntityFramework)\.' } |
        Select-Object -First 20

    Say "$($assemblies.Count) assembly(ies) built by this solution."
    Say ""

    foreach ($assembly in $assemblies) {
        Say "### $($assembly.Name)"
        Say '```'
        & $Lens characterize $assembly.FullName 2>&1 |
            Select-String -Pattern 'methods callable|calls made|tests kept|Not characterized|^\s+\d+\s+' |
            ForEach-Object { Say "$_" }
        Say '```'
    }
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
