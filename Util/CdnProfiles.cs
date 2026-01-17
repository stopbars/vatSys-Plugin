using Newtonsoft.Json;
using System;
using System.Collections.Concurrent;
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
        private static Task _warmTask;
        private static readonly ConcurrentDictionary<string, XmlCacheEntry> _xmlCache = new ConcurrentDictionary<string, XmlCacheEntry>(StringComparer.OrdinalIgnoreCase);
        private static readonly TimeSpan XmlCacheTtl = TimeSpan.FromMinutes(10);
        private class XmlCacheEntry
        {
            public string Xml { get; set; }
            public DateTime FetchedUtc { get; set; }
        }

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

        /// <summary>
        /// Fire-and-forget warmup of the CDN index to reduce first-use latency on the UI thread.
        /// Safe to call multiple times; only the first outstanding warm-up runs.
        /// </summary>
        public static void WarmCacheAsync()
        {
            if (_warmTask != null && !_warmTask.IsCompleted)
            {
                return;
            }

            _warmTask = Task.Run(async () =>
            {
                try
                {
                    await GetIndexAsync().ConfigureAwait(false);
                }
                catch
                {
                    // Best-effort warmup; callers still fall back to normal fetch path.
                }
            });
        }

        public static ProfilesIndex GetIndex()
        {
            // Blocking wrapper for convenience in non-async call sites
            return GetIndexAsync().GetAwaiter().GetResult();
        }

        private static string NormalizeIcao(string icao)
        {
            return (icao ?? string.Empty).Trim().ToUpperInvariant();
        }

        private static bool TryGetCachedXml(string icao, out string xml)
        {
            xml = null;
            string key = NormalizeIcao(icao);
            if (_xmlCache.TryGetValue(key, out var entry))
            {
                if ((DateTime.UtcNow - entry.FetchedUtc) < XmlCacheTtl)
                {
                    xml = entry.Xml;
                    return true;
                }
                _xmlCache.TryRemove(key, out _);
            }
            return false;
        }

        private static void CacheXml(string icao, string xml)
        {
            if (string.IsNullOrWhiteSpace(xml)) return;
            string key = NormalizeIcao(icao);
            _xmlCache[key] = new XmlCacheEntry
            {
                Xml = xml,
                FetchedUtc = DateTime.UtcNow
            };
        }

        public static string GetAirportXmlUrl(string icao)
        {
            var idx = GetIndex();
            string wantIcao = (icao ?? string.Empty).Trim().ToUpperInvariant();
            // Expect exact file name like "YMML.xml"
            return idx.Profiles.FirstOrDefault(p => string.Equals(p.Icao, wantIcao, StringComparison.OrdinalIgnoreCase)
                                                 && string.Equals(p.Name, $"{wantIcao}.xml", StringComparison.OrdinalIgnoreCase))?.Url;
        }

        /// <summary>
        /// Returns the airport XML, using a short-lived cache if available to avoid blocking UI on repeated opens.
        /// Falls back to live download on cache miss.
        /// </summary>
        public static string GetAirportXml(string icao)
        {
            if (TryGetCachedXml(icao, out var cached))
            {
                return cached;
            }

            string url = GetAirportXmlUrl(icao);
            if (string.IsNullOrWhiteSpace(url))
            {
                return null;
            }

            string xml = DownloadXml(url);
            if (!string.IsNullOrWhiteSpace(xml))
            {
                CacheXml(icao, xml);
            }
            return xml;
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

        /// <summary>
        /// Preload airport XML in the background to reduce UI-thread blocking on first open.
        /// Safe to call multiple times per ICAO.
        /// </summary>
        public static Task WarmAirportXmlAsync(string icao)
        {
            string key = NormalizeIcao(icao);
            if (TryGetCachedXml(key, out _))
            {
                return Task.CompletedTask;
            }

            string url = GetAirportXmlUrl(icao);
            if (string.IsNullOrWhiteSpace(url))
            {
                return Task.CompletedTask;
            }

            return Task.Run(async () =>
            {
                try
                {
                    string xml = await http.GetStringAsync(url).ConfigureAwait(false);
                    CacheXml(key, xml);
                }
                catch
                {
                    // Best-effort warmup; normal code path will still attempt on demand.
                }
            });
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
