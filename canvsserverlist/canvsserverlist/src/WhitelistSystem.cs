using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Vintagestory.API.Server;

namespace canvsserverlist.src
{
    /// <summary>
    /// Polls owner-approved whitelist applications from the site, adds those players to
    /// the in-game whitelist, then acks them so they are not returned again.
    /// Same poll/ack shape as <see cref="VoteRewardSystem"/>.
    ///
    /// Only active when the owner enables auto-whitelist on the server page — otherwise the
    /// pending endpoint returns [] and this does nothing. Stops itself when the server is
    /// archived (410). Contract: an entry is acked only if it was actually added in-game;
    /// failures are left unacked and returned again next cycle (idempotent, re-add is safe).
    /// </summary>
    public class WhitelistSystem : IDisposable
    {
        private readonly ICoreServerAPI api;
        private ModConfig config;
        private readonly ApiClient client;
        private Timer? timer;
        private int consecutiveFailures;
        private int polling;
        private volatile bool stopped;

        public WhitelistSystem(ICoreServerAPI api, ModConfig config, ApiClient client)
        {
            this.api = api;
            this.config = config;
            this.client = client;
        }

        public void Start()
        {
            int intervalMs = config.WhitelistPollIntervalSeconds * 1000;
            // Small startup delay so we don't pile on top of the first heartbeat/vote poll.
            timer = new Timer(_ => Poll(), null, 7000, intervalMs);
        }

        public void Dispose()
        {
            timer?.Dispose();
        }

        public void Reconfigure(ModConfig newConfig)
        {
            config = newConfig;
            consecutiveFailures = 0;
            if (stopped) return; // archived — leave the timer parked
            int intervalMs = newConfig.WhitelistPollIntervalSeconds * 1000;
            timer?.Change(intervalMs, intervalMs);
        }

        private void Poll()
        {
            if (stopped) return;
            if (Interlocked.CompareExchange(ref polling, 1, 0) != 0) return;

            Task.Run(async () =>
            {
                try
                {
                    var entries = await client.GetPendingWhitelist();

                    if (entries == null) // 410 — server archived
                    {
                        stopped = true;
                        timer?.Change(Timeout.Infinite, Timeout.Infinite);
                        api.Logger.Notification("[canvsserverlist] Server archived; whitelist sync stopped.");
                        return;
                    }

                    // Success: reset backoff back to the configured interval.
                    consecutiveFailures = 0;
                    if (timer != null)
                    {
                        int intervalMs = config.WhitelistPollIntervalSeconds * 1000;
                        timer.Change(intervalMs, intervalMs);
                    }

                    if (entries.Count == 0) return;

                    // Whitelist changes touch server player data → run on the main thread.
                    var applied = new List<int>();
                    await EnqueueMain(() =>
                    {
                        foreach (var e in entries)
                        {
                            if (AddToWhitelist(e.PlayerUid, e.PlayerName))
                                applied.Add(e.Id);
                        }
                    });

                    // Ack only the ids actually added. Un-added ones return next cycle.
                    if (applied.Count > 0)
                    {
                        bool acked = await client.AckWhitelist(applied);
                        if (acked)
                            api.Logger.Notification("[canvsserverlist] Whitelisted {0} player(s).", applied.Count);
                        else
                            api.Logger.Warning("[canvsserverlist] Whitelist ack failed, will retry next poll.");
                    }
                }
                catch (Exception ex)
                {
                    consecutiveFailures++;
                    api.Logger.Warning("[canvsserverlist] Whitelist poll failed ({0}): {1}",
                        consecutiveFailures, ex.Message);

                    // Exponential backoff: double the interval on repeated failures, cap at 10 min.
                    if (consecutiveFailures >= 3 && timer != null && !stopped)
                    {
                        int backoffMs = Math.Min(
                            config.WhitelistPollIntervalSeconds * 1000 * (1 << Math.Min(consecutiveFailures - 2, 5)),
                            600_000
                        );
                        timer.Change(backoffMs, backoffMs);
                    }
                }
                finally
                {
                    Interlocked.Exchange(ref polling, 0);
                }
            });
        }

        // Wrap the callback-style EnqueueMainThreadTask in an awaitable.
        private Task EnqueueMain(Action work)
        {
            var tcs = new TaskCompletionSource<bool>();
            api.Event.EnqueueMainThreadTask(() =>
            {
                try { work(); tcs.SetResult(true); }
                catch (Exception ex) { tcs.SetException(ex); }
            }, "canvsserverlist_whitelist");
            return tcs.Task;
        }

        /// <summary>
        /// Add a player to the in-game whitelist via the server's PlayerDataManager
        /// (same path as the built-in <c>/whitelist add</c> command). Prefer the stable
        /// UID; fall back to name when the site did not have a UID. Returns true when the
        /// player is now whitelisted (already-present counts as success → safe to ack).
        /// Must be called on the main thread.
        /// </summary>
        private bool AddToWhitelist(string playerUid, string playerName)
        {
            try
            {
                if (api.World is not Vintagestory.Server.ServerMain server)
                {
                    api.Logger.Warning("[canvsserverlist] whitelist add failed for {0}: server not available.", playerName);
                    return false;
                }

                // UID must be null (not "") when unknown: GetPlayerEntry only falls back to
                // name matching for null UIDs — an empty string would never match the real
                // UID at join time. GetPlayerWhitelist then backfills the UID on first join.
                string? uid = string.IsNullOrEmpty(playerUid) ? null : playerUid;
                string name = playerName ?? "";

                // Malformed entry (no name, no uid): whitelisting it is meaningless and
                // leaving it unacked would retry forever. Ack it away with a warning.
                if (uid == null && name.Length == 0)
                {
                    api.Logger.Warning("[canvsserverlist] Skipping whitelist entry with empty name and uid.");
                    return true;
                }

                // Ensure a player-data row exists (mirrors the vanilla command) — but only
                // when we have a real UID; PlayerDataByUid is keyed by UID.
                // WhitelistPlayer is idempotent: re-adding just refreshes the existing entry.
                if (uid != null)
                    server.PlayerDataManager.GetOrCreateServerPlayerData(uid, name);
                DateTime until = DateTime.Now.AddYears(50);
                server.PlayerDataManager.WhitelistPlayer(name, uid, "canvsserverlist", "", until);
                return true;
            }
            catch (Exception ex)
            {
                api.Logger.Warning("[canvsserverlist] whitelist add failed for {0}: {1}", playerName, ex.Message);
                return false; // left unacked → retried next cycle
            }
        }
    }
}
