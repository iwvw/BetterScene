using System.Diagnostics;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace BetterScene.Core.Control;

public static class ControlProtocol
{
    public const int Version = 1;

    public const string PipePrefix = "BetterScene.Control.v1";

    public static string PipeNameFor(int sessionId) => $"{PipePrefix}.{sessionId}";

    public static string CurrentPipeName => PipeNameFor(Process.GetCurrentProcess().SessionId);

    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}

public sealed class ControlRequest
{
    public int V { get; set; } = ControlProtocol.Version;

    public string Id { get; set; } = string.Empty;

    public string Cmd { get; set; } = string.Empty;

    public JsonObject? Args { get; set; }
}

public sealed class ControlResponse
{
    public int V { get; set; } = ControlProtocol.Version;

    public string Id { get; set; } = string.Empty;

    public bool Ok { get; set; }

    public JsonNode? Result { get; set; }

    public ControlError? Error { get; set; }

    public static ControlResponse Success(string id, JsonNode? result) =>
        new() { Id = id, Ok = true, Result = result };

    public static ControlResponse Fail(string id, string code, string message) =>
        new()
        {
            Id = id,
            Ok = false,
            Error = new ControlError { Code = code, Message = message },
        };
}

public sealed class ControlError
{
    public string Code { get; set; } = string.Empty;

    public string Message { get; set; } = string.Empty;
}

public static class ControlErrorCodes
{
    public const string BadRequest = "bad_request";
    public const string UnknownCommand = "unknown_command";
    public const string DisplayNotFound = "display_not_found";
    public const string DisplayAmbiguous = "display_ambiguous";
    public const string Unsupported = "unsupported";
    public const string ApplyFailed = "apply_failed";
    public const string Timeout = "timeout";
    public const string RateLimited = "rate_limited";
    public const string Internal = "internal";
}

public sealed class ControlException : Exception
{
    public ControlException(string code, string message)
        : base(message)
    {
        Code = code;
    }

    public string Code { get; }
}
