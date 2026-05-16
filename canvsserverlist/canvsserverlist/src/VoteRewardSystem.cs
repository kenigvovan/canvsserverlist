using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.Server;
using Vintagestory.API.Util;

namespace canvsserverlist.src
{
    public class VoteRewardSystem : IDisposable
    {
        private const string SeenVoteIdsKey = "canvsserverlist_seen_vote_ids";
        private const int SeenVoteIdsMaxSize = 10_000;
        private const int SeenVoteIdsTrimTo = 5_000;

        private readonly ICoreServerAPI api;
        private ModConfig config;
        private readonly ApiClient client;
        private readonly object configLock;
        private RewardQueue queue = null!;
        private HashSet<int> seenVoteIds = null!;
        private Timer? timer;
        private int consecutiveFailures;
        private int polling;

        public Action<ModConfig>? OnConfigChanged { get; set; }

        public VoteRewardSystem(ICoreServerAPI api, ModConfig config, ApiClient client, object configLock)
        {
            this.api = api;
            this.config = config;
            this.client = client;
            this.configLock = configLock;
        }

        public void Start()
        {
            queue = new RewardQueue(api);

            var raw = api.WorldManager.SaveGame.GetData(SeenVoteIdsKey);
            if (raw != null)
            {
                try { seenVoteIds = SerializerUtil.Deserialize<HashSet<int>>(raw); }
                catch { seenVoteIds = new HashSet<int>(); }
            }
            else
            {
                seenVoteIds = new HashSet<int>();
            }

            // Notify player on join if they have pending rewards
            api.Event.PlayerJoin += OnPlayerJoin;

            // Register /voteclaim command for players
            api.ChatCommands.Create("voteclaim").RequiresPrivilege(Privilege.chat)
                .WithDescription(Lang.Get("canvsserverlist:cmd-voteclaim-desc"))
                .HandleWith(OnClaimCommand);

            // Register /clearvoterewards command for admins
            api.ChatCommands.Create("clearvoterewards").RequiresPrivilege(Privilege.controlserver)
                .WithDescription(Lang.Get("canvsserverlist:cmd-clearvoterewards-desc"))
                .HandleWith(OnClearRewardsCommand);

            // Register /addvotereward command for admins
            api.ChatCommands.Create("addvotereward").RequiresPrivilege(Privilege.controlserver)
                .WithDescription(Lang.Get("canvsserverlist:cmd-addvotereward-desc"))
                .WithArgs(api.ChatCommands.Parsers.Int("quantity"))
                .HandleWith(OnAddRewardCommand);

            int intervalMs = config.VotePollIntervalSeconds * 1000;
            timer = new Timer(_ => PollVotes(), null, 5000, intervalMs);
        }

        public void Dispose()
        {
            api.Event.PlayerJoin -= OnPlayerJoin;
            timer?.Dispose();
        }

        public RewardQueue Queue => queue;

        public void Reconfigure(ModConfig newConfig)
        {
            config = newConfig;
            consecutiveFailures = 0;
            int intervalMs = newConfig.VotePollIntervalSeconds * 1000;
            timer?.Change(intervalMs, intervalMs);
        }

