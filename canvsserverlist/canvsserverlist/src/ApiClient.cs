using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace canvsserverlist.src
{
    public class ApiClient : IDisposable
    {
        private readonly HttpClient http;
        private ModConfig config;

        public ApiClient(ModConfig config)
        {
            this.config = config;
            http = new HttpClient();
            http.Timeout = TimeSpan.FromSeconds(10);
            http.DefaultRequestHeaders.Add("X-Api-Key", config.ApiKey);
        }

        public void Reconfigure(ModConfig newConfig)
        {
            config = newConfig;
            http.DefaultRequestHeaders.Remove("X-Api-Key");
            http.DefaultRequestHeaders.Add("X-Api-Key", newConfig.ApiKey);
        }

        public async Task<bool> SendHeartbeat(int playerCount, List<string> playerNames)
        {
            var payload = JsonConvert.SerializeObject(new
            {
                player_count = playerCount,
                online_players = playerNames
            });
            var content = new StringContent(payload, Encoding.UTF8, "application/json");
            var url = $"{config.ApiUrl.TrimEnd('/')}/api/servers/{config.ServerUuid}/heartbeat/";
            var resp = await http.PostAsync(url, content);
            return resp.IsSuccessStatusCode;
        }

        public async Task<List<PendingVote>> GetPendingVotes()
        {
            var url = $"{config.ApiUrl.TrimEnd('/')}/api/servers/{config.ServerUuid}/pending-votes/";
            var resp = await http.GetAsync(url);
            if (!resp.IsSuccessStatusCode) return new List<PendingVote>();
            var json = await resp.Content.ReadAsStringAsync();
            return JsonConvert.DeserializeObject<List<PendingVote>>(json) ?? new List<PendingVote>();
        }

        public async Task<bool> AckVotes(List<int> voteIds)
        {
            var payload = JsonConvert.SerializeObject(new { vote_ids = voteIds });
            var content = new StringContent(payload, Encoding.UTF8, "application/json");
            var url = $"{config.ApiUrl.TrimEnd('/')}/api/servers/{config.ServerUuid}/votes/ack/";
            var resp = await http.PostAsync(url, content);
            return resp.IsSuccessStatusCode;
        }

        public void Dispose()
        {
            http?.Dispose();
        }
    }

    public class PendingVote
    {
        [JsonProperty("id")]
        public int Id { get; set; }

        [JsonProperty("ingame_nickname")]
        public string IngameNickname { get; set; } = "";

        [JsonProperty("voted_at")]
        public string VotedAt { get; set; } = "";
    }
}
