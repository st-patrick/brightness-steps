# Creates the public GitHub repo, pushes, and cuts the v1.0.0 release with the
# installer attached. Run after `gh auth login`.
# Native tools report failure through exit codes; PowerShell turning their
# stderr into terminating errors just aborts on expected probes.
$ErrorActionPreference = 'Continue'
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$gh   = "C:\Program Files\GitHub CLI\gh.exe"
$repo = "brightness-steps"
$desc = "Your laptop's brightness keys, with usable steps at the dark end. A small Windows tray app."
$site = "https://projects.patrickreinbold.com/brightness-steps/"

& $gh auth status | Out-Null
if ($LASTEXITCODE -ne 0) { throw "not logged in - run: gh auth login" }

$user = (& $gh api user --jq .login).Trim()
Write-Host "authenticated as: $user"

Set-Location $root

& $gh repo view "$user/$repo" | Out-Null
if ($LASTEXITCODE -eq 0) {
    Write-Host "repo already exists: $user/$repo"
} else {
    & $gh repo create $repo --public --description $desc --homepage $site --source . --remote origin
    if ($LASTEXITCODE -ne 0) { throw "repo create failed" }
}

git remote get-url origin | Out-Null
if ($LASTEXITCODE -ne 0) { git remote add origin "https://github.com/$user/$repo.git" }

git push -u origin master
if ($LASTEXITCODE -ne 0) { throw "push failed" }

# Build a fresh installer so the release asset matches the pushed source.
& "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe" /Q "$root\installer\BrightnessSteps.iss"
if ($LASTEXITCODE -ne 0) { throw "installer build failed" }

$asset = "$root\dist\BrightnessSteps-1.0.0-setup.exe"
if (-not (Test-Path $asset)) { throw "installer missing at $asset" }

$notes = @'
First public release.

Windows moves screen brightness in jumps of 10, which leaves nothing usable at
the bottom of the range. This walks a 26-rung ladder instead, with 1-point steps
where your eyes need them, and adds six rungs *below* hardware zero ending in a
fully black screen.

- Installs per user, so no UAC prompt
- Tray icon only: no window, no service, no telemetry
- Needs .NET Framework 4.x, included with Windows 8 and later
- Built-in laptop panels only; external monitors need DDC/CI and are not supported

Not specific to any vendor or keyboard layout: keys are matched on the standard
HID brightness usages and decoded through each device's own report descriptor.
If it does not work on your machine, the tray menu has "Copy compatibility
report" - please open an issue with it.
'@

& $gh release create "v1.0.0" $asset --title "BrightnessSteps 1.0.0" --notes $notes
if ($LASTEXITCODE -ne 0) { throw "release failed" }

Write-Host ""
Write-Host "published: https://github.com/$user/$repo"
