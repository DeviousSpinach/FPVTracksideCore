using DB;
using DB.JSON;
using Newtonsoft.Json;
using RaceLib;
using Sound;
using Channel = RaceLib.Channel;
using Detection = RaceLib.Detection;
using Lap = RaceLib.Lap;
using Pilot = RaceLib.Pilot;
using PilotChannel = RaceLib.PilotChannel;
using Race = RaceLib.Race;
using Round = RaceLib.Round;
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Reflection.Metadata;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using Tools;

namespace Webb
{
    public class EventWebServer : IDisposable
    {
        private EventManager eventManager;
        private SoundManager soundManager;
        private IRaceControl raceControl;

        private Thread thread;
        private volatile bool running;
        public bool Running => running;

        private HttpListener listener;
        private SseManager sseManager;

        public string Url { get; private set; }

        public FileInfo CSSStyleSheet { get; private set; }

        private bool localOnly;

        private string eventStorageLocation;

        public ToolColor[] ChannelColors { get; private set; }

        public EventWebServer(EventManager eventManager, SoundManager soundManager, IRaceControl raceControl, IEnumerable<Tools.ToolColor> channelColors, string eventStorageLocation = null)
        {
            CSSStyleSheet = new FileInfo(Path.Combine("httpfiles", "style.css"));
            this.eventManager = eventManager;
            this.soundManager = soundManager;
            this.raceControl = raceControl;
            this.eventStorageLocation = eventStorageLocation;
            Url = "http://localhost:8080/";

            if (!CSSStyleSheet.Exists)
            {
                FileStream fileStream = CSSStyleSheet.Create();
                fileStream.Dispose();
            }

            ChannelColors = channelColors.ToArray();

            SseSettings sseSettings = SseSettings.Load();
            sseManager = new SseManager(sseSettings.ReplayBufferSize);
            foreach (string eventType in sseSettings.EphemeralEvents)
                sseManager.EphemeralEvents.Add(eventType);
            SubscribeToRaceEvents();
        }

        private void SubscribeToRaceEvents()
        {
            eventManager.RaceManager.OnLapDetected += OnLapDetected;
            eventManager.RaceManager.OnLapDisqualified += OnLapDisqualified;
            eventManager.RaceManager.OnLapsRecalculated += OnLapsRecalculated;
            eventManager.RaceManager.OnRacePreStart += OnRacePreStart;
            eventManager.RaceManager.OnRaceStart += OnRaceStart;
            eventManager.RaceManager.OnRaceStartScheduled += OnRaceStartScheduled;
            eventManager.RaceManager.OnRacePilotsSet += OnRacePilotsSet;
            eventManager.RaceManager.OnRaceEnd += OnRaceEnd;
            eventManager.RaceManager.OnRaceChanged += OnRaceChanged;
            eventManager.RaceManager.OnRaceTimeRemaining += OnRaceTimeRemaining;
            eventManager.RaceManager.OnRaceTimesUp += OnRaceTimesUp;
            eventManager.RaceManager.OnRaceReset += OnRaceReset;
            eventManager.RaceManager.OnRaceCancelled += OnRaceCancelled;
            eventManager.RaceManager.OnSplitDetection += OnSplitDetection;
            eventManager.RaceManager.OnChannelCrashedOut += OnChannelCrashedOut;
            eventManager.RaceManager.OnChannelRecovered += OnChannelRecovered;
            eventManager.RaceManager.OnPilotAdded += OnPilotAdded;
            eventManager.RaceManager.OnPilotRemoved += OnPilotRemoved;
            eventManager.ResultManager.RaceResultsChanged += OnRaceResultsChanged;
            eventManager.OnEventChange += OnEventChanged;
            eventManager.OnPilotRefresh += OnPilotsUpdated;
            eventManager.RoundManager.OnRoundAdded += OnRoundAdded;
            eventManager.RoundManager.OnRoundRemoved += OnRoundRemoved;
        }

