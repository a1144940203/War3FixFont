param(
    [Parameter(Mandatory = $true)]
    [string] $ExecutablePath,
    [switch] $KeepTemporaryFiles,
    [switch] $Worker,
    [string] $TemporaryDirectory
)

# Run with Windows PowerShell 5.1: powershell.exe -NoProfile -STA -File .\tests\BackgroundRegression.ps1 -ExecutablePath <built exe>
# The executable and its adjacent runtime files are copied into a dedicated temporary directory.
# A real Warcraft III window causes all form tests to be skipped before Main is constructed.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if ($PSVersionTable.PSEdition -ne 'Desktop' -or [Threading.Thread]::CurrentThread.ApartmentState -ne 'STA') {
    throw 'Run this script using Windows PowerShell 5.1 with -STA.'
}

$sourceExe = (Resolve-Path -LiteralPath $ExecutablePath).ProviderPath
$sourceDirectory = [IO.Path]::GetDirectoryName($sourceExe)
$temporaryRoot = if ($Worker) { [IO.Path]::GetFullPath($TemporaryDirectory) } else { [IO.Path]::GetFullPath([IO.Path]::Combine([IO.Path]::GetTempPath(), 'War3FixFont-BackgroundTests-' + [Guid]::NewGuid().ToString('N'))) }
$originalLocation = Get-Location
$originalDirectory = [Environment]::CurrentDirectory
$script:checks = 0
$script:mainType = $null
$script:assembly = $null
$script:loopError = $null
$script:form = $null
$script:configPath = $null
$privateInstance = [Reflection.BindingFlags]'Instance, NonPublic'

function Assert-That([bool] $Condition, [string] $Description) {
    if (-not $Condition) { throw "FAIL: $Description" }
    $script:checks++
    Write-Host "PASS: $Description"
}

function Get-Field($Instance, [string] $Name) {
    $field = $Instance.GetType().GetField($Name, $privateInstance)
    if ($null -eq $field) { throw "Missing field: $Name" }
    return $field.GetValue($Instance)
}

function Invoke-Private($Instance, [string] $Name, [object[]] $Arguments = @()) {
    $method = $Instance.GetType().GetMethod($Name, $privateInstance)
    if ($null -eq $method) { throw "Missing method: $Name" }
    return $method.Invoke($Instance, $Arguments)
}

function Test-GameWindowAbsent {
    $api = $script:assembly.GetType('War3FixFont.WinAPI.API', $true)
    $find = $api.GetMethod('FindWindowA')
    foreach ($className in @('Warcraft III', 'Black Warcraft III')) {
        if ($find.Invoke($null, [object[]]@($className, 'Warcraft III')) -ne [IntPtr]::Zero) {
            return $false
        }
    }
    return $true
}

function Get-SavedSettings {
    return (Get-Content -LiteralPath $script:configPath -Raw | ConvertFrom-Json)
}

function Exit-TestForm($Instance) {
    if ($null -ne $Instance -and -not $Instance.IsDisposed) {
        Invoke-Private $Instance 'ExitApplication' ([object[]]@($null, [EventArgs]::Empty))
        [Windows.Forms.Application]::DoEvents()
    }
}

function Invoke-InMessageLoop([scriptblock] $Checks) {
    if (-not (Test-GameWindowAbsent)) {
        throw 'A Warcraft III window appeared; refusing to construct Main.'
    }

    $script:form = [Activator]::CreateInstance($script:mainType)
    $script:loopError = $null
    $script:activeChecks = $Checks
    $tick = New-Object Windows.Forms.Timer
    $tick.Interval = 100
    $tick.Add_Tick({
        $tick.Stop()
        try {
            & $script:activeChecks $script:form
        }
        catch {
            $script:loopError = $_
        }
        finally {
            try { Exit-TestForm $script:form }
            catch {
                if ($null -eq $script:loopError) { $script:loopError = $_ }
                [Windows.Forms.Application]::ExitThread()
            }
        }
    })

    try {
        $tick.Start()
        [Windows.Forms.Application]::Run($script:form)
        if ($null -ne $script:loopError) { throw $script:loopError }
        Assert-That $script:form.IsDisposed 'tray Exit closes the main form and ends its message loop'
    }
    finally {
        $tick.Dispose()
        if ($null -ne $script:form -and -not $script:form.IsDisposed) {
            try { Exit-TestForm $script:form }
            finally { $script:form.Dispose() }
        }
        $script:form = $null
    }
}

