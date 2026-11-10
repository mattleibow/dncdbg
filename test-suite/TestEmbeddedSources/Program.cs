using System;

using DbgTest;
using DbgTest.DAP;
using DbgTest.Script;

namespace TestEmbeddedSources
{
class Program
{
    static void Main(string[] args)
    {
        Label.Checkpoint("init", "sources_test",
            (Object context) =>
            {
                Context Context = (Context)context;
                Context.Initialize(@"__FILE__:__LINE__");
                Context.AddBreakpointAndAddID(@"__FILE__:__LINE__", "bp1");
                Context.SetBreakpointsAndCheckIDs(@"__FILE__:__LINE__");
                Context.ConfigurationDone(@"__FILE__:__LINE__");
                Context.Launch(@"__FILE__:__LINE__");

                Context.WasEntryPointHit(@"__FILE__:__LINE__");
                Context.Continue(@"__FILE__:__LINE__");
            });

        // The repeated lines below make this file large enough for Roslyn to
        // store its embedded source compressed in the PDB, while the tiny
        // Program2.cs is stored uncompressed. Both variants are checked via
        // "source" requests after the breakpoint is hit.
        System.Console.WriteLine("Compressed source file");
        System.Console.WriteLine("Compressed source file");
        System.Console.WriteLine("Compressed source file");
        System.Console.WriteLine("Compressed source file");
        System.Console.WriteLine("Compressed source file");
        System.Console.WriteLine("Compressed source file");
        System.Console.WriteLine("Compressed source file");
        System.Console.WriteLine("Compressed source file");
        System.Console.WriteLine("Compressed source file");
        System.Console.WriteLine("Compressed source file");
        System.Console.WriteLine("Compressed source file");
        System.Console.WriteLine("Compressed source file");
        System.Console.WriteLine("Compressed source file");
        System.Console.WriteLine("Compressed source file");
        System.Console.WriteLine("Compressed source file");
        System.Console.WriteLine("Compressed source file");
        System.Console.WriteLine("Compressed source file");
        System.Console.WriteLine("Compressed source file");
        System.Console.WriteLine("Compressed source file");
        System.Console.WriteLine("Compressed source file");
        System.Console.WriteLine("Compressed source file");
        System.Console.WriteLine("Compressed source file");
        System.Console.WriteLine("Compressed source file");
        System.Console.WriteLine("Compressed source file");
        System.Console.WriteLine("Compressed source file");
        System.Console.WriteLine("Compressed source file");
        System.Console.WriteLine("Compressed source file");

        GC.KeepAlive(args);                                                Label.Breakpoint("bp1");

        Label.Checkpoint("sources_test", "finish",
            (Object context) =>
            {
                Context Context = (Context)context;
                Context.WasBreakpointHit(@"__FILE__:__LINE__", "bp1", checkSourcePath: false);

                int sourceReference = Context.GetSourceReferenceFromTopFrame(@"__FILE__:__LINE__");
                Context.CompareSources(@"__FILE__:__LINE__", "Program.cs", sourceReference: sourceReference, sourcePath: "");

                Context.CompareSources(@"__FILE__:__LINE__", "Program2.cs", sourceReference: 0, sourcePath: "Program2.cs");

                sourceReference = Context.GetSourceReferenceFromLoadedSources(@"__FILE__:__LINE__", sourceName: "Program.cs");
                Context.CompareSources(@"__FILE__:__LINE__", "Program.cs", sourceReference: sourceReference, sourcePath: "");

                Context.CheckSourceRequestFallbacks(@"__FILE__:__LINE__", sourceReference);
                Context.CheckIncomingSourceRequests(@"__FILE__:__LINE__", sourceReference);

                sourceReference = Context.GetSourceReferenceFromLoadedSources(@"__FILE__:__LINE__", sourceName: "Program2.cs");
                Context.CompareSources(@"__FILE__:__LINE__", "Program2.cs", sourceReference: sourceReference, sourcePath: "");

                Context.Continue(@"__FILE__:__LINE__");
            });

        Label.Checkpoint("finish", "",
            (Object context) =>
            {
                Context Context = (Context)context;
                Context.WasExit(@"__FILE__:__LINE__");
                Context.DebuggerExit(@"__FILE__:__LINE__");
                Context.CheckSourceRequestResults(@"__FILE__:__LINE__");
            });
    }
}

}

