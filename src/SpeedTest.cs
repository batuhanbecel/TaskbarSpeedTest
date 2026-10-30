using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Cache;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using System.Web.Script.Serialization;

namespace TaskbarSpeedTest
{
    enum Phase { Preparing, Ping, Download, Upload, Done }

    // One measurement. Speeds in Mbps, latencies in ms; -1 = not measured.
    class Result
    {
        public DateTime Time;                  // UTC
        public double PingMs = -1, JitterMs = -1, DownMbps = -1, UpMbps = -1;
        public string Server = "";             // Cloudflare data center, "Istanbul (IST)"
        public string Isp = "";
        public string Link = "";               // "Wi-Fi", "Ethernet" or the adapter name
        public string Error;                   // null when the test went through (not kept in the history)

        public bool Complete { get { return Error == null && DownMbps >= 0 && UpMbps >= 0; } }

        public Result Clone() { return (Result)MemberwiseClone(); }

        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        // History entry: "ticks|ping|jitter|down|up|server|isp|link".
        public string Serialize()
        {
            return string.Join("|", Time.Ticks.ToString(Inv), PingMs.ToString("0.##", Inv), JitterMs.ToString("0.##", Inv),
                DownMbps.ToString("0.##", Inv), UpMbps.ToString("0.##", Inv), Clean(Server), Clean(Isp), Clean(Link));
        }

        public static Result Parse(string s)
        {
            string[] p = s.Split('|');
            if (p.Length < 5) return null;
            try
            {
                return new Result
                {
                    Time = new DateTime(long.Parse(p[0], Inv), DateTimeKind.Utc),
                    PingMs = double.Parse(p[1], Inv),
                    JitterMs = double.Parse(p[2], Inv),
                    DownMbps = double.Parse(p[3], Inv),
                    UpMbps = double.Parse(p[4], Inv),
                    Server = p.Length > 5 ? p[5] : "",
                    Isp = p.Length > 6 ? p[6] : "",
                    Link = p.Length > 7 ? p[7] : ""
                };
            }
            catch (FormatException) { return null; }
            catch (OverflowException) { return null; }
            catch (ArgumentOutOfRangeException) { return null; }
        }

        static string Clean(string s)
        {
            return (s ?? "").Replace('|', ' ').Replace(';', ' ');
        }
    }

    // A progress report. Result is a copy holding the values of the phases finished so far.
    class TestStatus
    {
        public Phase Phase;
        public double Live;        // Mbps right now, or the last round trip in ms during the ping phase
        public double Progress;    // 0..1 within the phase
        public bool Running;
        public bool Cancelled;
        public Result Result;
    }

    // Measures latency, download and upload against Cloudflare's speed test (speed.cloudflare.com).
    // Runs on its own thread; Changed reports progress about ten times a second, from that thread.
    class SpeedTest
    {
        const string Host = "speed.cloudflare.com";
        const int PingCount = 12;
        // Requests of 10 MB and more count toward an hourly limit on the server (HTTP 429 with a Retry-After of up to
        // an hour); smaller ones do not. Just under it keeps the per-request overhead negligible even at multi-gigabit
        // speeds. Should the server ever refuse that size, the streams fall back to smaller requests on their own.
        const long Chunk = 9999999, MinChunk = 1000000;
        public int DownloadStreams = 6, UploadStreams = 4;
        public long DownloadChunk = Chunk, UploadChunk = Chunk;

        public int Seconds = 10;               // per direction
        public event Action<TestStatus> Changed;

        readonly List<HttpWebRequest> requests = new List<HttpWebRequest>();
        volatile Thread thread;
        volatile bool cancel, stopStreams;
        volatile string streamError;
        long transferred, requestCount, chunk;
        Result result = new Result();
        static byte[] payload;

        public bool Running { get { return thread != null; } }

        public void Start()
        {
            if (thread != null) return;
            cancel = false;
            result = new Result { Time = DateTime.UtcNow };
            thread = new Thread(Run) { IsBackground = true, Name = "SpeedTest" };
            thread.Start();
        }