        private void UnsubscribeFromRaceEvents()
        {
            eventManager.RaceManager.OnLapDetected -= OnLapDetected;
            eventManager.RaceManager.OnLapDisqualified -= OnLapDisqualified;
            eventManager.RaceManager.OnLapsRecalculated -= OnLapsRecalculated;
            eventManager.RaceManager.OnRacePreStart -= OnRacePreStart;
            eventManager.RaceManager.OnRaceStart -= OnRaceStart;
            eventManager.RaceManager.OnRaceStartScheduled -= OnRaceStartScheduled;
            eventManager.RaceManager.OnRacePilotsSet -= OnRacePilotsSet;
            eventManager.RaceManager.OnRaceEnd -= OnRaceEnd;
            eventManager.RaceManager.OnRaceChanged -= OnRaceChanged;
            eventManager.RaceManager.OnRaceTimeRemaining -= OnRaceTimeRemaining;
            eventManager.RaceManager.OnRaceTimesUp -= OnRaceTimesUp;
            eventManager.RaceManager.OnRaceReset -= OnRaceReset;
            eventManager.RaceManager.OnRaceCancelled -= OnRaceCancelled;
            eventManager.RaceManager.OnSplitDetection -= OnSplitDetection;
            eventManager.RaceManager.OnChannelCrashedOut -= OnChannelCrashedOut;
            eventManager.RaceManager.OnChannelRecovered -= OnChannelRecovered;
            eventManager.RaceManager.OnPilotAdded -= OnPilotAdded;
            eventManager.RaceManager.OnPilotRemoved -= OnPilotRemoved;
            eventManager.ResultManager.RaceResultsChanged -= OnRaceResultsChanged;
            eventManager.OnEventChange -= OnEventChanged;
            eventManager.OnPilotRefresh -= OnPilotsUpdated;
            eventManager.RoundManager.OnRoundAdded -= OnRoundAdded;
            eventManager.RoundManager.OnRoundRemoved -= OnRoundRemoved;
        }

        private void OnLapDetected(Lap lap)
        {
            sseManager.Broadcast("lap_detected", new
            {
                raceId = lap.Race?.ID,
                raceNumber = lap.Race?.RaceNumber,
                pilotId = lap.Pilot?.ID,
                pilotName = lap.Pilot?.Name,
                lapNumber = lap.Number,
                lapLengthMs = (long)lap.Length.TotalMilliseconds,
                valid = lap.Detection?.Valid ?? false,
                endTime = lap.End
            });
        }

        private void OnLapDisqualified(Lap lap)
        {
            sseManager.Broadcast("lap_disqualified", new
            {
                raceId = lap.Race?.ID,
                raceNumber = lap.Race?.RaceNumber,
                pilotId = lap.Pilot?.ID,
                pilotName = lap.Pilot?.Name,
                lapNumber = lap.Number,
                lapLengthMs = (long)lap.Length.TotalMilliseconds,
                endTime = lap.End
            });
        }

        private void OnLapsRecalculated(Race race)
        {
            sseManager.Broadcast("laps_recalculated", new
            {
                raceId = race.ID,
                raceNumber = race.RaceNumber,
                roundNumber = race.RoundNumber,
                laps = race.Laps.ToArray().Select(lap => new
                {
                    pilotId = lap.Pilot?.ID,
                    pilotName = lap.Pilot?.Name,
                    lapNumber = lap.Number,
                    lapLengthMs = (long)lap.Length.TotalMilliseconds,
                    valid = lap.Detection?.Valid ?? false,
                    endTime = lap.End
                }).ToArray()
            });
        }

        private void OnRacePreStart(Race race)
        {
            sseManager.Broadcast("race_pre_start", new
            {
                raceId = race.ID,
                raceNumber = race.RaceNumber,
                roundNumber = race.RoundNumber
            });
        }

        private void OnRaceStart(Race race)
        {
            sseManager.Broadcast("race_start", new
            {
                raceId = race.ID,
                raceNumber = race.RaceNumber,
                roundNumber = race.RoundNumber,
                startTime = race.Start
            });
        }

        private void OnRaceStartScheduled(Race race, DateTime scheduledTime)
        {
            sseManager.Broadcast("race_start_scheduled", new
            {
                raceId = race.ID,
                raceNumber = race.RaceNumber,
                roundNumber = race.RoundNumber,
                scheduledTime
            });
        }

        private void OnRacePilotsSet(Race race)
        {
            sseManager.Broadcast("race_pilots_set", new
            {
                raceId = race.ID,
                raceNumber = race.RaceNumber,
                roundNumber = race.RoundNumber,
                pilots = race.PilotChannelsSafe.Select(pc => new
                {
                    pilotId = pc.Pilot?.ID,
                    pilotName = pc.Pilot?.Name,
                    channelId = pc.Channel?.ID,
                    channelNumber = pc.Channel?.Number,
                    band = pc.Channel?.Band.ToString(),
                    frequency = pc.Channel?.Frequency
                }).ToArray()
            });
        }

