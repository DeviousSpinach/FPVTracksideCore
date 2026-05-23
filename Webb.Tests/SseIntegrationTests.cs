using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Tools;
using Webb;
using Xunit;

namespace Webb.Tests
{
    public class SseIntegrationTests : IDisposable
    {
        public SseIntegrationTests()
        {
            Logger.Init(new DirectoryInfo(Path.GetTempPath()));
        }

        public void Dispose()
        {
            Logger.CleanUp();
        }

        [Fact]
        public async Task ConnectedClient_ReceivesContentTypeEventStream()
        {
            using var env = new SseTestEnvironment();
            using var client = new HttpClient();

            var response = await client.GetAsync(env.Url, HttpCompletionOption.ResponseHeadersRead);

            Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
        }

        [Fact]
        public async Task ConnectedClient_ReceivesInitialConnectedComment()
        {
            using var env = new SseTestEnvironment();
            using var client = new HttpClient();

            var response = await client.GetAsync(env.Url, HttpCompletionOption.ResponseHeadersRead);
            using var reader = new StreamReader(await response.Content.ReadAsStreamAsync());

            // The connected comment block is ": connected instance=<guid>\n\n"
            var commentLine = await ReadLineAsync(reader);
            var blankLine = await ReadLineAsync(reader);

            Assert.StartsWith(": connected instance=", commentLine);
            Assert.Equal("", blankLine);
        }

        [Fact]
        public async Task ConnectedClient_ConnectedCommentContainsConsistentInstanceId()
        {
            using var env = new SseTestEnvironment(maxClients: 2);
            using var clientA = new HttpClient();
            using var clientB = new HttpClient();

            var responseA = await clientA.GetAsync(env.Url, HttpCompletionOption.ResponseHeadersRead);
            var responseB = await clientB.GetAsync(env.Url, HttpCompletionOption.ResponseHeadersRead);

            var lineA = await ReadLineAsync(new StreamReader(await responseA.Content.ReadAsStreamAsync()));
            var lineB = await ReadLineAsync(new StreamReader(await responseB.Content.ReadAsStreamAsync()));

            // Both clients connect to the same server instance — instance IDs must match
            Assert.Equal(lineA, lineB);
        }

        [Fact]
        public async Task Dispose_BroadcastsServerStoppingEvent()
        {
            using var env = new SseTestEnvironment();
            using var client = new HttpClient();

            var response = await client.GetAsync(env.Url, HttpCompletionOption.ResponseHeadersRead);
            using var reader = new StreamReader(await response.Content.ReadAsStreamAsync());

            await DrainUntilBlankLineAsync(reader);
            await env.WaitForClientAsync();

            // Dispose broadcasts server_stopping to all live clients before cancelling
            env.SseManager.Dispose();

            var message = await ReadSseMessageAsync(reader);
            Assert.Contains("event: server_stopping", message);
        }

        [Fact]
        public async Task Broadcast_DeliversEventToConnectedClient()
        {
            using var env = new SseTestEnvironment();
            using var client = new HttpClient();

            var response = await client.GetAsync(env.Url, HttpCompletionOption.ResponseHeadersRead);
            using var reader = new StreamReader(await response.Content.ReadAsStreamAsync());

            await DrainUntilBlankLineAsync(reader); // consume ": connected\n\n"
            await env.WaitForClientAsync();

            env.SseManager.Broadcast("test_event", new { value = 42 });

            var message = await ReadSseMessageAsync(reader);
            Assert.Contains("event: test_event", message);
            Assert.Contains(message, l => l.StartsWith("data:") && l.Contains("\"value\":42"));
        }

        [Fact]
        public async Task Broadcast_MultipleClients_AllReceiveEvent()
        {
            using var env = new SseTestEnvironment(maxClients: 2);
            using var clientA = new HttpClient();
            using var clientB = new HttpClient();

            var responseA = await clientA.GetAsync(env.Url, HttpCompletionOption.ResponseHeadersRead);
            var responseB = await clientB.GetAsync(env.Url, HttpCompletionOption.ResponseHeadersRead);

            var readerA = new StreamReader(await responseA.Content.ReadAsStreamAsync());
            var readerB = new StreamReader(await responseB.Content.ReadAsStreamAsync());

            await DrainUntilBlankLineAsync(readerA);
            await DrainUntilBlankLineAsync(readerB);
            await env.WaitForClientAsync(count: 2);

            env.SseManager.Broadcast("multi_event", new { });

            var msgA = await ReadSseMessageAsync(readerA);
            var msgB = await ReadSseMessageAsync(readerB);

            Assert.Contains("event: multi_event", msgA);
            Assert.Contains("event: multi_event", msgB);
        }