        public void Cancel()
        {
            cancel = true;
            AbortRequests();
        }

        void Run()
        {
            Result r = result;
            try
            {
                Report(Phase.Preparing, 0, 0);
                if (!NetworkInterface.GetIsNetworkAvailable())
                    throw new TestException(L.T("No network connection.", "Ağ bağlantısı yok."));
                ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072;   // TLS 1.2
                ServicePointManager.Expect100Continue = false;
                if (ServicePointManager.DefaultConnectionLimit < 16) ServicePointManager.DefaultConnectionLimit = 16;

                r.Link = Network.Describe();
                ReadMeta(r);
                MeasurePing(r);
                r.DownMbps = MeasureThroughput(Phase.Download);
                r.UpMbps = MeasureThroughput(Phase.Upload);
                Log.Write(string.Format(CultureInfo.InvariantCulture, "test done: ping {0:0.0} ms, jitter {1:0.0} ms, down {2:0.0} Mbps, up {3:0.0} Mbps, {4}",
                    r.PingMs, r.JitterMs, r.DownMbps, r.UpMbps, r.Server));
            }
            catch (Exception ex)
            {
                r.Error = cancel ? L.T("Test stopped.", "Test durduruldu.") : ex is TestException ? ex.Message : Describe(ex);
                Log.Write("test failed: " + ex);
            }
            thread = null;
            Report(Phase.Done, 0, 1);
        }

        void Report(Phase phase, double live, double progress)
        {
            var handler = Changed;
            if (handler == null) return;
            handler(new TestStatus
            {
                Phase = phase, Live = live, Progress = progress,
                Running = thread != null, Cancelled = cancel, Result = result.Clone()
            });
        }

        void Check()
        {
            if (cancel) throw new OperationCanceledException();
        }

        // Who and where: the ISP and the Cloudflare data center that answers (also warms up DNS and TLS).
        void ReadMeta(Result r)
        {
            try
            {
                HttpWebRequest req = NewRequest("https://" + Host + "/meta");
                req.Referer = "https://" + Host + "/";   // the endpoint answers {} without it
                Track(req);
                string json;
                try
                {
                    using (WebResponse resp = req.GetResponse())
                    using (var reader = new StreamReader(resp.GetResponseStream())) json = reader.ReadToEnd();
                }
                finally { Untrack(req); }
                var meta = new JavaScriptSerializer().DeserializeObject(json) as Dictionary<string, object>;
                if (meta == null) return;
                r.Isp = Text(meta, "asOrganization");
                object colo;
                if (meta.TryGetValue("colo", out colo))
                {
                    var c = colo as Dictionary<string, object>;
                    string iata = c != null ? Text(c, "iata") : colo as string ?? "";
                    string city = c != null ? Text(c, "city") : "";
                    r.Server = city.Length > 0 ? city + (iata.Length > 0 ? " (" + iata + ")" : "") : iata;
                }
            }
            catch (WebException ex)
            {
                Check();
                // Not being able to reach the server at all ends the test here; anything else only loses the names.
                if (ex.Status == WebExceptionStatus.NameResolutionFailure || ex.Status == WebExceptionStatus.ConnectFailure)
                    throw new TestException(Describe(ex));
                Log.Write("meta: " + ex.Message);
            }
            catch (ArgumentException ex) { Log.Write("meta: " + ex.Message); }
            catch (InvalidOperationException ex) { Log.Write("meta: " + ex.Message); }
        }

        static string Text(Dictionary<string, object> d, string key)
        {
            object v;
            return d.TryGetValue(key, out v) && v != null ? v.ToString() : "";
        }

