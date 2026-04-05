using System;

namespace canvsserverlist
{
    public class ModConfig
    {
        public string ServerUuid { get; set; } = "";
        public string ApiKey { get; set; } = "";
        public string ApiUrl { get; set; } = "https://vsserverlist.com";
        public int HeartbeatIntervalSeconds { get; set; } = 600;
        public int VotePollIntervalSeconds { get; set; } = 60;
        public RewardItem[] Rewards { get; set; } = new[] { new RewardItem { ItemCode = "game:gear-rusty", Quantity = 1 } };
    }

    public class RewardItem
    {
        public string ItemCode { get; set; } = "";
        public int Quantity { get; set; } = 1;
    }
}