namespace DbgTest.Script
    {
    partial class Context
    {
        sealed class RawSourceRequest : Request
        {
            public Newtonsoft.Json.Linq.JObject arguments = new();
        }

        readonly System.Collections.Generic.List<string> m_sourceRequestFailures = new();

        public void CheckSourceRequestFallbacks(string callerTrace, int sourceReference)
        {
            Assert.True(sourceReference > 0, callerTrace);
            int otherReference = GetSourceReferenceFromLoadedSources(callerTrace, "Program2.cs");
            Assert.True(otherReference > 0 && otherReference != sourceReference, callerTrace);
            string directory = System.IO.Path.GetDirectoryName(m_controlInfo.SourceFilesPath!.Split(';')[0])!;
            string main = System.IO.File.ReadAllText(System.IO.Path.Combine(directory, "Program.cs"));
            string other = System.IO.File.ReadAllText(System.IO.Path.Combine(directory, "Program2.cs"));

            foreach (bool nested in new[] { true, false })
            foreach (string field in new[] { "path", "name" })
            foreach (string value in new[] { "null", "42", "false", "[]", "{}" })
            {
                var source = new Newtonsoft.Json.Linq.JObject
                {
                    [field] = Newtonsoft.Json.Linq.JToken.Parse(value),
                };
                if (nested)
                    source["sourceReference"] = sourceReference;
                Check(new() { ["source"] = source, ["sourceReference"] = nested ? 0 : sourceReference },
                    main, (nested ? "nested" : "top-level") + " reference, " + field + "=" + value);
            }

            Check(new() { ["source"] = new Newtonsoft.Json.Linq.JObject { ["name"] = "Program2.cs" },
                ["sourceReference"] = 0 }, other, "name-only, absent path");
            Check(new() { ["source"] = new Newtonsoft.Json.Linq.JObject { ["name"] = "Program2.cs", ["path"] = "" },
                ["sourceReference"] = 0 }, other, "name-only, empty path");

            Check(new() { ["sourceReference"] = sourceReference }, main, "absent source");
            Check(new() { ["source"] = new Newtonsoft.Json.Linq.JObject(), ["sourceReference"] = sourceReference },
                main, "absent path and name");
            foreach (int nestedReference in new[] { sourceReference, 0, -1 })
            {
                Check(new() { ["source"] = new Newtonsoft.Json.Linq.JObject { ["sourceReference"] = nestedReference },
                    ["sourceReference"] = otherReference }, nestedReference > 0 ? main : other,
                    "nested reference precedence " + nestedReference);
            }
            Check(new() { ["source"] = new Newtonsoft.Json.Linq.JObject { ["path"] = "Program2.cs", ["name"] = "Program.cs" },
                ["sourceReference"] = 0 }, other, "nonempty path precedes name");

            Check(new() { ["source"] = new Newtonsoft.Json.Linq.JObject { ["path"] = "missing-source.cs", ["name"] = "Program2.cs" },
                ["sourceReference"] = 0 }, null, "unresolvable nonempty path does not fall back to name");
            Check(new() { ["source"] = new Newtonsoft.Json.Linq.JObject { ["path"] = 42, ["name"] = false },
                ["sourceReference"] = 0 }, null, "no usable path, name or reference");
            foreach (string value in new[] { "42", "false", "[]", "\"invalid\"" })
                Check(new() { ["source"] = Newtonsoft.Json.Linq.JToken.Parse(value), ["sourceReference"] = sourceReference },
                    null, "invalid source object " + value);
            foreach (string value in new[] { "\"invalid\"", "[]", "{}" })
            {
                Check(new() { ["source"] = new Newtonsoft.Json.Linq.JObject { ["sourceReference"] = Newtonsoft.Json.Linq.JToken.Parse(value) },
                    ["sourceReference"] = sourceReference }, null, "invalid nested reference " + value);
                Check(new() { ["source"] = new Newtonsoft.Json.Linq.JObject { ["sourceReference"] = sourceReference },
                    ["sourceReference"] = Newtonsoft.Json.Linq.JToken.Parse(value) }, null, "invalid top-level reference " + value);
            }
            Check(new() { ["expression"] = 42 }, null, "required expression remains a string", "evaluate");
            Check(new() { ["source"] = new Newtonsoft.Json.Linq.JObject { ["path"] = "Program.cs" }, ["line"] = "invalid" },
                null, "required line remains an integer", "breakpointLocations");

            void Check(Newtonsoft.Json.Linq.JObject arguments, string? expected, string label, string command = "source")
            {
                var result = m_dapDebugger.Request(new RawSourceRequest { command = command, arguments = arguments });
                var response = Newtonsoft.Json.Linq.JObject.Parse(result.ResponseStr);
                bool matches = (string?)response["command"] == command && (string?)response["type"] == "response" &&
                    (expected == null
                        ? !result.Success && !string.IsNullOrEmpty((string?)response["message"]) && response["body"]?["content"] == null
                        : result.Success && (string?)response["body"]?["content"] == expected);
                if (!matches)
                    m_sourceRequestFailures.Add(label + ": " + (result.Success ? "unexpected content/success" : (string?)response["message"]));
                if (expected == null)
                {
                    var recovery = new SourceRequest();
                    recovery.arguments.sourceReference = sourceReference;
                    var recovered = m_dapDebugger.Request(recovery);
                    var body = Newtonsoft.Json.Linq.JObject.Parse(recovered.ResponseStr);
                    if (!recovered.Success || (string?)body["command"] != "source" || (string?)body["body"]?["content"] != main)
                        m_sourceRequestFailures.Add(label + ": source recovery failed");
                }
            }
        }

        public void CheckIncomingSourceRequests(string callerTrace, int sourceReference)
        {
            int line = ((LineBreakpoint)m_controlInfo.Breakpoints["bp1"]).NumLine;
            foreach (string command in new[] { "setBreakpoints", "gotoTargets", "breakpointLocations" })
            {
                foreach (string field in new[] { "name", "path" })
                foreach (string value in new[] { "null", "42", "false", "[]", "{}" })
                {
                    var source = new Newtonsoft.Json.Linq.JObject
                    {
                        [field == "name" ? "path" : "name"] = "Program.cs",
                        [field] = Newtonsoft.Json.Linq.JToken.Parse(value),
                    };
                    Check(source, true, field + "=" + value);
                }
                Check(new Newtonsoft.Json.Linq.JObject { ["path"] = "Program.cs", ["name"] = "missing-source.cs" }, true, "path precedes name");
                Check(new Newtonsoft.Json.Linq.JObject { ["path"] = "", ["name"] = "Program.cs" }, true, "empty path falls back to name");
                Check(new Newtonsoft.Json.Linq.JObject { ["name"] = "Program.cs" }, true, "absent path falls back to name");
                Check(new Newtonsoft.Json.Linq.JObject(), false, "missing source identity");
                Check(new Newtonsoft.Json.Linq.JObject { ["path"] = 42, ["name"] = false }, false, "unusable source identity");
                foreach (string value in new[] { "null", "42", "[]" })
                    Check(Newtonsoft.Json.Linq.JToken.Parse(value), false, "invalid source object " + value);
                foreach (string value in new[] { "\"invalid\"", "[]", "{}" })
                    Check(new Newtonsoft.Json.Linq.JObject
                    {
                        ["path"] = "Program.cs",
                        ["sourceReference"] = Newtonsoft.Json.Linq.JToken.Parse(value),
                    }, false, "invalid source reference " + value);
                Check(new Newtonsoft.Json.Linq.JObject { ["path"] = "Program.cs" }, false, "required line remains an integer", invalidLine: true);
                if (command == "setBreakpoints")
                    Check(new Newtonsoft.Json.Linq.JObject { ["path"] = "Program.cs" }, false, "condition remains a string", invalidCondition: true);

                void Check(Newtonsoft.Json.Linq.JToken source, bool expectedSuccess, string label,
                           bool invalidLine = false, bool invalidCondition = false)
                {
                    var arguments = new Newtonsoft.Json.Linq.JObject { ["source"] = source };
                    var requestedLine = Newtonsoft.Json.Linq.JToken.Parse(invalidLine ? "\"invalid\"" :
                        line.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    if (command == "setBreakpoints")
                    {
                        var breakpoint = new Newtonsoft.Json.Linq.JObject { ["line"] = requestedLine };
                        if (invalidCondition)
                            breakpoint["condition"] = 42;
                        arguments["breakpoints"] = new Newtonsoft.Json.Linq.JArray(breakpoint);
                    }
                    else
                        arguments["line"] = requestedLine;

                    var result = m_dapDebugger.Request(new RawSourceRequest { command = command, arguments = arguments });
                    var response = Newtonsoft.Json.Linq.JObject.Parse(result.ResponseStr);
                    bool matches = (string?)response["command"] == command && (string?)response["type"] == "response" &&
                        result.Success == expectedSuccess;
                    if (expectedSuccess)
                    {
                        var locations = response["body"]?[command == "gotoTargets" ? "targets" : "breakpoints"] as Newtonsoft.Json.Linq.JArray;
                        matches &= locations?.Count == 1 && (int?)locations[0]["line"] == line &&
                            (int?)locations[0]["column"] == 9 && (int?)locations[0]["endLine"] == line &&
                            (int?)locations[0]["endColumn"] == 28;
                        if (command == "setBreakpoints")
                            matches &= locations?.Count == 1 && (bool?)locations[0]["verified"] == true &&
                                (string?)locations[0]["source"]?["name"] == "Program.cs";
                        if (command == "gotoTargets")
                            matches &= locations?.Count == 1 && (int?)locations[0]["id"] > 0 &&
                                !string.IsNullOrEmpty((string?)locations[0]["label"]);
                    }
                    else
                        matches &= !string.IsNullOrEmpty((string?)response["message"]) &&
                            response["body"]?["breakpoints"] == null && response["body"]?["targets"] == null;
                    if (!matches)
                        m_sourceRequestFailures.Add(command + ", " + label + ": " +
                            (result.Success ? "unexpected result" : (string?)response["message"]));

                    // A valid same-command request must recover after every malformed input.
                    if (!expectedSuccess)
                    {
                        var recovery = new RawSourceRequest
                        {
                            command = command,
                            arguments = (Newtonsoft.Json.Linq.JObject)arguments.DeepClone(),
                        };
                        recovery.arguments["source"] = new Newtonsoft.Json.Linq.JObject { ["path"] = "Program.cs" };
                        if (command == "setBreakpoints")
                            recovery.arguments["breakpoints"] = new Newtonsoft.Json.Linq.JArray(new Newtonsoft.Json.Linq.JObject { ["line"] = line });
                        else
                            recovery.arguments["line"] = line;
                        var recovered = m_dapDebugger.Request(recovery);
                        var body = Newtonsoft.Json.Linq.JObject.Parse(recovered.ResponseStr);
                        var locations = body["body"]?[command == "gotoTargets" ? "targets" : "breakpoints"] as Newtonsoft.Json.Linq.JArray;
                        if (!recovered.Success || (string?)body["command"] != command ||
                            locations?.Count != 1 || (int?)locations[0]["line"] != line)
                            m_sourceRequestFailures.Add(command + ", " + label + ": recovery failed");
                    }
                    CompareSources(callerTrace, "Program.cs", sourceReference, "");
                }
            }
        }

        public void CheckSourceRequestResults(string callerTrace)
        {
            Assert.Equal("", string.Join("\n", m_sourceRequestFailures), callerTrace);
        }
    }
}