function Assert-InitialHidden($Instance) {
    Assert-That (-not $Instance.Visible) 'the first Application.Run display request leaves the form hidden'
    Assert-That (-not $Instance.ShowInTaskbar) 'the background form has no taskbar button'
    Assert-That $Instance.IsHandleCreated 'the hidden main form has a window handle'
    Assert-That (Get-Field $Instance 'NotifyIcon').Visible 'the notification-area icon is available at startup'
}

function Assert-Cleanup($Instance, $Timer, $Monitor, $Icon) {
    Exit-TestForm $Instance
    Assert-That $Instance.IsDisposed 'tray Exit disposes the form'
    Assert-That (-not $Timer.Enabled) 'tray Exit stops the repair timer'
    Assert-That (-not $Monitor.Enabled) 'tray Exit stops the process monitor'
    Assert-That (-not $Icon.Visible) 'tray Exit removes the notification-area icon'
    $hook = Get-Field $Instance '_hook'
    $nativeWindow = Get-Field $hook '_window'
    Assert-That ($nativeWindow.Handle -eq [IntPtr]::Zero) 'tray Exit destroys the hotkey message window'
}

function Test-SilentSecondInstance([string] $CopiedExe) {
    $created = $false
    $mutex = New-Object Threading.Mutex($true, 'War3FixFont', [ref] $created)
    $process = $null
    try {
        $worker = Join-Path $temporaryRoot 'SecondInstance.ps1'
        $workerOutput = Join-Path $temporaryRoot 'second-instance.stdout.txt'
        $workerError = Join-Path $temporaryRoot 'second-instance.stderr.txt'
        @'
param([string] $ExePath)
$ErrorActionPreference = 'Stop'
try {
    $assembly = [Reflection.Assembly]::LoadFrom($ExePath)
    $entry = $assembly.EntryPoint
    if ($entry.GetParameters().Length -eq 0) {
        $entry.Invoke($null, $null) | Out-Null
    } else {
        $entry.Invoke($null, [object[]]@([string[]]@())) | Out-Null
    }
    Write-Output 'ENTRYPOINT_RETURNED'
    exit 0
} catch {
    Write-Error $_
    exit 1
}
'@ | Set-Content -LiteralPath $worker -Encoding UTF8

        $windowsPowerShell = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
        $workerArguments = '-NoProfile -NonInteractive -STA -ExecutionPolicy Bypass -File "' + $worker + '" -ExePath "' + $CopiedExe + '"'
        $process = Start-Process -FilePath $windowsPowerShell -ArgumentList $workerArguments -WorkingDirectory $temporaryRoot -WindowStyle Hidden -PassThru -RedirectStandardOutput $workerOutput -RedirectStandardError $workerError
        $null = $process.Handle
        $completed = $process.WaitForExit(15000)
        if (-not $completed) {
            $process.Kill()
            $process.WaitForExit()
            throw 'The second entry point did not return in 15 seconds; a modal duplicate-instance dialog may be blocking it.'
        }
        $process.WaitForExit()
        $output = Get-Content -LiteralPath $workerOutput -Raw
        $errorText = Get-Content -LiteralPath $workerError -Raw
        Assert-That ($process.ExitCode -eq 0 -and $output -match 'ENTRYPOINT_RETURNED') ("a second instance returns without a modal dialog; exit=$($process.ExitCode), stderr=$errorText")
    }
    finally {
        if ($null -ne $process) {
            if (-not $process.HasExited) { $process.Kill(); $process.WaitForExit() }
            $process.Dispose()
        }
        if ($created) { $mutex.ReleaseMutex() }
        $mutex.Dispose()
    }
}

