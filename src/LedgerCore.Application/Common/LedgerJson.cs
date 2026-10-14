using System.Text.Json;
using System.Text.Json.Serialization;

namespace LedgerCore.Application.Common;

/// <summary>
/// The single JSON configuration used for anything LedgerCore persists (idempotent responses, outbox
/// payloads, audit details). Persisted JSON must not change shape when unrelated API settings change.
/// </summary>
public static class LedgerJson
{
    public static readonly JsonSerializerOptions Options = Create();

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            NumberHandling = JsonNumberHandling.Strict,
        };
        options.Converters.Add(new JsonStringEnumConverter());
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }
}
