// <copyright file="Program.cs" company="d365fo-cli contributors">
// MIT
// </copyright>

using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace D365FO.Bridge
{
    /// <summary>
    /// Entry point for the .NET Framework 4.8 metadata bridge. Reads JSON-RPC 2.0
    /// requests from stdin (one request per line, LF-terminated) and writes
    /// responses to stdout in the same framing. Stays alive until stdin closes
    /// or the parent process sends {"method":"shutdown"}.
    /// </summary>
    internal static class Program
    {
        internal const string BridgeVersion = "0.1.0-poc";

        private static int Main(string[] args)
        {
            // Force UTF-8 stdio — net48 defaults to the console code page which
            // corrupts JSON for non-ASCII metadata (labels, captions).
            Console.InputEncoding = new UTF8Encoding(false);
            Console.OutputEncoding = new UTF8Encoding(false);

            var stdin = Console.In;
            var stdout = Console.Out;
            var handlers = new Handlers();

            string line;
            while ((line = stdin.ReadLine()) != null)
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                JsonObject response;
                try
                {
                    response = Dispatch(line, handlers, out bool shutdown);
                    WriteResponse(stdout, response);
                    if (shutdown)
                    {
                        return 0;
                    }
                }
                catch (Exception ex)
                {
                    WriteResponse(stdout, Error(null, -32603, "Internal error: " + ex.Message));
                }
            }

            return 0;
        }

        private static JsonObject Dispatch(string line, Handlers handlers, out bool shutdown)
        {
            shutdown = false;

            JsonNode parsed;
            try
            {
                parsed = JsonNode.Parse(line);
            }
            catch (JsonException ex)
            {
                return Error(null, -32700, "Parse error: " + ex.Message);
            }

            if (!(parsed is JsonObject req))
            {
                return Error(null, -32600, "Invalid Request: expected JSON object.");
            }

            JsonNode idNode = req["id"];
            string method = (string)req["method"];
            JsonNode paramsNode = req["params"];

            if (string.IsNullOrEmpty(method))
            {
                return Error(idNode, -32600, "Invalid Request: missing method.");
            }

            switch (method)
            {
                case "ping":
                    return Ok(idNode, handlers.Ping());
                case "shutdown":
                    shutdown = true;
                    return Ok(idNode, new JsonObject { ["ok"] = true });
                case "readClass":
                    return Ok(idNode, handlers.ReadClass(paramsNode as JsonObject));
                case "readTable":
                    return Ok(idNode, handlers.ReadTable(paramsNode as JsonObject));
                case "readEdt":
                    return Ok(idNode, handlers.ReadEdt(paramsNode as JsonObject));
                case "readEnum":
                    return Ok(idNode, handlers.ReadEnum(paramsNode as JsonObject));
                case "readForm":
                    return Ok(idNode, handlers.ReadForm(paramsNode as JsonObject));
                case "createObject":
                case "saveObject":
                    return Ok(idNode, handlers.SaveObject(paramsNode as JsonObject));
                case "updateObject":
                case "modifyObject":
                    return Ok(idNode, handlers.UpdateObject(paramsNode as JsonObject));
                case "deleteObject":
                    return Ok(idNode, handlers.DeleteObject(paramsNode as JsonObject));
                case "findReferences":
                    return Ok(idNode, handlers.FindReferences(paramsNode as JsonObject));
                case "getModelFolder":
                    return Ok(idNode, handlers.GetModelFolder(paramsNode as JsonObject));
                default:
                    return Error(idNode, -32601, "Method not found: " + method);
            }
        }

        private static JsonObject Ok(JsonNode id, JsonNode result)
        {
            return new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id?.DeepClone(),
                ["result"] = result,
            };
        }

        private static JsonObject Error(JsonNode id, int code, string message)
        {
            return new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id?.DeepClone(),
                ["error"] = new JsonObject
                {
                    ["code"] = code,
                    ["message"] = message,
                },
            };
        }

        private static void WriteResponse(TextWriter stdout, JsonObject response)
        {
            // One JSON per line — matches upstream d365fo-mcp-server's bridge
            // framing. Explicit flush so parent can block on ReadLine.
            stdout.Write(response.ToJsonString());
            stdout.Write('\n');
            stdout.Flush();
        }
    }
}
