using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.UI;

namespace BOCCHI.MobFarmer.Services;

/// <summary>
///     Occult Crescent EXP chain eligibility. A kill counts toward the chain when the mob's
///     Knowledge level is at least the party's level plus a lead that grows with party size
///     (Patch 7.25 notes: solo +2, 2 → +3, 3–4 → +4–5, 5–6 → +6–7, 7–8 → +7–8).
///     Balanced parties use their averaged level; we approximate with the local player's
///     effective (synced) Knowledge, which matches once Knowledge Level Sync is on.
/// </summary>
internal static class ChainBonus
{
    private const uint ContentValueEffectiveKnowledge = 6;

    private const uint ContentValueCurrentKnowledge = 7;

    private static readonly int[] LeadByPartySize = [2, 2, 3, 4, 5, 6, 7, 7, 8];

    public static int RequiredLevelLead(int partySize) =>
        LeadByPartySize[Math.Clamp(partySize, 0, LeadByPartySize.Length - 1)];

    /// <summary>Lowest mob Knowledge level that chains right now, or null when our level is unknown.</summary>
    public static int? MinimumChainLevel(IObjectTable objects, IPartyList party)
    {
        int? level = GetPlayerKnowledgeLevel(objects);
        return level is { } l ? l + RequiredLevelLead(party.Length) : null;
    }

    public static unsafe bool IsEligible(IBattleNpc mob, int minimumChainLevel)
    {
        // Level 0 = foray info unavailable; can't tell, so don't prefer it.
        byte level = ((BattleChara*)mob.Address)->ForayInfo.Level;
        return level > 0 && level >= minimumChainLevel;
    }

    private static unsafe int? GetPlayerKnowledgeLevel(IObjectTable objects)
    {
        PlayerState* playerState = PlayerState.Instance();
        if (playerState != null)
        {
            uint effective = playerState->GetContentValue(ContentValueEffectiveKnowledge);
            if (effective > 0)
            {
                return (int)effective;
            }

            uint current = playerState->GetContentValue(ContentValueCurrentKnowledge);
            if (current > 0)
            {
                return (int)current;
            }
        }

        if (objects.LocalPlayer is not { } local)
        {
            return null;
        }

        byte foray = ((BattleChara*)local.Address)->ForayInfo.Level;
        return foray > 0 ? foray : null;
    }
}
