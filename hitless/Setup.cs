using StackExchange.Redis;

namespace HitlessDemo;

// Setup.cs - the complete connection setup, and nothing else.
//
// Three ConfigurationOptions properties are the whole feature: MaintenanceNotifications (whether to ask the
// server for maintenance push notifications), MaintenanceMovingEndpointType (whether MOVING should name the
// replacement proxy), and a LoggerFactory so the library can tell you what it did. Everything the client does
// in response - relaxing timeouts, re-reading topology, moving off a retiring proxy - needs no application
// code; the event handlers below only put it on screen.
sealed partial class HandoffEngine
{
    /// <summary>Whether to ask for notifications: <see cref="MaintenanceNotificationMode"/>. Cycled with 'N'.</summary>
    public static readonly NotificationChoice[] Modes =
    [
        new("Auto", MaintenanceNotificationMode.Auto,
            "CLIENT MAINT_NOTIFICATIONS ON is sent during the handshake; a refusal is logged and the connection carries on without the feature. What the Redis Cloud, Redis Enterprise and Azure Managed Redis providers select for you"),
        new("Enabled", MaintenanceNotificationMode.Enabled,
            "the opt-in is required: a server that refuses it, or a connection that ends up on RESP2, is rejected rather than run without warning"),
        new("Disabled", MaintenanceNotificationMode.Disabled,
            "never asked for. The default for an endpoint no provider recognises: the connection works, and a MOVING closes the socket under in-flight commands with no warning"),
    ];

    /// <summary>What MOVING should name as the successor: <see cref="MaintenanceEndpointType"/>. Cycled with 'E'.</summary>
    public static readonly EndpointChoice[] EndpointTypes =
    [
        new("Auto", MaintenanceEndpointType.Auto,
            "ask MOVING to name the replacement proxy, choosing the form per connection: external-ip without TLS, external-fqdn with it (a certificate cannot validate a bare address). The client reconnects to it at once, before DNS has moved"),
        new("ServerDefault", MaintenanceEndpointType.ServerDefault,
            "ask for nothing (the bare CLIENT MAINT_NOTIFICATIONS ON, as earlier versions did): MOVING carries '_', so the client must re-resolve DNS, which has been measured trailing the notification by 4 to 19s, or recycle at half the window"),
        new("ExternalFqdn", MaintenanceEndpointType.ExternalFqdn,
            "moving-endpoint-type external-fqdn: the successor as node<N>.<cluster> plus port, the form a TLS certificate can validate"),
        new("ExternalIp", MaintenanceEndpointType.ExternalIp,
            "moving-endpoint-type external-ip: the successor as a public address plus port"),
    ];

    /// <summary>The app connection: ordinary options, plus the three maintenance settings.</summary>
    private async Task<ConnectionMultiplexer> ConnectAppAsync(DatabaseTarget target)
    {
        var options = new ConfigurationOptions
        {
            EndPoints = { target.Endpoint },
            User = target.User,
            Password = target.Password,
            Ssl = target.Tls,
            ClientName = "hitless-demo",
            // a refused opt-in under Enabled should surface as an error we can show, not a silent retry loop
            AbortOnConnectFail = true,
            ConnectTimeout = 10_000,
            SyncTimeout = _settings.TimeoutMs,
            AsyncTimeout = _settings.TimeoutMs,
            // RESP3 is the default; RESP2 is offered here only to show that it silently disables the feature
            Protocol = Resp2 ? RedisProtocol.Resp2 : RedisProtocol.Resp3,

            MaintenanceNotifications = Mode.Mode,
            MaintenanceMovingEndpointType = EndpointType.Type,
            LoggerFactory = _loggerFactory, // the library reports the opt-in, each notification and each handoff here

            // only for the display's CLIENT INFO probe, which shows which proxy node this connection is on
            AllowAdmin = true,
        };
        if (target.TrustAnyCertificate) options.CertificateValidation += (_, _, _, _) => true;
        _options = options;

        var muxer = await ConnectionMultiplexer.ConnectAsync(options);

        // reporting only: the client acts on these by itself
        muxer.ServerMaintenanceEvent += OnMaintenanceEvent;
        muxer.ConnectionFailed += OnConnectionFailed;
        muxer.ConnectionRestored += OnConnectionRestored;
        return muxer;
    }
}
