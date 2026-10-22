using System;
using System.Collections.Generic;
using TaleWorlds.Core;
using TaleWorlds.Localization;
using TaleWorlds.MountAndBlade;

namespace CK3Bridge
{
    /// <summary>
    /// The stock generals-and-captains logic, plus heroes[].captainOf (PROTOCOL.md §13.4 item 2): a
    /// hero asked to captain a formation class leads that formation of his team when it has units, whether or not he is
    /// mounted like it. In v1.4.8, GeneralsAndCaptainsAssignmentLogic.AssignBestCaptainsForTeam sorts the team's
    /// heroes (SortCaptainsByPriority), gives the general his bodyguard if there is one, and lets each hero in turn pick from
    /// the remaining non-empty regular formations (PickBestRegularFormationToLead). It runs for AI teams, and for the player's
    /// team only when it deploys without Order of Battle (OnTeamDeployed), so in practice captainOf acts on the AI side; the
    /// player's own Order of Battle choices are never overridden. Without any captainOf it behaves exactly as the stock class.
    /// </summary>
    internal sealed class CK3CaptainsAssignmentLogic : GeneralsAndCaptainsAssignmentLogic
    {
        public CK3CaptainsAssignmentLogic(TextObject attackerGeneralName, TextObject defenderGeneralName)
            : base(attackerGeneralName, defenderGeneralName)
        {
        }

        /// <summary>
        /// Stock order (general first, then by power), then the heroes with a captainOf move ahead of the others, keeping
        /// their order, so they pick their formation before a stronger hero takes it by size.
        /// </summary>
        protected override void SortCaptainsByPriority(Team team, ref List<Agent> captains)
        {
            base.SortCaptainsByPriority(team, ref captains);
            try
            {
                List<Agent> stock = captains;
                Agent general = team.GeneralAgent;
                var sorted = new List<Agent>(stock.Count);
                if (stock.Count > 0 && stock[0] == general) sorted.Add(general);
                foreach (Agent agent in stock)
                    if (agent != general && CK3BuffRegistry.CaptainOf(agent).HasValue) sorted.Add(agent);
                foreach (Agent agent in stock)
                    if (!sorted.Contains(agent)) sorted.Add(agent);
                captains = sorted;
            }
            catch (Exception e)
            {
                CK3BuffRegistry.Report(nameof(SortCaptainsByPriority), e);
            }
        }

        protected override Formation PickBestRegularFormationToLead(Agent agent, List<Formation> candidateFormations)
        {
            try
            {
                FormationClass? wanted = CK3BuffRegistry.CaptainOf(agent);
                if (wanted.HasValue)
                {
                    foreach (Formation formation in candidateFormations)
                    {
                        if (formation.FormationIndex != wanted.Value || formation.CountOfUnits <= 0) continue;
                        BridgeIo.Log($"captain: {agent.Name} leads the {wanted.Value} of the {agent.Team?.Side}");
                        return formation;
                    }
                    BridgeIo.Log($"captain: {agent.Name} wanted the {wanted.Value} of the {agent.Team?.Side}, which is empty or taken; stock choice");
                }
            }
            catch (Exception e)
            {
                CK3BuffRegistry.Report(nameof(PickBestRegularFormationToLead), e);
            }
            return base.PickBestRegularFormationToLead(agent, candidateFormations);
        }
    }
}
