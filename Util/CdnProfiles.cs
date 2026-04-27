using Newtonsoft.Json;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;

namespace BARS.Util
{
    public static class CdnProfiles
    {
        private const string GenerateUrl = "https://v2.stopbars.com/vatsys/profiles/generate";
        private const string IntasFormat = "intas";
        private const string LegacyFormat = "legacy";
        private static readonly HttpClient http = new HttpClient();
        private static readonly ConcurrentDictionary<string, GeneratedProfilesCacheEntry> _generatedProfilesCache = new ConcurrentDictionary<string, GeneratedProfilesCacheEntry>(StringComparer.OrdinalIgnoreCase);
        private static readonly TimeSpan GeneratedProfilesCacheTtl = TimeSpan.FromMinutes(10);

        private class GeneratedProfilesCacheEntry
        {
            public GeneratedProfilesResponse Response { get; set; }
            public string ApiKey { get; set; }
            public DateTime FetchedUtc { get; set; }
        }

        private class ApiErrorResponse
        {
            [JsonProperty("error")] public string Error { get; set; }
        }

        public class GeneratedProfilesResponse
        {
            [JsonProperty("format")] public string Format { get; set; }
            [JsonProperty("icao")] public string Icao { get; set; }
            [JsonProperty("profiles")] public List<GeneratedProfile> Profiles { get; set; } = new List<GeneratedProfile>();
            [JsonProperty("warnings")] public List<string> Warnings { get; set; } = new List<string>();
        }

        public class GeneratedProfile
        {
            [JsonProperty("filename")] public string Filename { get; set; }
            [JsonProperty("xml")] public string Xml { get; set; }
            [JsonProperty("warnings")] public List<string> Warnings { get; set; } = new List<string>();
        }

        public class ProfileGenerationException : Exception
        {
            public HttpStatusCode? StatusCode { get; private set; }

            public ProfileGenerationException(string message, HttpStatusCode? statusCode = null, Exception innerException = null)
                : base(message, innerException)
            {
                StatusCode = statusCode;
            }
        }

        private static string NormalizeIcao(string icao)
        {
            return (icao ?? string.Empty).Trim().ToUpperInvariant();
        }

        public static string NormalizeFormat(string format)
        {
            return (format ?? string.Empty).Trim().ToLowerInvariant();
        }

        public static bool IsIntasFormat(string format)
        {
            return string.Equals(NormalizeFormat(format), IntasFormat, StringComparison.Ordinal);
        }

        public static bool IsLegacyFormat(string format)
        {
            return string.Equals(NormalizeFormat(format), LegacyFormat, StringComparison.Ordinal);
        }

        private static bool TryGetCachedGeneratedProfiles(string icao, string apiKey, out GeneratedProfilesResponse response)
        {
            response = null;
            string key = NormalizeIcao(icao);
            if (_generatedProfilesCache.TryGetValue(key, out var entry))
            {
                if (string.Equals(entry.ApiKey, apiKey, StringComparison.Ordinal) &&
                    (DateTime.UtcNow - entry.FetchedUtc) < GeneratedProfilesCacheTtl)
                {
                    response = entry.Response;
                    return true;
                }

                _generatedProfilesCache.TryRemove(key, out _);
            }

            return false;
        }

        private static void CacheGeneratedProfiles(string icao, string apiKey, GeneratedProfilesResponse response)
        {
            if (response == null) return;
            string key = NormalizeIcao(icao);
            _generatedProfilesCache[key] = new GeneratedProfilesCacheEntry
            {
                Response = response,
                ApiKey = apiKey,
                FetchedUtc = DateTime.UtcNow
            };
        }

        private static string ExtractErrorMessage(string body, string fallback)
        {
            if (!string.IsNullOrWhiteSpace(body))
            {
                try
                {
                    var error = JsonConvert.DeserializeObject<ApiErrorResponse>(body);
                    if (!string.IsNullOrWhiteSpace(error?.Error))
                    {
                        return error.Error;
                    }
                }
                catch
                {
                    // Non-JSON error responses fall through to the generic message.
                }
            }

            return fallback;
        }

