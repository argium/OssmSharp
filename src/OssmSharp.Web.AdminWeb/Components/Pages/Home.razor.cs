using System.Globalization;

namespace OssmSharp.Web.AdminWeb.Components.Pages;

/// <summary>
/// Renders the loopback-only user control page.
/// </summary>
public partial class Home
{
    private const double CentimetresPerInch = 2.54;
    /// <summary>The attachment length used until the user chooses one, in centimetres.</summary>
    private const double DefaultAttachmentLengthCm = 24;

    private readonly CancellationTokenSource _lifetime = new();
    private UserNoticeState _notice = null!;
    private UserSessionState? _state;
    private IJSObjectReference? _module;
    private Task? _pollingTask;
    private bool _loaded;
    private bool _busy;
    private bool _safetyBusy;
    private bool _connecting;
    private bool _connectionLost;
    private bool _previewStopped;
    private double _attachmentLengthCm = DefaultAttachmentLengthCm;

    private DeviceSettingsSnapshot? _stoppedSettings;
    private string _noticeVariant = "success";

    [Inject]
    private IAdminApiClient Control { get; set; } = null!;
    [Inject]
    private IJSRuntime Js { get; set; } = null!;
    [Inject]
    private TimeProvider Time { get; set; } = null!;
    [Inject]
    private IOptions<AdminOptions> Options { get; set; } = null!;

    private string InvitationText =>
        $"{Options.Value.GuestHostname.TrimEnd('/')}/join#invite={Uri.EscapeDataString(_state!.Invitation.Token)}";
    private string GuestStatus => _state switch
    {
        { GuestJoined: false } => "No guest",
        { GuestControlAllowed: true } => "Guest allowed",
        _ => "Guest joined",
    };
    private string GuestVariant => _state switch
    {
        { GuestControlAllowed: true } => "success",
        { GuestJoined: true } => "warning",
        _ => "neutral",
    };
    private string ConnectionStatus => _connecting ? "Connecting" : _state?.Device switch
    {
        { IsConnecting: true } => "Connecting",
        { IsConnected: true } => "Connected",
        { ConnectionFailed: true } => "Failed",
        null => "Unknown",
        _ => "Disconnected",
    };
    private string ConnectionVariant => ConnectionStatus switch
    {
        "Connected" => "success",
        "Connecting" => "warning",
        "Failed" => "danger",
        _ => "neutral",
    };
    private bool DeviceUnavailable => _connectionLost || _state?.Device is not { IsConnected: true, IsConnecting: false };
    private LengthUnit Unit => Options.Value.LengthUnit;
    private string LengthUnitLabel => Unit is LengthUnit.Inches ? "\"" : "cm";
    private string LengthStep => Unit is LengthUnit.Inches ? "0.25" : "0.5";
    private string RailLengthText => LengthText(Options.Value.RailLengthCm);
    private string AttachmentLengthText => LengthText(_attachmentLengthCm);