        // Latency from TCP handshakes with the server: the network round trip, without HTTP or TLS on top.
        // The median is the ping; the average change between consecutive samples is the jitter. A handshake far
        // above the rest had its SYN lost and resent (a second or more): that is a lost packet, not latency.
        void MeasurePing(Result r)
        {
            Report(Phase.Ping, 0, 0);
            IPAddress[] addresses;
            try { addresses = Dns.GetHostAddresses(Host); }
            catch (SocketException) { throw new TestException(L.T("Could not find the speed test server. Are you online?", "Hız testi sunucusu bulunamadı. İnternete bağlı mısın?")); }
            if (addresses.Length == 0) throw new TestException(L.T("Could not find the speed test server.", "Hız testi sunucusu bulunamadı."));
            IPAddress ip = addresses[0];

            var samples = new List<double>();
            for (int i = 0; i < PingCount; i++)
            {
                Check();
                double ms = Handshake(ip);
                if (ms >= 0) samples.Add(ms);
                Report(Phase.Ping, ms >= 0 ? ms : 0, (i + 1) / (double)PingCount);
                Thread.Sleep(40);
            }
            if (samples.Count == 0) throw new TestException(L.T("The speed test server did not answer.", "Hız testi sunucusu yanıt vermedi."));

            double typical = Median(samples);
            List<double> kept = samples.Where(v => v <= typical * 3 + 20).ToList();
            if (kept.Count < samples.Count) Log.Write("ping: left out " + string.Join(", ", samples.Except(kept).Select(v => v.ToString("0"))) + " ms");
            r.PingMs = Median(kept);
            double sum = 0;
            for (int i = 1; i < kept.Count; i++) sum += Math.Abs(kept[i] - kept[i - 1]);
            r.JitterMs = kept.Count > 1 ? sum / (kept.Count - 1) : 0;
        }

        static double Median(List<double> values)
        {
            List<double> sorted = values.OrderBy(v => v).ToList();
            int mid = sorted.Count / 2;
            return sorted.Count % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2;
        }

        static double Handshake(IPAddress ip)
        {
            using (var s = new Socket(ip.AddressFamily, SocketType.Stream, ProtocolType.Tcp))
            {
                s.NoDelay = true;
                try
                {
                    Stopwatch clock = Stopwatch.StartNew();
                    IAsyncResult ar = s.BeginConnect(ip, 443, null, null);
                    if (!ar.AsyncWaitHandle.WaitOne(3000)) return -1;
                    s.EndConnect(ar);
                    return clock.Elapsed.TotalMilliseconds;
                }
                catch (SocketException) { return -1; }
            }
        }

        // Several parallel streams for Seconds; the result is the average over the steady part, leaving out the
        // first quarter (connection setup, TCP slow start). Live is the speed over the last second.
        double MeasureThroughput(Phase phase)
        {
            bool upload = phase == Phase.Upload;
            Report(phase, 0, 0);
            Interlocked.Exchange(ref transferred, 0);
            Interlocked.Exchange(ref requestCount, 0);
            Interlocked.Exchange(ref chunk, upload ? UploadChunk : DownloadChunk);
            stopStreams = false;
            streamError = null;

            var workers = new List<Thread>();
            for (int i = 0; i < (upload ? UploadStreams : DownloadStreams); i++)
            {
                var t = new Thread(() => Pump(upload)) { IsBackground = true, Name = "SpeedTest " + phase };
                workers.Add(t);
                t.Start();
            }

            double warmup = Seconds * 0.25;
            var window = new Queue<KeyValuePair<double, long>>();
            double from = -1, to = 0, live = 0;
            long fromBytes = 0, toBytes = 0;
            Stopwatch clock = Stopwatch.StartNew();
            try
            {
                while (true)
                {
                    Thread.Sleep(100);
                    Check();
                    double t = clock.Elapsed.TotalSeconds;
                    long bytes = Interlocked.Read(ref transferred);
                    window.Enqueue(new KeyValuePair<double, long>(t, bytes));
                    while (t - window.Peek().Key > 1.0) window.Dequeue();
                    KeyValuePair<double, long> first = window.Peek();
                    if (t - first.Key > 0.05) live = Mbps(bytes - first.Value, t - first.Key);
                    if (from < 0 && t >= warmup) { from = t; fromBytes = bytes; }
                    to = t;
                    toBytes = bytes;
                    Report(phase, live, Math.Min(1, t / Seconds));
                    if (t >= Seconds || workers.All(w => !w.IsAlive)) break;
                }
            }
            finally
            {
                stopStreams = true;
                AbortRequests();
                foreach (Thread w in workers) w.Join(2000);
                Log.Write(string.Format(CultureInfo.InvariantCulture, "{0}: {1} requests of up to {2} bytes, {3:0} MB in {4:0.0} s",
                    phase, Interlocked.Read(ref requestCount), Interlocked.Read(ref chunk), toBytes / 1e6, to));
            }
            if (toBytes == 0)
                throw new TestException(streamError ?? L.T("No data could be transferred.", "Veri aktarılamadı."));
            // Every stream gave up early: go with what got through.
            if (from < 0 || to - from < 0.5) return Mbps(toBytes, to);
            return Mbps(toBytes - fromBytes, to - from);
        }

