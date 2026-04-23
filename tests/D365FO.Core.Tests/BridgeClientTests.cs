using System.IO;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using D365FO.Core.Bridge;

namespace D365FO.Core.Tests;

/// <summary>
/// Verifies the JSON-RPC framing and error surface of <see cref="BridgeClient"/>
/// without spawning a real process. Uses an in-memory <see cref="AnonymousPipeStream"/>
/// pair wrapped in text readers/writers so the test simulates the bridge's
/// stdin/stdout at the line-framed JSON level.
/// </summary>
public sealed class BridgeClientTests
{
    [Fact]
    public async Task Ping_success_returns_result_object()
    {
        using var harness = FakeBridge.Create(respondWith: request =>
        {
            Assert.Equal("ping", (string?)request["method"]);
            return new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = request["id"]?.DeepClone(),
                ["result"] = new JsonObject
                {
                    ["pong"] = true,
                    ["version"] = "test",
                },
            };
        });

        var result = await harness.Client.PingAsync();

        Assert.NotNull(result);
        Assert.True((bool?)result!["pong"]);
        Assert.Equal("test", (string?)result["version"]);
    }

    [Fact]
    public async Task Error_response_throws_BridgeException_with_code_and_message()
    {
        using var harness = FakeBridge.Create(respondWith: request => new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = request["id"]?.DeepClone(),
            ["error"] = new JsonObject
            {
                ["code"] = -32601,
                ["message"] = "Method not found: unknown",
            },
        });

        var ex = await Assert.ThrowsAsync<BridgeException>(
            async () => await harness.Client.SendAsync("unknown", new JsonObject()));

        Assert.Contains("-32601", ex.Message);
        Assert.Contains("Method not found", ex.Message);
    }

    [Fact]
    public async Task Stream_closed_before_reply_throws_BridgeException()
    {
        using var harness = FakeBridge.Create(respondWith: null);
        var ex = await Assert.ThrowsAsync<BridgeException>(
            async () => await harness.Client.SendAsync("ping", new JsonObject()));
        Assert.Contains("closed", ex.Message);
    }

    /// <summary>
    /// Stand-in for <c>D365FO.Bridge.exe</c> backed by a pair of
    /// in-memory readers/writers. Delivers whatever the test wants as the
    /// next response on each request.
    /// </summary>
    private sealed class FakeBridge : System.IDisposable
    {
        public BridgeClient Client { get; }

        private readonly StringWriter responseWriter;
        private readonly StringWriter requestCapture;

        private FakeBridge(BridgeClient client, StringWriter responseWriter, StringWriter requestCapture)
        {
            Client = client;
            this.responseWriter = responseWriter;
            this.requestCapture = requestCapture;
        }

        public static FakeBridge Create(System.Func<JsonObject, JsonObject>? respondWith)
        {
            // Client writes requests into `requestCapture`. We pre-compute the
            // response and place it in a StringReader that the client reads
            // from. Each test exchanges exactly one request/response pair —
            // enough for the current POC-level assertions.
            var requestCapture = new StringWriter();
            var reader = new DeferredResponseReader(respondWith, requestCapture);
            var responseWriter = new StringWriter();
            var client = new BridgeClient(writer: new TeeWriter(requestCapture, reader), reader: reader);
            return new FakeBridge(client, responseWriter, requestCapture);
        }

        public void Dispose()
        {
            Client.Dispose();
        }
    }

    /// <summary>
    /// TextWriter that forwards every write into a capture buffer AND
    /// notifies a <see cref="DeferredResponseReader"/> when a full line
    /// (a single JSON-RPC request) has been flushed.
    /// </summary>
    private sealed class TeeWriter : TextWriter
    {
        private readonly StringWriter capture;
        private readonly DeferredResponseReader signal;
        private readonly System.Text.StringBuilder buffer = new();

        public TeeWriter(StringWriter capture, DeferredResponseReader signal)
        {
            this.capture = capture;
            this.signal = signal;
        }

        public override System.Text.Encoding Encoding => System.Text.Encoding.UTF8;

        public override void Write(char value)
        {
            capture.Write(value);
            if (value == '\n')
            {
                var line = buffer.ToString();
                buffer.Clear();
                signal.OnRequest(line);
            }
            else
            {
                buffer.Append(value);
            }
        }

        public override void Write(string? value)
        {
            if (value is null)
            {
                return;
            }

            foreach (var ch in value)
            {
                Write(ch);
            }
        }

        public override void WriteLine(string? value)
        {
            Write(value);
            Write('\n');
        }

        public override void Flush() => capture.Flush();
    }

    /// <summary>
    /// TextReader whose <see cref="ReadLine"/> blocks until the paired
    /// <see cref="TeeWriter"/> delivers a request, at which point the
    /// response factory is invoked.
    /// </summary>
    private sealed class DeferredResponseReader : TextReader
    {
        private readonly System.Func<JsonObject, JsonObject>? respondWith;
        private readonly StringWriter log;
        private readonly System.Collections.Generic.Queue<string> queued = new();
        private readonly object gate = new();

        public DeferredResponseReader(System.Func<JsonObject, JsonObject>? respondWith, StringWriter log)
        {
            this.respondWith = respondWith;
            this.log = log;
        }

        public void OnRequest(string requestLine)
        {
            if (respondWith is null)
            {
                return;
            }

            var parsed = JsonNode.Parse(requestLine) as JsonObject
                ?? throw new System.InvalidOperationException("bad request JSON");
            var response = respondWith(parsed);
            lock (gate)
            {
                queued.Enqueue(response.ToJsonString());
            }
        }

        public override string? ReadLine()
        {
            lock (gate)
            {
                return queued.Count == 0 ? null : queued.Dequeue();
            }
        }
    }
}