        [Fact]
        public async Task IncludeFilter_OnlyDeliversMatchedEvents()
        {
            // Set filter before connecting — it is captured at connection time
            using var env = new SseTestEnvironment();
            env.SetFilter(new SseEventFilter(
                new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "wanted" },
                new HashSet<string>()));

            using var client = new HttpClient();
            var response = await client.GetAsync(env.Url, HttpCompletionOption.ResponseHeadersRead);
            using var reader = new StreamReader(await response.Content.ReadAsStreamAsync());

            await DrainUntilBlankLineAsync(reader);
            await env.WaitForClientAsync();

            env.SseManager.Broadcast("unwanted", new { });
            env.SseManager.Broadcast("wanted", new { value = 1 });

            var message = await ReadSseMessageAsync(reader);
            Assert.Contains("event: wanted", message);
            Assert.DoesNotContain("event: unwanted", message);
        }

        [Fact]
        public async Task Broadcast_IncludesEventId()
        {
            using var env = new SseTestEnvironment();
            using var client = new HttpClient();

            var response = await client.GetAsync(env.Url, HttpCompletionOption.ResponseHeadersRead);
            using var reader = new StreamReader(await response.Content.ReadAsStreamAsync());

            await DrainUntilBlankLineAsync(reader);
            await env.WaitForClientAsync();

            env.SseManager.Broadcast("id_test", new { });

            var message = await ReadSseMessageAsync(reader);
            Assert.Contains(message, l => l.StartsWith("id:"));
        }

        [Fact]
        public async Task Reconnect_WithLastEventId_ReceivesMissedEvents()
        {
            using var env = new SseTestEnvironment(maxClients: 2);

            // First connection: receive one live event and record its ID
            var clientA = new HttpClient();
            var responseA = await clientA.GetAsync(env.Url, HttpCompletionOption.ResponseHeadersRead);
            var readerA = new StreamReader(await responseA.Content.ReadAsStreamAsync());

            await DrainUntilBlankLineAsync(readerA);
            await env.WaitForClientAsync();

            env.SseManager.Broadcast("seeded", new { n = 0 });

            var seededMsg = await ReadSseMessageAsync(readerA);
            var idLine = seededMsg.FirstOrDefault(l => l.StartsWith("id:"));
            Assert.NotNull(idLine);
            string lastId = idLine!.Substring("id:".Length).Trim();

            // Tear down first connection, then broadcast two events into the buffer
            readerA.Dispose();
            responseA.Dispose();
            clientA.Dispose();

            env.SseManager.Broadcast("missed_1", new { n = 1 });
            env.SseManager.Broadcast("missed_2", new { n = 2 });

            // Reconnect with Last-Event-ID — should receive both missed events as catch-up
            using var clientB = new HttpClient();
            clientB.DefaultRequestHeaders.Add("Last-Event-ID", lastId);
            var responseB = await clientB.GetAsync(env.Url, HttpCompletionOption.ResponseHeadersRead);
            using var readerB = new StreamReader(await responseB.Content.ReadAsStreamAsync());

            await DrainUntilBlankLineAsync(readerB);

            var msg1 = await ReadSseMessageAsync(readerB);
            var msg2 = await ReadSseMessageAsync(readerB);

            Assert.Contains("event: missed_1", msg1);
            Assert.Contains("event: missed_2", msg2);
        }

        [Fact]
        public async Task EphemeralEvent_IsNotReplayedOnReconnect()
        {
            using var env = new SseTestEnvironment(maxClients: 2);
            env.SseManager.EphemeralEvents.Add("noisy_event");

            // First connection: receive a seeding event and record its ID
            var clientA = new HttpClient();
            var responseA = await clientA.GetAsync(env.Url, HttpCompletionOption.ResponseHeadersRead);
            var readerA = new StreamReader(await responseA.Content.ReadAsStreamAsync());

            await DrainUntilBlankLineAsync(readerA);
            await env.WaitForClientAsync();

            env.SseManager.Broadcast("seeded", new { });
            var seededMsg = await ReadSseMessageAsync(readerA);
            string lastId = seededMsg.First(l => l.StartsWith("id:")).Substring("id:".Length).Trim();

            readerA.Dispose();
            responseA.Dispose();
            clientA.Dispose();

            // Broadcast one ephemeral and one durable event while disconnected
            env.SseManager.Broadcast("noisy_event", new { });
            env.SseManager.Broadcast("durable_event", new { });

            // Reconnect — only the durable event should be replayed
            using var clientB = new HttpClient();
            clientB.DefaultRequestHeaders.Add("Last-Event-ID", lastId);
            var responseB = await clientB.GetAsync(env.Url, HttpCompletionOption.ResponseHeadersRead);
            using var readerB = new StreamReader(await responseB.Content.ReadAsStreamAsync());

            await DrainUntilBlankLineAsync(readerB);

            var replayed = await ReadSseMessageAsync(readerB);
            Assert.Contains("event: durable_event", replayed);
            Assert.DoesNotContain("event: noisy_event", replayed);
        }

