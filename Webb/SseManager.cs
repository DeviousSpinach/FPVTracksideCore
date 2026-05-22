using Newtonsoft.Json;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using Tools;

namespace Webb
{
    internal class SseManager : IDisposable
    {
        private class SseClient
        {
            public StreamWriter Writer { get; }
            public IReadOnlySet<string> EventFilter { get; }

            public SseClient(StreamWriter writer, IReadOnlySet<string> eventFilter)
            {
                Writer = writer;
                EventFilter = eventFilter;
            }

            public bool WantsEvent(string eventType)
            {
                return EventFilter.Count == 0 || EventFilter.Contains(eventType);
            }
        }

        private readonly ConcurrentDictionary<Guid, SseClient> clients = new();
        private readonly CancellationTokenSource cts = new();

        private static readonly JsonSerializerSettings JsonSettings = new JsonSerializerSettings
        {
            DateFormatString = "yyyy-MM-ddTHH:mm:ss.fffZ"
        };

        public void HandleClient(HttpListenerContext context, IReadOnlySet<string> eventFilter)
        {
            var response = context.Response;
            response.ContentType = "text/event-stream";
            response.Headers["Cache-Control"] = "no-cache";

            var clientId = Guid.NewGuid();
            var writer = new StreamWriter(response.OutputStream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), bufferSize: 1024, leaveOpen: false);
            var client = new SseClient(writer, eventFilter);

            clients[clientId] = client;

            string filterDescription = eventFilter.Count == 0 ? "all events" : string.Join(", ", eventFilter);
            Logger.HTTP.Log(this, "SSE client connected: " + clientId + " (" + filterDescription + ")");

            try
            {
                lock (writer)
                {
                    writer.Write(": connected\n\n");
                    writer.Flush();
                }

                while (!cts.IsCancellationRequested)
                {
                    cts.Token.WaitHandle.WaitOne(15000);
                    if (cts.IsCancellationRequested) break;

                    lock (writer)
                    {
                        writer.Write(": keepalive\n\n");
                        writer.Flush();
                    }
                }
            }
            catch (Exception)
            {
                // Client disconnected before server stopped
            }
            finally
            {
                clients.TryRemove(clientId, out _);
                try { writer.Dispose(); } catch { }
                Logger.HTTP.Log(this, "SSE client disconnected: " + clientId);
            }
        }

        public void Broadcast(string eventType, object payload)
        {
            string json = JsonConvert.SerializeObject(payload, JsonSettings);
            string message = "event: " + eventType + "\ndata: " + json + "\n\n";

            foreach (var (clientId, client) in clients)
            {
                if (!client.WantsEvent(eventType))
                    continue;

                try
                {
                    lock (client.Writer)
                    {
                        client.Writer.Write(message);
                        client.Writer.Flush();
                    }
                }
                catch (Exception)
                {
                    clients.TryRemove(clientId, out _);
                }
            }
        }

        public void Dispose()
        {
            cts.Cancel();
        }
    }
}
