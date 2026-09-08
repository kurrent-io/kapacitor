using System.Text.Json.Serialization;

namespace Capacitor.Remote.Models;

[JsonSerializable(typeof(AgentInstanceDto))]
[JsonSerializable(typeof(AgentInstanceDto[]))]
[JsonSerializable(typeof(DaemonInfo))]
[JsonSerializable(typeof(List<DaemonInfo>))]
public partial class RemoteModelsJsonContext : JsonSerializerContext;