        private void OnRaceEnd(Race race)
        {
            sseManager.Broadcast("race_end", new
            {
                raceId = race.ID,
                raceNumber = race.RaceNumber,
                roundNumber = race.RoundNumber,
                endTime = race.End
            });
        }

        private void OnRaceChanged(Race race)
        {
            sseManager.Broadcast("race_changed", new
            {
                raceId = race.ID,
                raceNumber = race.RaceNumber,
                roundNumber = race.RoundNumber
            });
        }

        private void OnRaceTimeRemaining(Race race, TimeSpan remaining)
        {
            sseManager.Broadcast("race_time_remaining", new
            {
                raceId = race.ID,
                raceNumber = race.RaceNumber,
                remainingMs = (long)remaining.TotalMilliseconds
            });
        }

        private void OnRaceTimesUp(Race race)
        {
            sseManager.Broadcast("race_times_up", new
            {
                raceId = race.ID,
                raceNumber = race.RaceNumber,
                roundNumber = race.RoundNumber
            });
        }

        private void OnRaceReset(Race race)
        {
            sseManager.Broadcast("race_reset", new
            {
                raceId = race.ID,
                raceNumber = race.RaceNumber,
                roundNumber = race.RoundNumber
            });
        }

        private void OnRaceCancelled(Race race, bool wasFullCancel)
        {
            sseManager.Broadcast("race_cancelled", new
            {
                raceId = race.ID,
                raceNumber = race.RaceNumber,
                roundNumber = race.RoundNumber,
                wasFullCancel
            });
        }

        private void OnSplitDetection(Detection detection)
        {
            sseManager.Broadcast("split_detection", new
            {
                pilotId = detection.Pilot?.ID,
                pilotName = detection.Pilot?.Name,
                channelId = detection.Channel?.ID,
                channelNumber = detection.Channel?.Number,
                sectorNumber = detection.SectorNumber,
                time = detection.Time,
                valid = detection.Valid
            });
        }

        private void OnChannelCrashedOut(Channel channel, Pilot pilot, bool isOut)
        {
            sseManager.Broadcast("channel_crashed_out", new
            {
                pilotId = pilot?.ID,
                pilotName = pilot?.Name,
                channelId = channel?.ID,
                channelNumber = channel?.Number,
                isOut
            });
        }

        private void OnChannelRecovered(Channel channel, Pilot pilot)
        {
            sseManager.Broadcast("channel_recovered", new
            {
                pilotId = pilot?.ID,
                pilotName = pilot?.Name,
                channelId = channel?.ID,
                channelNumber = channel?.Number
            });
        }

        private void OnPilotAdded(PilotChannel pilotChannel)
        {
            sseManager.Broadcast("pilot_added", new
            {
                pilotId = pilotChannel.Pilot?.ID,
                pilotName = pilotChannel.Pilot?.Name,
                channelId = pilotChannel.Channel?.ID,
                channelNumber = pilotChannel.Channel?.Number,
                band = pilotChannel.Channel?.Band.ToString(),
                frequency = pilotChannel.Channel?.Frequency
            });
        }

        private void OnPilotRemoved(PilotChannel pilotChannel)
        {
            sseManager.Broadcast("pilot_removed", new
            {
                pilotId = pilotChannel.Pilot?.ID,
                pilotName = pilotChannel.Pilot?.Name,
                channelId = pilotChannel.Channel?.ID,
                channelNumber = pilotChannel.Channel?.Number,
                band = pilotChannel.Channel?.Band.ToString(),
                frequency = pilotChannel.Channel?.Frequency
            });
        }

        private void OnRaceResultsChanged(Race race)
        {
            sseManager.Broadcast("race_results", new
            {
                raceId = race.ID,
                raceNumber = race.RaceNumber,
                roundNumber = race.RoundNumber,
                results = eventManager.ResultManager.GetOrderedResults(race).Select(r => new
                {
                    pilotId = r.Pilot?.ID,
                    pilotName = r.Pilot?.Name,
                    position = r.Position,
                    points = r.Points,
                    dnf = r.DNF,
                    lapsFinished = r.LapsFinished,
                    timeMs = (long)r.Time.TotalMilliseconds
                }).ToArray()
            });
        }

