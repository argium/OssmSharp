# OssmSharp

OssmSharp is a .NET 10 toolkit for controlling a compatible OSSM device over
Bluetooth on Windows and macOS. It provides:

- a local user web application for connecting the device, stopping motion, and
  sharing a guest invitation;
- a guest web application with requested-setting controls and motion previews;
- an interactive command-line interface for direct local control; and
- a reusable runtime and control API for .NET applications.

The web applications and API listen on loopback by default. The guest application
can be exposed through a same-machine HTTPS reverse proxy, but the user application
and control API must remain private.

## Prerequisites

The quick starts require a compatible OSSM device, Bluetooth, Git, PowerShell 7.4
or later, and the .NET 10 SDK. Install the **SDK**, not only the .NET runtime.

### Windows

The supported quick starts target 64-bit Windows 10 version 2004 (build 19041) or
later.

Open Windows Terminal or PowerShell and install the tools:

```powershell
winget install --exact --id Microsoft.DotNet.SDK.10
winget install --exact --id Microsoft.PowerShell
winget install --exact --id Git.Git
```

Open a new terminal after installation, then verify the commands:

```powershell
dotnet --version
pwsh --version
git --version
```

Enable Bluetooth before starting OssmSharp. Windows may ask for Bluetooth
permission the first time the application scans.

### macOS

The supported quick starts target Apple silicon and macOS 26 or later.

