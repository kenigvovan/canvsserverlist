using System;

namespace canvsserverlist.src
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

        /// <summary>
        /// Item attributes serialized as base64 string (e.g., durability, enchantments, etc.)
        /// Can be null if no attributes are needed
        /// </summary>
        public string? Attributes { get; set; }

        /// <summary>
        /// Character class filter. If set, this reward is only given to players of this class.
        /// If null, the reward is given to everyone.
        /// </summary>
        public string? Class { get; set; }
    }
}
