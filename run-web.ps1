#Requires -Version 7.4
<#
.SYNOPSIS
Builds and runs the local user and guest web applications.
.DESCRIPTION
Builds the control API, user web application, and guest web application, then starts
them on fixed IPv4 loopback ports with a temporary administrator service key. The
script remains attached so that Ctrl+C stops every process it started.

The selected ports must be different and available. The device identifier is optional;
when omitted, the user can connect to a discovered device from the user web application.
.PARAMETER AdminPort
The loopback port for the local user web application. The default is 5002.
.PARAMETER GuestPort
The loopback port for the guest web application. The default is 5001.
.PARAMETER ApiPort
The loopback port for the private control API. The default is 5003.
.PARAMETER DeviceIdentifier
The device identifier to select automatically. Leave empty to use discovery.
.EXAMPLE
pwsh -File ./run-web.ps1
.EXAMPLE
pwsh -File ./run-web.ps1 -AdminPort 5102 -GuestPort 5101 -ApiPort 5103
.EXAMPLE
pwsh -File ./run-web.ps1 -WhatIf
.OUTPUTS
None. Application output and local URLs are written to the host.
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [ValidateRange(1024, 65535)]
    [int] $AdminPort = 5002,

    [ValidateRange(1024, 65535)]
    [int] $GuestPort = 5001,

    [ValidateRange(1024, 65535)]
    [int] $ApiPort = 5003,

    [string] $DeviceIdentifier = ''
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false

function New-ServiceKey {
    $bytes = [Security.Cryptography.RandomNumberGenerator]::GetBytes(32)
    return [Convert]::ToBase64String($bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_')
}

function Assert-PortAvailable {
    param(
        [Parameter(Mandatory)]
        [int] $Port
    )

    $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, $Port)
    try {
        $listener.Start()
    }
    catch {
        throw "Loopback port $Port is unavailable. Stop the process using it or select another port."
    }
    finally {
        $listener.Stop()
    }
}

function Invoke-DotNet {
    param(
        [Parameter(Mandatory)]
        [string[]] $Arguments
    )

    Write-Verbose "dotnet $($Arguments -join ' ')"
    & $script:DotNet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet exited with code $LASTEXITCODE."
    }
}

function New-ProcessEnvironment {
    param(
        [Parameter(Mandatory)]
        [hashtable] $Values
    )

    $environment = @{
        'ASPNETCORE_ENVIRONMENT' = 'Development'
        'DOTNET_ENVIRONMENT' = 'Development'
        'DOTNET_NOLOGO' = 'true'
    }
    foreach ($entry in $Values.GetEnumerator()) {
        $environment[$entry.Key] = $entry.Value
    }

    return $environment
}

function Start-WebProcess {
    param(
        [Parameter(Mandatory)]
        [string] $Name,

        [Parameter(Mandatory)]
        [string] $Project,

        [Parameter(Mandatory)]
        [string] $Framework,

        [Parameter(Mandatory)]
        [hashtable] $Environment
    )

    Write-Host "Starting $Name..."
    $arguments = @(
        'run',
        '--project', $Project,
        '--framework', $Framework,
        '--no-build',
        '--no-restore',
        '--no-launch-profile'
    )
    return Start-Process -FilePath $script:DotNet -ArgumentList $arguments `
        -WorkingDirectory $script:RepositoryRoot -NoNewWindow -PassThru `
        -Environment (New-ProcessEnvironment $Environment)
}

function Wait-Healthy {
    param(
        [Parameter(Mandatory)]
        [string] $Name,

        [Parameter(Mandatory)]
        [Uri] $Uri,

        [Parameter(Mandatory)]
        [Diagnostics.Process] $Process
    )

    $deadline = [Diagnostics.Stopwatch]::StartNew()
    while ($deadline.Elapsed -lt [TimeSpan]::FromSeconds(30)) {
        if ($Process.HasExited) {
            throw "$Name exited before becoming ready (exit code $($Process.ExitCode))."
        }

        try {
            $response = Invoke-WebRequest -Uri $Uri -TimeoutSec 1 -SkipHttpErrorCheck
            if ($response.StatusCode -eq 200) {
                return
            }
        }
        catch {
            Write-Debug "$Name is not ready: $($_.Exception.Message)"
        }

        Start-Sleep -Milliseconds 250
    }

    throw "$Name did not become healthy at $Uri within 30 seconds."
}