        private void PollVotes()
        {
            if (Interlocked.CompareExchange(ref polling, 1, 0) != 0) return;

            Task.Run(async () =>
            {
                try
                {
                    var votes = await client.GetPendingVotes();

                    consecutiveFailures = 0;
                    if (timer != null)
                    {
                        int intervalMs = config.VotePollIntervalSeconds * 1000;
                        timer.Change(intervalMs, intervalMs);
                    }

                    if (votes.Count == 0) return;

                    // Phase 1: filter out votes we have already enqueued (dedup guard).
                    // seenVoteIds is persisted, so this survives restarts and ack failures.
                    var newVotes = votes.Where(v => !seenVoteIds.Contains(v.Id)).ToList();

                    // Phase 2: queue new votes to persistent storage.
                    foreach (var vote in newVotes)
                        queue.Enqueue(vote.IngameNickname);

                    // Phase 3: mark new vote IDs as seen BEFORE acking.
                    // If ack fails and backend returns the same IDs next poll, we skip them.
                    foreach (var vote in newVotes)
                        seenVoteIds.Add(vote.Id);

                    if (seenVoteIds.Count > SeenVoteIdsMaxSize)
                    {
                        // Keep only the most recent IDs (largest values = newest votes).
                        seenVoteIds = new HashSet<int>(
                            seenVoteIds.OrderByDescending(id => id).Take(SeenVoteIdsTrimTo));
                    }

                    api.WorldManager.SaveGame.StoreData(
                        SeenVoteIdsKey, SerializerUtil.Serialize(seenVoteIds));

                    // Phase 4: ack ALL votes returned by backend (including already-seen),
                    // so the backend stops returning them on future polls.
                    var voteIds = votes.Select(v => v.Id).ToList();
                    bool acked = await client.AckVotes(voteIds);
                    if (!acked)
                        api.Logger.Warning("[canvsserverlist] Vote ack failed, will retry next poll.");

                    // Build lookup of online players
                    var onlinePlayers = api.World.AllOnlinePlayers
                        .ToDictionary(p => p.PlayerName.ToLowerInvariant(), p => (IServerPlayer)p);

                    // Phase 5: notify online players that they have rewards to claim.
                    foreach (var vote in newVotes)
                    {
                        var key = vote.IngameNickname.ToLowerInvariant();
                        if (onlinePlayers.TryGetValue(key, out var player))
                        {
                            int pending = queue.PendingFor(player.PlayerName);
                            if (pending > 0)
                            {
                                api.Event.EnqueueMainThreadTask(() =>
                                {
                                    player.SendMessage(
                                        GlobalConstants.GeneralChatGroup,
                                        Lang.Get("canvsserverlist:notify-pending", pending),
                                        EnumChatType.Notification
                                    );
                                }, "canvsserverlist_notify");
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    consecutiveFailures++;
                    api.Logger.Warning("[canvsserverlist] Vote poll failed ({0}): {1}",
                        consecutiveFailures, ex.Message);

                    if (consecutiveFailures >= 3 && timer != null)
                    {
                        int backoffMs = Math.Min(
                            config.VotePollIntervalSeconds * 1000 * (1 << Math.Min(consecutiveFailures - 2, 5)),
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

        private void OnPlayerJoin(IServerPlayer player)
        {
            int pending = queue.PendingFor(player.PlayerName);
            if (pending > 0)
            {
                player.SendMessage(
                    GlobalConstants.GeneralChatGroup,
                    Lang.Get("canvsserverlist:notify-pending-join", pending),
                    EnumChatType.Notification
                );
            }
        }

        private TextCommandResult OnClaimCommand(TextCommandCallingArgs args)
        {
            var player = args.Caller.Player as IServerPlayer;
            if (player == null) return TextCommandResult.Error(Lang.Get("canvsserverlist:error-server-only"));

            int count = queue.Dequeue(player.PlayerName);
            if (count == 0)
            {
                return TextCommandResult.Success(Lang.Get("canvsserverlist:claim-none"));
            }

            for (int i = 0; i < count; i++)
            {
                GiveReward(player);
            }

            return TextCommandResult.Success(Lang.Get("canvsserverlist:claim-success", count));
        }

        private TextCommandResult OnClearRewardsCommand(TextCommandCallingArgs args)
        {
            lock (configLock)
            {
                var fresh = api.LoadModConfig<ModConfig>("canvsserverlist.json") ?? config;
                fresh.Rewards = Array.Empty<RewardItem>();
                api.StoreModConfig(fresh, "canvsserverlist.json");
                config = fresh;
                OnConfigChanged?.Invoke(fresh);
            }
            return TextCommandResult.Success(Lang.Get("canvsserverlist:clear-success"));
        }

        private TextCommandResult OnAddRewardCommand(TextCommandCallingArgs args)
        {
            var player = args.Caller.Player as IServerPlayer;
            if (player == null) return TextCommandResult.Error(Lang.Get("canvsserverlist:error-server-only"));

            var itemStack = player.InventoryManager.ActiveHotbarSlot?.Itemstack;
            if (itemStack == null)
            {
                return TextCommandResult.Error(Lang.Get("canvsserverlist:add-no-item"));
            }

            int quantity = (int)args.Parsers[0].GetValue();
            if (quantity <= 0)
            {
                return TextCommandResult.Error(Lang.Get("canvsserverlist:add-bad-quantity"));
            }

            string? attributesBase64 = null;
            if (itemStack.Attributes?.Count > 0)
            {
                try
                {
                    using (var ms = new MemoryStream())
                    using (var bw = new BinaryWriter(ms))
                    {
                        itemStack.Attributes.ToBytes(bw);
                        byte[] attributeBytes = ms.ToArray();
                        attributesBase64 = Convert.ToBase64String(attributeBytes);
                    }
                }
                catch (Exception ex)
                {
                    api.Logger.Warning("[canvsserverlist] Failed to serialize item attributes: {0}", ex.Message);
                }
            }

            var newReward = new RewardItem
            {
                ItemCode = itemStack.Collectible.Code.ToString(),
                Quantity = quantity,
                Attributes = attributesBase64
            };

            lock (configLock)
            {
                var fresh = api.LoadModConfig<ModConfig>("canvsserverlist.json") ?? config;
                var rewardsList = new List<RewardItem>(fresh.Rewards ?? Array.Empty<RewardItem>());
                rewardsList.Add(newReward);
                fresh.Rewards = rewardsList.ToArray();
                api.StoreModConfig(fresh, "canvsserverlist.json");
                config = fresh;
                OnConfigChanged?.Invoke(fresh);
            }

            return TextCommandResult.Success(
                Lang.Get("canvsserverlist:add-success", quantity, itemStack.Collectible.Code) +
                (attributesBase64 != null ? Lang.Get("canvsserverlist:add-with-attributes") : "")
            );
        }

        public void GiveReward(IServerPlayer player)
        {
            if (config.Rewards == null || config.Rewards.Length == 0) return;

            string playerClass = player.Entity?.WatchedAttributes?.GetString("characterClass") ?? "";
            int givenCount = 0;

            foreach (var reward in config.Rewards)
            {
                if (string.IsNullOrEmpty(reward.ItemCode)) continue;

                // Skip rewards meant for a different class
                if (!string.IsNullOrEmpty(reward.Class) &&
                    !reward.Class.Equals(playerClass, StringComparison.OrdinalIgnoreCase))
                    continue;

                var assetLoc = new AssetLocation(reward.ItemCode);

                // Try as item first, then as block
                CollectibleObject collectible = api.World.GetItem(assetLoc);
                if (collectible == null)
                {
                    collectible = api.World.GetBlock(assetLoc);
                }

                if (collectible == null)
                {
                    api.Logger.Warning("[canvsserverlist] Reward '{0}' not found as item or block.",
                        reward.ItemCode);
                    continue;
                }

                var itemStack = new ItemStack(collectible, reward.Quantity);

                // Apply attributes if present
                if (!string.IsNullOrEmpty(reward.Attributes))
                {
                    try
                    {
                        byte[] attributeBytes = Convert.FromBase64String(reward.Attributes);
                        itemStack.Attributes = Vintagestory.API.Datastructures.TreeAttribute.CreateFromBytes(attributeBytes);
                    }
                    catch (Exception ex)
                    {
                        api.Logger.Warning("[canvsserverlist] Failed to deserialize attributes for reward '{0}': {1}",
                            reward.ItemCode, ex.Message);
                    }
                }

                if (!player.InventoryManager.TryGiveItemstack(itemStack))
                {
                    api.World.SpawnItemEntity(itemStack, player.Entity.Pos.XYZ);
                }

                givenCount++;
            }

            if (givenCount > 0)
            {
                player.SendMessage(
                    GlobalConstants.GeneralChatGroup,
                    Lang.Get("canvsserverlist:reward-thanks"),
                    EnumChatType.Notification
                );
            }
        }
    }
}
