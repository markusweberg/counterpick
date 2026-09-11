using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Counterpick.App.Services.Lcu;

/// <summary>One pushed update from the client's event bus.</summary>
public sealed record LcuEvent(string Uri, string EventType, JsonNode? Data);

/// <summary>
/// HTTP and WebSocket access to one running League client.
///
/// The client's certificate is self-signed, so validation is switched off - but only on
/// the handler this class owns, never process-wide. Auth is HTTP Basic with the literal
/// username "riot" and the password from <see cref="LcuEndpoint"/>.
/// </summary>
public sealed class LcuClient : IDisposable
{
    public const string ChampSelectUri = "/lol-champ-select/v1/session";
    public const string GameflowPhaseUri = "/lol-gameflow/v1/gameflow-phase";

    private readonly LcuEndpoint _endpoint;
    private readonly HttpClient _http;
    private readonly string _basicAuth;

    public LcuClient(LcuEndpoint endpoint)
    {
        _endpoint = endpoint;
        _basicAuth = "Basic " + Convert.ToBase64String(Encoding.ASCII.GetBytes("riot:" + endpoint.Password));

        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true
        };
        _http = new HttpClient(handler)
        {
            BaseAddress = new Uri(endpoint.BaseUrl + "/"),
            Timeout = TimeSpan.FromSeconds(5)
        };
        _http.DefaultRequestHeaders.Authorization = AuthenticationHeaderValue.Parse(_basicAuth);
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    /// <summary>GET a JSON resource. Null on 404, which the client uses for "not in that state".</summary>
    public async Task<JsonNode?> GetAsync(string path, CancellationToken ct = default)
    {
        using var response = await _http.GetAsync(path.TrimStart('/'), ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync(ct);
        return body.Length == 0 ? null : JsonNode.Parse(body);
    }

    /// <summary>"None", "Lobby", "Matchmaking", "ChampSelect", "InProgress", "EndOfGame", ...</summary>
    public async Task<string> GetGameflowPhaseAsync(CancellationToken ct = default)
    {
        var node = await GetAsync(GameflowPhaseUri, ct);
        return node?.GetValue<string>() ?? "None";
    }

    public Task<JsonNode?> GetChampSelectSessionAsync(CancellationToken ct = default) =>
        GetAsync(ChampSelectUri, ct);

    /// <summary>
    /// Subscribe to every client event and hand each one to <paramref name="onEvent"/>.
    /// Returns only when the socket closes or the token is cancelled; a dropped connection
    /// surfaces as an exception so the caller can reconnect.
    /// </summary>
    public async Task ListenAsync(Func<LcuEvent, Task> onEvent, CancellationToken ct)
    {
        using var socket = new ClientWebSocket();
        socket.Options.AddSubProtocol("wamp");
        socket.Options.SetRequestHeader("Authorization", _basicAuth);
        socket.Options.RemoteCertificateValidationCallback = (_, _, _, _) => true;

        await socket.ConnectAsync(new Uri($"wss://127.0.0.1:{_endpoint.Port}/"), ct);

        // WAMP subscribe: [5, "topic"]. OnJsonApiEvent is the firehose for every endpoint.
        var subscribe = Encoding.UTF8.GetBytes("[5, \"OnJsonApiEvent\"]");
        await socket.SendAsync(subscribe, WebSocketMessageType.Text, endOfMessage: true, ct);

        var buffer = new byte[64 * 1024];
        using var message = new MemoryStream();

        while (!ct.IsCancellationRequested && socket.State == WebSocketState.Open)
        {
            message.SetLength(0);
            WebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(buffer, ct);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
                    return;
                }
                message.Write(buffer, 0, result.Count);
            } while (!result.EndOfMessage);

            if (message.Length == 0) continue; // the client sends keepalive empties

            var evt = Parse(message);
            if (evt is not null) await onEvent(evt);
        }
    }

    /// <summary>Events arrive as [8, "OnJsonApiEvent", { uri, eventType, data }].</summary>
    private static LcuEvent? Parse(MemoryStream message)
    {
        try
        {
            var node = JsonNode.Parse(Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length));
            if (node is not JsonArray arr || arr.Count < 3) return null;
            if (arr[0]?.GetValue<int>() != 8) return null;

            var body = arr[2];
            var uri = body?["uri"]?.GetValue<string>();
            var type = body?["eventType"]?.GetValue<string>();
            if (uri is null || type is null) return null;
            return new LcuEvent(uri, type, body?["data"]);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    public void Dispose() => _http.Dispose();
}