1. Install the Arm64 [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).
2. Install [PowerShell 7](https://learn.microsoft.com/powershell/scripting/install/installing-powershell-on-macos).
3. Install Xcode 27 from the App Store, open it once, and let it finish installing
   its components. Xcode includes Git.
4. Open a new terminal and install the matching .NET macOS workload:

   ```powershell
   dotnet workload install macos
   ```

Verify the commands:

```powershell
dotnet --version
pwsh --version
git --version
```

Enable Bluetooth before starting OssmSharp. macOS may ask for Bluetooth
permission the first time the application scans.

## Quick start: user and guest web applications

This path runs the web applications without .NET Aspire AppHost. It builds and
starts the private control API, local user application, and guest application with
matching settings.

```powershell
git clone https://github.com/argium/OssmSharp.git
cd OssmSharp
pwsh -File ./run-web.ps1
```

Wait for the script to print:

```text
User controls: http://127.0.0.1:5002
Guest web:     http://127.0.0.1:5001
```

Open `http://127.0.0.1:5002`, connect the device, and use the invitation shown
there for the guest browser. The control API remains private at
`http://127.0.0.1:5003`.

Keep the terminal open. Press <kbd>Ctrl</kbd>+<kbd>C</kbd> to stop all three
applications. Use `pwsh -File ./run-web.ps1 -?` to see port and device-selection
options.

## Quick start: CLI

Clone the repository if you did not do so above:

```powershell
git clone https://github.com/argium/OssmSharp.git
cd OssmSharp
```

Publish the self-contained CLI once:

```powershell
pwsh -File ./make.ps1
```

List nearby devices:

```powershell
pwsh -File ./make.ps1 run list
```

Start the interactive prompt and connect to the first discovered device:

```powershell
pwsh -File ./make.ps1 run prompt
```

To select a specific device, copy its identifier from `list`:

```powershell
pwsh -File ./make.ps1 run prompt --device <identifier>
```

Type `help` inside the prompt for motion, status, stop, and reset commands.

## Using .NET Aspire AppHost

AppHost starts the same control API, user application, and guest application with
service discovery and an Aspire dashboard:

```powershell
dotnet run --project ./src/OssmSharp.AppHost
```

Development ports and the optional reverse-proxy hostname are configured in
[`src/OssmSharp.AppHost/appsettings.Development.json`](src/OssmSharp.AppHost/appsettings.Development.json).
The checked-in settings use fixed user and guest ports so their URLs stay stable.
AppHost generates the private user-service key automatically.

## Remote guest access

The guest application supports a same-machine HTTPS reverse proxy, including
Tailscale and Cloudflare Tunnel. Expose **only the guest application**, not the
user application or control API. A guest still needs an invitation, and the local
user must allow control.

Set the public origin and fixed listener ports in
[`src/OssmSharp.AppHost/appsettings.Development.json`](src/OssmSharp.AppHost/appsettings.Development.json):

```json
{
  "Parameters": {
    "guest-hostname": "https://guest.example",
    "guest-port": 5001,
    "admin-port": 5002
  }
}
```

Restart AppHost, then configure the tunnel to forward HTTPS and WebSockets to
`http://127.0.0.1:5001`. Preserve the browser's public `Host` and `Origin`, and
forward `X-Forwarded-Proto: https`. Do not rewrite `Host` to the loopback target.
Share the invitation link from the local user application.

[Tailscale Serve](https://tailscale.com/kb/1242/tailscale-serve) requires the guest
to be on your tailnet; Funnel provides public access. For Cloudflare, route the
public hostname to the loopback target and set
[`httpHostHeader`](https://developers.cloudflare.com/cloudflare-one/networks/connectors/cloudflare-tunnel/configure-tunnels/origin-parameters/)
to the public authority if the tunnel rewrites `Host`.

The public origin must be HTTPS with no path, credentials, query, or fragment.
Root hosting is supported, not a subpath. Restart when the public URL changes.
Without `guest-hostname`, access remains local-only.

For standalone hosts, configure:

| Host | Settings |
| --- | --- |
| Guest web | `OSSM__Guest__GuestPort=5001`, `OSSM__Guest__Hostname=https://guest.example`, and `OSSM__ControlClient__EndpointUri=http://127.0.0.1:5003` |
| User web | `OSSM__Admin__ListenPort=5002`, `OSSM__Admin__GuestHostname=https://guest.example`, the same control origin, and `OSSM__ControlClient__ServiceKey` |
| Control API | `ASPNETCORE_URLS=http://127.0.0.1:5003` and `OSSM__Control__AdminWebServiceKey` matching the user web service key |

Use a random canonical Base64url-encoded 32-byte service key. Keep the standalone
API and user application private. The guest application always listens on IPv4
loopback; listener overrides are rejected.

The guest application authenticates its server-side API connection with the
invitation. Joining consumes the invitation and binds that connection. Invitation
rotation blocks replay without terminating an admitted connection. Control remains
separately approved and is revoked when the guest leaves, the local user ends the
guest session, or the connection closes.

## Speed control

The runtime, CLI, control API, sliders, and previews use speed percentages.
Requested speed and stroke are integers in `[0, 100]`, using `50` for 50%.
Only the device runtime converts requested speed to raw velocity:

```text
raw velocity = floor(speed * stroke / 100)
```

Depth and sensation do not affect the conversion. Zero speed or stroke, or a
calculated raw velocity below 1%, sends zero raw velocity.

Current speed comes from raw velocity and stroke in the same device snapshot. The
runtime preserves decimals and values above 100%, without rounding or limiting.
Speed is unknown at zero device stroke. Pending selections cannot change it.

For example, 53% requested speed at 10% stroke sends 5% raw velocity and reads
back as 50% current speed, not 53%. CLI status shows `%` or `Unknown`.

The guest preview uses the requested selection. The user preview uses the last
complete requested selection successfully sent by the API session. Rejected,
cancelled, superseded, or partial requests do not replace it. Stop retains that
selection. Preview timing is illustrative and depends on speed, not stroke length
or depth. These percentages are not physical speed or strokes per minute, and the
previews are not measurements of physical position or calibrated cycle time.

## Troubleshooting

### A port is already in use

The standalone launcher reports the unavailable port before building. Stop the
other application or select three different ports:

```powershell
pwsh -File ./run-web.ps1 -GuestPort 5101 -AdminPort 5102 -ApiPort 5103
```

### The macOS workload requires a different Xcode version

If the build says the installed .NET macOS workload requires a different Xcode
version, update Xcode and refresh the workload for the SDK selected by
[`global.json`](global.json):

```powershell
dotnet workload update
dotnet workload install macos
```
