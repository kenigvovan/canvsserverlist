using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Server;

namespace canvsserverlist.src
{
    public class canvsserverlist : ModSystem
    {
        private ICoreServerAPI? serverApi;
        private ModConfig? config;
        private ApiClient? apiClient;
        private HeartbeatSystem? heartbeat;
        private VoteRewardSystem? voteRewards;
        private readonly object configLock = new object();

        public override bool ShouldLoad(EnumAppSide forSide) => forSide == EnumAppSide.Server;

        public override void StartServerSide(ICoreServerAPI api)
        {
            config = api.LoadModConfig<ModConfig>("canvsserverlist.json");
            if (config == null)
            {
                config = new ModConfig();
                api.StoreModConfig(config, "canvsserverlist.json");
                Mod.Logger.Notification("[canvsserverlist] Default config created. Set ServerUuid and ApiKey to activate.");
            }

            if (string.IsNullOrEmpty(config.ServerUuid) || string.IsNullOrEmpty(config.ApiKey))
            {
                Mod.Logger.Warning("[canvsserverlist] ServerUuid or ApiKey not configured. Mod inactive.");
                return;
            }

            this.serverApi = api;

            apiClient = new ApiClient(config);
            heartbeat = new HeartbeatSystem(api, config, apiClient);
            voteRewards = new VoteRewardSystem(api, config, apiClient, configLock);
            voteRewards.OnConfigChanged = newConfig =>
            {
                this.config = newConfig;
                apiClient?.Reconfigure(newConfig);
                heartbeat?.Reconfigure(newConfig);
            };

            heartbeat.Start();
            voteRewards.Start();

            // Server command: /vslist
            api.ChatCommands.Create("vslist")
                .WithDescription(Lang.Get("canvsserverlist:cmd-vslist-desc"))
                .RequiresPrivilege(Privilege.controlserver)
                .BeginSubCommand("status")
                    .WithDescription(Lang.Get("canvsserverlist:cmd-status-desc"))
                    .HandleWith((args) =>
                    {
                        int pending = voteRewards.Queue.PendingCount;
                        var rewardsList = config.Rewards != null && config.Rewards.Length > 0
                            ? string.Join(", ", System.Array.ConvertAll(config.Rewards, r => $"{r.ItemCode} x{r.Quantity}"))
                            : Lang.Get("canvsserverlist:status-rewards-none");
                        return TextCommandResult.Success(
                            Lang.Get("canvsserverlist:status",
                                config.ServerUuid,
                                config.HeartbeatIntervalSeconds,
                                config.VotePollIntervalSeconds,
                                rewardsList,
                                pending)
                        );
                    })
                .EndSubCommand()
                .BeginSubCommand("reload")
                    .WithDescription(Lang.Get("canvsserverlist:cmd-reload-desc"))
                    .HandleWith((args) =>
                    {
                        lock (configLock)
                        {
                            var newConfig = serverApi!.LoadModConfig<ModConfig>("canvsserverlist.json");
                            if (newConfig == null)
                                return TextCommandResult.Error(Lang.Get("canvsserverlist:reload-fail"));

                            if (string.IsNullOrEmpty(newConfig.ServerUuid) || string.IsNullOrEmpty(newConfig.ApiKey))
                                return TextCommandResult.Error(Lang.Get("canvsserverlist:reload-empty-credentials"));

                            var changes = new System.Collections.Generic.List<string>();
                            if (config!.HeartbeatIntervalSeconds != newConfig.HeartbeatIntervalSeconds)
                                changes.Add(Lang.Get("canvsserverlist:reload-heartbeat", config.HeartbeatIntervalSeconds, newConfig.HeartbeatIntervalSeconds));
                            if (config.VotePollIntervalSeconds != newConfig.VotePollIntervalSeconds)
                                changes.Add(Lang.Get("canvsserverlist:reload-votepoll", config.VotePollIntervalSeconds, newConfig.VotePollIntervalSeconds));

                            int oldRewards = config.Rewards?.Length ?? 0;
                            int newRewards = newConfig.Rewards?.Length ?? 0;
                            if (oldRewards != newRewards)
                                changes.Add(Lang.Get("canvsserverlist:reload-rewards", oldRewards, newRewards));

                            this.config = newConfig;
                            apiClient?.Reconfigure(newConfig);
                            heartbeat?.Reconfigure(newConfig);
                            voteRewards?.Reconfigure(newConfig);

                            string summary = changes.Count > 0
                                ? string.Join(", ", changes)
                                : Lang.Get("canvsserverlist:reload-no-changes");

                            return TextCommandResult.Success(Lang.Get("canvsserverlist:reload-success", summary));
                        }
                    })
                .EndSubCommand()
                .BeginSubCommand("testreward")
                    .WithDescription(Lang.Get("canvsserverlist:cmd-testreward-desc"))
                    .WithArgs(api.ChatCommands.Parsers.Word("playerName"))
                    .HandleWith((args) =>
                    {
                        string targetName = (string)args.Parsers[0].GetValue();
                        var target = api.World.AllOnlinePlayers
                            .FirstOrDefault(p => p.PlayerName.Equals(targetName, System.StringComparison.OrdinalIgnoreCase))
                            as IServerPlayer;

                        if (target == null)
                            return TextCommandResult.Error(Lang.Get("canvsserverlist:testreward-not-found", targetName));

                        voteRewards!.GiveReward(target);
                        return TextCommandResult.Success(Lang.Get("canvsserverlist:testreward-success", target.PlayerName));
                    })
                .EndSubCommand()
                .BeginSubCommand("simulatevote")
                    .WithDescription(Lang.Get("canvsserverlist:cmd-simulatevote-desc"))
                    .WithArgs(api.ChatCommands.Parsers.Word("playerName"))
                    .HandleWith((args) =>
                    {
                        string targetName = (string)args.Parsers[0].GetValue();
                        voteRewards!.Queue.Enqueue(targetName);

                        int pending = voteRewards.Queue.PendingFor(targetName);
                        var online = api.World.AllOnlinePlayers
                            .FirstOrDefault(p => p.PlayerName.Equals(targetName, System.StringComparison.OrdinalIgnoreCase))
                            as IServerPlayer;

                        if (online != null)
                        {
                            online.SendMessage(
                                Vintagestory.API.Config.GlobalConstants.GeneralChatGroup,
                                Lang.Get("canvsserverlist:notify-pending", pending),
                                EnumChatType.Notification
                            );
                        }

                        return TextCommandResult.Success(
                            Lang.Get("canvsserverlist:simulatevote-success", targetName, pending)
                        );
                    })
                .EndSubCommand()
                .BeginSubCommand("votestats")
                    .WithDescription(Lang.Get("canvsserverlist:cmd-votestats-desc"))
                    .HandleWith((args) =>
                    {
                        var pending = voteRewards!.Queue.GetAllPending()
                            .OrderByDescending(kv => kv.Value)
                            .ToList();
                        var claimed = voteRewards!.Queue.GetClaimStats()
                            .OrderByDescending(kv => kv.Value)
                            .ToList();

                        string pendingStr = pending.Count > 0
                            ? string.Join(", ", pending.Select(kv => $"{kv.Key}: {kv.Value}"))
                            : Lang.Get("canvsserverlist:votestats-none");

                        string claimedStr = claimed.Count > 0
                            ? string.Join(", ", claimed.Select(kv => $"{kv.Key}: {kv.Value}"))
                            : Lang.Get("canvsserverlist:votestats-none");

                        return TextCommandResult.Success(
                            Lang.Get("canvsserverlist:votestats-all", pendingStr, claimedStr)
                        );
                    })
                .EndSubCommand();

            Mod.Logger.Notification("[canvsserverlist] Active for server {0}", config.ServerUuid);
        }

        public override void Dispose()
        {
            heartbeat?.Dispose();
            voteRewards?.Dispose();
            apiClient?.Dispose();
        }
    }
}
