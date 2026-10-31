using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Rondel.Core.Logging;

namespace Rondel.Core.Activity;

/// <summary>
/// Receives the events of the agents Rondel starts. An agent is started with a settings file (<see cref="FileName"/>,
/// passed as its "{hooks}") whose HTTP hooks post every event Rondel follows to this server, together with the
/// session's id from <see cref="AgentActivity.EnvironmentVariable"/>.
/// </summary>
/// <remarks>
/// The server listens on the loopback address only and accepts a request only with the token from the hooks file,
/// in a header: a web page cannot read the token, and a custom header makes a browser ask first (CORS), which this
/// server never allows. It answers before it handles an event, so an agent never waits for Rondel; an empty answer
/// leaves every decision (a permission prompt, stopping) to the agent. The port stays the same across restarts, so
/// agents started before one keep reporting.
/// </remarks>
public sealed class HookServer(AgentActivity activity) : IDisposable
{
    /// <summary>The hooks file, in the settings folder.</summary>
    public const string FileName = "agent-hooks.json";

    public const string TokenHeader = "X-Rondel-Token";

    public const string SessionHeader = "X-Rondel-Session";

    const string HookPath = "/hook";

    /// <summary>Seconds an agent waits for an answer. Rondel answers at once; this only matters if it hangs.</summary>
    const int HookTimeoutSeconds = 3;

