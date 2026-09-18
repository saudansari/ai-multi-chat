using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SharedAgentChat.Application.Ports;
using SharedAgentChat.Domain;

namespace SharedAgentChat.Infrastructure;

public sealed class ComposerAgentAdapter : IAgentAdapter
{
    public const string HttpClientName = "CursorComposer";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly ConcurrentDictionary<string, string> _agentIds = new(StringComparer.Ordinal);
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly AgentOptions _options;
    private readonly ILogger<ComposerAgentAdapter> _logger;

    public ComposerAgentAdapter(
        IHttpClientFactory httpClientFactory,
        IOptions<AgentOptions> options,
        ILogger<ComposerAgentAdapter> logger)
    {
        _httpClientFactory = httpClientFactory;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<string> GetReplyAsync(
        string sessionId,
        IReadOnlyList<Message> transcript,
        string senderDisplayName,
        string text,
        string credential,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(8));
        var ct = timeout.Token;

        string agentId;
        string runId;
        if (_agentIds.TryGetValue(sessionId, out var existingId))
        {
            var followUp = await TryFollowUpAsync(existingId, senderDisplayName, text, credential, ct);
            if (followUp is null)
            {
                _agentIds.TryRemove(sessionId, out _);
                (agentId, runId) = await CreateAgentAsync(transcript, credential, ct);
                _agentIds[sessionId] = agentId;
            }
            else
            {
                (agentId, runId) = followUp.Value;
            }
        }
        else
        {
            (agentId, runId) = await CreateAgentAsync(transcript, credential, ct);
            _agentIds[sessionId] = agentId;
        }

        return await WaitForRunResultAsync(agentId, runId, credential, ct);
    }

    public bool TryGetBoundAgentId(string sessionId, out string agentId) =>
        _agentIds.TryGetValue(sessionId, out agentId!);

    public async Task DeleteBoundAgentAsync(string sessionId, string credential, CancellationToken cancellationToken)
    {
        if (!_agentIds.TryRemove(sessionId, out var agentId) || string.IsNullOrWhiteSpace(agentId))
        {
            return;
        }

        using var sent = await SendJsonAsync(HttpMethod.Delete, $"v1/agents/{agentId}", credential, body: null, cancellationToken);
        if (!sent.Response.IsSuccessStatusCode && sent.Response.StatusCode != HttpStatusCode.NotFound)
        {
            _logger.LogError("Composer delete-agent failed with status {Status}.", (int)sent.Response.StatusCode);
            throw new InvalidOperationException($"Composer delete-agent failed with status {(int)sent.Response.StatusCode}.");
        }

        _logger.LogInformation("Deleted Composer agent {AgentId}.", agentId);
    }

    private async Task<(string AgentId, string RunId)> CreateAgentAsync(
        IReadOnlyList<Message> transcript,
        string credential,
        CancellationToken cancellationToken)
    {
        var body = BuildCreateBody(transcript);
        using var sent = await SendJsonAsync(HttpMethod.Post, "v1/agents", credential, body, cancellationToken);
        var json = sent.Body;
        if (!sent.Response.IsSuccessStatusCode)
        {
            _logger.LogError("Composer create-agent failed with status {Status}.", (int)sent.Response.StatusCode);
            throw new InvalidOperationException(
                $"Composer create-agent failed with status {(int)sent.Response.StatusCode}: {Truncate(json)}");
        }

        using var doc = JsonDocument.Parse(json);
        var agentId = ReadAgentId(doc.RootElement)
            ?? throw new InvalidOperationException("Composer create-agent response missing agent id.");
        var runId = ReadRunId(doc.RootElement)
            ?? throw new InvalidOperationException("Composer create-agent response missing run id.");
        _logger.LogInformation("Created Composer agent {AgentId} run {RunId}.", agentId, runId);
        return (agentId, runId);
    }