        [Fact]
        public async Task ExcludeFilter_BlocksExcludedEvents()
        {
            // Set filter before connecting — it is captured at connection time
            using var env = new SseTestEnvironment();
            env.SetFilter(new SseEventFilter(
                new HashSet<string>(),
                new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "blocked" }));

            using var client = new HttpClient();
            var response = await client.GetAsync(env.Url, HttpCompletionOption.ResponseHeadersRead);
            using var reader = new StreamReader(await response.Content.ReadAsStreamAsync());

            await DrainUntilBlankLineAsync(reader);
            await env.WaitForClientAsync();

            env.SseManager.Broadcast("blocked", new { });
            env.SseManager.Broadcast("allowed", new { value = 99 });

            var message = await ReadSseMessageAsync(reader);
            Assert.Contains("event: allowed", message);
            Assert.DoesNotContain("event: blocked", message);
        }

        // --- Helpers ---

        private static async Task<string> ReadLineAsync(StreamReader reader)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            return await reader.ReadLineAsync(cts.Token) ?? "";
        }

        // Reads lines until a blank line — drains one complete SSE block (comment or message)
        private static async Task DrainUntilBlankLineAsync(StreamReader reader)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                while (true)
                {
                    var line = await reader.ReadLineAsync(cts.Token);
                    if (line == null || line == "") return;
                }
            }
            catch (OperationCanceledException) { }
        }

        // Reads one SSE message block (lines until blank line), skipping comments
        private static async Task<List<string>> ReadSseMessageAsync(StreamReader reader)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var lines = new List<string>();
            try
            {
                while (true)
                {
                    var line = await reader.ReadLineAsync(cts.Token);
                    if (line == null || line == "") break;
                    if (!line.StartsWith(":")) lines.Add(line);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception) { /* stream closed by server (e.g. after server_stopping) */ }
            return lines;
        }

        // Wraps a real HttpListener + SseManager so each test gets its own port and clean state
        private sealed class SseTestEnvironment : IDisposable
        {
            public SseManager SseManager { get; } = new SseManager();
            public string Url { get; }

            private readonly HttpListener listener;
            private readonly SemaphoreSlim clientsConnected;
            private SseEventFilter currentFilter = new SseEventFilter(
                new HashSet<string>(), new HashSet<string>());

            public SseTestEnvironment(int maxClients = 1)
            {
                clientsConnected = new SemaphoreSlim(0);
                int port = FindFreePort();
                Url = $"http://localhost:{port}/";
                listener = new HttpListener();
                listener.Prefixes.Add(Url);
                listener.Start();
                AcceptLoop();
            }

            // Must be called before the client connects — filter is captured at connection time
            public void SetFilter(SseEventFilter filter) => currentFilter = filter;

            public Task WaitForClientAsync(int count = 1, int timeoutMs = 5000)
            {
                return Task.WhenAll(
                    Enumerable.Range(0, count).Select(_ =>
                        Task.Run(() => clientsConnected.Wait(timeoutMs))));
            }

            private void AcceptLoop()
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        while (listener.IsListening)
                        {
                            var ctx = await listener.GetContextAsync();
                            var filter = currentFilter;
                            _ = Task.Run(() =>
                            {
                                clientsConnected.Release();
                                SseManager.HandleClient(ctx, filter);
                            });
                        }
                    }
                    catch { }
                });
            }

            public void Dispose()
            {
                try { listener.Stop(); } catch { }
                SseManager.Dispose();
                clientsConnected.Dispose();
            }

            private static int FindFreePort()
            {
                var l = new TcpListener(IPAddress.Loopback, 0);
                l.Start();
                int port = ((IPEndPoint)l.LocalEndpoint).Port;
                l.Stop();
                return port;
            }
        }
    }
}