        // One stream: requests back to back on a kept-alive connection until the phase ends.
        void Pump(bool upload)
        {
            var buffer = new byte[64 * 1024];
            int failures = 0;
            while (!stopStreams && !cancel)
            {
                HttpWebRequest req = null;
                long size = Interlocked.Read(ref chunk);
                Interlocked.Increment(ref requestCount);
                try
                {
                    if (upload)
                    {
                        req = NewRequest("https://" + Host + "/__up");
                        req.Method = "POST";
                        req.ContentType = "application/octet-stream";
                        req.ContentLength = size;
                        req.AllowWriteStreamBuffering = false;
                        Track(req);
                        byte[] data = Payload();
                        using (Stream s = req.GetRequestStream())
                        {
                            for (long left = size; left > 0 && !stopStreams; )
                            {
                                int count = (int)Math.Min(left, data.Length);
                                s.Write(data, 0, count);
                                Interlocked.Add(ref transferred, count);
                                left -= count;
                            }
                            if (stopStreams) { req.Abort(); break; }
                        }
                        using (req.GetResponse()) { }
                    }
                    else
                    {
                        req = NewRequest("https://" + Host + "/__down?bytes=" + size);
                        Track(req);
                        using (WebResponse resp = req.GetResponse())
                        using (Stream s = resp.GetResponseStream())
                        {
                            int count;
                            while (!stopStreams && (count = s.Read(buffer, 0, buffer.Length)) > 0)
                                Interlocked.Add(ref transferred, count);
                            // Closing a half-read response would first drain the rest of it.
                            if (stopStreams) req.Abort();
                        }
                    }
                    failures = 0;
                }
                catch (Exception ex)
                {
                    if (stopStreams || cancel) break;
                    // Refused because of the size: all streams go on with smaller requests right away.
                    int code = StatusCode(ex);
                    if ((code == 429 || code == 413) && size > MinChunk)
                    {
                        if (Interlocked.CompareExchange(ref chunk, Math.Max(MinChunk, size / 4), size) == size)
                            Log.Write("stream: HTTP " + code + " for " + size + " bytes, smaller requests from now on");
                        continue;
                    }
                    streamError = Describe(ex);
                    Log.Write("stream: " + ex.Message);
                    if (++failures >= 3) break;
                    Thread.Sleep(200);
                }
                finally { Untrack(req); }
            }
        }

        static byte[] Payload()
        {
            // Random, so nothing on the way can compress it.
            if (payload == null)
            {
                var p = new byte[64 * 1024];
                new Random().NextBytes(p);
                payload = p;
            }
            return payload;
        }

        static double Mbps(long bytes, double seconds)
        {
            return seconds <= 0 ? 0 : bytes * 8 / seconds / 1e6;
        }

        static HttpWebRequest NewRequest(string url)
        {
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.UserAgent = "TaskbarSpeedTest/" + Updater.Current.ToString(3);
            req.KeepAlive = true;
            req.Timeout = 15000;
            req.ReadWriteTimeout = 15000;
            req.AutomaticDecompression = DecompressionMethods.None;
            req.CachePolicy = new RequestCachePolicy(RequestCacheLevel.NoCacheNoStore);
            return req;
        }

