using Vintagestory.API.Common;
using Vintagestory.API.Server;

namespace canvsserverlist
{
    public class canvsserverlist : ModSystem
    {
        private ApiClient? apiClient;
        private HeartbeatSystem? heartbeat;
        private VoteRewardSystem? voteRewards;

        public override bool ShouldLoad(EnumAppSide forSide) => forSide == EnumAppSide.Server;

        public override void StartServerSide(ICoreServerAPI api)
        {
            var config = api.LoadModConfig<ModConfig>("canvsserverlist.json");
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
