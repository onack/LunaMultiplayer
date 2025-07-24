using System;
using System.Collections.Generic;
using Contracts;
using Contracts.Templates;
using LmpClient.Base;
using LmpClient.Systems.Lock;
using LmpClient.Systems.SettingsSys;
using LmpClient.Systems.VesselProtoSys;
using LmpClient.Systems.KerbalSys;
using LmpClient.Utilities;
using LmpCommon.Locks;

namespace LmpClient.Systems.ShareContracts
{
    public class ShareContractsEvents : SubSystem<ShareContractsSystem>
    {
        /// <summary>
        /// If we get the contract lock then generate contracts
        /// </summary>
        public void LockAcquire(LockDefinition lockDefinition)
        {
            if (lockDefinition.Type == LockType.Contract && lockDefinition.PlayerName == SettingsSystem.CurrentSettings.PlayerName)
            {
                ContractSystem.generateContractIterations = ShareContractsSystem.Singleton.DefaultContractGenerateIterations;
            }
        }

        /// <summary>
        /// Try to get contract lock
        /// </summary>
        public void LockReleased(LockDefinition lockDefinition)
        {
            if (lockDefinition.Type == LockType.Contract)
            {
                System.TryGetContractLock();
            }
        }

        /// <summary>
        /// Try to get contract lock when loading a level
        /// </summary>
        public void LevelLoaded(GameScenes data)
        {
            System.TryGetContractLock();
        }

        #region EventHandlers

        public void ContractAccepted(Contract contract)
        {
            if (System.IgnoreEvents) return;

            System.MessageSender.SendContractMessage(contract);
            LunaLog.Log($"Contract accepted: {contract.ContractGuid}");

            // Rescue contracts will be handled by the NewVesselCreated event
            if (contract.GetType() == typeof(RecoverAsset))
            {
                LunaLog.Log($"Rescue contract accepted: {contract.ContractGuid} - {contract.Title}");
            }
        }

        public void ContractCancelled(Contract contract)
        {
            if (System.IgnoreEvents) return;

            System.MessageSender.SendContractMessage(contract);
            LunaLog.Log($"Contract cancelled: {contract.ContractGuid}");
        }

        public void ContractCompleted(Contract contract)
        {
            if (System.IgnoreEvents) return;

            System.MessageSender.SendContractMessage(contract);
            LunaLog.Log($"Contract completed: {contract.ContractGuid}");
        }

        public void ContractsListChanged()
        {
            LunaLog.Log("Contract list changed.");
        }

        public void ContractsLoaded()
        {
            LunaLog.Log("Contracts loaded.");
        }

        public void ContractDeclined(Contract contract)
        {
            if (System.IgnoreEvents) return;

            System.MessageSender.SendContractMessage(contract);
            LunaLog.Log($"Contract declined: {contract.ContractGuid}");
        }

        public void ContractFailed(Contract contract)
        {
            if (System.IgnoreEvents) return;

            System.MessageSender.SendContractMessage(contract);
            LunaLog.Log($"Contract failed: {contract.ContractGuid}");
        }

        public void ContractFinished(Contract contract)
        {
            /*
            Doesn't need to be synchronized because there is no ContractFinished state.
            Also the contract will be finished on the contract complete / failed / cancelled / ...
            */
        }

        public void ContractOffered(Contract contract)
        {
            if (!LockSystem.LockQuery.ContractLockBelongsToPlayer(SettingsSystem.CurrentSettings.PlayerName))
            {
                //We don't have the contract lock so remove the contract that we spawned.
                //The idea is that ONLY THE PLAYER with the contract lock spawn contracts to the other players
                contract.Withdraw();
                contract.Kill();
                return;
            }

            LunaLog.Log($"Contract offered: {contract.ContractGuid} - {contract.Title}");

            //This should be only called on the client with the contract lock, because it has the generationCount != 0.
            System.MessageSender.SendContractMessage(contract);
        }

        public void ContractParameterChanged(Contract contract, ContractParameter contractParameter)
        {
            //Do not send contract parameter changes as other players might override them
            //See: https://github.com/LunaMultiplayer/LunaMultiplayer/issues/186

            //TODO: Perhaps we can send only when the parameters are complete?
            //if (contractParameter.State == ParameterState.Complete)
            //    System.MessageSender.SendContractMessage(contract);

            LunaLog.Log($"Contract parameter changed on:{contract.ContractGuid}");
        }

        public void ContractRead(Contract contract)
        {
            LunaLog.Log($"Contract read:{contract.ContractGuid}");
        }

        public void ContractSeen(Contract contract)
        {
            LunaLog.Log($"Contract seen:{contract.ContractGuid}");
        }

        #endregion

        #region New Vessel Handling

        private static readonly HashSet<Guid> ProcessedRescueVessels = new HashSet<Guid>();

        /// <summary>
        /// Handle new vessels created, particularly rescue vessels from contracts
        /// </summary>
        public void NewVesselCreated(Vessel vessel)
        {
            if (vessel == null || vessel.id == Guid.Empty) return;

            // Only handle rescue vessels, ignore all other vessels
            if (IsRescueVessel(vessel))
            {
                // Prevent processing the same vessel multiple times
                if (ProcessedRescueVessels.Contains(vessel.id))
                {
                    return;
                }
                
                ProcessedRescueVessels.Add(vessel.id);
                LunaLog.Log($"Rescue vessel created: {vessel.vesselName} ({vessel.id})");
                
                // Add a small delay to ensure the vessel is fully initialized
                CoroutineUtil.StartDelayedRoutine("DelayedRescueVesselSync", () => SyncRescueVessel(vessel), 0.5f);
            }
        }

        /// <summary>
        /// Sync rescue vessel and its kerbals after a delay
        /// </summary>
        private void SyncRescueVessel(Vessel vessel)
        {
            if (vessel == null || vessel.id == Guid.Empty) return;
            
            try
            {
                // Send the vessel to all players
                VesselProtoSystem.Singleton.MessageSender.SendVesselMessage(vessel, true);
                
                // Sync the kerbals on the rescue vessel
                var crew = vessel.GetVesselCrew();
                foreach (var kerbal in crew)
                {
                    LunaLog.Log($"Syncing rescue kerbal: {kerbal.name}");
                    KerbalSystem.Singleton.MessageSender.SendKerbal(kerbal);
                }
            }
            catch (Exception e)
            {
                LunaLog.LogError($"Error syncing rescue vessel {vessel.vesselName}: {e}");
            }
        }

        /// <summary>
        /// Determine if a vessel is likely a rescue vessel
        /// </summary>
        private bool IsRescueVessel(Vessel vessel)
        {
            // Rescue vessels are typically:
            // - Landed on Kerbin
            // - Have crew
            // - Have specific naming patterns
            // - Are small vessels (rescue missions are usually simple)
            
            if (vessel.mainBody != FlightGlobals.GetHomeBody()) return false;
            if (vessel.situation != Vessel.Situations.LANDED) return false;
            if (vessel.GetCrewCount() == 0) return false;
            
            // Check for rescue-related naming patterns - be more specific
            var vesselName = vessel.vesselName.ToLower();
            if (vesselName.Contains("rescue") || 
                vesselName.Contains("stranded") || 
                vesselName.Contains("kerbal") ||
                vesselName.Contains("escape") ||
                vesselName.Contains("survival"))
            {
                return true;
            }
            
            // Additional check: if it's a small vessel with exactly 1 crew member, it might be a rescue vessel
            if (vessel.GetCrewCount() == 1 && vessel.parts.Count <= 5)
            {
                return true;
            }
            
            return false;
        }

        #endregion

    }
}
