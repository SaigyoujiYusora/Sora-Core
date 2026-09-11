using System.Text;
using System.Text.Json;
using Sora.Core;

internal sealed class BoundedRequestReader(Stream stream)
{
    public const int Maximum = 1024 * 1024;
    private readonly byte[] buffer = new byte[16384];
    private int position, available;
    public async Task<(string? Text, string? Error)?> ReadAsync()
    {
        using var line = new MemoryStream();
        bool oversized = false, any = false;
        while (true)
        {
            if (position == available)
            {
                available = await stream.ReadAsync(buffer);
                position = 0;
                if (available == 0)
                {
                    if (!any) return null;
                    break;
                }
            }
            byte value = buffer[position++];
            any = true;
            if (value == (byte)'\n') break;
            if (line.Length < Maximum) line.WriteByte(value); else oversized = true;
        }
        if (oversized) return (null, "Request exceeds the 1 MiB limit");
        try { return (new UTF8Encoding(false, true).GetString(line.GetBuffer(), 0, (int)line.Length), null); }
        catch (DecoderFallbackException) { return (null, "Request is not valid UTF-8"); }
    }
}

/// <summary>One active request, no work queue; the reader remains available for targeted cancellation.</summary>
internal static class TaskTransport
{
    private sealed record Active(string Id, CancellationTokenSource Cancellation)
    {
        public bool Committed { get; set; }
    }

    public static async Task Run(Func<string, string> handle, bool persistent)
    {
        var reader = new BoundedRequestReader(Console.OpenStandardInput());
        var stateGate = new object(); var outputGate = new object();
        Active? active = null;
        Task? lastWork = null;
        void Write(string text) { lock (outputGate) { Console.WriteLine(text); Console.Out.Flush(); } }
        string Failure(string? id, string code, string message) => JsonSerializer.Serialize(new { protocol = 1, id, ok = false, error = new { code, message } }, WireJson.Options);

        bool Process((string? Text, string? Error) input, bool controlsOnly)
        {
            if (input.Error is not null) { Write(Failure(null, "invalid_input", input.Error)); return false; }
            string? id = null;
            try
            {
                using var json = WireJson.Parse(Encoding.UTF8.GetBytes(input.Text!));
                var root = json.RootElement;
                Validation.Require(root.ValueKind == JsonValueKind.Object, "Request must be an object");
                string? method = root.GetProperty("method").GetString();
                if (method == "cancel")
                {
                    string? target = root.TryGetProperty("targetId", out var targetValue) ? targetValue.GetString() : null;
                    bool accepted = false;
                    bool tooLate = false;
                    lock (stateGate)
                    {
                        if (active is not null && (target == active.Id || !persistent && target is null))
                        {
                            tooLate = active.Committed;
                            if (!tooLate) { active.Cancellation.Cancel(); accepted = true; }
                        }
                    }
                    if (!accepted) Write(JsonSerializer.Serialize(new { protocol = 1, @event = "control", targetId = target, accepted = false, reason = tooLate ? "too-late-committed" : "No matching active request; persistent cancel requires targetId" }, WireJson.Options));
                    return false;
                }
                id = root.GetProperty("id").GetString();
                Validation.Require(id is { Length: > 0 and <= 128 }, "Invalid request ID");
                Active request;
                lock (stateGate)
                {
                    if (controlsOnly || active is not null)
                    { Write(Failure(id, "busy", "Wait for the active request's terminal response before submitting another request")); return false; }
                    request = new(id!, new CancellationTokenSource()); active = request;
                }
                lastWork = Task.Run(() =>
                {
                    OperationProgress.Sink = update =>
                    {
                        if (!request.Committed) request.Cancellation.Token.ThrowIfCancellationRequested();
                        Write(JsonSerializer.Serialize(new { protocol = 1, id = request.Id, @event = "progress", update.Stage, update.Completed, update.Total, update.Detail }, WireJson.Options));
                    };
                    OperationProgress.Commit = replace =>
                    {
                        lock (stateGate)
                        {
                            request.Cancellation.Token.ThrowIfCancellationRequested();
                            replace();
                            request.Committed = true;
                        }
                    };
                    try
                    {
                        string response=handle(input.Text!);
                        var terminal = System.Text.Json.Nodes.JsonNode.Parse(response)!.AsObject();
                        terminal["committed"] = request.Committed;
                        response = terminal.ToJsonString(WireJson.Options);
                        lock(stateGate) { Write(response); if(ReferenceEquals(active,request))active=null; }
                    }
                    finally
                    {
                        OperationProgress.Sink = null;
                        OperationProgress.Commit = null;
                        lock (stateGate) { if (ReferenceEquals(active, request)) active = null; }
                        request.Cancellation.Dispose();
                    }
                });
                return true;
            }
            catch (Exception error) when (error is IOException or JsonException or KeyNotFoundException or ArgumentException or InvalidOperationException)
            { Write(Failure(id, "invalid_input", error.Message)); return false; }
        }

        if (persistent)
        {
            while (await reader.ReadAsync() is { } line) Process(line, false);
            if (lastWork is not null) await lastWork;
            return;
        }
        var first = await reader.ReadAsync();
        if (first is null) { Write(Failure(null, "invalid_input", "Missing task request")); return; }
        if (!Process(first.Value, false) || lastWork is null) return;
        Task work = lastWork;
        _ = Task.Run(async () =>
        {
            while (!work.IsCompleted && await reader.ReadAsync() is { } line) Process(line, true);
        });
        await work;
    }
}