        private void OnEventChanged()
        {
            var ev = eventManager?.Event;
            sseManager.Broadcast("event_changed", new
            {
                id = ev?.ID,
                name = ev?.Name,
                eventType = ev?.EventType.ToString(),
                laps = ev?.Laps,
                raceLengthMs = (long)(ev?.RaceLength.TotalMilliseconds ?? 0),
                minLapTimeMs = (long)(ev?.MinLapTime.TotalMilliseconds ?? 0)
            });
        }

        private void OnRoundAdded(Round round)
        {
            sseManager.Broadcast("round_created", new
            {
                id = round.ID,
                roundNumber = round.RoundNumber,
                eventType = round.EventType.ToString(),
                name = round.Name
            });
        }

        private void OnRoundRemoved(Round round)
        {
            sseManager.Broadcast("round_removed", new
            {
                id = round.ID,
                roundNumber = round.RoundNumber
            });
        }

        private void OnPilotsUpdated()
        {
            var ev = eventManager?.Event;
            sseManager.Broadcast("pilots_updated", new
            {
                pilots = ev?.PilotChannels.Select(pc => new
                {
                    pilotId = pc.Pilot?.ID,
                    pilotName = pc.Pilot?.Name,
                    firstName = pc.Pilot?.FirstName,
                    lastName = pc.Pilot?.LastName,
                    discordId = pc.Pilot?.DiscordID,
                    photoPath = pc.Pilot?.PhotoPath,
                    channelId = pc.Channel?.ID,
                    channelNumber = pc.Channel?.Number,
                    band = pc.Channel?.Band.ToString(),
                    frequency = pc.Channel?.Frequency
                }).ToArray() ?? Array.Empty<object>()
            });
        }

        public void Dispose()
        {
            Stop();
        }

        public bool Start()
        {
            try
            {
                running = true;

                if (thread != null)
                {
                    Stop();
                }
                
                thread = new Thread(Run);

                thread.Name = "Webb Thread";
                thread.Start();

                return true;
            }
            catch
            {
                return false;
            }


        }

        private void Run()
        {
            while (Running)
            {
                if (listener == null)
                {
                    CreateListener();
                }

                try
                {
                    HttpListenerContext context = listener.GetContext();
                    HandleRequest(context);
                }
                catch (Exception ex)
                {
                    Logger.HTTP.LogException(this, ex);
                    listener.Abort();
                    listener = null;
                }
            }
        }

        private void CreateListener()
        {
            try
            {
                // Try open all interfaces first
                listener = new HttpListener();
                listener.Prefixes.Add(Url.Replace("localhost", "+"));
                listener.Start();
                Logger.HTTP.Log(this, "Listening on " + listener.Prefixes.FirstOrDefault());
            }
            catch (Exception ex)
            {
                localOnly = true;
                Logger.HTTP.LogException(this, ex);

                // just open localhost
                listener = new HttpListener();
                listener.Prefixes.Add(Url);
                listener.Start();
                Logger.HTTP.Log(this, "Listening on " + listener.Prefixes.FirstOrDefault());
            }
        }

