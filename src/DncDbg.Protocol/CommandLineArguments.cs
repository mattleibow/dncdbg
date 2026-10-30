using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DncDbg.Protocol;

public static class CommandLineArguments
{
    public static string[] Split(string commandLine)
    {
        var result = new List<string>();
        var current = new StringBuilder();
        bool quoted = false, hasArgument = false;
        for (int i = 0; i < commandLine.Length; i++)
        {
            char c = commandLine[i];
            if (c == '\\' && i + 1 < commandLine.Length && commandLine[i + 1] == '"')
            {
                current.Append('"');
                hasArgument = true;
                i++;
            }
            else if (c == '"')
            {
                quoted = !quoted;
                hasArgument = true;
            }
            else if ((c is ' ' or '\t' or '\r' or '\n' or '\v' or '\f') && !quoted)
            {
                if (hasArgument)
                    result.Add(current.ToString());
                current.Clear();
                hasArgument = false;
            }
            else
            {
                current.Append(c);
                hasArgument = true;
            }
        }
        if (hasArgument)
            result.Add(current.ToString());
        return result.ToArray();
    }
}

public sealed class LaunchArgumentListConverter : JsonConverter<string[]>
{
    public override string[] Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
            return CommandLineArguments.Split(reader.GetString()!);
        if (reader.TokenType == JsonTokenType.StartArray)
        {
            var result = new List<string>();
            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            {
                if (reader.TokenType != JsonTokenType.String)
                    throw new JsonException("The 'args' option must be a string or an array of strings.");
                result.Add(reader.GetString()!);
            }
            return result.ToArray();
        }
        throw new JsonException("The 'args' option must be a string or an array of strings.");
    }

    public override void Write(Utf8JsonWriter writer, string[] value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        foreach (string argument in value)
            writer.WriteStringValue(argument);
        writer.WriteEndArray();
    }
}
