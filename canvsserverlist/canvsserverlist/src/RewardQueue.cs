using System;
using System.Collections.Generic;
using Vintagestory.API.Server;
using Vintagestory.API.Util;

namespace canvsserverlist.src
{
    /// <summary>
    /// Thread-safe persistent queue for offline player rewards.
    /// Survives server restarts via SaveGame data.
    /// </summary>
    public class RewardQueue
    {
        private const string DataKey = "canvsserverlist_pending_rewards";
        private const string ClaimStatsKey = "canvsserverlist_claim_stats";
        private const string DailyClaimsKey = "canvsserverlist_daily_claims";
        private const string DailyClaimsDayKey = "canvsserverlist_daily_claims_day";
        private readonly ICoreServerAPI api;
        private readonly object lockObj = new object();
        private Dictionary<string, int> queue;
        private Dictionary<string, int> claimStats;
        private Dictionary<string, int> dailyClaims;
        private string dailyClaimsDay;

        public RewardQueue(ICoreServerAPI api)
        {
            this.api = api;

            var raw = api.WorldManager.SaveGame.GetData(DataKey);
            if (raw != null)
            {
                try
                {
                    var deserialized = SerializerUtil.Deserialize<Dictionary<string, int>>(raw);
                    queue = new Dictionary<string, int>(deserialized, StringComparer.OrdinalIgnoreCase);
                }
                catch
                {
                    queue = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                }
            }
            else
            {
                queue = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            }

            var rawStats = api.WorldManager.SaveGame.GetData(ClaimStatsKey);
            if (rawStats != null)
            {
                try
                {
                    var deserialized = SerializerUtil.Deserialize<Dictionary<string, int>>(rawStats);
                    claimStats = new Dictionary<string, int>(deserialized, StringComparer.OrdinalIgnoreCase);
                }
                catch
                {
                    claimStats = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                }
            }
            else
            {
                claimStats = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            }

            var rawDaily = api.WorldManager.SaveGame.GetData(DailyClaimsKey);
            if (rawDaily != null)
            {
                try
                {
                    var deserialized = SerializerUtil.Deserialize<Dictionary<string, int>>(rawDaily);
                    dailyClaims = new Dictionary<string, int>(deserialized, StringComparer.OrdinalIgnoreCase);
                }
                catch
                {
                    dailyClaims = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                }
            }
            else
            {
                dailyClaims = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            }

            var rawDay = api.WorldManager.SaveGame.GetData(DailyClaimsDayKey);
            dailyClaimsDay = rawDay != null
                ? System.Text.Encoding.UTF8.GetString(rawDay)
                : CurrentDay();
        }

        private static string CurrentDay() => DateTime.UtcNow.ToString("yyyy-MM-dd");

        /// <summary>
        /// Resets the per-player daily claim counters when the UTC day changes.
        /// Must be called while holding lockObj.
        /// </summary>
        private void ResetDailyIfNeeded()
        {
            string today = CurrentDay();
            if (dailyClaimsDay == today) return;

            dailyClaimsDay = today;
            dailyClaims.Clear();
            PersistDaily();
        }

        public void Enqueue(string nickname, int count = 1)
        {
            lock (lockObj)
            {
                if (queue.ContainsKey(nickname))
                    queue[nickname] += count;
                else
                    queue[nickname] = count;
                Persist();
            }
        }

        /// <summary>
        /// Returns how many rewards to give, then removes that amount from the queue.
        /// When <paramref name="maxPerDay"/> is greater than 0, the amount returned is
        /// capped so the player does not exceed that many rewards in a single UTC day;
        /// any remainder stays in the queue for the following day.
        /// </summary>
        public int Dequeue(string nickname, int maxPerDay = 0)
        {
            lock (lockObj)
            {
                if (!queue.TryGetValue(nickname, out int count) || count <= 0) return 0;

                int toGive = count;
                if (maxPerDay > 0)
                {
                    ResetDailyIfNeeded();
                    int claimedToday = dailyClaims.TryGetValue(nickname, out int c) ? c : 0;
                    int remaining = maxPerDay - claimedToday;
                    if (remaining <= 0) return 0;
                    toGive = Math.Min(count, remaining);
                }

                int rest = count - toGive;
                if (rest > 0)
                    queue[nickname] = rest;
                else
                    queue.Remove(nickname);

                if (claimStats.ContainsKey(nickname))
                    claimStats[nickname] += toGive;
                else
                    claimStats[nickname] = toGive;

                Persist();
                PersistStats();

                if (maxPerDay > 0)
                {
                    dailyClaims[nickname] = (dailyClaims.TryGetValue(nickname, out int d) ? d : 0) + toGive;
                    PersistDaily();
                }

                return toGive;
            }
        }

        public int PendingFor(string nickname)
        {
            lock (lockObj)
            {
                return queue.TryGetValue(nickname, out int count) ? count : 0;
            }
        }

        public int PendingCount
        {
            get
            {
                lock (lockObj)
                {
                    int total = 0;
                    foreach (var kv in queue) total += kv.Value;
                    return total;
                }
            }
        }

        public Dictionary<string, int> GetAllPending()
        {
            lock (lockObj)
            {
                return new Dictionary<string, int>(queue, StringComparer.OrdinalIgnoreCase);
            }
        }

        public Dictionary<string, int> GetClaimStats()
        {
            lock (lockObj)
            {
                return new Dictionary<string, int>(claimStats, StringComparer.OrdinalIgnoreCase);
            }
        }

        private void Persist()
        {
            api.WorldManager.SaveGame.StoreData(DataKey, SerializerUtil.Serialize(queue));
        }

        private void PersistStats()
        {
            api.WorldManager.SaveGame.StoreData(ClaimStatsKey, SerializerUtil.Serialize(claimStats));
        }

        private void PersistDaily()
        {
            api.WorldManager.SaveGame.StoreData(DailyClaimsKey, SerializerUtil.Serialize(dailyClaims));
            api.WorldManager.SaveGame.StoreData(DailyClaimsDayKey, System.Text.Encoding.UTF8.GetBytes(dailyClaimsDay));
        }
    }
}
