using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Server;

namespace canvsserverlist
{
    public class VoteRewardSystem : IDisposable
    {
        private readonly ICoreServerAPI api;
        private readonly ModConfig config;
        private readonly ApiClient client;
        private RewardQueue queue = null!;
        private Timer? timer;
        private int consecutiveFailures;

        public VoteRewardSystem(ICoreServerAPI api, ModConfig config, ApiClient client)
        {
            this.api = api;
            this.config = config;
            this.client = client;
        }

        public void Start()
        {
            queue = new RewardQueue(api);

            // Notify player on join if they have pending rewards
            api.Event.PlayerJoin += OnPlayerJoin;

            // Register /voteclaim command for players
            api.ChatCommands.Create("voteclaim").RequiresPrivilege(Privilege.chat)
                .WithDescription("Claim your vote rewards")
                .HandleWith(OnClaimCommand);

            int intervalMs = config.VotePollIntervalSeconds * 1000;
            timer = new Timer(_ => PollVotes(), null, 5000, intervalMs);
        }

        public void Dispose()
        {
            api.Event.PlayerJoin -= OnPlayerJoin;
            timer?.Dispose();
        }

        public RewardQueue Queue => queue;

        private void PollVotes()
        {
            Task.Run(async () =>
            {
                try
                {
                    var votes = await client.GetPendingVotes();
                    if (votes.Count == 0) return;

                    // Build lookup of online players
                    var onlinePlayers = api.World.AllOnlinePlayers
                        .ToDictionary(p => p.PlayerName.ToLowerInvariant(), p => (IServerPlayer)p);

                    // Phase 1: queue ALL votes (both online and offline) to persistent storage.
                    // This guarantees no reward is lost even if the server crashes after ack.
                    foreach (var vote in votes)
                    {
                        queue.Enqueue(vote.IngameNickname);
                    }

                    // Phase 2: ack to backend — safe now because rewards are persisted locally.
                    var voteIds = votes.Select(v => v.Id).ToList();
                    bool acked = await client.AckVotes(voteIds);
                    if (!acked)
                    {
                        // Ack failed — votes will come again next poll.
                        // Duplicates in queue are harmless (player gets extra reward at worst).
                        api.Logger.Warning("[canvsserverlist] Vote ack failed, will retry next poll.");
                    }

                    // Phase 3: notify online players that they have rewards to claim.
                    foreach (var vote in votes)
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
                                        $"You have {pending} vote reward(s)! Type /voteclaim to collect.",
                                        EnumChatType.Notification
                                    );
                                }, "canvsserverlist_notify");
                            }
                        }
                    }

                    consecutiveFailures = 0;
                    if (timer != null)
                    {
                        int intervalMs = config.VotePollIntervalSeconds * 1000;
                        timer.Change(intervalMs, intervalMs);
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
            });
        }

        private void OnPlayerJoin(IServerPlayer player)
        {
            int pending = queue.PendingFor(player.PlayerName);
            if (pending > 0)
            {
                player.SendMessage(
                    GlobalConstants.GeneralChatGroup,
                    $"You have {pending} vote reward(s) waiting! Type /voteclaim to collect.",
                    EnumChatType.Notification
                );
            }
        }

        private TextCommandResult OnClaimCommand(TextCommandCallingArgs args)
        {
            var player = args.Caller.Player as IServerPlayer;
            if (player == null) return TextCommandResult.Error("Server-side only.");

            int count = queue.Dequeue(player.PlayerName);
            if (count == 0)
            {
                return TextCommandResult.Success("You have no vote rewards to claim.");
            }

            for (int i = 0; i < count; i++)
            {
                GiveReward(player);
            }

            return TextCommandResult.Success($"Claimed {count} vote reward(s)! Thank you for voting!");
        }

        private void GiveReward(IServerPlayer player)
        {
            if (config.Rewards == null || config.Rewards.Length == 0) return;

            foreach (var reward in config.Rewards)
            {
                if (string.IsNullOrEmpty(reward.ItemCode)) continue;

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
                if (!player.InventoryManager.TryGiveItemstack(itemStack))
                {
                    api.World.SpawnItemEntity(itemStack, player.Entity.Pos.XYZ);
                }
            }

            player.SendMessage(
                GlobalConstants.GeneralChatGroup,
                "Thank you for voting! Here's your reward.",
                EnumChatType.Notification
            );
        }
    }
}
