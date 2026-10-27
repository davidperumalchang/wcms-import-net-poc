using System.Text.Json;
using System.Text.Json.Serialization;

namespace WcmsImport.Common;

// OMITTED (production): the tenant would come from the authenticated caller, not a constant.
public static class Defaults
{
    public const string Tenant = "acme"; // one hard-coded tenant keeps the prototype small

    /// <summary>camelCase, nulls left out. Used for everything we store as jsonb or send over the wire.</summary>
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}