    private async Task<(string AgentId, string RunId)?> TryFollowUpAsync(
        string agentId,
        string senderDisplayName,
        string text,
        string credential,
        CancellationToken cancellationToken)
    {
        var body = new
        {
            prompt = new { text = FormatLine(senderDisplayName, text) }
        };

        using var sent = await SendJsonAsync(
            HttpMethod.Post,
            $"v1/agents/{agentId}/runs",
            credential,
            body,
            cancellationToken);
        var json = sent.Body;

        if (sent.Response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            _logger.LogInformation(
                "Composer follow-up on {AgentId} returned {Status}; creating a new agent.",
                agentId,
                (int)sent.Response.StatusCode);
            return null;
        }

        if ((int)sent.Response.StatusCode == 409)
        {
            var latestRunId = await WaitUntilAgentAcceptsFollowUpAsync(agentId, credential, cancellationToken);
            if (latestRunId is not null)
            {
                using var retry = await SendJsonAsync(
                    HttpMethod.Post,
                    $"v1/agents/{agentId}/runs",
                    credential,
                    body,
                    cancellationToken);
                if (retry.Response.IsSuccessStatusCode)
                {
                    using var retryDoc = JsonDocument.Parse(retry.Body);
                    var retryRunId = ReadRunId(retryDoc.RootElement)
                        ?? throw new InvalidOperationException("Composer follow-up response missing run id.");
                    return (agentId, retryRunId);
                }
            }

            _logger.LogInformation("Composer follow-up on {AgentId} was busy; creating a new agent.", agentId);
            return null;
        }

        if (!sent.Response.IsSuccessStatusCode)
        {
            _logger.LogError("Composer follow-up failed with status {Status}.", (int)sent.Response.StatusCode);
            throw new InvalidOperationException($"Composer follow-up failed with status {(int)sent.Response.StatusCode}.");
        }

        using var doc = JsonDocument.Parse(json);
        var runId = ReadRunId(doc.RootElement)
            ?? throw new InvalidOperationException("Composer follow-up response missing run id.");
        return (agentId, runId);
    }

    private async Task<string?> WaitUntilAgentAcceptsFollowUpAsync(
        string agentId,
        string credential,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 30; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var sent = await SendJsonAsync(HttpMethod.Get, $"v1/agents/{agentId}", credential, body: null, cancellationToken);
            var json = sent.Body;
            if (!sent.Response.IsSuccessStatusCode)
            {
                return null;
            }

            using var doc = JsonDocument.Parse(json);
            var status = ReadStatus(doc.RootElement);
            var latestRunId = doc.RootElement.TryGetProperty("latestRunId", out var latest) && latest.ValueKind == JsonValueKind.String
                ? latest.GetString()
                : null;

            if (status.Equals("IDLE", StringComparison.OrdinalIgnoreCase)
                || status.Equals("ARCHIVED", StringComparison.OrdinalIgnoreCase))
            {
                if (status.Equals("ARCHIVED", StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }

                return latestRunId;
            }

            if (!string.IsNullOrWhiteSpace(latestRunId))
            {
                try
                {
                    await WaitForRunResultAsync(agentId, latestRunId, credential, cancellationToken);
                    return latestRunId;
                }
                catch (InvalidOperationException)
                {
                    return latestRunId;
                }
            }

            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }

        return null;
    }

