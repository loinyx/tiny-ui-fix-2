using System.Text.Json.Serialization;

namespace TinyUIFixApp;

/// JSON read and written by the app, generated at build time so trimming can't remove what it needs.
[JsonSerializable(typeof(State))]
[JsonSerializable(typeof(GameInstall))]
[JsonSerializable(typeof(Dictionary<string, string>))]
internal partial class AppJson : JsonSerializerContext;
