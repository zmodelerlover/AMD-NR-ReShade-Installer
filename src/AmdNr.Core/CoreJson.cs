// The JSON the engine reads and writes, described at compile time. Reflection over these types is
// what a trimmed executable cannot do, and a trimmed executable is less than half the size of one
// that is not.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace AmdNr.Core;

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(PayloadManifest))]
[JsonSerializable(typeof(ApiDatabase))]
[JsonSerializable(typeof(Dictionary<string, string>))]
internal sealed partial class CoreJson : JsonSerializerContext;