if ((@($AdminPort, $GuestPort, $ApiPort) | Sort-Object -Unique).Count -ne 3) {
    throw 'AdminPort, GuestPort, and ApiPort must use different ports.'
}

$script:RepositoryRoot = $PSScriptRoot
$script:DotNet = (Get-Command dotnet -CommandType Application -ErrorAction Stop).Path
$nativeFramework = if ($IsWindows) {
    'net10.0-windows10.0.19041.0'
}
elseif ($IsMacOS) {
    'net10.0-macos27.0'
}
else {
    throw 'The web applications support Windows and macOS only.'
}

if (-not $PSCmdlet.ShouldProcess(
    $RepositoryRoot,
    "Build and run the local web applications on ports $AdminPort, $GuestPort, and $ApiPort")) {
    return
}

foreach ($port in @($AdminPort, $GuestPort, $ApiPort)) {
    Assert-PortAvailable $port
}

$apiProject = 'src/OssmSharp.Web.Api/OssmSharp.Web.Api.csproj'
$guestProject = 'src/OssmSharp.Web.GuestWeb/OssmSharp.Web.GuestWeb.csproj'
$adminProject = 'src/OssmSharp.Web.AdminWeb/OssmSharp.Web.AdminWeb.csproj'

Push-Location -LiteralPath $RepositoryRoot
try {
    Write-Host 'Building the web applications...'
    Invoke-DotNet @('build', $apiProject, '--framework', $nativeFramework)
    Invoke-DotNet @('build', $guestProject, '--framework', 'net10.0')
    Invoke-DotNet @('build', $adminProject, '--framework', 'net10.0')
}
finally {
    Pop-Location
}

$serviceKey = New-ServiceKey
$apiOrigin = "http://127.0.0.1:$ApiPort"
$guestOrigin = "http://127.0.0.1:$GuestPort"
$adminOrigin = "http://127.0.0.1:$AdminPort"
$processes = [Collections.Generic.List[Diagnostics.Process]]::new()

try {
    $api = Start-WebProcess 'control API' $apiProject $nativeFramework @{
        'ASPNETCORE_URLS' = $apiOrigin
        'OSSM__Control__AdminWebServiceKey' = $serviceKey
        'OSSM__Control__DeviceIdentifier' = $DeviceIdentifier
    }
    $processes.Add($api)
    Wait-Healthy 'Control API' "$apiOrigin/health" $api

    $guest = Start-WebProcess 'guest web application' $guestProject 'net10.0' @{
        'OSSM__ControlClient__EndpointUri' = $apiOrigin
        'OSSM__ControlClient__ServiceKey' = $null
        'OSSM__Guest__GuestPort' = "$GuestPort"
        'OSSM__Guest__Hostname' = ''
    }
    $processes.Add($guest)

    $admin = Start-WebProcess 'user web application' $adminProject 'net10.0' @{
        'OSSM__Admin__GuestHostname' = $guestOrigin
        'OSSM__Admin__ListenPort' = "$AdminPort"
        'OSSM__ControlClient__EndpointUri' = $apiOrigin
        'OSSM__ControlClient__ServiceKey' = $serviceKey
    }
    $processes.Add($admin)

    Wait-Healthy 'Guest web application' "$guestOrigin/health" $guest
    Wait-Healthy 'User web application' "$adminOrigin/health" $admin

    Write-Host ''
    Write-Host "User controls: $adminOrigin"
    Write-Host "Guest web:     $guestOrigin"
    Write-Host 'Press Ctrl+C to stop all three applications.'

    while ($true) {
        foreach ($process in $processes) {
            if ($process.HasExited) {
                throw "A web process exited unexpectedly with code $($process.ExitCode)."
            }
        }
        Start-Sleep -Seconds 1
    }
}
finally {
    foreach ($process in $processes) {
        if (-not $process.HasExited) {
            $process.Kill($true)
            $process.WaitForExit(5000) | Out-Null
        }
        $process.Dispose()
    }
}
