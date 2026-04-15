using System.Linq;
using Vintagestory.API.Common;
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
            voteRewards = new VoteRewardSystem(api, config, apiClient);

            heartbeat.Start();
            voteRewards.Start();

            // Server command: /vslist status
            api.ChatCommands.Create("vslist")
                .WithDescription("VSServerList mod commands")
                .RequiresPrivilege(Privilege.controlserver)
                .BeginSubCommand("status")
                    .WithDescription("Show mod status")
                    .HandleWith((args) =>
                    {
                        int pending = voteRewards.Queue.PendingCount;
                        var rewardsList = config.Rewards != null && config.Rewards.Length > 0
                            ? string.Join(", ", System.Array.ConvertAll(config.Rewards, r => $"{r.ItemCode} x{r.Quantity}"))
                            : "none";
                        return TextCommandResult.Success(
                            $"[canvsserverlist] Active. Server: {config.ServerUuid}\n" +
                            $"Heartbeat interval: {config.HeartbeatIntervalSeconds}s\n" +
                            $"Vote poll interval: {config.VotePollIntervalSeconds}s\n" +
                            $"Rewards: {rewardsList}\n" +
                            $"Pending offline rewards: {pending}"
                        );
                    })
                .EndSubCommand()
                .BeginSubCommand("reload")
                    .WithDescription("Reload config from disk")
                    .HandleWith((args) =>
                    {
                        var newConfig = serverApi!.LoadModConfig<ModConfig>("canvsserverlist.json");
                        if (newConfig == null)
                            return TextCommandResult.Error("Failed to load config file.");

                        if (string.IsNullOrEmpty(newConfig.ServerUuid) || string.IsNullOrEmpty(newConfig.ApiKey))
                            return TextCommandResult.Error("ServerUuid or ApiKey is empty in config. Reload aborted.");

                        var changes = new System.Collections.Generic.List<string>();
                        if (config!.HeartbeatIntervalSeconds != newConfig.HeartbeatIntervalSeconds)
                            changes.Add($"Heartbeat: {config.HeartbeatIntervalSeconds}s -> {newConfig.HeartbeatIntervalSeconds}s");
                        if (config.VotePollIntervalSeconds != newConfig.VotePollIntervalSeconds)
                            changes.Add($"Vote poll: {config.VotePollIntervalSeconds}s -> {newConfig.VotePollIntervalSeconds}s");

                        int oldRewards = config.Rewards?.Length ?? 0;
                        int newRewards = newConfig.Rewards?.Length ?? 0;
                        if (oldRewards != newRewards)
                            changes.Add($"Rewards: {oldRewards} -> {newRewards} items");

                        this.config = newConfig;
                        apiClient?.Reconfigure(newConfig);
                        heartbeat?.Reconfigure(newConfig);
                        voteRewards?.Reconfigure(newConfig);

                        string summary = changes.Count > 0
                            ? string.Join(", ", changes)
                            : "No changes detected";

                        return TextCommandResult.Success($"[canvsserverlist] Config reloaded. {summary}");
                    })
                .EndSubCommand()
                .BeginSubCommand("testreward")
                    .WithDescription("Give a test reward to a player")
                    .WithArgs(api.ChatCommands.Parsers.Word("playerName"))
                    .HandleWith((args) =>
                    {
                        string targetName = (string)args.Parsers[0].GetValue();
                        var target = api.World.AllOnlinePlayers
                            .FirstOrDefault(p => p.PlayerName.Equals(targetName, System.StringComparison.OrdinalIgnoreCase))
                            as IServerPlayer;

                        if (target == null)
                            return TextCommandResult.Error($"Player '{targetName}' not found online.");

                        voteRewards!.GiveReward(target);
                        return TextCommandResult.Success($"Test reward given to {target.PlayerName}.");
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