    const int MaxHeadBytes = 16 * 1024;
    const int MaxBodyBytes = 1024 * 1024;
    static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);

    /// <summary>The events Rondel follows; PreToolUse only for the tool that asks the user a question.</summary>
    static readonly (string Event, string? Matcher)[] Events =
    [
        ("UserPromptSubmit", null),
        ("PreToolUse", "AskUserQuestion"),
        ("PostToolUse", null),
        ("PostToolUseFailure", null),
        ("PermissionRequest", null),
        ("Notification", null),
        ("Stop", null),
        ("StopFailure", null),
        ("SessionEnd", null),
    ];

    readonly CancellationTokenSource _stop = new();
    TcpListener? _listener;
    byte[] _token = [];

    /// <summary>The port it listens on, once started.</summary>
    public int Port { get; private set; }

    /// <summary>
    /// Listens on the port named in the hooks file in <paramref name="directory"/>, or on a free one when there is no
    /// file or the port is taken, (re)writes the file and sets <see cref="AgentActivity.HooksFile"/>.
    /// </summary>
    /// <returns>False when it can neither listen nor write the file (logged); agents then start without being followed.</returns>
    public bool Start(string directory)
    {
        string path = Path.Combine(directory, FileName);
        var (savedPort, savedToken) = Read(path);
        string token = savedToken ?? Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        var listener = (savedPort > 0 ? Listen(savedPort) : null) ?? Listen(0);
        if (listener is null)
            return false;
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        try
        {
            string json = Build(Port, token);
            Directory.CreateDirectory(directory);
            if (!File.Exists(path) || File.ReadAllText(path) != json)
            {
                string temp = path + ".tmp";
                File.WriteAllText(temp, json);
                File.Move(temp, path, overwrite: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error($"Writing {path} failed; agents will not be followed", ex);
            listener.Stop();
            return false;
        }
        _listener = listener;
        _token = Encoding.UTF8.GetBytes(token);
        activity.HooksFile = path;
        Log.Info($"Following agents on port {Port}" + (savedPort > 0 && savedPort != Port ? $" (port {savedPort} is taken)" : ""));
        _ = Task.Run(() => AcceptAsync(listener, _stop.Token));
        return true;
    }

    public void Dispose()
    {
        activity.HooksFile = null;
        _stop.Cancel();
        _listener?.Stop();
        _listener = null;
    }

    static TcpListener? Listen(int port)
    {
        var listener = new TcpListener(IPAddress.Loopback, port);
        if (OperatingSystem.IsWindows())
            listener.ExclusiveAddressUse = true; // no other program can listen on the same port
        try
        {
            listener.Start();
            return listener;
        }
        catch (SocketException ex)
        {
            if (port == 0)
                Log.Error("Listening for agent hooks failed; agents will not be followed", ex);
            return null;
        }
    }

    async Task AcceptAsync(TcpListener listener, CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(stop).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
            {
                return;
            }
            catch (SocketException ex)
            {
                Log.Warn("Accepting an agent hook failed", ex);
                continue;
            }
            _ = HandleAsync(client, stop);
        }
    }

    /// <summary>
    /// Closes the connection once the agent has closed its end (it was told "Connection: close"), or after a moment.
    /// The side that closes first keeps the port in TIME_WAIT for minutes, and that must not be Rondel: a Rondel started
    /// again in that time could not listen on the same port, and the agents still running would report to nobody.
    /// </summary>
    static async Task LingerAsync(TcpClient client, CancellationToken stop)
    {
        using var _ = client;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            var buffer = new byte[256];
            while (await client.GetStream().ReadAsync(buffer, timeout.Token).ConfigureAwait(false) > 0)
            {
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
        {
        }
    }

    async Task HandleAsync(TcpClient client, CancellationToken stop)
    {
        bool lingering = false;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop);
        timeout.CancelAfter(RequestTimeout);
        try
        {
            var stream = client.GetStream();
            var (status, session, body) = await ReadAsync(stream, timeout.Token).ConfigureAwait(false);
            await stream.WriteAsync(Response(status), timeout.Token).ConfigureAwait(false);
            lingering = true;
            _ = LingerAsync(client, stop);
            if (status != 200)
            {
                Log.Warn($"Refused an agent hook request ({status})");
                return;
            }
            if (HookEvent.Parse(body) is { } e)
                activity.Apply(session!, e);
            else
                Log.Debug("Ignored a hook request that is not a hook event");
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
        {
            Log.Debug($"An agent hook connection dropped: {ex.Message}");
        }
        catch (Exception ex)
        {
            // A handler of AgentActivity.Changed failed; the server keeps going.
            Log.Error("Handling an agent hook failed", ex);
        }
        finally
        {
            if (!lingering)
                client.Dispose();
        }
    }

    /// <summary>Reads one request: its head, and its body when the head is acceptable.</summary>
    async Task<(int Status, string? Session, byte[] Body)> ReadAsync(NetworkStream stream, CancellationToken cancel)
    {
        var buffer = new byte[4096];
        int length = 0, end;
        while ((end = HeadEnd(buffer.AsSpan(0, length))) < 0)
        {
            if (length == buffer.Length)
            {
                if (buffer.Length >= MaxHeadBytes)
                    return (431, null, []);
                Array.Resize(ref buffer, buffer.Length * 2);
            }
            int read = await stream.ReadAsync(buffer.AsMemory(length), cancel).ConfigureAwait(false);
            if (read == 0)
                return (400, null, []);
            length += read;
        }

        var (status, session, contentLength) = CheckHead(Encoding.Latin1.GetString(buffer, 0, end), _token);
        if (status != 200)
            return (status, null, []);

        var body = new byte[contentLength];
        int have = Math.Min(length - end - 4, contentLength);
        buffer.AsSpan(end + 4, have).CopyTo(body);
        while (have < contentLength)
        {
            int read = await stream.ReadAsync(body.AsMemory(have), cancel).ConfigureAwait(false);
            if (read == 0)
                return (400, null, []);
            have += read;
        }
        return (200, session, body);
    }

    static int HeadEnd(ReadOnlySpan<byte> data) => data.IndexOf("\r\n\r\n"u8);

    /// <summary>Checks a request's head (request line and headers, without the blank line).</summary>
    /// <returns>200 with the session id and the body's length, or the HTTP status to refuse it with.</returns>
    internal static (int Status, string? Session, int ContentLength) CheckHead(string head, ReadOnlySpan<byte> token)
    {
        string[] lines = head.Split("\r\n");
        string[] request = lines[0].Split(' ');
        if (request.Length != 3 || !request[2].StartsWith("HTTP/1.", StringComparison.Ordinal))
            return (400, null, 0);
        if (request[0] != "POST")
            return (405, null, 0);
        string target = request[1];
        int query = target.IndexOf('?');
        if ((query < 0 ? target : target[..query]) != HookPath)
            return (404, null, 0);

        string? sentToken = null, session = null, contentLength = null;
        bool chunked = false;
        foreach (string line in lines.AsSpan(1))
        {
            int colon = line.IndexOf(':');
            if (colon <= 0)
                return (400, null, 0);
            string name = line[..colon].Trim(), value = line[(colon + 1)..].Trim();
            if (name.Equals(TokenHeader, StringComparison.OrdinalIgnoreCase))
                sentToken = value;
            else if (name.Equals(SessionHeader, StringComparison.OrdinalIgnoreCase))
                session = value;
            else if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                contentLength = value;
            else if (name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase))
                chunked = true;
        }

        if (sentToken is null || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(sentToken), token))
            return (401, null, 0);
        if (!IsSessionId(session))
            return (400, null, 0);
        if (chunked || !int.TryParse(contentLength, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int bodyLength))
            return (411, null, 0);
        if (bodyLength > MaxBodyBytes)
            return (413, null, 0);
        return (200, session, bodyLength);
    }

    /// <summary>Ids Rondel gives out are hex; anything short of letters, digits and '-' is not one of them.</summary>
    static bool IsSessionId(string? value) =>
        value is { Length: > 0 and <= 64 } && value.All(c => char.IsAsciiLetterOrDigit(c) || c == '-');

    static byte[] Response(int status)
    {
        string reason = status switch
        {
            200 => "OK",
            400 => "Bad Request",
            401 => "Unauthorized",
            404 => "Not Found",
            405 => "Method Not Allowed",
            411 => "Length Required",
            413 => "Content Too Large",
            431 => "Request Header Fields Too Large",
            _ => "Error",
        };
        return Encoding.ASCII.GetBytes($"HTTP/1.1 {status} {reason}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
    }

    /// <summary>The hooks file: an HTTP hook to this server for every followed event.</summary>
    internal static string Build(int port, string token)
    {
        using var stream = new MemoryStream();
        using (var json = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            json.WriteStartObject();
            json.WriteStartObject("hooks");
            foreach (var (name, matcher) in Events)
            {
                json.WriteStartArray(name);
                json.WriteStartObject();
                if (matcher is not null)
                    json.WriteString("matcher", matcher);
                json.WriteStartArray("hooks");
                json.WriteStartObject();
                json.WriteString("type", "http");
                json.WriteString("url", $"http://127.0.0.1:{port}{HookPath}");
                json.WriteNumber("timeout", HookTimeoutSeconds);
                json.WriteStartObject("headers");
                json.WriteString(TokenHeader, token);
                json.WriteString(SessionHeader, "$" + AgentActivity.EnvironmentVariable);
                json.WriteEndObject();
                json.WriteStartArray("allowedEnvVars");
                json.WriteStringValue(AgentActivity.EnvironmentVariable);
                json.WriteEndArray();
                json.WriteEndObject();
                json.WriteEndArray();
                json.WriteEndObject();
                json.WriteEndArray();
            }
            json.WriteEndObject();
            json.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>The port and token of an existing hooks file; (0, null) when there is none or it cannot be read.</summary>
    internal static (int Port, string? Token) Read(string path)
    {
        try
        {
            if (!File.Exists(path))
                return (0, null);
            using var document = JsonDocument.Parse(File.ReadAllBytes(path));
            foreach (var handlers in document.RootElement.GetProperty("hooks").EnumerateObject())
            {
                foreach (var group in handlers.Value.EnumerateArray())
                {
                    foreach (var hook in group.GetProperty("hooks").EnumerateArray())
                    {
                        if (Uri.TryCreate(hook.GetProperty("url").GetString(), UriKind.Absolute, out var url)
                            && hook.GetProperty("headers").TryGetProperty(TokenHeader, out var token)
                            && token.GetString() is { Length: >= 16 } value)
                            return (url.Port, value);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException
            or InvalidOperationException)
        {
            Log.Warn($"Reading {path} failed; a new one is written", ex);
        }
        return (0, null);
    }
}
