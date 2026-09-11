using System.Text.Json.Nodes;

namespace Counterpick.App.Services.Lcu;

/// <summary>What the topbar shows about the League client.</summary>
/// <param name="Phase">The gameflow phase verbatim ("ChampSelect", "InProgress", ...) or "Offline".</param>
public sealed record ClientStatus(bool Connected, string Phase, string? Message = null);

/// <summary>
/// Keeps a connection to the League client for the life of the app and turns its events
/// into the two the UI cares about: "client.status" and "draft.changed".
///
/// The client may not be running, may start later, and restarts with a new port and
/// password every time. So this polls for it, reconnects with backoff when the socket
/// drops, and never throws out of its loop. Everything it sees goes to the trace log,
/// raw payloads included, so a draft can be replayed against the mapper afterwards.
/// </summary>
public sealed class LcuWatcher : IDisposable
{
    public const string StatusEvent = "client.status";
    public const string DraftEvent = "draft.changed";
    public const string DraftEndedEvent = "draft.ended";

    private readonly ChampionCatalog _catalog;
    private readonly Action<string, object?> _emit;
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;

    public ClientStatus Status { get; private set; } = new(false, "Offline");
    public DraftPayload? CurrentDraft { get; private set; }

    public LcuWatcher(ChampionCatalog catalog, Action<string, object?> emit)
    {
        _catalog = catalog;
        _emit = emit;
    }

    public void Start()
    {
        _loop ??= Task.Run(() => RunAsync(_cts.Token));
    }

    private async Task RunAsync(CancellationToken ct)
    {
        var backoff = TimeSpan.FromSeconds(2);

        while (!ct.IsCancellationRequested)
        {
            var endpoint = LcuLocator.Find();
            if (endpoint is null)
            {
                SetStatus(new ClientStatus(false, "Offline"));
                await Delay(TimeSpan.FromSeconds(4), ct);
                continue;
            }

            try
            {
                Trace.Write("lcu", $"client found on port {endpoint.Port}");
                await _catalog.EnsureLoadedAsync(ct);
                Trace.Write("lcu", $"catalog ready: Data Dragon {_catalog.Version}, {_catalog.Champions.Count} champions");

                using var client = new LcuClient(endpoint);
                var phase = await client.GetGameflowPhaseAsync(ct);
                SetStatus(new ClientStatus(true, phase));

                // Catch up on a draft already in progress before events start flowing.
                if (phase == "ChampSelect")
                {
                    var session = await client.GetChampSelectSessionAsync(ct);
                    Trace.Payload("lcu", "session/rest", session);
                    ApplySession(session);
                }

                backoff = TimeSpan.FromSeconds(2);
                Trace.Write("lcu", "websocket connecting");
                await client.ListenAsync(OnEvent, ct);
                Trace.Write("lcu", "websocket closed by the client");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // Client closed, port changed, catalog offline - all recoverable. Say so,
                // wait, and try again.
                Trace.Write("lcu", $"disconnected: {ex.GetType().Name}: {ex.Message}; retry in {backoff.TotalSeconds}s");
                SetStatus(new ClientStatus(false, "Offline", ex.Message));
                if (CurrentDraft is not null) EndDraft();
                await Delay(backoff, ct);
                backoff = TimeSpan.FromSeconds(Math.Min(30, backoff.TotalSeconds * 2));
            }
        }
    }

    private Task OnEvent(LcuEvent evt)
    {
        switch (evt.Uri)
        {
            case LcuClient.GameflowPhaseUri:
                var phase = evt.Data?.GetValue<string>() ?? "None";
                Trace.Write("lcu", $"gameflow {evt.EventType}: {phase}");
                SetStatus(new ClientStatus(true, phase));
                if (phase != "ChampSelect" && CurrentDraft is not null) EndDraft();
                break;

            case LcuClient.ChampSelectUri:
                Trace.Payload("lcu", $"session/{evt.EventType}", evt.Data);
                if (evt.EventType == "Delete") EndDraft();
                else ApplySession(evt.Data);
                break;
        }
        return Task.CompletedTask;
    }

    private void ApplySession(JsonNode? session)
    {
        if (session is null) return;
        var draft = DraftMapper.Map(session, _catalog);
        if (draft is null)
        {
            Trace.Write("lcu", "session rejected by the mapper (bench enabled or team is not five)");
            SetStatus(Status with { Message = "Unsupported queue" });
            return;
        }
        CurrentDraft = draft;
        Trace.Write("lcu", Describe(draft));
        _emit(DraftEvent, draft);
    }

    private static string Describe(DraftPayload d)
    {
        static string Seat(DraftSlot s) =>
            (s.ChampionKey ?? "-") + (s.Hovering ? "?" : "") + ":" + s.Role + (s.IsYou ? "*" : "") + (s.OnTheClock ? "!" : "");
        return $"draft {d.TimerPhase} {(d.TimerInfinite ? "∞" : d.TimerMs + "ms")}" +
               $" ally[{string.Join(" ", d.Ally.Select(Seat))}] enemy[{string.Join(" ", d.Enemy.Select(Seat))}]" +
               $" bans[{string.Join(",", d.Bans)}] remaining={d.EnemyPicksRemaining}" +
               $" yourTurn={d.YourTurn} locked={d.LockedKey ?? "-"} hover={d.HoverKey ?? "-"}";
    }

    private void EndDraft()
    {
        // GameStart, the session Delete and InProgress all arrive within a second; one end is enough.
        if (CurrentDraft is null) return;
        Trace.Write("lcu", "draft ended");
        CurrentDraft = null;
        _emit(DraftEndedEvent, null);
    }

    private void SetStatus(ClientStatus status)
    {
        if (status == Status) return;
        Trace.Write("lcu", $"status: {(status.Connected ? "connected" : "offline")} {status.Phase}{(status.Message is null ? "" : " - " + status.Message)}");
        Status = status;
        _emit(StatusEvent, status);
    }

    private static async Task Delay(TimeSpan span, CancellationToken ct)
    {
        try { await Task.Delay(span, ct); } catch (OperationCanceledException) { }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }
}