        private void HandleRequest(HttpListenerContext context)
        {
            string path = Uri.UnescapeDataString(context.Request.Url.AbsolutePath);

            if (path == "/sse")
            {
                context.Response.AppendHeader("Access-Control-Allow-Origin", "*");
                SseEventFilter eventFilter = SseQueryParser.Parse(context.Request.Url.Query);
                Task.Run(() => sseManager.HandleClient(context, eventFilter));
                return;
            }

            if (path == "/api/state")
            {
                context.Response.AppendHeader("Access-Control-Allow-Origin", "*");
                context.Response.ContentType = "application/json";
                bool full = HttpUtility.ParseQueryString(context.Request.Url.Query)["full"] == "true";
                byte[] snapshot = SerializeState(full);
                context.Response.ContentLength64 = snapshot.Length;
                context.Response.OutputStream.Write(snapshot, 0, snapshot.Length);
                context.Response.OutputStream.Close();
                return;
            }

            if (path.StartsWith("/api/pilot/") && path.EndsWith("/photo"))
            {
                string idSegment = path.Substring("/api/pilot/".Length, path.Length - "/api/pilot/".Length - "/photo".Length);
                if (Guid.TryParse(idSegment, out Guid pilotId))
                {
                    var pilot = eventManager?.Event?.PilotChannels
                        .Select(pc => pc.Pilot)
                        .FirstOrDefault(p => p?.ID == pilotId);

                    if (pilot != null && !string.IsNullOrEmpty(pilot.PhotoPath))
                    {
                        string absolutePath = Path.IsPathRooted(pilot.PhotoPath)
                            ? pilot.PhotoPath
                            : Path.Combine(IOTools.WorkingDirectory?.FullName ?? "", pilot.PhotoPath);

                        if (File.Exists(absolutePath))
                        {
                            context.Response.AppendHeader("Access-Control-Allow-Origin", "*");
                            context.Response.ContentType = Path.GetExtension(absolutePath).ToLowerInvariant() switch
                            {
                                ".png" => "image/png",
                                ".jpg" or ".jpeg" => "image/jpeg",
                                ".gif" => "image/gif",
                                ".webp" => "image/webp",
                                ".mp4" => "video/mp4",
                                ".wmv" => "video/x-ms-wmv",
                                ".mkv" => "video/x-matroska",
                                _ => "application/octet-stream"
                            };
                            byte[] photoBytes = File.ReadAllBytes(absolutePath);
                            context.Response.ContentLength64 = photoBytes.Length;
                            context.Response.OutputStream.Write(photoBytes, 0, photoBytes.Length);
                            context.Response.OutputStream.Close();
                            return;
                        }
                    }
                }

                context.Response.StatusCode = 404;
                context.Response.OutputStream.Close();
                return;
            }

            HttpListenerResponse response = context.Response;

            if (context.Request.HttpMethod == "OPTIONS")
            {
                response.AddHeader("Access-Control-Allow-Headers", "Content-Type, Accept, X-Requested-With");
                response.AddHeader("Access-Control-Allow-Methods", "GET, POST");
                response.AddHeader("Access-Control-Max-Age", "1728000");
            }
            response.AppendHeader("Access-Control-Allow-Origin", "*");

            byte[] buffer = Response(context);
            // Get a response stream and write the response to it.
            response.ContentLength64 = buffer.Length;
            System.IO.Stream output = response.OutputStream;
            output.Write(buffer, 0, buffer.Length);
            // You must close the output stream.
            output.Close();
        }

