using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Web.Script.Serialization;

namespace TaskbarSpeedTest
{
    // Checks GitHub Releases for a newer version and installs it silently in the background.
    class Updater
    {
        public const string Repo = "batuhanbecel/TaskbarSpeedTest";

        // Handled by the installer: run without UI and start TaskbarSpeedTest again when done.
        const string SilentArgs = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /RESTARTAPP=1";

        public static readonly Version Current = Normalize(Assembly.GetExecutingAssembly().GetName().Version);

        public Version Latest { get; private set; }
        public string InstallerUrl { get; private set; }
        public string InstallerSha256 { get; private set; }   // from GitHub's asset digest, when present
        public string PageUrl { get; private set; }

        public bool UpdateAvailable { get { return Latest != null && Latest > Current; } }

        public static string CurrentText { get { return "v" + Current.ToString(3); } }

        public string LatestText { get { return Latest != null ? "v" + Latest.ToString(3) : ""; } }

        // Blocking; call off the UI thread. Returns false when GitHub could not be reached.
        public bool Check()
        {
            try
            {
                string json;
                using (WebClient web = NewClient())
                    json = web.DownloadString("https://api.github.com/repos/" + Repo + "/releases/latest");

                var release = (Dictionary<string, object>)new JavaScriptSerializer().DeserializeObject(json);
                string tag = ((string)release["tag_name"]).TrimStart('v');
                PageUrl = release.ContainsKey("html_url") ? (string)release["html_url"] : "https://github.com/" + Repo + "/releases/latest";
                InstallerUrl = InstallerSha256 = null;

                object[] assets = release.ContainsKey("assets") ? release["assets"] as object[] : null;
                foreach (object o in assets ?? new object[0])
                {
                    var asset = (Dictionary<string, object>)o;
                    string name = (string)asset["name"];
                    if (!name.StartsWith("TaskbarSpeedTest-Setup-", StringComparison.OrdinalIgnoreCase) ||
                        !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
                    InstallerUrl = (string)asset["browser_download_url"];
                    object digest;
                    if (asset.TryGetValue("digest", out digest) && digest is string && ((string)digest).StartsWith("sha256:"))
                        InstallerSha256 = ((string)digest).Substring(7);
                }
                Latest = Normalize(new Version(tag));
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        // Downloads the installer to %TEMP% and verifies its SHA-256. Blocking; returns the file path.
        public string Download()
        {
            if (InstallerUrl == null) throw new InvalidOperationException("No installer attached to the release.");
            string file = Path.Combine(Path.GetTempPath(), "TaskbarSpeedTest-Setup-" + Latest.ToString(3) + ".exe");
            using (WebClient web = NewClient())
                web.DownloadFile(InstallerUrl, file);

            if (InstallerSha256 != null)
            {
                string actual;
                using (var sha = SHA256.Create())
                using (FileStream f = File.OpenRead(file))
                    actual = BitConverter.ToString(sha.ComputeHash(f)).Replace("-", "");
                if (!actual.Equals(InstallerSha256, StringComparison.OrdinalIgnoreCase))
                {
                    File.Delete(file);
                    throw new InvalidDataException("Downloaded installer does not match the release checksum.");
                }
            }
            return file;
        }

        // Runs the installer without any UI; it closes this app, updates it and starts it again.
        public static void InstallSilently(string installer)
        {
            Process.Start(new ProcessStartInfo(installer, SilentArgs) { UseShellExecute = true });
        }

        static WebClient NewClient()
        {
            ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072;   // TLS 1.2
            var web = new WebClient();
            web.Headers[HttpRequestHeader.UserAgent] = "TaskbarSpeedTest/" + Current.ToString(3);
            return web;
        }

        static Version Normalize(Version v)
        {
            return new Version(v.Major, v.Minor, Math.Max(v.Build, 0), Math.Max(v.Revision, 0));
        }
    }
}
