using System.Text.Json.Serialization;

namespace LocalCoop.Mod.Runtime;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum BrokerClientRole
{
    Host,
    Client
}
