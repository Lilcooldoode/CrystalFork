using Shared;

public class AssassinAI : BaseAI
{
    public AssassinAI(GameClient client) : base(client) { }

    protected override int GetItemScore(UserItem item, EquipmentSlot slot)
    {
        if (item.Info == null) return 0;

        bool offensive = IsOffensiveSlot(slot);

        if (offensive)
        {
            return item.Info.Stats[Stat.MinDC] + item.Info.Stats[Stat.MaxDC]
                 + item.AddedStats[Stat.MinDC] + item.AddedStats[Stat.MaxDC]
                 + item.Info.Stats[Stat.AttackSpeed] + item.AddedStats[Stat.AttackSpeed];
        }

        return item.Info.Stats[Stat.MinAC] + item.Info.Stats[Stat.MaxAC]
             + item.AddedStats[Stat.MinAC] + item.AddedStats[Stat.MaxAC];
    }
}
