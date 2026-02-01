using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using Pathoschild.Http.Client;
using TidepoolToNightScoutSync.Core.Model.Nightscout;

namespace TidepoolToNightScoutSync.Core.Services.Nightscout
{
    public class NightscoutClient
    {
        private readonly IClient _client;
        private readonly NightscoutClientOptions _options;

        public NightscoutClient(IOptions<NightscoutClientOptions> options, HttpClient client)
        {
            _options = options.Value;
            _client = new FluentClient(new Uri(_options.BaseUrl), client);

            // Token-based auth (read)
            _client.AddDefault(x => x.WithArgument("token", _options.ApiKey));

            _client.Formatters.JsonFormatter.SerializerSettings.NullValueHandling =
                Newtonsoft.Json.NullValueHandling.Ignore;
        }

        private static string SHA1(in string input)
        {
            using var sha1 = SHA1Managed.Create();
            var hash = sha1.ComputeHash(Encoding.UTF8.GetBytes(input));
            return string.Concat(hash.Select(b => b.ToString("x2")));
        }

        /* -------------------- Profiles -------------------- */
        public async Task<IReadOnlyList<Profile>> GetProfiles() =>
            await _client.GetAsync("api/v1/profile").AsArray<Profile>();

        public async Task<Profile> SetProfile(Profile profile) =>
            await _client
                .PutAsync("api/v1/profile", profile)
                .WithHeader("api-secret", SHA1(_options.ApiKey))
                .As<Profile>();

        /* -------------------- Treatments -------------------- */
        public async Task<IReadOnlyList<Treatment>> AddTreatmentsAsync(IEnumerable<Treatment> treatments) =>
            await _client
                .PostAsync("api/v1/treatments", treatments)
                .WithHeader("api-secret", SHA1(_options.ApiKey))
                .AsArray<Treatment>();

        public async Task<IReadOnlyList<Treatment>> GetTreatmentsAsync(string? find, int? count) =>
            await _client
                .GetAsync("api/v1/treatments")
                .WithArgument("find", find)
                .WithArgument("count", count)
                .AsArray<Treatment>();

        /* -------------------- CGM Entries -------------------- */
        public async Task AddEntriesAsync(IEnumerable<Entry> entries) =>
            await _client
                .PostAsync("api/v1/entries", entries)
                .WithHeader("api-secret", SHA1(_options.ApiKey))
                .AsString();

        /* -------------------- Status -------------------- */
        public async Task<Status> GetStatus() =>
            await _client.GetAsync("api/v1/status.json").As<Status>();
    } // end class
} // end namespace