        void Track(HttpWebRequest req)
        {
            lock (requests) requests.Add(req);
        }

        void Untrack(HttpWebRequest req)
        {
            if (req == null) return;
            lock (requests) requests.Remove(req);
        }

        void AbortRequests()
        {
            List<HttpWebRequest> list;
            lock (requests) list = requests.ToList();
            foreach (HttpWebRequest req in list)
            {
                try { req.Abort(); }
                catch (Exception) { }
            }
        }

        static int StatusCode(Exception ex)
        {
            var web = ex as WebException;
            var resp = web != null ? web.Response as HttpWebResponse : null;
            return resp != null ? (int)resp.StatusCode : 0;
        }

        static string Describe(Exception ex)
        {
            var web = ex as WebException;
            if (web != null)
            {
                switch (web.Status)
                {
                    case WebExceptionStatus.NameResolutionFailure:
                        return L.T("Could not find the speed test server. Are you online?", "Hız testi sunucusu bulunamadı. İnternete bağlı mısın?");
                    case WebExceptionStatus.ConnectFailure:
                        return L.T("Could not connect to the speed test server.", "Hız testi sunucusuna bağlanılamadı.");
                    case WebExceptionStatus.Timeout:
                        return L.T("The speed test server stopped answering.", "Hız testi sunucusu yanıt vermeyi kesti.");
                    case WebExceptionStatus.ProtocolError:
                        var resp = web.Response as HttpWebResponse;
                        int code = resp != null ? (int)resp.StatusCode : 0;
                        if (code == 429)
                        {
                            int seconds;
                            string wait = resp.Headers["Retry-After"];
                            int minutes = int.TryParse(wait, out seconds) && seconds > 0 ? (seconds + 59) / 60 : 0;
                            return minutes > 0
                                ? L.T("Cloudflare limits how often you can test. Try again in " + minutes + " min.", "Cloudflare test sıklığını sınırlıyor. " + minutes + " dk sonra tekrar dene.")
                                : L.T("Cloudflare limits how often you can test. Try again in a few minutes.", "Cloudflare test sıklığını sınırlıyor. Birkaç dakika sonra tekrar dene.");
                        }
                        return L.T("The speed test server refused the request (HTTP ", "Hız testi sunucusu isteği reddetti (HTTP ") + code + ").";
                }
            }
            return ex.Message;
        }
    }

    // A failure with a message meant for the user.
    class TestException : Exception
    {
        public TestException(string message) : base(message) { }
    }

    static class Network
    {
        // The adapter that carries the internet traffic: "Wi-Fi", "Ethernet", or its name.
        public static string Describe()
        {
            try
            {
                NetworkInterface best = null;
                int bestScore = -1;
                foreach (NetworkInterface n in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (n.OperationalStatus != OperationalStatus.Up) continue;
                    if (n.NetworkInterfaceType == NetworkInterfaceType.Loopback || n.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;
                    if (n.GetIPProperties().GatewayAddresses.Count == 0) continue;
                    string d = n.Description.ToLowerInvariant();
                    bool virtualAdapter = d.Contains("virtual") || d.Contains("hyper-v") || d.Contains("vmware") || d.Contains("virtualbox");
                    int score = virtualAdapter ? 0 : n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 || IsEthernet(n) ? 2 : 1;
                    if (score > bestScore) { best = n; bestScore = score; }
                }
                if (best == null) return "";
                if (best.NetworkInterfaceType == NetworkInterfaceType.Wireless80211) return "Wi-Fi";
                if (IsEthernet(best)) return "Ethernet";
                return best.Name;
            }
            catch (NetworkInformationException) { return ""; }
        }

        static bool IsEthernet(NetworkInterface n)
        {
            var t = n.NetworkInterfaceType;
            return t == NetworkInterfaceType.Ethernet || t == NetworkInterfaceType.GigabitEthernet || t == NetworkInterfaceType.FastEthernetT ||
                   t == NetworkInterfaceType.FastEthernetFx || t == NetworkInterfaceType.Ethernet3Megabit;
        }
    }
}
