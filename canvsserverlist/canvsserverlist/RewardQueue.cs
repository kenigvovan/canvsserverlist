using System;
using System.Collections.Generic;
using Vintagestory.API.Server;
using Vintagestory.API.Util;

namespace canvsserverlist
{
    /// <summary>
    /// Thread-safe persistent queue for offline player rewards.
    /// Survives server restarts via SaveGame data.
    /// </summary>
    public class RewardQueue
    {
        private const string DataKey = "canvsserverlist_pending_rewards";
        private readonly ICoreServerAPI api;
        private readonly object lockObj = new object();
        private Dictionary<string, int> queue;

        public RewardQueue(ICoreServerAPI api)
        {
            this.api = api;
            var raw = api.WorldManager.SaveGame.GetData(DataKey);
            if (raw != null)
            {
                try
                {
                    queue = SerializerUtil.Deserialize<Dictionary<string, int>>(raw);
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
        /// Returns how many rewards to give, then removes from queue.
        /// </summary>
        public int Dequeue(string nickname)
        {
            lock (lockObj)
            {
                if (!queue.TryGetValue(nickname, out int count)) return 0;
                queue.Remove(nickname);
                Persist();
                return count;
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

        private void Persist()
        {
            api.WorldManager.SaveGame.StoreData(DataKey, SerializerUtil.Serialize(queue));
        }
    }
}
