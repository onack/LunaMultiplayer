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
            LunaLog.Log($"Contract accepted: {contract.ContractGuid} - {contract.Title}");

            // Rescue contracts will be handled by the NewVesselCreated event
            if (contract.GetType() == typeof(RecoverAsset))
            {
                LunaLog.Log($"Rescue contract accepted: {contract.ContractGuid} - {contract.Title}");
                LunaLog.Log($"  - Contract state: {contract.ContractState}");
                LunaLog.Log($"  - Contract prestige: {contract.Prestige}");
                
                // Store information about the accepted rescue contract for better vessel detection
                // This could be used to improve rescue vessel detection in the future
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

        #region Vessel Load Event Handling

        /// <summary>
        /// Handle vessels that are loaded when a player joins (existing vessels)
        /// </summary>
        public void OnVesselLoaded(Vessel vessel)
        {
            if (vessel == null || vessel.id == Guid.Empty) return;

            LunaLog.Log($"Vessel loaded: {vessel.vesselName} (ID: {vessel.id})");
            
            // Check if this is a rescue vessel that we haven't processed yet
            if (IsRescueVessel(vessel) && !ProcessedRescueVessels.Contains(vessel.id))
            {
                LunaLog.Log($"Existing rescue vessel detected: {vessel.vesselName} (ID: {vessel.id})");
                ProcessedRescueVessels.Add(vessel.id);
                
                // Sync the rescue vessel immediately since it's already loaded
                SyncRescueVessel(vessel);
            }
        }

        #endregion

        #region New Vessel Handling

        private static readonly HashSet<Guid> ProcessedRescueVessels = new HashSet<Guid>();

        /// <summary>
        /// Handle new vessels created, particularly rescue vessels from contracts
        /// </summary>
        public void NewVesselCreated(Vessel vessel)
        {
            if (vessel == null || vessel.id == Guid.Empty) 
            {
                LunaLog.LogWarning("NewVesselCreated called with null or empty vessel");
                return;
            }

            LunaLog.Log($"New vessel created: {vessel.vesselName} (ID: {vessel.id})");
            LunaLog.Log($"  - Main body: {vessel.mainBody?.name ?? "null"}");
            LunaLog.Log($"  - Situation: {vessel.situation}");
            LunaLog.Log($"  - Crew count: {vessel.GetCrewCount()}");
            LunaLog.Log($"  - Parts count: {vessel.parts.Count}");
            LunaLog.Log($"  - Vessel type: {vessel.vesselType}");
            LunaLog.Log($"  - Mission time: {vessel.missionTime}");
            
            // Log crew details if any
            if (vessel.GetCrewCount() > 0)
            {
                LunaLog.Log($"  - Crew members:");
                foreach (var crew in vessel.GetVesselCrew())
                {
                    LunaLog.Log($"    * {crew.name} ({crew.trait})");
                }
            }

            // Only handle rescue vessels, ignore all other vessels
            if (IsRescueVessel(vessel))
            {
                // Prevent processing the same vessel multiple times
                if (ProcessedRescueVessels.Contains(vessel.id))
                {
                    LunaLog.Log($"Rescue vessel {vessel.vesselName} already processed, skipping");
                    return;
                }
                
                ProcessedRescueVessels.Add(vessel.id);
                LunaLog.Log($"✅ RESCUE VESSEL DETECTED: {vessel.vesselName} ({vessel.id})");
                
                // Add a longer delay to ensure the vessel is fully initialized
                // Rescue vessels can take time to fully spawn and initialize
                CoroutineUtil.StartDelayedRoutine("DelayedRescueVesselSync", () => SyncRescueVessel(vessel), 2.0f);
                
                // Also try again after a longer delay in case the first attempt fails
                CoroutineUtil.StartDelayedRoutine("DelayedRescueVesselSyncRetry", () => SyncRescueVessel(vessel), 5.0f);
            }
            else
            {
                LunaLog.Log($"❌ Not a rescue vessel: {vessel.vesselName} - does not meet rescue vessel criteria");
            }
        }

        /// <summary>
        /// Sync rescue vessel and its kerbals after a delay
        /// </summary>
        private void SyncRescueVessel(Vessel vessel)
        {
            if (vessel == null || vessel.id == Guid.Empty) 
            {
                LunaLog.LogError("Cannot sync rescue vessel: vessel is null or has empty ID");
                return;
            }
            
            try
            {
                LunaLog.Log($"Starting rescue vessel sync for: {vessel.vesselName} (ID: {vessel.id})");
                
                // Ensure the vessel is still valid
                if (vessel.state == Vessel.State.DEAD)
                {
                    LunaLog.LogWarning($"Vessel {vessel.vesselName} is dead, skipping sync");
                    return;
                }
                
                // Send the vessel to all players
                LunaLog.Log($"Sending vessel message for: {vessel.vesselName}");
                VesselProtoSystem.Singleton.MessageSender.SendVesselMessage(vessel, true);
                
                // Sync the kerbals on the rescue vessel
                var crew = vessel.GetVesselCrew();
                LunaLog.Log($"Syncing {crew.Count} crew members from rescue vessel");
                
                foreach (var kerbal in crew)
                {
                    if (kerbal != null)
                    {
                        LunaLog.Log($"Syncing rescue kerbal: {kerbal.name}");
                        KerbalSystem.Singleton.MessageSender.SendKerbal(kerbal);
                    }
                    else
                    {
                        LunaLog.LogWarning("Found null kerbal in rescue vessel crew");
                    }
                }
                
                LunaLog.Log($"Successfully synced rescue vessel: {vessel.vesselName}");
            }
            catch (Exception e)
            {
                LunaLog.LogError($"Error syncing rescue vessel {vessel.vesselName}: {e}");
            }
        }

        /// <summary>
        /// Check if there are any active rescue contracts that might be related to this vessel
        /// </summary>
        private bool HasActiveRescueContracts()
        {
            if (ContractSystem.Instance?.Contracts == null) return false;
            
            foreach (var contract in ContractSystem.Instance.Contracts)
            {
                if (contract.GetType() == typeof(RecoverAsset) && 
                    contract.ContractState == Contract.State.Active)
                {
                    LunaLog.Log($"Found active rescue contract: {contract.ContractGuid} - {contract.Title}");
                    return true;
                }
            }
            
            return false;
        }

        /// <summary>
        /// Determine if a vessel is likely a rescue vessel
        /// </summary>
        private bool IsRescueVessel(Vessel vessel)
        {
            if (vessel == null) return false;
            
            LunaLog.Log($"🔍 Checking if vessel is rescue vessel: {vessel.vesselName} (ID: {vessel.id})");
            LunaLog.Log($"  - Main body: {vessel.mainBody?.name}");
            LunaLog.Log($"  - Situation: {vessel.situation}");
            LunaLog.Log($"  - Crew count: {vessel.GetCrewCount()}");
            LunaLog.Log($"  - Parts count: {vessel.parts.Count}");
            
            // Rescue vessels can be in various situations, not just landed
            // They can be in orbit, sub-orbital, etc.
            bool isRescueVessel = false;
            string detectionReason = "";
            
            // Check vessel naming patterns
            var vesselNameLower = vessel.vesselName.ToLowerInvariant();
            if (vesselNameLower.Contains("recover") || vesselNameLower.Contains("lost") || 
                vesselNameLower.Contains("stranded") || vesselNameLower.Contains("rescue") ||
                vesselNameLower.Contains("derelict"))
            {
                isRescueVessel = true;
                detectionReason = "vessel name contains rescue-related terms";
            }
            
            // Check for very small vessels with crew
            if (!isRescueVessel && vessel.parts.Count <= 3 && vessel.GetCrewCount() == 1)
            {
                isRescueVessel = true;
                detectionReason = "very small vessel (≤3 parts) with crew";
            }
            
            // Check if there are active rescue contracts
            if (!isRescueVessel && HasActiveRescueContracts())
            {
                isRescueVessel = true;
                detectionReason = "active rescue contracts detected";
            }
            
            LunaLog.Log($"  - Detection result: {(isRescueVessel ? "✅ RESCUE VESSEL" : "❌ NOT RESCUE VESSEL")}");
            if (isRescueVessel)
            {
                LunaLog.Log($"  - Detection reason: {detectionReason}");
            }
            
            return isRescueVessel;
        }

        #endregion

    }
}
