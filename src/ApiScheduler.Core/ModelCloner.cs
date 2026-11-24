using System.Text.Json;
using ApiScheduler.Core.Storage;

namespace ApiScheduler.Core;

public static class ModelCloner
{
    public static T Clone<T>(T value) =>
        JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, JsonOpts.Compact), JsonOpts.Compact)!;
}