    private async Task<string> WaitForRunResultAsync(
        string agentId,
        string runId,
        string credential,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            using var sent = await SendJsonAsync(
                HttpMethod.Get,
                $"v1/agents/{agentId}/runs/{runId}",
                credential,
                body: null,
                cancellationToken);
            var json = sent.Body;
            if (!sent.Response.IsSuccessStatusCode)
            {
                _logger.LogError("Composer get-run failed with status {Status}.", (int)sent.Response.StatusCode);
                throw new InvalidOperationException($"Composer get-run failed with status {(int)sent.Response.StatusCode}.");
            }

            using var doc = JsonDocument.Parse(json);
            var status = ReadStatus(doc.RootElement);
            if (IsTerminalSuccess(status))
            {
                var text = ReadResultText(doc.RootElement);
                if (string.IsNullOrWhiteSpace(text))
                {
                    throw new InvalidOperationException("Composer run finished without reply text.");
                }

                return text.Trim();
            }

            if (IsTerminalFailure(status))
            {
                throw new InvalidOperationException($"Composer run ended with status {status}.");
            }

            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }
    }

    private object BuildCreateBody(IReadOnlyList<Message> transcript)
    {
        var composer = _options.Composer;
        var fast = composer.Mode.Equals("Fast", StringComparison.OrdinalIgnoreCase);
        object model = fast
            ? new { id = composer.ModelId, @params = new[] { new { id = "fast", value = "true" } } }
            : new { id = composer.ModelId };

        return new
        {
            prompt = new { text = FormatTranscript(transcript) },
            model,
            name = "Shared Agent Chat POC",
            mode = "agent"
        };
    }

    private static string FormatTranscript(IReadOnlyList<Message> transcript)
    {
        var prompt = new StringBuilder();
        prompt.AppendLine("You are the shared Agent in a two-human chat. Reply in plain text only.");
        prompt.AppendLine("Do not mention API keys. Continue the conversation based on this transcript:");
        prompt.AppendLine();
        foreach (var message in transcript)
        {
            prompt.AppendLine(FormatLine(message.SenderDisplayName, message.Text));
        }

        return prompt.ToString();
    }

    private static string FormatLine(string senderDisplayName, string text) =>
        $"{senderDisplayName}: {text}";

    private async Task<CursorHttpCall> SendJsonAsync(
        HttpMethod method,
        string path,
        string credential,
        object? body,
        CancellationToken cancellationToken)
    {
        var http = _httpClientFactory.CreateClient(HttpClientName);
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential);
        var requestBody = body is null ? "" : JsonSerializer.Serialize(body, JsonOptions);
        if (body is not null)
        {
            request.Content = new StringContent(requestBody, Encoding.UTF8, "application/json");
        }

        LogCursorHttp("request", method, path, status: null, requestBody);

        var response = await http.SendAsync(request, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        LogCursorHttp("response", method, path, (int)response.StatusCode, responseBody);
        return new CursorHttpCall(response, responseBody);
    }

    private void LogCursorHttp(string direction, HttpMethod method, string path, int? status, string body)
    {
        var isRunPoll = method == HttpMethod.Get && path.Contains("/runs/", StringComparison.Ordinal);
        var terminal = !isRunPoll
            || (status is not null
                && (status < 200 || status >= 300 || ContainsTerminalRunStatus(body)));

        var message = status is null
            ? "Cursor agent {Direction}: {Method} {Path} body={Body}"
            : "Cursor agent {Direction}: {Method} {Path} status={Status} body={Body}";

        if (terminal)
        {
            if (status is null)
            {
                _logger.LogInformation(message, direction, method.Method, path, RedactSecrets(body));
            }
            else
            {
                _logger.LogInformation(message, direction, method.Method, path, status, RedactSecrets(body));
            }
        }
        else if (status is null)
        {
            _logger.LogDebug(message, direction, method.Method, path, RedactSecrets(body));
        }
        else
        {
            _logger.LogDebug(message, direction, method.Method, path, status, RedactSecrets(body));
        }
    }

    private static bool ContainsTerminalRunStatus(string body) =>
        body.Contains("\"FINISHED\"", StringComparison.OrdinalIgnoreCase)
        || body.Contains("\"ERROR\"", StringComparison.OrdinalIgnoreCase)
        || body.Contains("\"CANCELLED\"", StringComparison.OrdinalIgnoreCase)
        || body.Contains("\"EXPIRED\"", StringComparison.OrdinalIgnoreCase)
        || body.Contains("\"completed\"", StringComparison.OrdinalIgnoreCase)
        || body.Contains("\"failed\"", StringComparison.OrdinalIgnoreCase);

    private static string RedactSecrets(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "(empty)";
        }

        var redacted = value;
        if (redacted.Contains("crsr_", StringComparison.OrdinalIgnoreCase))
        {
            redacted = System.Text.RegularExpressions.Regex.Replace(
                redacted,
                @"crsr_[A-Za-z0-9]+",
                "crsr_[redacted]",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        }

        return Truncate(redacted, 8000);
    }

    private sealed record CursorHttpCall(HttpResponseMessage Response, string Body) : IDisposable
    {
        public void Dispose() => Response.Dispose();
    }

    internal static string? ReadAgentId(JsonElement root)
    {
        if (root.TryGetProperty("agent", out var agent) && agent.ValueKind == JsonValueKind.Object
            && agent.TryGetProperty("id", out var nested) && nested.ValueKind == JsonValueKind.String)
        {
            return nested.GetString();
        }

        if (root.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
        {
            var value = id.GetString() ?? "";
            if (value.StartsWith("bc-", StringComparison.OrdinalIgnoreCase))
            {
                return value;
            }
        }

        return null;
    }

    internal static string? ReadRunId(JsonElement root)
    {
        if (root.TryGetProperty("run", out var run) && run.ValueKind == JsonValueKind.Object
            && run.TryGetProperty("id", out var nested) && nested.ValueKind == JsonValueKind.String)
        {
            return nested.GetString();
        }

        if (root.TryGetProperty("agent", out var agent) && agent.ValueKind == JsonValueKind.Object
            && agent.TryGetProperty("latestRunId", out var latest) && latest.ValueKind == JsonValueKind.String)
        {
            return latest.GetString();
        }

        if (root.TryGetProperty("latestRunId", out var top) && top.ValueKind == JsonValueKind.String)
        {
            return top.GetString();
        }

        if (root.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
        {
            var value = id.GetString() ?? "";
            if (value.StartsWith("run-", StringComparison.OrdinalIgnoreCase))
            {
                return value;
            }
        }

        return null;
    }

    internal static string ReadStatus(JsonElement root)
    {
        if (TryString(root, "status", out var status))
        {
            return status;
        }

        if (root.TryGetProperty("run", out var run) && run.ValueKind == JsonValueKind.Object
            && TryString(run, "status", out var nested))
        {
            return nested;
        }

        return "";
    }

    internal static string ReadResultText(JsonElement root)
    {
        if (root.TryGetProperty("run", out var run) && run.ValueKind == JsonValueKind.Object)
        {
            var nested = ReadResultTextCore(run);
            if (!string.IsNullOrWhiteSpace(nested))
            {
                return nested;
            }
        }

        return ReadResultTextCore(root);
    }

    private static string ReadResultTextCore(JsonElement root)
    {
        if (root.TryGetProperty("result", out var result))
        {
            if (result.ValueKind == JsonValueKind.String)
            {
                return result.GetString() ?? "";
            }

            if (result.ValueKind == JsonValueKind.Object && result.TryGetProperty("text", out var nested)
                && nested.ValueKind == JsonValueKind.String)
            {
                return nested.GetString() ?? "";
            }

            if (result.ValueKind == JsonValueKind.Array)
            {
                var parts = new StringBuilder();
                foreach (var item in result.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String)
                    {
                        parts.Append(item.GetString());
                    }
                    else if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty("text", out var text)
                             && text.ValueKind == JsonValueKind.String)
                    {
                        parts.Append(text.GetString());
                    }
                }

                return parts.ToString();
            }
        }

        return TryString(root, "text", out var textField) ? textField : "";
    }

    private static bool TryString(JsonElement element, string name, out string value)
    {
        if (element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String)
        {
            value = property.GetString() ?? "";
            return true;
        }

        value = "";
        return false;
    }

    private static bool IsTerminalSuccess(string status) =>
        status.Equals("FINISHED", StringComparison.OrdinalIgnoreCase)
        || status.Equals("completed", StringComparison.OrdinalIgnoreCase);

    private static bool IsTerminalFailure(string status) =>
        status.Equals("ERROR", StringComparison.OrdinalIgnoreCase)
        || status.Equals("CANCELLED", StringComparison.OrdinalIgnoreCase)
        || status.Equals("EXPIRED", StringComparison.OrdinalIgnoreCase)
        || status.Equals("failed", StringComparison.OrdinalIgnoreCase);

    private static string Truncate(string value, int max = 400) =>
        string.IsNullOrEmpty(value) || value.Length <= max ? value : value[..max];
}
