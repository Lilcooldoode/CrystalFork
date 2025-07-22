using Shared;

public sealed class ArcherAI : BaseAI
{
    public ArcherAI(GameClient client) : base(client) { }

    protected override int GetItemScore(UserItem item, EquipmentSlot slot)
    {
        if (item.Info == null) return 0;

        bool offensive = IsOffensiveSlot(slot);

        if (offensive)
        {
            return item.Info.Stats[Stat.MinMC] + item.Info.Stats[Stat.MaxMC]
                 + item.AddedStats[Stat.MinMC] + item.AddedStats[Stat.MaxMC];
        }

        return item.Info.Stats[Stat.MinMAC] + item.Info.Stats[Stat.MaxMAC]
             + item.AddedStats[Stat.MinMAC] + item.AddedStats[Stat.MaxMAC];
    }
}
