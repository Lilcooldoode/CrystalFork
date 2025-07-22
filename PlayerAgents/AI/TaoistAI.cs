using Shared;

public sealed class TaoistAI : BaseAI
{
    public TaoistAI(GameClient client) : base(client) { }

    protected override int GetItemScore(UserItem item, EquipmentSlot slot)
    {
        if (item.Info == null) return 0;

        bool offensive = IsOffensiveSlot(slot);

        if (offensive)
        {
            return item.Info.Stats[Stat.MinSC] + item.Info.Stats[Stat.MaxSC]
                 + item.AddedStats[Stat.MinSC] + item.AddedStats[Stat.MaxSC];
        }

        return item.Info.Stats[Stat.MinMAC] + item.Info.Stats[Stat.MaxMAC]
             + item.AddedStats[Stat.MinMAC] + item.AddedStats[Stat.MaxMAC];
    }
}
