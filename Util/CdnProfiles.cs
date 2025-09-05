using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;

namespace BARS.Util
{
    public static class CdnProfiles
    {
        private const string IndexUrl = "https://v2.stopbars.com/vatsys/profiles";
        private static readonly HttpClient http = new HttpClient();
        private static DateTime _lastFetch = DateTime.MinValue;
        private static ProfilesIndex _cache;
        private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(2);

        public class ProfilesIndex
        {
            [JsonProperty("profiles")] public List<ProfileEntry> Profiles { get; set; } = new List<ProfileEntry>();
        }

        public class ProfileEntry
        {
            [JsonProperty("icao")] public string Icao { get; set; }
            [JsonProperty("name")] public string Name { get; set; }
            [JsonProperty("url")] public string Url { get; set; }
        }

        public static async Task<ProfilesIndex> GetIndexAsync()
        {
            try
            {
                if (_cache != null && (DateTime.UtcNow - _lastFetch) < CacheTtl)
                {
                    return _cache;
                }

                var json = await http.GetStringAsync(IndexUrl).ConfigureAwait(false);
                var idx = JsonConvert.DeserializeObject<ProfilesIndex>(json) ?? new ProfilesIndex();
                _cache = idx;
                _lastFetch = DateTime.UtcNow;
                return idx;
            }
            catch
            {
                return _cache ?? new ProfilesIndex();
            }
        }

        public static ProfilesIndex GetIndex()
        {
            // Blocking wrapper for convenience in non-async call sites
            return GetIndexAsync().GetAwaiter().GetResult();
        }

        public static string GetAirportXmlUrl(string icao)
        {
            var idx = GetIndex();
            string wantIcao = (icao ?? string.Empty).Trim().ToUpperInvariant();
            // Expect exact file name like "YMML.xml"
            return idx.Profiles.FirstOrDefault(p => string.Equals(p.Icao, wantIcao, StringComparison.OrdinalIgnoreCase)
                                                 && string.Equals(p.Name, $"{wantIcao}.xml", StringComparison.OrdinalIgnoreCase))?.Url;
        }

        public static string GetLegacyProfileUrl(string icao, string profileName)
        {
            var idx = GetIndex();
            string wantIcao = (icao ?? string.Empty).Trim().ToUpperInvariant();
            string variant = (profileName ?? string.Empty).Replace("/", "-");
            string wantName = $"{wantIcao}_{variant}.xml";
            return idx.Profiles.FirstOrDefault(p => string.Equals(p.Icao, wantIcao, StringComparison.OrdinalIgnoreCase)
                                                 && string.Equals(p.Name, wantName, StringComparison.OrdinalIgnoreCase))?.Url;
        }

        public static List<string> GetLegacyProfileNames(string icao)
        {
            var idx = GetIndex();
            string wantIcao = (icao ?? string.Empty).Trim().ToUpperInvariant();
            var names = new List<string>();
            foreach (var p in idx.Profiles.Where(p => string.Equals(p.Icao, wantIcao, StringComparison.OrdinalIgnoreCase)))
            {
                if (p.Name.StartsWith(wantIcao + "_", StringComparison.OrdinalIgnoreCase))
                {
                    string suffix = p.Name.Substring(wantIcao.Length + 1); // after ICAO_
                    // Display profile using "/" instead of "-"
                    names.Add(suffix.Replace("-", "/").Replace(".xml", string.Empty));
                }
            }
            names.Sort(StringComparer.OrdinalIgnoreCase);
            return names;
        }

        public static string DownloadXml(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return null;
            try
            {
                return http.GetStringAsync(url).GetAwaiter().GetResult();
            }
            catch
            {
                return null;
            }
        }
    }
}
