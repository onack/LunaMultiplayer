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
using System.Linq;
using Contracts.Parameters;

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

            // Handle rescue contracts - check for recently created vessels that might be rescue vessels
            if (contract.GetType() == typeof(RecoverAsset))
            {
                LunaLog.Log($"Rescue contract accepted: {contract.ContractGuid} - {contract.Title}");
                
                // Check for recently created vessels that might be rescue vessels
                CheckForRescueVesselsAfterContractAccept(contract);
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
            if (System.IgnoreEvents) return;
            HandleContractParameterChange(contract, contractParameter);
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

        #region Contract Parameter Handling

        // Static array of permanent parameters that should be synced
        private static readonly string[] PermanentParameters = new[]
        {
            // Stock parameters
            "RecoverKerbal",
            "RecoverPart",
            "AcquirePart",
            "PartTest",
            "LandOnBody",
            "CollectScience",
            "PlantFlag",
            "AcquireCrew",
            "EnterSOI",
            "EnterOrbit",
            // Contract configurator parameters
            "ReturnHome",
            "Docking",
            "Rendezvous",
            "RecoverVessel",
            "VisitWaypoint",
            "PerformOrbitalSurvey",
            "SCANsatCoverage",
            "TargetDestroyed",
            "VesselDestroyed",
            "ReachSpace",
        };

        /// <summary>
        /// Handle contract parameter changes and determine if they should be synced
        /// </summary>
        private void HandleContractParameterChange(Contract contract, ContractParameter contractParameter)
        {
            // Log the parameter type for analysis
            var parameterType = contractParameter.GetType().Name;
            LunaLog.Log($"Contract parameter type: {parameterType} - Title: {contractParameter.Title} - State: {contractParameter.State}");

            // Only sync parameters that are permanently completed, not temporary ones
            if (contractParameter.State == ParameterState.Complete && IsPermanentParameter(contractParameter))
            {
                LunaLog.Log($"Permanent contract parameter completed: {contract.ContractGuid} - {contractParameter.Title}");
                System.MessageSender.SendContractMessage(contract);
            }
            else
            {
                LunaLog.Log($"Contract parameter changed (not syncing): {contract.ContractGuid} - {contractParameter.Title} - State: {contractParameter.State}");
            }
        }

        /// <summary>
        /// Check if a contract parameter is permanent (not temporary like flight height/speed)
        /// </summary>
        private bool IsPermanentParameter(ContractParameter parameter)
        {
            if (parameter == null) return false;

            var parameterType = parameter.GetType().Name;
            
            // Check if this is a permanent parameter
            foreach (var permParam in PermanentParameters)
            {
                if (parameterType.Contains(permParam))
                {
                    LunaLog.Log($"Parameter {parameterType} identified as permanent");
                    return true;
                }
            }

            // For unknown parameters, be conservative and don't sync
            LunaLog.Log($"Unknown parameter type {parameterType} - not syncing");
            return false;
        }

        #endregion

        #region Rescue Mission Handling

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

            // Add a small delay to allow for contract acceptance timing
            // This helps with the case where the vessel is created before the contract is accepted
            CoroutineUtil.StartDelayedRoutine("DelayedRescueVesselCheck", () => CheckVesselForRescueContracts(vessel), 0.5f);
        }

        /// <summary>
        /// Check if a vessel is a rescue vessel after a delay
        /// This allows for timing issues where the contract is accepted after the vessel is created
        /// </summary>
        private void CheckVesselForRescueContracts(Vessel vessel)
        {
            if (vessel == null || vessel.id == Guid.Empty) return;

            // Only handle rescue vessels, ignore all other vessels
            if (HasActiveRescueContracts(vessel))
            {
                // Prevent processing the same vessel multiple times
                if (ProcessedRescueVessels.Contains(vessel.id))
                {
                    LunaLog.Log($"Rescue vessel {vessel.vesselName} already processed, skipping");
                    return;
                }
                
                ProcessedRescueVessels.Add(vessel.id);
                LunaLog.Log($"Rescue vessel detected: {vessel.vesselName} ({vessel.id})");
                
                // Add a longer delay to ensure the vessel is fully initialized
                // Rescue vessels can take time to fully spawn and initialize
                CoroutineUtil.StartDelayedRoutine("DelayedRescueVesselSync", () => SyncRescueVessel(vessel), 2.0f);
                
                // Also try again after a longer delay in case the first attempt fails
                CoroutineUtil.StartDelayedRoutine("DelayedRescueVesselSyncRetry", () => SyncRescueVessel(vessel), 5.0f);
            }
        }

        /// <summary>
        /// Handle vessels that are loaded when a player joins (existing vessels)
        /// </summary>
        public void OnVesselLoaded(Vessel vessel)
        {
            if (vessel == null || vessel.id == Guid.Empty) return;

            LunaLog.Log($"Vessel loaded: {vessel.vesselName} (ID: {vessel.id})");
            
            // Check if this is a rescue vessel that we haven't processed yet
            if (HasActiveRescueContracts(vessel) && !ProcessedRescueVessels.Contains(vessel.id))
            {
                LunaLog.Log($"Existing rescue vessel detected: {vessel.vesselName} (ID: {vessel.id})");
                ProcessedRescueVessels.Add(vessel.id);
                
                // Sync the rescue vessel immediately since it's already loaded
                SyncRescueVessel(vessel);
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
                LunaLog.Log($"Starting rescue vessel sync for: {vessel.vesselName}");
                
                // Ensure the vessel is still valid
                if (vessel.state == Vessel.State.DEAD)
                {
                    LunaLog.LogWarning($"Vessel {vessel.vesselName} is dead, skipping sync");
                    return;
                }
                
                // Send the vessel to all players
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
        /// Check if there are active rescue contracts for this specific vessel
        /// </summary>
        private bool HasActiveRescueContracts(Vessel vessel)
        {
            if (vessel == null || ContractSystem.Instance == null) return false;

            LunaLog.Log($"Checking active rescue contracts for vessel: {vessel.vesselName}");
            
            foreach (var contract in ContractSystem.Instance.Contracts)
            {
                if (contract.ContractState != Contract.State.Active) continue;
                
                // Check for RecoverAsset contracts (which include rescue missions)
                if (contract is RecoverAsset recoverContract)
                {
                    LunaLog.Log($"Found active RecoverAsset contract: {contract.Title}");
                    
                    // Check if this vessel is the target of the rescue contract
                    var kerbalParam = recoverContract.GetParameter<RecoverKerbal>();
                    if (kerbalParam != null)
                    {
                        
                        // For now, let's check if the vessel has exactly 1 crew member
                        // and the contract title contains rescue-related terms
                        if (vessel.GetCrewCount() == 1 && 
                            (contract.Title.ToLowerInvariant().Contains("rescue") || 
                             contract.Title.ToLowerInvariant().Contains("recover")))
                        {
                            LunaLog.Log($"Vessel matches rescue contract criteria");
                            return true;
                        }
                    }
                }
            }

            LunaLog.Log("No matching rescue contracts found for this vessel");
            return false;
        }

        /// <summary>
        /// Check if a vessel matches a specific rescue contract
        /// </summary>
        private bool VesselMatchesRescueContract(Vessel vessel, Contract contract)
        {
            if (vessel == null || contract == null) return false;
            
            // Check if this is a RecoverAsset contract
            if (contract is RecoverAsset recoverContract)
            {
                // Check if the vessel has exactly 1 crew member
                // and the contract title contains rescue-related terms
                if (vessel.GetCrewCount() == 1 && 
                    (contract.Title.ToLowerInvariant().Contains("rescue") || 
                     contract.Title.ToLowerInvariant().Contains("recover")))
                {
                    LunaLog.Log($"Vessel {vessel.vesselName} matches rescue contract: {contract.Title}");
                    return true;
                }
            }
            
            return false;
        }

        /// <summary>
        /// Check for rescue vessels after a rescue contract is accepted
        /// This handles the case where the vessel is created before the contract is accepted
        /// </summary>
        private void CheckForRescueVesselsAfterContractAccept(Contract contract)
        {
            if (contract == null || !(contract is RecoverAsset)) return;
            
            LunaLog.Log($"Checking for rescue vessels after contract accept: {contract.Title}");
            
            // Get all vessels in the game
            var allVessels = FlightGlobals.Vessels;
            
            foreach (var vessel in allVessels)
            {
                if (vessel == null || vessel.id == Guid.Empty) continue;
                
                // Skip vessels we've already processed
                if (ProcessedRescueVessels.Contains(vessel.id)) continue;
                
                // Check if this vessel matches the rescue contract
                if (VesselMatchesRescueContract(vessel, contract))
                {
                    LunaLog.Log($"Found rescue vessel after contract accept: {vessel.vesselName}");
                    ProcessedRescueVessels.Add(vessel.id);
                    
                    // Sync the rescue vessel
                    CoroutineUtil.StartDelayedRoutine("DelayedRescueVesselSyncAfterContract", () => SyncRescueVessel(vessel), 1.0f);
                }
            }
        }

        #endregion

    }
}