try {
    if (-not $Worker) {
        New-Item -ItemType Directory -Path $temporaryRoot | Out-Null
        foreach ($file in Get-ChildItem -LiteralPath $sourceDirectory -File) {
            if ($file.Extension -in @('.exe', '.dll', '.config')) {
                Copy-Item -LiteralPath $file.FullName -Destination $temporaryRoot
            }
        }
        $copiedExe = Join-Path $temporaryRoot ([IO.Path]::GetFileName($sourceExe))
        $windowsPowerShell = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
        & $windowsPowerShell -NoProfile -STA -ExecutionPolicy Bypass -File $PSCommandPath -ExecutablePath $copiedExe -Worker -TemporaryDirectory $temporaryRoot
        if ($LASTEXITCODE -ne 0) { throw "Regression worker failed with exit code $LASTEXITCODE." }
        return
    }
    $copiedExe = Join-Path $temporaryRoot ([IO.Path]::GetFileName($sourceExe))
    $script:configPath = Join-Path $temporaryRoot ([IO.Path]::GetFileNameWithoutExtension($sourceExe) + '.json')
    Set-Location -LiteralPath $temporaryRoot
    [Environment]::CurrentDirectory = $temporaryRoot

    Add-Type -AssemblyName System.Windows.Forms
    $script:assembly = [Reflection.Assembly]::LoadFrom($copiedExe)
    $script:mainType = $script:assembly.GetType('War3FixFont.Main', $true)
    [Windows.Forms.Application]::EnableVisualStyles()
    [Windows.Forms.Application]::SetCompatibleTextRenderingDefault($false)

    Test-SilentSecondInstance $copiedExe

    if (-not (Test-GameWindowAbsent)) {
        Write-Output 'SKIP: A Warcraft III window is present. Main was not constructed and no game window was modified.'
    }
    else {
        # Omit UseTimer to verify that Settings defaults still activate repair on first launch.
        @{
            TimerInterval = 180
            AutoApplyWindow = $false
            LockCursor = $false
            UseHotKey = $false
            ShowMe = '1,1,1,F24'
        } | ConvertTo-Json | Set-Content -LiteralPath $script:configPath -Encoding UTF8

        $otherWorkingDirectory = Join-Path $temporaryRoot 'different-working-directory'
        New-Item -ItemType Directory -Path $otherWorkingDirectory | Out-Null
        Set-Location -LiteralPath $otherWorkingDirectory
        [Environment]::CurrentDirectory = $otherWorkingDirectory

        Invoke-InMessageLoop {
            param($instance)
            Assert-InitialHidden $instance
            $timer = Get-Field $instance '_timer'
            $monitor = Get-Field $instance '_war3RunningMonitor'
            $icon = Get-Field $instance 'NotifyIcon'
            $interval = Get-Field $instance 'IntervalInput'
            Assert-That $timer.Enabled 'repair starts on first launch with the default enabled setting'
            Assert-That ($timer.Interval -eq 180000 -and $interval.Value -eq 180) 'an existing interval greater than 100 seconds loads and starts correctly'
            Assert-That ($interval.Minimum -eq 1 -and $interval.Maximum -eq 86400) 'the interval input accepts 1 through 86400 seconds'
            Assert-That ((Get-Field $instance '_lastFixTicks') -gt 0) 'enabled repair makes an immediate repair attempt'

            $settingsManager = $instance.SettingsManager
            Assert-That ((Get-Field $settingsManager '_path') -eq $script:configPath) 'configuration stays beside the executable when the working directory differs'
            $beforeTick = Get-Field $instance '_lastFixTicks'
            $interval.Value = 1
            $elapsed = [Diagnostics.Stopwatch]::StartNew()
            while ($elapsed.ElapsedMilliseconds -lt 3000 -and (Get-Field $instance '_lastFixTicks') -eq $beforeTick) {
                [Windows.Forms.Application]::DoEvents()
                [Threading.Thread]::Sleep(25)
            }
            $elapsed.Stop()
            Assert-That ((Get-Field $instance '_lastFixTicks') -gt $beforeTick) 'the hidden message loop executes a real one-second repair timer tick'
            Assert-That (-not $instance.Visible) 'a running repair timer keeps the settings window hidden'

            (Get-Field $instance 'EnableTimerFixCheckBox').Checked = $false
            Assert-That (-not $timer.Enabled) 'disabling repair stops its timer'
            Assert-That (-not (Get-SavedSettings).UseTimer) 'disabled repair is saved to the isolated configuration'
            $interval.Value = 900
            Assert-That ((Get-SavedSettings).TimerInterval -eq 900) 'an interval change is saved while repair is disabled'
            Assert-Cleanup $instance $timer $monitor $icon
        }

        Invoke-InMessageLoop {
            param($instance)
            Assert-InitialHidden $instance
            $timer = Get-Field $instance '_timer'
            $monitor = Get-Field $instance '_war3RunningMonitor'
            $icon = Get-Field $instance 'NotifyIcon'
            Assert-That (-not $timer.Enabled) 'a saved disabled repair setting remains disabled after restart'
            Assert-That ((Get-Field $instance '_lastFixTicks') -eq 0) 'disabled repair does not make an immediate repair attempt'
            Assert-That ((Get-Field $instance 'IntervalInput').Value -eq 900) 'a saved interval is restored after restart'

            Invoke-Private $instance 'ShowWindow'
            [Windows.Forms.Application]::DoEvents()
            Assert-That ($instance.Visible -and $instance.ShowInTaskbar) 'tray Show displays the settings window and taskbar button'
            $instance.Close()
            [Windows.Forms.Application]::DoEvents()
            Assert-That (-not $instance.Visible -and -not $instance.IsDisposed) 'the X button hides the form while keeping the application alive'
            Assert-That $icon.Visible 'the tray icon remains available after closing settings'

            Invoke-Private $instance 'ShowWindow'
            $instance.WindowState = [Windows.Forms.FormWindowState]::Minimized
            [Windows.Forms.Application]::DoEvents()
            Assert-That (-not $instance.Visible -and -not $instance.ShowInTaskbar) 'minimizing settings returns the application to the tray'
            Invoke-Private $instance 'ShowWindow'
            Assert-That ($instance.Visible -and $instance.WindowState -eq [Windows.Forms.FormWindowState]::Normal) 'tray Show restores a minimized settings window'

            (Get-Field $instance 'EnableTimerFixCheckBox').Checked = $true
            Assert-That ($timer.Enabled -and $timer.Interval -eq 900000) 'enabling repair uses the restored interval'
            Assert-That (Get-SavedSettings).UseTimer 'enabled repair is saved'
            $instance.Close()
            Assert-That ($timer.Enabled -and -not $instance.IsDisposed) 'closing settings keeps enabled background repair running'
            Assert-Cleanup $instance $timer $monitor $icon
        }

        Invoke-InMessageLoop {
            param($instance)
            Assert-InitialHidden $instance
            $timer = Get-Field $instance '_timer'
            Assert-That ($timer.Enabled -and $timer.Interval -eq 900000) 'saved enabled repair resumes automatically after restart'
            Assert-Cleanup $instance $timer (Get-Field $instance '_war3RunningMonitor') (Get-Field $instance 'NotifyIcon')
        }
    }
    Write-Output "RESULT: $script:checks checks passed."
}
finally {
    [Environment]::CurrentDirectory = $originalDirectory
    Set-Location -LiteralPath $originalLocation
    if ($Worker) {
        # The launcher removes the directory after this process exits and releases its assembly lock.
    }
    elseif ($KeepTemporaryFiles) {
        Write-Output "Temporary test files: $temporaryRoot"
    }
    elseif (Test-Path -LiteralPath $temporaryRoot) {
        # Recursive cleanup is allowed only after checking the exact absolute test directory.
        $resolved = (Resolve-Path -LiteralPath $temporaryRoot).ProviderPath
        $expectedParent = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')
        if ([IO.Path]::GetDirectoryName($resolved).TrimEnd('\') -ne $expectedParent -or
            [IO.Path]::GetFileName($resolved) -notmatch '^War3FixFont-BackgroundTests-[0-9a-f]{32}$') {
            throw "Refusing unexpected cleanup path: $resolved"
        }
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