    /// <summary>
    /// Cancels and awaits polling before releasing the control connection.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        await _lifetime.CancelAsync();
        if (_pollingTask is not null)
            await _pollingTask;
        await Control.DisposeAsync();
        if (_module is not null)
        {
            try
            {
                await _module.InvokeVoidAsync("disposeConnectionTimer");
                await _module.DisposeAsync();
            }
            catch (JSDisconnectedException) { }
        }
        _lifetime.Dispose();
        GC.SuppressFinalize(this);
    }

    protected override void OnInitialized()
    {
        _notice = new(Time);
        _attachmentLengthCm = ClampAttachmentLength(DefaultAttachmentLengthCm);
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            _module = await Js.InvokeAsync<IJSObjectReference>("import", "./admin.js");
            _attachmentLengthCm = ClampAttachmentLength(await _module.InvokeAsync<double>("readAttachmentLength", _attachmentLengthCm));
            StateHasChanged();
            _pollingTask = PollAsync();
        }
        if (_module is not null)
            await _module.InvokeVoidAsync("updateConnectionTimer");
    }

    private async Task PollAsync()
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1), Time);
            string? renderedMessage = null;
            string? renderedCopyLabel = null;
            do
            {
                var state = await Control.GetStateAsync(_lifetime.Token);
                if (_previewStopped && !_busy && !_safetyBusy && state.Device?.TransmittedSettings != _stoppedSettings)
                    _previewStopped = false;
                if (!_loaded || _state != state || renderedMessage != _notice.Message || renderedCopyLabel != _notice.CopyLabel)
                {
                    _state = state;
                    _loaded = true;
                    renderedMessage = _notice.Message;
                    renderedCopyLabel = _notice.CopyLabel;
                    await InvokeAsync(StateHasChanged);
                }
            }
            while (await timer.WaitForNextTickAsync(_lifetime.Token));
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception exception) when (
            exception is ApiClientException or RpcClientRequestTimeoutException)
        {
            _loaded = true;
            _connectionLost = true;
            _notice.Show("Unable to connect to the user service. Reload the page.", true);
            await InvokeAsync(StateHasChanged);
        }
    }

    private Task AllowGuest() => RunAsync(Control.AllowGuestAsync, null);
    private Task Stop() => RunAsync(Control.StopAsync, "Stopping ...", safety: true, stopPreview: true);
    private Task ResetPosition() => RunAsync(Control.ResetPositionAsync, "Position reset. Stopped.", stopPreview: true, pendingMessage: "Resetting position ...");
    private Task EndGuest() => RunAsync(Control.EndGuestAsync, "Guest session ended.", safety: true);
    private Task Connect() => RunAsync(Control.ConnectAsync, null, confirmConnection: true);

    private async Task SetAttachmentLength(ChangeEventArgs args)
    {
        if (!double.TryParse(args.Value?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var entered)
            || !double.IsFinite(entered))
            return;
        _attachmentLengthCm = ClampAttachmentLength(Unit is LengthUnit.Inches ? entered * CentimetresPerInch : entered);
        if (_module is not null)
            await _module.InvokeVoidAsync("writeAttachmentLength", _attachmentLengthCm);
    }

    private double ClampAttachmentLength(double centimetres) =>
        double.IsFinite(centimetres) ? Math.Clamp(centimetres, 0, Options.Value.RailLengthCm) : 0;

    private string LengthText(double centimetres) =>
        (Unit is LengthUnit.Inches ? centimetres / CentimetresPerInch : centimetres).ToString("0.##", CultureInfo.InvariantCulture);

    private async Task Copy()
    {
        if (_module is null || _state is null)
            return;
        _notice.CopyCompleted(await _module.InvokeAsync<bool>("copy", InvitationText));
    }

    private async Task RunAsync(Func<CancellationToken, Task<ControlResponse>> command, string? message, bool safety = false, bool confirmConnection = false, bool stopPreview = false, string? pendingMessage = null)
    {
        if (_safetyBusy || (!safety && _busy))
            return;
        if (safety)
            _safetyBusy = true;
        else
        {
            _busy = true;
            _connecting = confirmConnection;
        }
        _noticeVariant = stopPreview ? "warning" : "success";
        if (stopPreview)
        {
            _previewStopped = true;
            _stoppedSettings = _state?.Device?.TransmittedSettings;
            _notice.Show(pendingMessage ?? message);
            await InvokeAsync(StateHasChanged);
        }
        try
        {
            await command(_lifetime.Token);
            if (!safety || !_busy)
                _state = await Control.GetStateAsync(_lifetime.Token);
            if (stopPreview)
                _stoppedSettings = _state?.Device?.TransmittedSettings;
            if (confirmConnection && _state?.Device?.IsConnected != true)
                _notice.Show("Device connection is not confirmed. Check the connection state.", true);
            else
                _notice.Show(message);
        }
        catch (RpcClientRequestTimeoutException)
        {
            _notice.Show("The action outcome is unknown. Check the latest state before trying again.", true);
        }
        catch (ApiClientException error)
        {
            _notice.Show($"The action was rejected ({error.Code}).", true);
        }
        finally
        {
            if (safety)
                _safetyBusy = false;
            else
            {
                _busy = false;
                _connecting = false;
            }
            await InvokeAsync(StateHasChanged);
        }
    }

}