        public static async Task<GeneratedProfilesResponse> GenerateProfilesAsync(string icao, string apiKey, bool forceRefresh = false)
        {
            string wantIcao = NormalizeIcao(icao);
            if (string.IsNullOrWhiteSpace(wantIcao))
            {
                throw new ProfileGenerationException("Airport ICAO is required for vatSys profile generation.");
            }

            if (string.IsNullOrWhiteSpace(apiKey))
            {
                throw new ProfileGenerationException("API Key is required for vatSys profile generation.");
            }
            apiKey = apiKey.Trim();

            if (!forceRefresh && TryGetCachedGeneratedProfiles(wantIcao, apiKey, out var cached))
            {
                return cached;
            }

            try
            {
                using (var request = new HttpRequestMessage(HttpMethod.Post, GenerateUrl))
                {
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
                    string json = JsonConvert.SerializeObject(new { icao = wantIcao });
                    request.Content = new StringContent(json, Encoding.UTF8, "application/json");

                    using (var response = await http.SendAsync(request).ConfigureAwait(false))
                    {
                        string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                        if (!response.IsSuccessStatusCode)
                        {
                            string message = ExtractErrorMessage(body, $"vatSys profile generation failed for {wantIcao}.");
                            throw new ProfileGenerationException(message, response.StatusCode);
                        }

                        var generated = JsonConvert.DeserializeObject<GeneratedProfilesResponse>(body);
                        if (generated == null)
                        {
                            throw new ProfileGenerationException($"vatSys profile generation returned an empty response for {wantIcao}.");
                        }

                        generated.Icao = NormalizeIcao(generated.Icao ?? wantIcao);
                        generated.Format = NormalizeFormat(generated.Format);
                        generated.Profiles = generated.Profiles ?? new List<GeneratedProfile>();
                        generated.Warnings = generated.Warnings ?? new List<string>();
                        foreach (var profile in generated.Profiles)
                        {
                            if (profile != null)
                            {
                                profile.Warnings = profile.Warnings ?? new List<string>();
                            }
                        }
                        CacheGeneratedProfiles(wantIcao, apiKey, generated);
                        return generated;
                    }
                }
            }
            catch (ProfileGenerationException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new ProfileGenerationException($"Unable to generate vatSys profiles for {wantIcao}: {ex.Message}", null, ex);
            }
        }

        public static async Task<GeneratedProfilesResponse> GenerateLegacyProfilesAsync(string icao, string apiKey, bool forceRefresh = false)
        {
            return await GenerateProfilesAsync(icao, apiKey, forceRefresh).ConfigureAwait(false);
        }

        public static GeneratedProfilesResponse GetGeneratedProfiles(string icao, string apiKey)
        {
            return GenerateProfilesAsync(icao, apiKey).GetAwaiter().GetResult();
        }

        public static GeneratedProfilesResponse GetGeneratedLegacyProfiles(string icao, string apiKey)
        {
            return GetGeneratedProfiles(icao, apiKey);
        }

        private static string BuildLegacyProfileFilename(string icao, string profileName)
        {
            string wantIcao = NormalizeIcao(icao);
            string variant = (profileName ?? string.Empty).Replace("/", "-");
            return $"{wantIcao}_{variant}.xml";
        }

        private static string GetLegacyProfileNameFromFilename(string icao, string filename)
        {
            string wantIcao = NormalizeIcao(icao);
            if (string.IsNullOrWhiteSpace(filename) ||
                !filename.StartsWith(wantIcao + "_", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            string suffix = filename.Substring(wantIcao.Length + 1);
            if (suffix.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
            {
                suffix = suffix.Substring(0, suffix.Length - ".xml".Length);
            }

            return suffix.Replace("-", "/");
        }

        private static string BuildIntasProfileFilename(string icao)
        {
            return $"{NormalizeIcao(icao)}.xml";
        }

        public static string GetIntasProfileXml(string icao)
        {
            string wantFilename = BuildIntasProfileFilename(icao);
            var generated = GetGeneratedProfiles(icao, Properties.Settings.Default.APIKey);
            if (!IsIntasFormat(generated.Format))
            {
                return null;
            }

            var exactProfile = generated.Profiles.FirstOrDefault(p =>
                p != null && string.Equals(p.Filename, wantFilename, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(exactProfile?.Xml))
            {
                return exactProfile.Xml;
            }

            return generated.Profiles.FirstOrDefault(p => !string.IsNullOrWhiteSpace(p?.Xml))?.Xml;
        }

        public static bool HasIntasProfile(string icao)
        {
            return !string.IsNullOrWhiteSpace(GetIntasProfileXml(icao));
        }

        public static List<string> GetLegacyProfileNames(string icao)
        {
            var generated = GetGeneratedLegacyProfiles(icao, Properties.Settings.Default.APIKey);
            string wantIcao = NormalizeIcao(icao);
            var names = new List<string>();
            foreach (var p in generated.Profiles)
            {
                if (p == null)
                {
                    continue;
                }

                string name = GetLegacyProfileNameFromFilename(wantIcao, p.Filename);
                if (!string.IsNullOrWhiteSpace(name))
                {
                    names.Add(name);
                }
            }
            names.Sort(StringComparer.OrdinalIgnoreCase);
            return names;
        }

        public static string GetLegacyProfileXml(string icao, string profileName)
        {
            string wantFilename = BuildLegacyProfileFilename(icao, profileName);
            var generated = GetGeneratedLegacyProfiles(icao, Properties.Settings.Default.APIKey);
            return generated.Profiles.FirstOrDefault(p =>
                p != null && string.Equals(p.Filename, wantFilename, StringComparison.OrdinalIgnoreCase))?.Xml;
        }
    }
}
