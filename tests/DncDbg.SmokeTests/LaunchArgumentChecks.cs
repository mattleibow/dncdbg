using System.Text.Json;
using DncDbg.Protocol;

internal static class LaunchArgumentChecks
{
    private const string Cases = """
        [
          {"input":"","expected":[]},
          {"input":" \t\r\n\u000b\f","expected":[]},
          {"input":"one \"two words\" three","expected":["one","two words","three"]},
          {"input":"\"\" tail","expected":["","tail"]},
          {"input":"a\\\"b tail","expected":["a\"b","tail"]},
          {"input":"\"unfinished words","expected":["unfinished words"]},
          {"input":"before\"inside\"after","expected":["beforeinsideafter"]},
          {"input":"a\u00a0b c","expected":["a\u00a0b","c"]},
          {"input":"C:\\plain\\path tail","expected":["C:\\plain\\path","tail"]}
        ]
        """;

    public static int Run()
    {
        int checks = 0;
        using JsonDocument cases = JsonDocument.Parse(Cases);
        foreach (JsonElement test in cases.RootElement.EnumerateArray())
        {
            string input = test.GetProperty("input").GetString()!;
            string[] expected = test.GetProperty("expected").Deserialize<string[]>()!;
            string[] actual = CommandLineArguments.Split(input);
            if (!expected.SequenceEqual(actual))
                throw new InvalidOperationException("Command line split mismatch: " + input);
            var arguments = new DapMessage { Arguments = JsonSerializer.SerializeToElement(new { args = input }) }
                .GetArguments<LaunchArguments>()!;
            if (!expected.SequenceEqual(arguments.Args!))
                throw new InvalidOperationException("Launch argument conversion mismatch: " + input);
            checks++;
        }
        foreach (string input in new[] { "42", "[\"ok\",42]" })
        {
            using JsonDocument document = JsonDocument.Parse("{\"args\":" + input + "}");
            try
            {
                new DapMessage { Arguments = document.RootElement }
                    .GetArguments<LaunchArguments>();
                throw new InvalidOperationException("Invalid argument input was accepted: " + input);
            }
            catch (JsonException error) when (error.Message == "The 'args' option must be a string or an array of strings.")
            {
                checks++;
            }
        }
        var omitted = JsonSerializer.Deserialize<LaunchArguments>("{}", DapJson.Options)!;
        var disabled = JsonSerializer.Deserialize<LaunchArguments>("{\"launchSettingsProfile\":null}", DapJson.Options)!;
        if (omitted.LaunchSettingsProfileSpecified || !disabled.LaunchSettingsProfileSpecified)
            throw new InvalidOperationException("Omitted and explicit-null launch profiles must remain distinct.");
        checks++;
        return checks;
    }
}
