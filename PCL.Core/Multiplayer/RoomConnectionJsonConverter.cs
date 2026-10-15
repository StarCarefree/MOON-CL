using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PCL.Core.Multiplayer;

/// <summary>Mode-specific wire fields; Java snapshots may emit null for unused fields.</summary>
public sealed class RoomConnectionJsonConverter : JsonConverter<RoomConnection>
{
    public override RoomConnection Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;
        string Text(string key) => root.TryGetProperty(key, out var value) && value.ValueKind != JsonValueKind.Null ? value.GetString() ?? "" : "";
        int Number(string key) => root.TryGetProperty(key, out var value) && value.ValueKind != JsonValueKind.Null ? value.GetInt32() : 0;
        var connection = new RoomConnection(Text("mode"), Text("address"), Number("port"), Text("serverHost"), Number("serverPort"), Text("proxyName"), Text("secretKey"));
        connection.Validate();
        return connection;
    }
    public override void Write(Utf8JsonWriter writer, RoomConnection connection, JsonSerializerOptions options)
    {
        connection.Validate();
        writer.WriteStartObject(); writer.WriteString("mode", connection.Mode);
        if (connection.Mode == "p2p")
        {
            writer.WriteString("serverHost", connection.ServerHost); writer.WriteNumber("serverPort", connection.ServerPort);
            writer.WriteString("proxyName", connection.ProxyName); writer.WriteString("secretKey", connection.SecretKey);
        }
        else { writer.WriteString("address", connection.Address); writer.WriteNumber("port", connection.Port); }
        writer.WriteEndObject();
    }
}
