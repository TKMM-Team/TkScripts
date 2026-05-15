using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TkScripts.LookupTables.Converters;

internal sealed class CompactJsonConverter : JsonConverter<List<SortedDictionary<int, long>>>
{
    public override List<SortedDictionary<int, long>> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var list = new List<SortedDictionary<int, long>>();

        if (reader.TokenType != JsonTokenType.StartArray)
            throw new JsonException();

        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray) {
            if (reader.TokenType != JsonTokenType.StartObject) continue;

            var dict = new SortedDictionary<int, long>();
            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject) {
                var key = int.Parse(reader.GetString()!, CultureInfo.InvariantCulture);
                reader.Read();
                dict[key] = reader.GetInt64();
            }
            list.Add(dict);
        }

        return list;
    }

    public override void Write(Utf8JsonWriter writer, List<SortedDictionary<int, long>> value, JsonSerializerOptions options)
    {
        var outerIndent = new string(' ', writer.CurrentDepth * 2);
        var innerIndent = new string(' ', (writer.CurrentDepth + 1) * 2);

        var sb = new System.Text.StringBuilder("[");
        for (var i = 0; i < value.Count; i++) {
            sb.Append('\n').Append(innerIndent);
            sb.Append("{ ");
            sb.Append(string.Join(", ", value[i].Select(e => $"\"{e.Key}\": {e.Value}")));
            sb.Append(i < value.Count - 1 ? " }," : " }");
        }

        if (value.Count > 0) sb.Append('\n').Append(outerIndent);
        sb.Append(']');

        writer.WriteRawValue(sb.ToString(), skipInputValidation: true);
    }
}
