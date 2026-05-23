using Newtonsoft.Json;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using Tools;

namespace Webb
{
    internal readonly struct SseEventFilter
    {
        public IReadOnlySet<string> Include { get; }
        public IReadOnlySet<string> Exclude { get; }

        public SseEventFilter(IReadOnlySet<string> include, IReadOnlySet<string> exclude)
        {
            Include = include;
            Exclude = exclude;
        }

        public bool Wants(string eventType)
        {
            if (Exclude.Contains(eventType)) return false;
            if (Include.Count == 0) return true;
            return Include.Contains(eventType);
        }

        public string Description
        {
            get
            {
                if (Include.Count == 0 && Exclude.Count == 0)
                    return "all events";

                if (Include.Count > 0 && Exclude.Count == 0)
                    return string.Join(", ", Include);

                if (Include.Count == 0 && Exclude.Count > 0)
                    return "all except " + string.Join(", ", Exclude);

                return string.Join(", ", Include) + " except " + string.Join(", ", Exclude);
            }
        }
    }

    internal class SseManager : IDisposable
    {
        private class SseClient
        {
            public StreamWriter Writer { get; }
            public SseEventFilter EventFilter { get; }

            public SseClient(StreamWriter writer, SseEventFilter eventFilter)
            {
                Writer = writer;
                EventFilter = eventFilter;
            }
        }

        private record BufferedEvent(long Id, string EventType, string DataJson);

        private readonly ConcurrentDictionary<Guid, SseClient> clients = new();
        private readonly CancellationTokenSource cts = new();
        private readonly LinkedList<BufferedEvent> eventBuffer = new();
        private readonly object eventBufferLock = new();
        private readonly int maxBufferedEvents;
        private long nextEventId = 0;

        // Unique per server process. Sent in the ": connected" comment so clients can detect restarts
        // and request a full state snapshot rather than relying on the replay buffer from a prior session.
        public string InstanceId { get; } = Guid.NewGuid().ToString();

        public SseManager(int replayBufferSize = 100)
        {
            maxBufferedEvents = replayBufferSize > 0 ? replayBufferSize : 100;
            EphemeralEvents.Add("server_stopping"); // never meaningful to replay
        }

        // Events added here are delivered to live clients but never stored in the replay buffer.
        // Use for high-frequency events (e.g. race_time_remaining) whose past values are meaningless on reconnect.
        public readonly HashSet<string> EphemeralEvents = new(StringComparer.OrdinalIgnoreCase);

        private static readonly JsonSerializerSettings JsonSettings = new JsonSerializerSettings
        {
            DateFormatString = "yyyy-MM-ddTHH:mm:ss.fffZ"
        };

        private static string BuildMessage(long id, string eventType, string dataJson)
            => "id: " + id + "\nevent: " + eventType + "\ndata: " + dataJson + "\n\n";

        public void HandleClient(HttpListenerContext context, SseEventFilter eventFilter)
        {
            var response = context.Response;
            response.ContentType = "text/event-stream";
            response.Headers["Cache-Control"] = "no-cache";

            var clientId = Guid.NewGuid();
            var writer = new StreamWriter(response.OutputStream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), bufferSize: 1024, leaveOpen: false);
            var client = new SseClient(writer, eventFilter);

            clients[clientId] = client;
            Logger.HTTP.Log(this, "SSE client connected: " + clientId + " (" + eventFilter.Description + ")");

            try
            {
                lock (writer)
                {
                    writer.Write(": connected instance=" + InstanceId + "\n\n");
                    writer.Flush();
                }

                // Replay any events the client missed during a reconnect
                string lastIdHeader = context.Request.Headers["Last-Event-ID"];
                if (long.TryParse(lastIdHeader, out long lastId))
                {
                    List<BufferedEvent> missed;
                    lock (eventBufferLock)
                    {
                        missed = eventBuffer
                            .Where(e => e.Id > lastId && eventFilter.Wants(e.EventType))
                            .ToList();
                    }
                    if (missed.Count > 0)
                    {
                        Logger.HTTP.Log(this, "SSE replaying " + missed.Count + " missed event(s) for client: " + clientId);
                        lock (writer)
                        {
                            foreach (var evt in missed)
                                writer.Write(BuildMessage(evt.Id, evt.EventType, evt.DataJson));
                            writer.Flush();
                        }
                    }
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
            long id = Interlocked.Increment(ref nextEventId);

            if (!EphemeralEvents.Contains(eventType))
            {
                lock (eventBufferLock)
                {
                    eventBuffer.AddLast(new BufferedEvent(id, eventType, json));
                    if (eventBuffer.Count > maxBufferedEvents)
                        eventBuffer.RemoveFirst();
                }
            }

            string message = BuildMessage(id, eventType, json);

            foreach (var (clientId, client) in clients)
            {
                if (!client.EventFilter.Wants(eventType))
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
            Broadcast("server_stopping", new { reason = "shutdown" });
            cts.Cancel();
        }
    }
}
