using System.Text.Json;
using System.Text.Json.Serialization;

namespace SmartColonistsCompanion
{
    internal static class ClefJson
    {
        public static readonly JsonSerializerOptions Options = new JsonSerializerOptions
        {
            WriteIndented = false,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };
    }
}
