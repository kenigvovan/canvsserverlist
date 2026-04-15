using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vintagestory.API.Common;
using Vintagestory.API.Server;

namespace canvsserverlist.src
{
    public class HeartbeatSystem : IDisposable
    {
        private readonly ICoreServerAPI api;
        private ModConfig config;
        private readonly ApiClient client;
        private Timer? timer;
        private int consecutiveFailures;
        private int sending;

        public HeartbeatSystem(ICoreServerAPI api, ModConfig config, ApiClient client)
        {
            this.api = api;
            this.config = config;
            this.client = client;
        }

        public void Start()
        {
            int intervalMs = config.HeartbeatIntervalSeconds * 1000;
            // Fire immediately (0 delay), then repeat at interval
            timer = new Timer(_ => FireHeartbeat(), null, 0, intervalMs);
        }

        public void Dispose()
        {
            timer?.Dispose();
        }

        private void FireHeartbeat()
        {
            // Snapshot player list on timer thread — AllOnlinePlayers is safe to read
            List<string> players;
            try
            {
                players = api.World.AllOnlinePlayers
                    .Select(p => p.PlayerName)
                    .ToList();
            }
            catch
            {
                return;
            }

            if (Interlocked.CompareExchange(ref sending, 1, 0) != 0) return;

            Task.Run(async () =>
            {
                try
                {
                    bool ok = await client.SendHeartbeat(players.Count, players);
                    if (ok)
                    {
                        consecutiveFailures = 0;
                    }
                    else
                    {
                        OnFailure("non-success status code");
                    }
                }
                catch (Exception ex)
                {
                    OnFailure(ex.Message);
                }
                finally
                {
                    Interlocked.Exchange(ref sending, 0);
                }
            });
        }

        private void OnFailure(string reason)
        {
            consecutiveFailures++;
            api.Logger.Warning("[canvsserverlist] Heartbeat failed ({0}): {1}",
                consecutiveFailures, reason);

            // Exponential backoff: double the interval on repeated failures, cap at 10 min
            if (consecutiveFailures >= 3 && timer != null)
            {
                int backoffMs = Math.Min(
                    config.HeartbeatIntervalSeconds * 1000 * (1 << Math.Min(consecutiveFailures - 2, 5)),
                    600_000
                );
                timer.Change(backoffMs, backoffMs);
                api.Logger.Warning("[canvsserverlist] Heartbeat backing off to {0}s", backoffMs / 1000);
            }
        }

        public void Reconfigure(ModConfig newConfig)
        {
            config = newConfig;
            consecutiveFailures = 0;
            int intervalMs = newConfig.HeartbeatIntervalSeconds * 1000;
            timer?.Change(intervalMs, intervalMs);
        }

        /// <summary>
        /// Reset interval back to configured value (e.g. after backoff recovery).
        /// </summary>
        public void ResetInterval()
        {
            consecutiveFailures = 0;
            int intervalMs = config.HeartbeatIntervalSeconds * 1000;
            timer?.Change(intervalMs, intervalMs);
        }
    }
}