        private byte[] Response(HttpListenerContext context)
        {
            string path = Uri.UnescapeDataString(context.Request.Url.AbsolutePath);
            string[] requestPath = path.Split('/').Where(s => !string.IsNullOrEmpty(s)).ToArray();

            NameValueCollection nameValueCollection;
            using (Stream receiveStream = context.Request.InputStream)
            {
                using (StreamReader readStream = new StreamReader(receiveStream, System.Text.Encoding.UTF8))
                {
                    string documentContents = readStream.ReadToEnd();
                    nameValueCollection = HttpUtility.ParseQueryString(documentContents);
                }
            }

            if (requestPath.Length > 0)
            {
                string action = requestPath[0];
                string[] parameters = requestPath.Skip(1).ToArray();

                string content = "";
                // Get the event storage location (passed in constructor or fall back to working directory)
                string eventsPath = eventStorageLocation;
                if (string.IsNullOrEmpty(eventsPath))
                {
                    // Fallback: use working directory + events
                    eventsPath = Path.Combine(IOTools.WorkingDirectory?.FullName ?? "", "events");
                }
                else if (!Path.IsPathRooted(eventsPath))
                {
                    // Make relative paths absolute
                    eventsPath = Path.Combine(IOTools.WorkingDirectory?.FullName ?? "", eventsPath);
                }
                DirectoryInfo eventRoot = new DirectoryInfo(Path.Combine(eventsPath, eventManager.Event.ID.ToString()));
                switch (action)
                {
                    case "events":
                        // Build the full path: replace "events" prefix with the actual event storage location
                        string[] pathWithoutEvents = requestPath.Skip(1).ToArray(); // Remove "events" from the path
                        string target = Path.Combine(eventsPath, Path.Combine(pathWithoutEvents));

                        Logger.HTTP.Log(this, "Request: " + path + " -> Target: " + target + " (EventsPath: " + eventsPath + ")");

                        if (target == eventsPath || string.IsNullOrEmpty(target))
                            target = eventRoot.FullName;

                        if (target.Contains("."))
                        {
                            if (File.Exists(target))
                            {
                                // Set content type for JSON files
                                if (target.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                                {
                                    context.Response.ContentType = "application/json";
                                }
                                Logger.HTTP.Log(this, "Serving file: " + target);
                                return File.ReadAllBytes(target);
                            }
                            Logger.HTTP.Log(this, "File not found: " + target);
                            return new byte[0];
                        }
                        else
                        {
                            DirectoryInfo di = new DirectoryInfo(target);
                            if (eventRoot.Exists && di.Exists)
                            {
                                content += ListDirectory(eventRoot, di);
                            }
                        }
                        break;
                }
            }
            else
            {
                requestPath = new string[] { "httpfiles", "index.html" };
            }


            FileInfo file = new FileInfo(Path.Combine(requestPath));

#if DEBUG
            string[] basehttpFilesDir = new string[] { "..", "..", "..", "..", "..", "..", "..","FPVTracksideCore","Webb" };

            string combined = Path.Combine(Path.Combine(basehttpFilesDir), Path.Combine(requestPath));

            FileInfo debugAdjustedFile = new FileInfo(combined);
            if (debugAdjustedFile.Exists)
            {
                file = debugAdjustedFile;
            }
#endif

            if (!file.Exists)
            {
                requestPath = new string[] { "httpfiles", "index.html" };
                file = new FileInfo(Path.Combine(requestPath));
                if (!file.Exists)
                {
                    return new byte[0];
                }
            }

            HttpListenerResponse response = context.Response;
            switch (file.Extension)
            {
                case ".html":
                case ".htm":
                    response.ContentType = "text/html";
                    break;
                case ".json":
                    response.ContentType = "text/json";
                    break;
            }

            if (file.Name == "index.html")
            {
                string text = File.ReadAllText(file.FullName);

                string replaced = text.Replace("%eventDirectory%", "events/" + eventManager.EventId.ToString());

                return Encoding.ASCII.GetBytes(replaced);
            }

            return File.ReadAllBytes(file.FullName);
        }

        private byte[] SerializeASCII<T>(IEnumerable<T> ts)
        {
            JsonSerializerSettings settings = new JsonSerializerSettings()
            {
                Formatting = Formatting.Indented,
                DateFormatString = "yyyy/MM/dd H:mm:ss.FFF"
            };

            string json = JsonConvert.SerializeObject(ts, settings);
            return Encoding.ASCII.GetBytes(json);
        }
              
        private byte[] GetHTML(HttpListenerContext context, string content)
        {
            string refreshText = "";

            int decimalPlaces = 2;
            int refresh = 60;
            bool autoScroll = false;

            string query = context.Request.Url.Query;
            if (query.StartsWith("?"))
            {
                string[] queries = query.Split('?', '&');

                foreach (string q in queries)
                {
                    string[] split = q.Split('=');
                    if (split.Length == 2)
                    {
                        string key = split[0].ToLower();
                        string value = split[1];

                        switch (key)
                        {
                            case "refresh":
                                int.TryParse(value, out refresh);
                                break;
                            case "decimalplaces":
                                int.TryParse(value, out decimalPlaces);
                                break;
                            case "autoscroll":
                                bool.TryParse(value, out autoScroll);
                                break;
                        }
                    }
                }
            }
            

            if (refresh == 0)
            {
                refreshText = "";
            }
            else
            {
                refreshText = "<meta http-equiv=\"refresh\" content=\"" + refresh + "\" >";
            }

            string output = "<html><head>" + refreshText + "</head><link rel=\"stylesheet\" href=\"/httpfiles/style.css\">";

            if (autoScroll)
                output += "<script src=\"/httpfiles/scroll.js\"></script>";
            output += "<script src=\"/httpfiles/EventManager.js\"></script>";
            output += "<script src=\"/httpfiles/Formatter.js\"></script>";

            output += "<body id=\"body\">";

            output += content;

            output += "</body></html>";
            return Encoding.ASCII.GetBytes(output);
        }

        private byte[] GetFormattedHTML(HttpListenerContext context, string content)
        {
            string output = "<div class=\"top\">";
            output += "<img src=\"/img/logo.png\">";
            output += "<div class=\"time\">" + DateTime.Now.ToString("h:mm tt").ToLower() + "</div>";
            output += "</div>";


            output += "<div class=\"content\">";
            output += "<div id=\"content\"></div>";

            output += content;


            output += "</div>";

            return GetHTML(context, output);
        }

        private string ListDirectory(DirectoryInfo docRoot, DirectoryInfo target)
        {
            string content = "";

            string name = Path.GetRelativePath(docRoot.FullName, target.FullName);
            if (name == ".")
                name = "event";

            content += "<h1>" + name + "</h2>";
            content += "<ul>";

            foreach (DirectoryInfo subDir in target.GetDirectories())
            {
                content += "<li><a href=\"" + Path.GetRelativePath(docRoot.FullName, subDir.FullName) + " \">" + subDir.Name + "</a></li>";
            }

            foreach (FileInfo filename in target.GetFiles())
            {
                content += "<li><a href=\"" + Path.GetRelativePath(docRoot.FullName, filename.FullName) + " \">" + filename.Name + "</a></li>";
            }

            content += "</ul>";
            return content;
        }

        private static readonly JsonSerializerSettings StateJsonSettings = new JsonSerializerSettings
        {
            DateFormatString = "yyyy-MM-ddTHH:mm:ss.fffZ",
            Formatting = Formatting.None
        };

        private static object SerializeRaceForState(Race race) => new
        {
            raceId = race.ID,
            raceNumber = race.RaceNumber,
            roundNumber = race.RoundNumber,
            startTime = race.Start,
            endTime = race.End,
            running = race.Running,
            pilots = race.PilotChannelsSafe.Select(pc => new
            {
                pilotId = pc.Pilot?.ID,
                pilotName = pc.Pilot?.Name,
                channelId = pc.Channel?.ID,
                channelNumber = pc.Channel?.Number,
                band = pc.Channel?.Band.ToString(),
                frequency = pc.Channel?.Frequency
            }).ToArray(),
            laps = race.Laps.ToArray().Select(lap => new
            {
                pilotId = lap.Pilot?.ID,
                pilotName = lap.Pilot?.Name,
                lapNumber = lap.Number,
                lapLengthMs = (long)lap.Length.TotalMilliseconds,
                valid = lap.Detection?.Valid ?? false,
                endTime = lap.End
            }).ToArray()
        };

        private byte[] SerializeState(bool full = false)
        {
            var ev = eventManager?.Event;
            var raceManager = eventManager?.RaceManager;
            var currentRace = raceManager?.CurrentRace;

            var state = new
            {
                instanceId = sseManager.InstanceId,
                @event = ev == null ? null : (object)new
                {
                    id = ev.ID,
                    name = ev.Name,
                    eventType = ev.EventType.ToString(),
                    laps = ev.Laps,
                    raceLengthMs = (long)ev.RaceLength.TotalMilliseconds,
                    minLapTimeMs = (long)ev.MinLapTime.TotalMilliseconds
                },
                pilots = ev?.PilotChannels.Select(pc => new
                {
                    pilotId = pc.Pilot?.ID,
                    pilotName = pc.Pilot?.Name,
                    firstName = pc.Pilot?.FirstName,
                    lastName = pc.Pilot?.LastName,
                    discordId = pc.Pilot?.DiscordID,
                    photoPath = pc.Pilot?.PhotoPath,
                    channelId = pc.Channel?.ID,
                    channelNumber = pc.Channel?.Number,
                    band = pc.Channel?.Band.ToString(),
                    frequency = pc.Channel?.Frequency
                }).ToArray() ?? Array.Empty<object>(),
                rounds = eventManager?.RoundManager.Rounds?.Select(r => new
                {
                    id = r.ID,
                    roundNumber = r.RoundNumber,
                    eventType = r.EventType.ToString(),
                    name = r.Name
                }).ToArray() ?? Array.Empty<object>(),
                currentRace = currentRace == null ? null : SerializeRaceForState(currentRace),
                allRaces = full
                    ? (raceManager?.Races.Select(SerializeRaceForState).ToArray() ?? Array.Empty<object>())
                    : null
            };

            string json = JsonConvert.SerializeObject(state, StateJsonSettings);
            return Encoding.UTF8.GetBytes(json);
        }

        public bool Stop()
        {
            UnsubscribeFromRaceEvents();
            sseManager?.Dispose();

            running = false;
            listener?.Abort();
            thread?.Join();

            return true;
        }
    }
}
