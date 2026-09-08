using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace AgentUsage.Windows
{
    internal static class ReleaseUpdateChecker
    {
        public const string ReleasesPage = "https://github.com/Liuike/AgentUsage/releases/latest";
        private const string LatestReleaseApi = "https://api.github.com/repos/Liuike/AgentUsage/releases/latest";

        public static Task<string> CheckAsync()
        {
            return Task.Factory.StartNew<string>(Check, TaskCreationOptions.LongRunning);
        }

        public static string Check()
        {
            // .NET Framework can otherwise inherit an older Windows TLS default;
            // GitHub requires TLS 1.2 or newer.
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            var request = (HttpWebRequest)WebRequest.Create(LatestReleaseApi);
            request.Method = "GET";
            request.Timeout = 10000;
            request.ReadWriteTimeout = 10000;
            request.UserAgent = "AgentUsage-Windows/" + BuildInfo.Version;
            request.Accept = "application/vnd.github+json";
            request.Headers["X-GitHub-Api-Version"] = "2022-11-28";

            using (var response = (HttpWebResponse)request.GetResponse())
            using (var reader = new StreamReader(response.GetResponseStream()))
            {
                var serializer = new JavaScriptSerializer();
                var root = serializer.DeserializeObject(reader.ReadToEnd()) as IDictionary<string, object>;
                string tag = JsonValue.String(root, "tag_name");
                if (string.IsNullOrWhiteSpace(tag)) throw new InvalidDataException("GitHub returned a release without a version tag.");
                return tag.Trim().TrimStart('v');
            }
        }
    }
}
