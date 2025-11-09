using LSPD_First_Response.Mod.API;
using Rage;
using Rage.Native;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace AutoBackupPlus
{
    #region Enums
    public enum AutoBackupUnitType
    {
        LocalUnit,
        StateUnit,
        K9,
        AirUnit,
        SwatTeam
    }

    public enum OfficerAvailabilityStatus
    {
        Available,
        OnCall,
        OffDuty,
        Unavailable
    }

    public enum PursuitTactic
    {
        Standard,
        PITManeuver,
        RollingRoadblock,
        DynamicPositioning,
        BoxingIn,
        Flanking
    }

    public enum CalloutType
    {
        Unknown,
        TrafficStop,
        Pursuit,
        ShotsFireds,
        OfficerDown,
        ArmedRobbery,
        DrugDeal,
        DomesticDisturbance,
        Burglary,
        GrandTheftAuto,
        AssaultInProgress,
        SuspiciousActivity
    }
    public enum StandDownBehavior
    {
        Dismiss,
        ReturnToPatrol,
        StandBy
    }
    #endregion Enums

    public class Main : Plugin
    {
        #region Variables
        private static Settings settings;
        private static bool isRunning = false;
        private static DateTime lastUnitAdded;
        private static LHandle currentPursuit = null;
        private static bool swatDispatched = false;
        private static bool airUnitDispatched = false;
        private static readonly object pursuitLock = new object();

        // Track units dispatched by this plugin
        private static List<Tuple<Ped, AutoBackupUnitType, bool>> activeDispatchedUnits = new List<Tuple<Ped, AutoBackupUnitType, bool>>();
        private static GameFiber officerMonitorFiber; // NEW: Fiber to monitor officer status
        private static GameFiber keybindMonitorFiber; // NEW: Fiber to monitor keybinds

        private static Dictionary<Ped, OfficerAvailabilityStatus> officerAvailabilitySystem = new Dictionary<Ped, OfficerAvailabilityStatus>();
        private static Random availabilityRandom = new Random();
        private static GameFiber availabilityMonitorFiber;

        private static Dictionary<Ped, PursuitTactic> officerTactics = new Dictionary<Ped, PursuitTactic>();
        private static Random tacticsRandom = new Random();
        private static GameFiber pursuitTacticsMonitorFiber;
        private static DateTime lastTacticChange = DateTime.MinValue;

        private static CalloutType currentCalloutType = CalloutType.Unknown;
        private static Dictionary<CalloutType, List<AutoBackupUnitType>> calloutBackupProfiles = new Dictionary<CalloutType, List<AutoBackupUnitType>>();

        private static bool standDownRequested = false;
        private static DateTime lastStandDownTime = DateTime.MinValue;
        private static List<Ped> unitsToStandDown = new List<Ped>();

        private static bool _prIntegrationEnabled = false;

        private static LHandle _currentTrafficStop = null;
        private static bool _trafficStopSWATDispatched = false;
        private static GameFiber _trafficStopMonitorFiber;
        #endregion Variables

        public override void Initialize()
        {
            try
            {
                Functions.OnOnDutyStateChanged += OnOnDutyStateChanged;
                AppDomain.CurrentDomain.UnhandledException += UnhandledException;
                LoadSettings();
                InitializeCalloutProfiles();

                _prIntegrationEnabled = settings.EnablePRIntegration ? IsPolicingRedefinedAvailable() : false;
                if (_prIntegrationEnabled)
                {
                    Game.LogTrivial("AutoBackup+: PolicingRedefined integration is ENABLED.");
                }
                else if (settings.EnablePRIntegration && !IsPolicingRedefinedAvailable())
                {
                    Game.LogTrivial("AutoBackup+: PolicingRedefined integration is requested in INI, but PR is not available (DLL not found/loaded). Using LSPDFR fallback.");
                }
                else
                {
                    Game.LogTrivial("AutoBackup+: PolicingRedefined integration is DISABLED by INI setting.");
                }

                if (IsUltimateBackupLoadedViaDLL() && settings.DisableIfUltimateBackupActive)
                {
                    Game.LogTrivial("AutoBackup+: Ultimate Backup detected and 'DisableIfUltimateBackupActive' is true. AutoBackup+ will not run.");
                    Game.DisplayNotification("~r~AutoBackup+~w~: Ultimate Backup detected. Disabled by INI setting.");
                    return; // Exit initialization if disabled
                }

                Game.LogTrivial("AutoBackup+ v1.1: Plugin Initialized.");
                Game.DisplayNotification("~b~AutoBackup+~w~ has been ~g~initialized~w~.");
            }
            catch (Exception e)
            {
                Game.LogTrivial($"AutoBackup+: Error during initialization: {e.Message}");
                Game.LogTrivial($"Stack Trace: {e.StackTrace}");
                Game.DisplayNotification("~r~AutoBackup+ failed to initialize. Check the log for details.");
            }
        }

        public override void Finally()
        {
            try
            {
                if (isRunning)
                {
                    Functions.OnOnDutyStateChanged -= OnOnDutyStateChanged;
                    isRunning = false;

                    if (officerMonitorFiber != null && officerMonitorFiber.IsAlive) officerMonitorFiber.Abort();
                    if (keybindMonitorFiber != null && keybindMonitorFiber.IsAlive) keybindMonitorFiber.Abort();
                    if (availabilityMonitorFiber != null && availabilityMonitorFiber.IsAlive) availabilityMonitorFiber.Abort();
                    if (pursuitTacticsMonitorFiber != null && pursuitTacticsMonitorFiber.IsAlive) pursuitTacticsMonitorFiber.Abort();
                    if (_trafficStopMonitorFiber != null && _trafficStopMonitorFiber.IsAlive) _trafficStopMonitorFiber.Abort();

                    ClearOnSceneUnits(); // Clear any remaining on-scene units
                    Game.LogTrivial("AutoBackup+ has been cleaned up.");
                }
            }
            catch (Exception e)
            {
                Game.LogTrivial($"AutoBackup+: Error during cleanup: {e.Message}");
            }
        }

        private static bool IsPolicingRedefinedAvailable()
        {
            try
            {
                Type prBackupApiType = typeof(PolicingRedefined.API.BackupAPI);
                return prBackupApiType != null;
            }
            catch
            {
                return false;
            }
        }

        private static void OnOnDutyStateChanged(bool onDuty)
        {
            try
            {
                _prIntegrationEnabled = settings.EnablePRIntegration && IsPolicingRedefinedAvailable();

                if (IsUltimateBackupLoadedViaDLL() && settings.DisableIfUltimateBackupActive)
                {
                    isRunning = false;
                    Game.LogTrivial("AutoBackup+: Remaining disabled due to Ultimate Backup being active.");
                    return;
                }

                if (onDuty)
                {
                    isRunning = true;
                    ResetState();
                    Game.LogTrivial("AutoBackup+ is now running.");
                    GameFiber.StartNew(MonitorPursuits, "AutoBackup+ Pursuit Monitor");
                    GameFiber.StartNew(ProcessTimeBasedEscalation, "AutoBackup+ Time Escalation");

                    officerMonitorFiber = GameFiber.StartNew(MonitorOfficerStatus, "AutoBackup+ Officer Monitor");
                    keybindMonitorFiber = GameFiber.StartNew(MonitorKeybinds, "AutoBackup+ Keybind Monitor");

                    availabilityMonitorFiber = GameFiber.StartNew(MonitorAvailabilitySystem, "AutoBackup+ Availability Monitor");
                    pursuitTacticsMonitorFiber = GameFiber.StartNew(MonitorPursuitTactics, "AutoBackup+ Pursuit Tactics Monitor");
                    _trafficStopMonitorFiber = GameFiber.StartNew(MonitorTrafficStopsForShotsFired, "AutoBackup+ Traffic Stop Shots Fired Monitor");
                }
                else
                {
                    isRunning = false;
                    Game.LogTrivial("AutoBackup+ is now stopped.");

                    // Abort specific fibers if they are running (main loops self-terminate)
                    if (officerMonitorFiber != null && officerMonitorFiber.IsAlive) officerMonitorFiber.Abort();
                    if (keybindMonitorFiber != null && keybindMonitorFiber.IsAlive) keybindMonitorFiber.Abort();
                    if (pursuitTacticsMonitorFiber != null && pursuitTacticsMonitorFiber.IsAlive) pursuitTacticsMonitorFiber.Abort();

                    ClearOnSceneUnits();
                }
            }
            catch (Exception e)
            {
                Game.LogTrivial($"AutoBackup+: Error handling duty state change: {e.Message}");
                Game.DisplayNotification("~r~AutoBackup+ encountered an error. Check the log for details.");
            }
        }

        private static void ResetState()
        {
            lock (pursuitLock)
            {
                swatDispatched = false;
                airUnitDispatched = false;
                lastUnitAdded = DateTime.MinValue;
                currentPursuit = null;
                _currentTrafficStop = null;
                _trafficStopSWATDispatched = false;
            }
            ClearOnSceneUnits();
        }

        private static void SafeSleep(int ms)
        {
            try
            {
                GameFiber.Sleep(ms);
            }
            catch (ThreadAbortException)
            {
                throw;
            }
            catch (Exception e)
            {
                Game.LogTrivial($"AutoBackup+: Error during sleep: {e.Message}");
            }
        }

        private static void MonitorPursuits()
        {
            while (isRunning)
            {
                try
                {
                    GameFiber.Yield();

                    if (!ValidateGameState()) continue;

                    // Check for pursuit start
                    if (currentPursuit == null)
                    {
                        LHandle pursuit = GetActivePursuitIfExists();
                        if (pursuit != null)
                        {
                            HandlePursuitBegan(pursuit);
                        }
                    }

                    else if (currentPursuit != null && !IsPursuitStillActive(currentPursuit))
                    {
                        HandlePursuitEnded();
                    }

                    if (currentPursuit != null && settings.EnableShotsFiredTrigger && !swatDispatched)
                    {
                        if (Game.LocalPlayer.Character.IsShooting || IsAnyNearbyPedShooting())
                        {
                            Game.LogTrivial("AutoBackup+: Shots fired detected near pursuit");
                            DisplaySubtitle("Shots fired! SWAT team dispatched!");
                            DispatchSWAT();
                            swatDispatched = true;
                        }
                    }
                }
                catch (ThreadAbortException) { Game.LogTrivial("AutoBackup+: Pursuit Monitor terminated gracefully."); return; } // Terminate fiber gracefully
                catch (Exception e)
                {
                    Game.LogTrivial($"AutoBackup+: Error in pursuit monitoring: {e.Message}");
                    SafeSleep(5000); // Wait a bit before continuing to prevent spam
                }

                SafeSleep(1000);
            }
        }

        private static void MonitorTrafficStopsForShotsFired()
        {
            while (isRunning)
            {
                try
                {
                    GameFiber.Yield();

                    if (!ValidateGameState())
                    {
                        _currentTrafficStop = null;
                        _trafficStopSWATDispatched = false;
                        SafeSleep(1000);
                        continue;
                    }

                    LHandle playerTrafficStopHandle = Functions.GetCurrentPullover();

                    if (playerTrafficStopHandle != null && _currentTrafficStop == null)
                    {
                        _currentTrafficStop = playerTrafficStopHandle;
                        _trafficStopSWATDispatched = false; // Reset for new traffic stop
                        Game.LogTrivial("AutoBackup+: Player-involved traffic stop detected.");
                    }
                    else if (playerTrafficStopHandle == null && _currentTrafficStop != null)
                    {
                        _currentTrafficStop = null;
                        _trafficStopSWATDispatched = false;
                        Game.LogTrivial("AutoBackup+: Player-involved traffic stop ended.");
                    }
                    if (_currentTrafficStop != null && settings.EnableShotsFiredTrafficStopSWAT && !swatDispatched && !airUnitDispatched && !_trafficStopSWATDispatched)
                    {
                        if (Game.LocalPlayer.Character.IsShooting || IsAnyNearbyPedShooting()) // IsAnyNearbyPedShooting already checks for peds shooting
                        {
                            Game.LogTrivial("AutoBackup+: Shots fired detected during traffic stop.");
                            DisplaySubtitle("Shots fired at traffic stop! SWAT team dispatched!");
                            DispatchSWATForTrafficStop(); // Use the method for traffic stop context
                            _trafficStopSWATDispatched = true;
                        }
                    }
                }
                catch (ThreadAbortException) { Game.LogTrivial("AutoBackup+: Traffic Stop Shots Fired Monitor terminated gracefully."); return; }
                catch (Exception e)
                {
                    Game.LogTrivial($"AutoBackup+: Error in traffic stop shots fired monitoring: {e.Message}");
                    SafeSleep(5000);
                }
                SafeSleep(500);
            }
        }

        private static void DispatchSWATForTrafficStop()
        {
            try
            {

                if (!settings.EnableSWAT || _trafficStopSWATDispatched || activeDispatchedUnits.Count(u => u.Item2 == AutoBackupUnitType.SwatTeam && u.Item1.Exists()) >= settings.MaxSWATUnits) return;

                bool dispatchedViaPR = false;

                // --- PolicingRedefined Integration (Direct Calls) ---
                if (_prIntegrationEnabled)
                {
                    try
                    {
                        PolicingRedefined.API.EBackupUnit prSwatUnit = PolicingRedefined.API.EBackupUnit.LocalSWAT;
                        PolicingRedefined.Backup.Entities.EBackupResponseCode prResponseCode = PolicingRedefined.Backup.Entities.EBackupResponseCode.Code3; // SWAT usually Code3

                        dispatchedViaPR = PolicingRedefined.API.BackupAPI.RequestTrafficStopBackup(prSwatUnit, prResponseCode, true, false, true);

                        if (dispatchedViaPR)
                        {
                            Game.LogTrivial("AutoBackup+: SWAT team dispatched via PR.BackupAPI.RequestTrafficStopBackup for traffic stop.");
                            DisplaySubtitle("PR SWAT team has been dispatched to your traffic stop.");
                        }
                    }
                    catch (Exception ex)
                    {
                        Game.LogTrivial($"AutoBackup+: Error dispatching SWAT via PR direct call for traffic stop: {ex.Message}");
                        dispatchedViaPR = false; // Fallback to LSPDFR if PR fails
                    }
                }

                // --- LSPDFR Fallback / Default Dispatch ---
                if (!dispatchedViaPR)
                {
                    Functions.RequestBackup(Game.LocalPlayer.Character.Position,
                        LSPD_First_Response.EBackupResponseType.Code3,
                        LSPD_First_Response.EBackupUnitType.SwatTeam);

                    Game.LogTrivial("AutoBackup+: SWAT team dispatched via LSPDFR API for traffic stop.");
                    DisplaySubtitle("LSPDFR SWAT team has been dispatched to your traffic stop.");
                }

                if (currentPursuit == null && _currentTrafficStop != null)
                {
                    List<Ped> suspects = new List<Ped>();

                    Ped primarySuspect = Functions.GetPulloverSuspect(_currentTrafficStop);
                    if (primarySuspect.Exists() && primarySuspect.IsAlive && IsPedHostileInContext(primarySuspect))
                    {
                        suspects.Add(primarySuspect);
                    }

                    // Add any other nearby hostile peds who are not cops (e.g., passengers, other involved individuals)
                    foreach (Ped ped in World.EnumeratePeds())
                    {
                        if (ped.Exists() && ped.IsAlive && !Functions.IsPedACop(ped) && ped.DistanceTo(Game.LocalPlayer.Character) < 50f && IsPedHostileInContext(ped) && !suspects.Contains(ped))
                        {
                            suspects.Add(ped);
                        }
                    }

                    if (suspects.Any())
                    {
                        currentPursuit = Functions.CreatePursuit();
                        foreach (Ped suspect in suspects)
                        {
                            Functions.AddPedToPursuit(currentPursuit, suspect);
                        }
                        Game.LogTrivial("AutoBackup+: Shots fired at traffic stop, initiated pursuit.");
                        DisplaySubtitle("Pursuit initiated due to hostile engagement at traffic stop!");
                    }
                }
            }
            catch (Exception e)
            {
                Game.LogTrivial($"AutoBackup+: Error dispatching SWAT for traffic stop: {e.Message}");
            }
        }

        private static LHandle GetActivePursuitIfExists()
        {
            try
            {
                return Functions.GetActivePursuit();
            }
            catch (Exception e)
            {
                Game.LogTrivial("AutoBackup+: Error getting active pursuit: " + e.Message);
            }
            return null;
        }

        private static bool IsPursuitStillActive(LHandle pursuit)
        {
            try
            {
                return Functions.IsPursuitStillRunning(pursuit);
            }
            catch
            {
                return false;
            }
        }

        private static void HandlePursuitBegan(LHandle pursuit)
        {
            try
            {
                lock (pursuitLock)
                {
                    currentPursuit = pursuit;
                    swatDispatched = false;
                    airUnitDispatched = false;
                }

                // Detect callout type and dispatch appropriate backup
                currentCalloutType = DetectCalloutType();
                Game.LogTrivial($"AutoBackup+: Detected callout type: {currentCalloutType}");

                if (settings.EnableCalloutSpecificBackup)
                {
                    // Dispatch callout-specific backup
                    DispatchCalloutSpecificBackup(pursuit, currentCalloutType);
                }
                else
                {
                    // Original dispatch logic
                    for (int i = 0; i < settings.InitialUnits; i++)
                    {
                        if (!isRunning) break;
                        DispatchRegularUnit(pursuit);
                    }

                    lock (pursuitLock)
                    {
                        lastUnitAdded = DateTime.Now;
                    }

                    DisplaySubtitle("Initial backup dispatched: " + settings.InitialUnits + " units");
                }

                Game.LogTrivial($"AutoBackup+: Pursuit began, dispatched backup for {currentCalloutType}");
            }
            catch (Exception e)
            {
                Game.LogTrivial($"AutoBackup+: Error handling pursuit start: {e.Message}");
            }
        }

        private static void HandlePursuitEnded()
        {
            try
            {
                Game.LogTrivial("AutoBackup+: Pursuit ended, processing on-scene protocol.");
                if (settings.EnableOnSceneProtocol)
                {
                    // Transition units to on-scene duty
                    foreach (var unitTuple in activeDispatchedUnits.ToList())
                    {
                        Ped unit = unitTuple.Item1;
                        if (unit.Exists() && unit.IsAlive && Functions.IsPedACop(unit))
                        {
                            Functions.RemovePedFromPursuit(unit);
                            unit.Tasks.ClearImmediately();

                            Vector3 pursuitEndPos = Game.LocalPlayer.Character.Position;
                            NativeFunction.Natives.TASK_PATROL(unit, pursuitEndPos.X, pursuitEndPos.Y, pursuitEndPos.Z, 50f, 5f);
                            Game.LogTrivial($"AutoBackup+: Unit {unit.Handle} transitioned to on-scene patrol.");
                        }
                    }
                    DisplaySubtitle("Backup units holding scene.");

                    lock (pursuitLock)
                    {
                        swatDispatched = false;
                        airUnitDispatched = false;
                        lastUnitAdded = DateTime.MinValue;
                        currentPursuit = null;
                    }
                }
                else
                {
                    // Despawn units if on-scene protocol is disabled
                    ClearOnSceneUnits();
                    DisplaySubtitle("Backup units clearing scene.");
                    ResetState(); // Only reset completely if protocol is disabled
                }

                Game.LogTrivial("AutoBackup+: Pursuit ended, state reset.");
            }
            catch (Exception e)
            {
                Game.LogTrivial($"AutoBackup+: Error handling pursuit end: {e.Message}");
            }
        }

        /// <summary>
        /// NEW: Monitor officers for death/injury
        /// </summary>
        private static void MonitorOfficerStatus()
        {
            while (isRunning)
            {
                try
                {
                    GameFiber.Yield();

                    if (currentPursuit != null && IsPursuitStillActive(currentPursuit) && settings.EnableOfficerLostNotification)
                    {
                        List<Tuple<Ped, AutoBackupUnitType, bool>> lostOfficers = new List<Tuple<Ped, AutoBackupUnitType, bool>>();

                        // Thread-safe copy of the list
                        List<Tuple<Ped, AutoBackupUnitType, bool>> unitsCopy;
                        lock (pursuitLock)
                        {
                            unitsCopy = new List<Tuple<Ped, AutoBackupUnitType, bool>>(activeDispatchedUnits);
                        }

                        foreach (var unitTuple in unitsCopy)
                        {
                            Ped unit = unitTuple.Item1;
                            if (unit != null && unit.Exists() && (unit.IsDead || unit.IsInjured))
                            {
                                lostOfficers.Add(unitTuple);
                            }
                        }

                        // Remove lost officers thread-safely
                        lock (pursuitLock)
                        {
                            foreach (var lostUnitTuple in lostOfficers)
                            {
                                activeDispatchedUnits.Remove(lostUnitTuple);
                                Game.LogTrivial($"AutoBackup+: Officer {lostUnitTuple.Item1.Handle} ({lostUnitTuple.Item2}) is lost (dead/injured).");
                                DisplaySubtitle("Officer down! Dispatching additional unit to pursuit!");
                                DispatchRegularUnit(currentPursuit);
                            }
                        }
                    }
                }
                catch (ThreadAbortException)
                {
                    Game.LogTrivial("AutoBackup+: Officer Monitor terminated gracefully.");
                    return;
                }
                catch (Exception e)
                {
                    Game.LogTrivial($"AutoBackup+: Error in officer status monitoring: {e.Message}");
                }
                SafeSleep(2000);
            }
        }

        /// <summary>
        /// Clear units remaining on scene
        /// </summary>
        private static void ClearOnSceneUnits()
        {
            try
            {
                List<Tuple<Ped, AutoBackupUnitType, bool>> unitsCopy;
                lock (pursuitLock)
                {
                    unitsCopy = new List<Tuple<Ped, AutoBackupUnitType, bool>>(activeDispatchedUnits);
                    activeDispatchedUnits.Clear(); // Clear immediately while locked
                }

                bool prGlobalDismissCalled = false; // Flag to ensure PR's global dismiss is called only once

                foreach (var unitTuple in unitsCopy)
                {
                    try
                    {
                        Ped unit = unitTuple.Item1;
                        bool isPRUnit = unitTuple.Item3;

                        if (unit != null && unit.Exists())
                        {
                            if (isPRUnit && _prIntegrationEnabled)
                            {
                                // For a general "clear all", we call PR's global dismiss once for all PR units.
                                // PR doesn't expose an API to dismiss individual units by Ped handle.
                                if (!prGlobalDismissCalled)
                                {
                                    PolicingRedefined.API.BackupAPI.DismissAllBackupUnits(false); // force = false
                                    prGlobalDismissCalled = true;
                                    Game.LogTrivial($"AutoBackup+: Called PR.BackupAPI.DismissAllBackupUnits for PR units.");
                                }
                                // Individual PR units don't need a separate dismiss call here as the global one handles them.
                            }
                            else // LSPDFR units
                            {
                                if (Functions.IsPedACop(unit))
                                {
                                    Functions.RemovePedFromPursuit(unit);
                                    unit.Tasks.ClearImmediately();
                                    unit.Dismiss();
                                }
                                else
                                {
                                    unit.Delete();
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Game.LogTrivial($"AutoBackup+: Error clearing individual unit: {ex.Message}");
                    }
                }

                Game.LogTrivial("AutoBackup+: All on-scene units cleared.");
            }
            catch (Exception e)
            {
                Game.LogTrivial($"AutoBackup+: Error clearing on-scene units: {e.Message}");
            }
        }

        /// <summary>
        /// Monitor keybinds for dismissing units
        /// </summary>
        private static void MonitorKeybinds()
        {
            while (isRunning)
            {
                try
                {
                    GameFiber.Yield();

                    // Original dismiss all units functionality
                    if (settings.EnableOnSceneProtocol && settings.DismissAllUnitsKey != System.Windows.Forms.Keys.None)
                    {
                        bool isModifierPressed = (settings.DismissAllUnitsModifierKey == System.Windows.Forms.Keys.None) || Game.IsKeyDown(settings.DismissAllUnitsModifierKey);

                        if (isModifierPressed && Game.IsKeyDown(settings.DismissAllUnitsKey))
                        {
                            DisplaySubtitle("Dismissing all on-scene units.");
                            ClearOnSceneUnits();
                        }
                    }

                    // NEW: Stand Down functionality
                    if (settings.EnableStandDownFunctionality && settings.StandDownKey != System.Windows.Forms.Keys.None)
                    {
                        bool isStandDownModifierPressed = (settings.StandDownModifierKey == System.Windows.Forms.Keys.None) || Game.IsKeyDown(settings.StandDownModifierKey);

                        if (isStandDownModifierPressed && Game.IsKeyDown(settings.StandDownKey))
                        {
                            // Prevent spam clicking
                            if (DateTime.Now.Subtract(lastStandDownTime).TotalSeconds >= 2)
                            {
                                ExecuteStandDown();
                                lastStandDownTime = DateTime.Now;
                            }
                        }
                    }

                    // NEW: Selective Stand Down (dismiss specific unit types)
                    if (settings.EnableSelectiveStandDown && settings.SelectiveStandDownKey != System.Windows.Forms.Keys.None)
                    {
                        bool isSelectiveModifierPressed = (settings.SelectiveStandDownModifierKey == System.Windows.Forms.Keys.None) || Game.IsKeyDown(settings.SelectiveStandDownModifierKey);

                        if (isSelectiveModifierPressed && Game.IsKeyDown(settings.SelectiveStandDownKey))
                        {
                            if (DateTime.Now.Subtract(lastStandDownTime).TotalSeconds >= 2)
                            {
                                ExecuteSelectiveStandDown();
                                lastStandDownTime = DateTime.Now;
                            }
                        }
                    }
                }
                catch (ThreadAbortException) { Game.LogTrivial("AutoBackup+: Keybind Monitor terminated gracefully."); return; }
                catch (Exception e)
                {
                    Game.LogTrivial($"AutoBackup+: Error in keybind monitoring: {e.Message}");
                }
                SafeSleep(100); // Check frequently for key presses
            }
        }

        private static bool IsOfficerAvailable(Ped officer)
        {
            if (!officerAvailabilitySystem.ContainsKey(officer))
            {
                // Randomly assign availability status for new officers
                int availabilityChance = availabilityRandom.Next(0, 100);
                OfficerAvailabilityStatus status;

                if (availabilityChance < settings.OfficerAvailabilityChance)
                    status = OfficerAvailabilityStatus.Available;
                else if (availabilityChance < settings.OfficerAvailabilityChance + 15)
                    status = OfficerAvailabilityStatus.OnCall;
                else if (availabilityChance < settings.OfficerAvailabilityChance + 25)
                    status = OfficerAvailabilityStatus.OffDuty;
                else
                    status = OfficerAvailabilityStatus.Unavailable;

                officerAvailabilitySystem[officer] = status;
            }

            return officerAvailabilitySystem[officer] == OfficerAvailabilityStatus.Available;
        }

        private static void SetOfficerStatus(Ped officer, OfficerAvailabilityStatus status)
        {
            officerAvailabilitySystem[officer] = status;
            Game.LogTrivial($"AutoBackup+: Officer {officer.Handle} status changed to {status}");
        }

        private static void UpdateOfficerAvailability()
        {
            // Periodically update officer availability status
            foreach (var officer in officerAvailabilitySystem.Keys.ToList())
            {
                if (!officer.Exists())
                {
                    officerAvailabilitySystem.Remove(officer);
                    continue;
                }

                // Random chance to change status over time
                if (availabilityRandom.Next(0, 1000) < 5) // 0.5% chance per update
                {
                    var currentStatus = officerAvailabilitySystem[officer];
                    if (currentStatus == OfficerAvailabilityStatus.OnCall)
                    {
                        SetOfficerStatus(officer, OfficerAvailabilityStatus.Available);
                    }
                    else if (currentStatus == OfficerAvailabilityStatus.Available && availabilityRandom.Next(0, 100) < 10)
                    {
                        SetOfficerStatus(officer, OfficerAvailabilityStatus.OnCall);
                    }
                }
            }
        }

        private static void MonitorAvailabilitySystem()
        {
            while (isRunning)
            {
                try
                {
                    GameFiber.Yield();
                    if (settings.EnableOfficerAvailabilitySystem)
                    {
                        UpdateOfficerAvailability();
                    }
                }
                catch (ThreadAbortException)
                {
                    Game.LogTrivial("AutoBackup+: Availability Monitor terminated gracefully.");
                    return;
                }
                catch (Exception e)
                {
                    Game.LogTrivial($"AutoBackup+: Error in availability monitoring: {e.Message}");
                }
                SafeSleep(10000); // Update every 10 seconds
            }
        }

        private static PursuitTactic GetRandomTactic()
        {
            var tactics = new PursuitTactic[]
            {
        PursuitTactic.Standard,
        PursuitTactic.PITManeuver,
        PursuitTactic.RollingRoadblock,
        PursuitTactic.DynamicPositioning,
        PursuitTactic.BoxingIn,
        PursuitTactic.Flanking
            };

            // Weight the tactics based on settings
            List<PursuitTactic> weightedTactics = new List<PursuitTactic>();

            // Standard tactics (always available)
            for (int i = 0; i < settings.StandardTacticWeight; i++)
                weightedTactics.Add(PursuitTactic.Standard);

            if (settings.EnablePITManeuvers)
            {
                for (int i = 0; i < settings.PITManeuverWeight; i++)
                    weightedTactics.Add(PursuitTactic.PITManeuver);
            }

            if (settings.EnableRollingRoadblocks)
            {
                for (int i = 0; i < settings.RollingRoadblockWeight; i++)
                    weightedTactics.Add(PursuitTactic.RollingRoadblock);
            }

            if (settings.EnableDynamicPositioning)
            {
                for (int i = 0; i < settings.DynamicPositioningWeight; i++)
                    weightedTactics.Add(PursuitTactic.DynamicPositioning);
            }

            return weightedTactics[tacticsRandom.Next(weightedTactics.Count)];
        }

        private static void AssignTacticToOfficer(Ped officer)
        {
            if (!officerTactics.ContainsKey(officer))
            {
                PursuitTactic tactic = GetRandomTactic();
                officerTactics[officer] = tactic;
                Game.LogTrivial($"AutoBackup+: Officer {officer.Handle} assigned tactic: {tactic}");

                if (settings.ShowTacticNotifications)
                {
                    DisplaySubtitle($"Unit {officer.Handle}: {tactic} tactic assigned");
                }
            }
        }

        private static void ExecutePursuitTactic(Ped officer, Ped suspect)
        {
            if (!officerTactics.ContainsKey(officer) || !officer.Exists() || !suspect.Exists())
                return;

            PursuitTactic tactic = officerTactics[officer];

            try
            {
                switch (tactic)
                {
                    case PursuitTactic.PITManeuver:
                        ExecutePITManeuver(officer, suspect);
                        break;
                    case PursuitTactic.RollingRoadblock:
                        ExecuteRollingRoadblock(officer, suspect);
                        break;
                    case PursuitTactic.DynamicPositioning:
                        ExecuteDynamicPositioning(officer, suspect);
                        break;
                    case PursuitTactic.BoxingIn:
                        ExecuteBoxingIn(officer, suspect);
                        break;
                    case PursuitTactic.Flanking:
                        ExecuteFlanking(officer, suspect);
                        break;
                    default:
                        // Standard pursuit behavior (default LSPDFR behavior)
                        break;
                }
            }
            catch (Exception e)
            {
                Game.LogTrivial($"AutoBackup+: Error executing tactic {tactic} for officer {officer.Handle}: {e.Message}");
            }
        }

        private static void ExecutePITManeuver(Ped officer, Ped suspect)
        {
            if (!officer.IsInAnyVehicle(false) || !suspect.IsInAnyVehicle(false))
                return;

            Vehicle officerVehicle = officer.CurrentVehicle;
            Vehicle suspectVehicle = suspect.CurrentVehicle;

            // Calculate PIT position (rear quarter of suspect vehicle)
            Vector3 suspectPos = suspectVehicle.Position;
            Vector3 suspectHeading = suspectVehicle.ForwardVector;
            Vector3 pitPosition = suspectPos - (suspectHeading * 3f) + (suspectVehicle.RightVector * 2f);

            // Drive to PIT position
            NativeFunction.Natives.TASK_VEHICLE_DRIVE_TO_COORD_LONGRANGE(
                officer, officerVehicle, pitPosition.X, pitPosition.Y, pitPosition.Z,
                suspectVehicle.Speed + 5f, 787004, 2f);

            Game.LogTrivial($"AutoBackup+: Officer {officer.Handle} executing PIT maneuver");
        }

        private static void ExecuteRollingRoadblock(Ped officer, Ped suspect)
        {
            if (!officer.IsInAnyVehicle(false) || !suspect.IsInAnyVehicle(false))
                return;

            Vehicle officerVehicle = officer.CurrentVehicle;
            Vehicle suspectVehicle = suspect.CurrentVehicle;

            // Position ahead of suspect to create roadblock
            Vector3 suspectPos = suspectVehicle.Position;
            Vector3 suspectHeading = suspectVehicle.ForwardVector;
            Vector3 blockPosition = suspectPos + (suspectHeading * 50f);

            NativeFunction.Natives.TASK_VEHICLE_DRIVE_TO_COORD_LONGRANGE(
                officer, officerVehicle, blockPosition.X, blockPosition.Y, blockPosition.Z,
                suspectVehicle.Speed + 10f, 787004, 1f);

            Game.LogTrivial($"AutoBackup+: Officer {officer.Handle} executing rolling roadblock");
        }

        private static void ExecuteDynamicPositioning(Ped officer, Ped suspect)
        {
            if (!officer.IsInAnyVehicle(false) || !suspect.IsInAnyVehicle(false))
                return;

            Vehicle officerVehicle = officer.CurrentVehicle;
            Vehicle suspectVehicle = suspect.CurrentVehicle;

            // Dynamic positioning based on pursuit situation
            Vector3 suspectPos = suspectVehicle.Position;
            Vector3 suspectHeading = suspectVehicle.ForwardVector;

            // Calculate optimal position (side approach)
            Vector3 optimalPosition = suspectPos + (suspectVehicle.RightVector * 8f) - (suspectHeading * 5f);

            NativeFunction.Natives.TASK_VEHICLE_DRIVE_TO_COORD_LONGRANGE(
                officer, officerVehicle, optimalPosition.X, optimalPosition.Y, optimalPosition.Z,
                suspectVehicle.Speed, 787004, 3f);

            Game.LogTrivial($"AutoBackup+: Officer {officer.Handle} executing dynamic positioning");
        }

        private static void ExecuteBoxingIn(Ped officer, Ped suspect)
        {
            if (!officer.IsInAnyVehicle(false) || !suspect.IsInAnyVehicle(false))
                return;

            Vehicle suspectVehicle = suspect.CurrentVehicle;
            Vector3 suspectPos = suspectVehicle.Position;
            Vector3 suspectHeading = suspectVehicle.ForwardVector;

            // Box in from behind
            Vector3 boxPosition = suspectPos - (suspectHeading * 4f);

            NativeFunction.Natives.TASK_VEHICLE_DRIVE_TO_COORD_LONGRANGE(
                officer, officer.CurrentVehicle, boxPosition.X, boxPosition.Y, boxPosition.Z,
                suspectVehicle.Speed - 2f, 787004, 1f);

            Game.LogTrivial($"AutoBackup+: Officer {officer.Handle} executing boxing-in maneuver");
        }

        private static void ExecuteFlanking(Ped officer, Ped suspect)
        {
            if (!officer.IsInAnyVehicle(false) || !suspect.IsInAnyVehicle(false))
                return;

            Vehicle suspectVehicle = suspect.CurrentVehicle;
            Vector3 suspectPos = suspectVehicle.Position;

            // Flank from alternating sides
            float side = (officer.Handle % 2 == 0) ? 15f : -15f;
            Vector3 flankPosition = suspectPos + (suspectVehicle.RightVector * side);

            NativeFunction.Natives.TASK_VEHICLE_DRIVE_TO_COORD_LONGRANGE(
                officer, officer.CurrentVehicle, flankPosition.X, flankPosition.Y, flankPosition.Z,
                suspectVehicle.Speed + 3f, 787004, 5f);

            Game.LogTrivial($"AutoBackup+: Officer {officer.Handle} executing flanking maneuver");
        }

        private static void MonitorPursuitTactics()
        {
            while (isRunning)
            {
                try
                {
                    GameFiber.Yield();

                    if (currentPursuit != null && IsPursuitStillActive(currentPursuit) && settings.EnableAIPursuitTactics)
                    {
                        // Get pursuit suspects
                        Ped[] suspects = Functions.GetPursuitPeds(currentPursuit);
                        if (suspects.Length > 0)
                        {
                            Ped primarySuspect = suspects[0];

                            // Assign tactics to officers who don't have them
                            foreach (var unitTuple in activeDispatchedUnits.ToList())
                            {
                                Ped officer = unitTuple.Item1;
                                if (officer.Exists() && officer.IsAlive && officer.IsInAnyVehicle(false))
                                {
                                    AssignTacticToOfficer(officer);
                                }
                            }

                            // Execute tactics periodically
                            if (DateTime.Now.Subtract(lastTacticChange).TotalSeconds >= settings.TacticChangeInterval)
                            {
                                foreach (var unitTuple in activeDispatchedUnits.ToList())
                                {
                                    Ped officer = unitTuple.Item1;
                                    if (officer.Exists() && officer.IsAlive && officer.IsInAnyVehicle(false))
                                    {
                                        ExecutePursuitTactic(officer, primarySuspect);
                                    }
                                }
                                lastTacticChange = DateTime.Now;
                            }

                            // Randomly change tactics
                            if (settings.EnableRandomTacticChanges && tacticsRandom.Next(0, 1000) < settings.TacticChangeChance)
                            {
                                var officersToChange = activeDispatchedUnits.Where(u => u.Item1.Exists() && u.Item1.IsInAnyVehicle(false)).ToList();
                                if (officersToChange.Count > 0)
                                {
                                    var randomOfficer = officersToChange[tacticsRandom.Next(officersToChange.Count)];
                                    PursuitTactic newTactic = GetRandomTactic();
                                    officerTactics[randomOfficer.Item1] = newTactic;

                                    Game.LogTrivial($"AutoBackup+: Officer {randomOfficer.Item1.Handle} changed to tactic: {newTactic}");
                                    if (settings.ShowTacticNotifications)
                                    {
                                        DisplaySubtitle($"Unit {randomOfficer.Item1.Handle}: Switching to {newTactic}");
                                    }
                                }
                            }
                        }
                    }
                }
                catch (ThreadAbortException)
                {
                    Game.LogTrivial("AutoBackup+: Pursuit Tactics Monitor terminated gracefully.");
                    return;
                }
                catch (Exception e)
                {
                    Game.LogTrivial($"AutoBackup+: Error in pursuit tactics monitoring: {e.Message}");
                }
                SafeSleep(2000); // Check every 2 seconds
            }
        }

        private static void InitializeCalloutProfiles()
        {
            calloutBackupProfiles.Clear();

            // Traffic Stop - Light backup
            calloutBackupProfiles[CalloutType.TrafficStop] = new List<AutoBackupUnitType>
    {
        AutoBackupUnitType.LocalUnit
    };

            // Pursuit - Standard backup
            calloutBackupProfiles[CalloutType.Pursuit] = new List<AutoBackupUnitType>
    {
        AutoBackupUnitType.LocalUnit,
        AutoBackupUnitType.LocalUnit,
        AutoBackupUnitType.K9
    };

            // Shots Fired - Heavy backup
            calloutBackupProfiles[CalloutType.ShotsFireds] = new List<AutoBackupUnitType>
    {
        AutoBackupUnitType.LocalUnit,
        AutoBackupUnitType.LocalUnit,
        AutoBackupUnitType.StateUnit,
        AutoBackupUnitType.SwatTeam
    };

            // Officer Down - Maximum response
            calloutBackupProfiles[CalloutType.OfficerDown] = new List<AutoBackupUnitType>
    {
        AutoBackupUnitType.LocalUnit,
        AutoBackupUnitType.LocalUnit,
        AutoBackupUnitType.StateUnit,
        AutoBackupUnitType.SwatTeam,
        AutoBackupUnitType.AirUnit
    };

            // Armed Robbery - Heavy backup
            calloutBackupProfiles[CalloutType.ArmedRobbery] = new List<AutoBackupUnitType>
    {
        AutoBackupUnitType.LocalUnit,
        AutoBackupUnitType.StateUnit,
        AutoBackupUnitType.SwatTeam,
        AutoBackupUnitType.K9
    };

            // Drug Deal - Specialized units
            calloutBackupProfiles[CalloutType.DrugDeal] = new List<AutoBackupUnitType>
    {
        AutoBackupUnitType.LocalUnit,
        AutoBackupUnitType.K9,
        AutoBackupUnitType.StateUnit
    };

            // Domestic Disturbance - Standard backup
            calloutBackupProfiles[CalloutType.DomesticDisturbance] = new List<AutoBackupUnitType>
    {
        AutoBackupUnitType.LocalUnit,
        AutoBackupUnitType.LocalUnit
    };

            // Burglary - Light backup with K9
            calloutBackupProfiles[CalloutType.Burglary] = new List<AutoBackupUnitType>
    {
        AutoBackupUnitType.LocalUnit,
        AutoBackupUnitType.K9
    };

            // Grand Theft Auto - Pursuit ready
            calloutBackupProfiles[CalloutType.GrandTheftAuto] = new List<AutoBackupUnitType>
    {
        AutoBackupUnitType.LocalUnit,
        AutoBackupUnitType.StateUnit,
        AutoBackupUnitType.K9
    };

            // Assault In Progress - Quick response
            calloutBackupProfiles[CalloutType.AssaultInProgress] = new List<AutoBackupUnitType>
    {
        AutoBackupUnitType.LocalUnit,
        AutoBackupUnitType.LocalUnit,
        AutoBackupUnitType.StateUnit
    };

            // Suspicious Activity - Light backup
            calloutBackupProfiles[CalloutType.SuspiciousActivity] = new List<AutoBackupUnitType>
    {
        AutoBackupUnitType.LocalUnit
    };

            Game.LogTrivial("AutoBackup+: Callout backup profiles initialized");
        }

        /// <summary>
        /// Helper method to check if a ped is hostile in the context of a traffic stop or general interaction
        /// </summary>
        private static bool IsPedHostileInContext(Ped ped)
        {
            if (ped == null || !ped.Exists() || !ped.IsAlive || Functions.IsPedACop(ped)) return false;


            if (ped.IsShooting || NativeFunction.Natives.IS_PED_ATTACKING<bool>(ped, false)) return true;

            if (NativeFunction.Natives.GET_RELATIONSHIP_BETWEEN_PEDS<int>(ped, Game.LocalPlayer.Character) == 5 ||
                NativeFunction.Natives.GET_RELATIONSHIP_BETWEEN_PEDS<int>(ped, Game.LocalPlayer.Character) == 4)
                return true;

            // Check if the ped is fleeing (strong indicator of hostility in a traffic stop context)
            if (NativeFunction.Natives.IS_PED_FLEEING<bool>(ped)) return true;

            return false;
        }

        private static CalloutType DetectCalloutType()
        {
            try
            {
                // Try to detect callout type based on various factors

                // Check if there's an active pursuit
                if (currentPursuit != null && IsPursuitStillActive(currentPursuit))
                {
                    return CalloutType.Pursuit;
                }

                // Check if there's an active traffic stop
                LHandle playerTrafficStopHandle = Functions.GetCurrentPullover();
                if (playerTrafficStopHandle != null)
                {
                    // If shots fired during traffic stop, it's a "ShotsFireds" scenario
                    if (Game.LocalPlayer.Character.IsShooting || IsAnyNearbyPedShooting())
                    {
                        return CalloutType.ShotsFireds;
                    }
                    // Otherwise, it's a general traffic stop
                    return CalloutType.TrafficStop;
                }


                // Check for shots fired in the area (without an active pursuit or traffic stop)
                if (Game.LocalPlayer.Character.IsShooting || IsAnyNearbyPedShooting())
                {
                    return CalloutType.ShotsFireds;
                }

                // Check for officer down
                if (IsAnyNearbyOfficerDown())
                {
                    return CalloutType.OfficerDown;
                }

                // Check player's current activity/situation
                Vector3 playerPos = Game.LocalPlayer.Character.Position;

                // Check for nearby crimes/situations
                foreach (Ped ped in World.EnumeratePeds())
                {
                    if (ped.Exists() && ped.DistanceTo(Game.LocalPlayer.Character) < 100f)
                    {
                        // Check if ped is fleeing (possible GTA)
                        if (ped.IsInAnyVehicle(false) && ped.CurrentVehicle.Speed > 20f &&
                            NativeFunction.Natives.IS_PED_FLEEING<bool>(ped))
                        {
                            return CalloutType.GrandTheftAuto;
                        }

                        // Check if ped is armed and hostile using the new helper
                        if (IsPedHostileInContext(ped))
                        {
                            return CalloutType.ArmedRobbery;
                        }

                        // Check if ped has a weapon in hand (even if not actively hostile yet)
                        if (NativeFunction.Natives.GET_SELECTED_PED_WEAPON<uint>(ped) != NativeFunction.Natives.GET_HASH_KEY<uint>("WEAPON_UNARMED"))
                        {
                            return CalloutType.SuspiciousActivity; // Armed but not necessarily hostile
                        }
                    }
                }

                // Check current zone for context
                string zoneName = NativeFunction.Natives.GET_NAME_OF_ZONE(playerPos.X, playerPos.Y, playerPos.Z);

                // Residential areas might be domestic disturbances or burglaries
                if (zoneName.Contains("RESIDENTIAL") || zoneName.Contains("SUBURB"))
                {
                    // Random chance between domestic and burglary
                    return (new Random().Next(0, 2) == 0) ? CalloutType.DomesticDisturbance : CalloutType.Burglary;
                }

                // Commercial areas might be robberies
                if (zoneName.Contains("COMMERCIAL") || zoneName.Contains("BUSINESS"))
                {
                    return CalloutType.ArmedRobbery;
                }

                // Default fallback
                return CalloutType.SuspiciousActivity;
            }
            catch (Exception e)
            {
                Game.LogTrivial($"AutoBackup+: Error detecting callout type: {e.Message}");
                return CalloutType.Unknown;
            }
        }

        private static List<AutoBackupUnitType> GetCalloutSpecificBackup(CalloutType calloutType)
        {
            if (!settings.EnableCalloutSpecificBackup)
            {
                // Return default backup if feature is disabled
                var defaultBackup = new List<AutoBackupUnitType>();
                for (int i = 0; i < settings.InitialUnits; i++)
                {
                    defaultBackup.Add(AutoBackupUnitType.LocalUnit);
                }
                return defaultBackup;
            }

            if (calloutBackupProfiles.ContainsKey(calloutType))
            {
                var profile = calloutBackupProfiles[calloutType];
                Game.LogTrivial($"AutoBackup+: Using callout-specific backup for {calloutType}: {profile.Count} units");

                if (settings.ShowCalloutNotifications)
                {
                    DisplaySubtitle($"Callout detected: {calloutType} - Dispatching specialized backup");
                }

                return new List<AutoBackupUnitType>(profile);
            }

            // Fallback to default if no profile exists
            Game.LogTrivial($"AutoBackup+: No specific profile for {calloutType}, using default backup");
            var fallbackBackup = new List<AutoBackupUnitType>();
            for (int i = 0; i < settings.InitialUnits; i++)
            {
                fallbackBackup.Add(AutoBackupUnitType.LocalUnit);
            }
            return fallbackBackup;
        }

        private static void DispatchCalloutSpecificBackup(LHandle pursuitHandle, CalloutType calloutType)
        {
            try
            {
                var backupUnits = GetCalloutSpecificBackup(calloutType);

                Game.LogTrivial($"AutoBackup+: Dispatching {backupUnits.Count} callout-specific units for {calloutType}");

                foreach (var unitType in backupUnits)
                {
                    if (!isRunning) break;

                    DispatchSpecificUnitType(pursuitHandle, unitType);
                    SafeSleep(500); // Small delay between dispatches
                }

                // Update current units count
                lock (pursuitLock)
                {
                    lastUnitAdded = DateTime.Now;
                }

                DisplaySubtitle($"Callout-specific backup dispatched: {backupUnits.Count} units ({calloutType})");
            }
            catch (Exception e)
            {
                Game.LogTrivial($"AutoBackup+: Error dispatching callout-specific backup: {e.Message}");
            }
        }

        private static void DispatchSpecificUnitType(LHandle pursuitHandle, AutoBackupUnitType unitType)
        {
            try
            {
                if (pursuitHandle == null || !IsPursuitStillActive(pursuitHandle)) return;

                LSPD_First_Response.EBackupResponseType lspdfrResponseType = LSPD_First_Response.EBackupResponseType.Pursuit;
                LSPD_First_Response.EBackupUnitType lspdfrUnitType;
                bool dispatchedViaPR = false;

                // Check unit limits
                if (unitType == AutoBackupUnitType.K9 && activeDispatchedUnits.Count(u => u.Item2 == AutoBackupUnitType.K9 && u.Item1.Exists()) >= settings.MaxK9Units)
                {
                    Game.LogTrivial($"AutoBackup+: Max {unitType} units reached, skipping dispatch");
                    return;
                }
                if (unitType == AutoBackupUnitType.AirUnit && activeDispatchedUnits.Count(u => u.Item2 == AutoBackupUnitType.AirUnit && u.Item1.Exists()) >= settings.MaxAirUnits)
                {
                    Game.LogTrivial($"AutoBackup+: Max {unitType} units reached, skipping dispatch");
                    return;
                }
                if (unitType == AutoBackupUnitType.SwatTeam && activeDispatchedUnits.Count(u => u.Item2 == AutoBackupUnitType.SwatTeam && u.Item1.Exists()) >= settings.MaxSWATUnits)
                {
                    Game.LogTrivial($"AutoBackup+: Max {unitType} units reached, skipping dispatch");
                    return;
                }

                // --- PolicingRedefined Integration (Direct Calls) ---
                if (_prIntegrationEnabled)
                {
                    try
                    {
                        PolicingRedefined.API.EBackupUnit prBackupUnit;
                        PolicingRedefined.Backup.Entities.EBackupResponseCode prResponseCode = PolicingRedefined.Backup.Entities.EBackupResponseCode.Code2; // Default for specific dispatch

                        switch (unitType)
                        {
                            case AutoBackupUnitType.LocalUnit: prBackupUnit = PolicingRedefined.API.EBackupUnit.LocalPatrol; break;
                            case AutoBackupUnitType.StateUnit: prBackupUnit = PolicingRedefined.API.EBackupUnit.StatePatrol; break;
                            case AutoBackupUnitType.K9: prBackupUnit = PolicingRedefined.API.EBackupUnit.LocalK9Patrol; break;
                            case AutoBackupUnitType.AirUnit: prBackupUnit = PolicingRedefined.API.EBackupUnit.LocalAir; break;
                            case AutoBackupUnitType.SwatTeam: prBackupUnit = PolicingRedefined.API.EBackupUnit.LocalSWAT; break;
                            default: prBackupUnit = PolicingRedefined.API.EBackupUnit.LocalPatrol; break;
                        }

                        // Use specific PR methods for air units
                        if (unitType == AutoBackupUnitType.AirUnit)
                        {
                            dispatchedViaPR = PolicingRedefined.API.BackupAPI.RequestAirBackup(prBackupUnit, true, false, true);
                        }
                        else // For other unit types
                        {
                            dispatchedViaPR = PolicingRedefined.API.BackupAPI.RequestBackup(prBackupUnit, prResponseCode, true, false, true);
                        }

                        if (dispatchedViaPR)
                        {
                            Game.LogTrivial($"AutoBackup+: Dispatched specific {unitType} via PR API.");
                            DisplaySubtitle($"PR {unitType} unit responding.");
                        }
                    }
                    catch (Exception ex)
                    {
                        Game.LogTrivial($"AutoBackup+: Error dispatching specific {unitType} via PR direct call: {ex.Message}");
                        dispatchedViaPR = false; // Fallback to LSPDFR if PR fails
                    }
                }

                // --- LSPDFR Fallback / Default Dispatch ---
                if (!dispatchedViaPR)
                {
                    // Map unit type to LSPDFR type
                    switch (unitType)
                    {
                        case AutoBackupUnitType.LocalUnit:
                            lspdfrUnitType = LSPD_First_Response.EBackupUnitType.LocalUnit;
                            break;
                        case AutoBackupUnitType.StateUnit:
                            lspdfrUnitType = LSPD_First_Response.EBackupUnitType.StateUnit;
                            break;
                        case AutoBackupUnitType.K9:
                            lspdfrUnitType = LSPD_First_Response.EBackupUnitType.LocalUnit; // K9 requested as LocalUnit
                            break;
                        case AutoBackupUnitType.AirUnit:
                            lspdfrUnitType = LSPD_First_Response.EBackupUnitType.AirUnit;
                            break;
                        case AutoBackupUnitType.SwatTeam:
                            lspdfrUnitType = LSPD_First_Response.EBackupUnitType.SwatTeam;
                            break;
                        default:
                            lspdfrUnitType = LSPD_First_Response.EBackupUnitType.LocalUnit;
                            break;
                    }

                    Functions.RequestBackup(Game.LocalPlayer.Character.Position, lspdfrResponseType, lspdfrUnitType);
                    Game.LogTrivial($"AutoBackup+: Dispatched specific {unitType} via LSPDFR API.");
                    DisplaySubtitle($"LSPDFR {unitType} unit responding.");
                }

                SafeSleep(1500);

                // Find and add the spawned officer
                Ped spawnedOfficer = null;
                foreach (Ped ped in World.EnumeratePeds())
                {
                    if (ped.Exists() && Functions.IsPedACop(ped) && !ped.IsPlayer &&
                        !activeDispatchedUnits.Any(t => t.Item1 == ped))
                    {
                        if (ped.DistanceTo(Game.LocalPlayer.Character) < 200f)
                        {
                            // If PR dispatched, verify if this ped is a PR backup ped
                            if (dispatchedViaPR && _prIntegrationEnabled) // Direct call
                            {
                                try
                                {
                                    if (PolicingRedefined.API.PedAPI.IsPedBackupPed(ped)) // Direct call
                                    {
                                        spawnedOfficer = ped;
                                        break;
                                    }
                                }
                                catch (Exception ex)
                                {
                                    Game.LogTrivial($"AutoBackup+: Error verifying PR ped: {ex.Message}");
                                    spawnedOfficer = ped; // Fallback if verification fails
                                    break;
                                }
                            }
                            else if (!dispatchedViaPR) // If LSPDFR dispatched
                            {
                                spawnedOfficer = ped;
                                break;
                            }
                        }
                    }
                }

                if (spawnedOfficer != null)
                {
                    if (settings.EnableOfficerAvailabilitySystem && !IsOfficerAvailable(spawnedOfficer))
                    {
                        spawnedOfficer.Dismiss();
                        Game.LogTrivial($"AutoBackup+: Specific unit {unitType} officer unavailable, dismissed");
                        // Recursively call to try dispatching again if officer was unavailable
                        DispatchSpecificUnitType(pursuitHandle, unitType);
                        return;
                    }

                    Functions.AddCopToPursuit(currentPursuit, spawnedOfficer);
                    activeDispatchedUnits.Add(Tuple.Create(spawnedOfficer, unitType, dispatchedViaPR)); // Track source
                    if (settings.EnableOfficerAvailabilitySystem)
                    {
                        SetOfficerStatus(spawnedOfficer, OfficerAvailabilityStatus.OnCall);
                    }
                    Game.LogTrivial($"AutoBackup+: Added specific unit {unitType} officer {spawnedOfficer.Handle} to pursuit (Source: {(dispatchedViaPR ? "PR" : "LSPDFR")})");
                }
            }
            catch (Exception e)
            {
                Game.LogTrivial($"AutoBackup+: Error dispatching specific unit type {unitType}: {e.Message}");
            }
        }

        private static void ExecuteStandDown()
        {
            try
            {
                if (currentPursuit == null || !IsPursuitStillActive(currentPursuit))
                {
                    DisplaySubtitle("No active pursuit to stand down from");
                    return;
                }

                int unitsStoodDown = 0;

                // Stand down all units except essential ones
                foreach (var unitTuple in activeDispatchedUnits.ToList())
                {
                    Ped unit = unitTuple.Item1;
                    AutoBackupUnitType unitType = unitTuple.Item2;

                    if (unit.Exists() && unit.IsAlive)
                    {
                        // Keep essential units based on settings
                        if (ShouldKeepUnitDuringStandDown(unitType))
                        {
                            Game.LogTrivial($"AutoBackup+: Keeping essential unit {unit.Handle} ({unitType}) during stand down");
                            continue;
                        }

                        // Stand down the unit
                        StandDownUnit(unit, unitType);
                        unitsStoodDown++;
                    }
                }

                Game.LogTrivial($"AutoBackup+: Stand down executed - {unitsStoodDown} units dismissed");
                DisplaySubtitle($"Stand down executed: {unitsStoodDown} units dismissed");

                // Update current units count
                lock (pursuitLock)
                {
                }
            }
            catch (Exception e)
            {
                Game.LogTrivial($"AutoBackup+: Error executing stand down: {e.Message}");
            }
        }

        private static void ExecuteSelectiveStandDown()
        {
            try
            {
                if (currentPursuit == null || !IsPursuitStillActive(currentPursuit))
                {
                    DisplaySubtitle("No active pursuit for selective stand down");
                    return;
                }

                // Determine which unit type to stand down based on priority
                var unitTypeCounts = new Dictionary<AutoBackupUnitType, int>();
                foreach (var unitTuple in activeDispatchedUnits)
                {
                    if (unitTuple.Item1.Exists())
                    {
                        var unitType = unitTuple.Item2;
                        unitTypeCounts[unitType] = unitTypeCounts.ContainsKey(unitType) ? unitTypeCounts[unitType] + 1 : 1;
                    }
                }

                // Priority order for selective stand down (least essential first)
                var standDownPriority = new AutoBackupUnitType[]
                {
            AutoBackupUnitType.AirUnit,
            AutoBackupUnitType.SwatTeam,
            AutoBackupUnitType.K9,
            AutoBackupUnitType.StateUnit,
            AutoBackupUnitType.LocalUnit
                };

                AutoBackupUnitType? typeToStandDown = null;
                foreach (var unitType in standDownPriority)
                {
                    if (unitTypeCounts.ContainsKey(unitType) && unitTypeCounts[unitType] > 0)
                    {
                        typeToStandDown = unitType;
                        break;
                    }
                }

                if (typeToStandDown.HasValue)
                {
                    // Find and stand down one unit of the selected type
                    var unitToStandDown = activeDispatchedUnits.FirstOrDefault(u =>
                        u.Item1.Exists() && u.Item2 == typeToStandDown.Value);

                    if (unitToStandDown != null)
                    {
                        StandDownUnit(unitToStandDown.Item1, unitToStandDown.Item2);
                        Game.LogTrivial($"AutoBackup+: Selective stand down - dismissed {typeToStandDown.Value} unit");
                        DisplaySubtitle($"Selective stand down: {typeToStandDown.Value} unit dismissed");
                    }
                }
                else
                {
                    DisplaySubtitle("No units available for selective stand down");
                }
            }
            catch (Exception e)
            {
                Game.LogTrivial($"AutoBackup+: Error executing selective stand down: {e.Message}");
            }
        }

        private static void StandDownUnit(Ped unit, AutoBackupUnitType unitType)
        {
            try
            {
                if (!unit.Exists()) return;

                bool isPRUnit = false;
                // Find if this unit was dispatched by PR
                var unitEntry = activeDispatchedUnits.FirstOrDefault(u => u.Item1 == unit);
                if (unitEntry != null)
                {
                    isPRUnit = unitEntry.Item3;
                }

                // Remove from pursuit
                if (currentPursuit != null && IsPursuitStillActive(currentPursuit))
                {
                    Functions.RemovePedFromPursuit(unit);
                }

                // Clear current tasks
                unit.Tasks.ClearImmediately();

                // Set availability status if system is enabled
                if (settings.EnableOfficerAvailabilitySystem && officerAvailabilitySystem.ContainsKey(unit))
                {
                    SetOfficerStatus(unit, OfficerAvailabilityStatus.Available);
                }

                // Remove from tracking
                activeDispatchedUnits.RemoveAll(u => u.Item1 == unit);

                // Clear tactics if assigned
                if (officerTactics.ContainsKey(unit))
                {
                    officerTactics.Remove(unit);
                }

                // Dismiss the unit based on settings and source
                if (settings.StandDownBehavior == StandDownBehavior.Dismiss)
                {
                    if (isPRUnit && _prIntegrationEnabled)
                    {
                        // PolicingRedefined does not have an API to dismiss individual units by Ped handle.
                        // Calling DismissAllBackupUnits here would dismiss ALL PR units, which is not desired for individual stand down.
                        // Therefore, for individual PR units, we rely on PR's internal cleanup or just remove them from our tracking.
                        Game.LogTrivial($"AutoBackup+: PR unit {unit.Handle} ({unitType}) marked for dismissal. Relying on PR's internal logic for despawn/cleanup.");
                    }
                    else // LSPDFR units
                    {
                        unit.Dismiss();
                    }
                }
                else if (settings.StandDownBehavior == StandDownBehavior.ReturnToPatrol)
                {
                    // Send unit on patrol in the area
                    Vector3 patrolArea = Game.LocalPlayer.Character.Position + new Vector3(MathHelper.GetRandomSingle(-200f, 200f), MathHelper.GetRandomSingle(-200f, 200f), 0f);
                    NativeFunction.Natives.TASK_PATROL(unit, patrolArea.X, patrolArea.Y, patrolArea.Z, 100f, 10f);
                }
                else if (settings.StandDownBehavior == StandDownBehavior.StandBy)
                {
                    // Make unit stand by in current location
                    Vector3 standByPos = unit.Position;
                    NativeFunction.Natives.TASK_STAND_GUARD(unit, standByPos.X, standByPos.Y, standByPos.Z, unit.Heading, "WORLD_HUMAN_COP_IDLES");
                }

                Game.LogTrivial($"AutoBackup+: Unit {unit.Handle} ({unitType}) stood down with behavior: {settings.StandDownBehavior} (Source: {(isPRUnit ? "PR" : "LSPDFR")})");
            }
            catch (Exception e)
            {
                Game.LogTrivial($"AutoBackup+: Error standing down unit {unit.Handle}: {e.Message}");
            }
        }

        private static bool ShouldKeepUnitDuringStandDown(AutoBackupUnitType unitType)
        {
            switch (unitType)
            {
                case AutoBackupUnitType.LocalUnit:
                    return !settings.StandDownLocalUnits;
                case AutoBackupUnitType.StateUnit:
                    return !settings.StandDownStateUnits;
                case AutoBackupUnitType.K9:
                    return !settings.StandDownK9Units;
                case AutoBackupUnitType.AirUnit:
                    return !settings.StandDownAirUnits;
                case AutoBackupUnitType.SwatTeam:
                    return !settings.StandDownSwatUnits;
                default:
                    return false;
            }
        }

        private static void CheckAutomaticStandDownConditions()
        {
            try
            {
                if (!settings.EnableAutomaticStandDown || currentPursuit == null || !IsPursuitStillActive(currentPursuit))
                    return;

                // Check if pursuit has been going on too long
                if (settings.StandDownAfterTime > 0)
                {
                    // This would need pursuit start time tracking - simplified for now
                    // You could add pursuit start time tracking if needed
                }

                // Check if too many units are active
                if (settings.StandDownIfTooManyUnits && activeDispatchedUnits.Count(u => u.Item1.Exists()) > settings.MaxUnitsBeforeStandDown)
                {
                    Game.LogTrivial($"AutoBackup+: Too many units ({activeDispatchedUnits.Count}), executing automatic stand down");
                    ExecuteStandDown();
                }

                // Check if pursuit suspects are down/arrested
                if (settings.StandDownIfSuspectsDown)
                {
                    Ped[] suspects = Functions.GetPursuitPeds(currentPursuit);
                    bool allSuspectsDown = true;

                    foreach (var suspect in suspects)
                    {
                        if (suspect.Exists() && suspect.IsAlive && !NativeFunction.Natives.IS_PED_BEING_ARRESTED<bool>(suspect))
                        {
                            allSuspectsDown = false;
                            break;
                        }
                    }

                    if (allSuspectsDown && suspects.Length > 0)
                    {
                        Game.LogTrivial("AutoBackup+: All suspects down/arrested, executing automatic stand down");
                        ExecuteStandDown();
                    }
                }
            }
            catch (Exception e)
            {
                Game.LogTrivial($"AutoBackup+: Error checking automatic stand down conditions: {e.Message}");
            }
        }

        private static bool IsAnyNearbyPedShooting()
        {
            try
            {
                var nearbyPeds = World.GetAllPeds().Where(p => p != null && p.Exists() && !p.IsPlayer).ToArray();

                foreach (Ped ped in nearbyPeds)
                {
                    try
                    {
                        if (ped.DistanceTo(Game.LocalPlayer.Character) < 100f && ped.IsShooting)
                        {
                            return true;
                        }
                    }
                    catch (Exception ex)
                    {
                        Game.LogTrivial($"AutoBackup+: Error checking ped shooting status: {ex.Message}");
                        continue;
                    }
                }
            }
            catch (Exception e)
            {
                Game.LogTrivial($"AutoBackup+: Error checking for shooting peds: {e.Message}");
            }
            return false;
        }

        private static bool IsAnyNearbyOfficerDown()
        {
            try
            {
                var nearbyPeds = World.GetAllPeds().Where(p => p != null && p.Exists() && !p.IsPlayer).ToArray();

                foreach (Ped ped in nearbyPeds)
                {
                    try
                    {
                        if (Functions.IsPedACop(ped) && (ped.IsDead || ped.IsInjured))
                        {
                            bool isTrackedUnit = false;
                            bool isPRBackupPed = false;

                            // Check if it's a PR backup ped (Direct Call)
                            if (_prIntegrationEnabled)
                            {
                                try
                                {
                                    isPRBackupPed = PolicingRedefined.API.PedAPI.IsPedBackupPed(ped);
                                }
                                catch (Exception ex)
                                {
                                    Game.LogTrivial($"AutoBackup+: Error calling PR.PedAPI.IsPedBackupPed: {ex.Message}");
                                }
                            }

                            // Check if it's a unit tracked by this plugin
                            lock (pursuitLock)
                            {
                                isTrackedUnit = activeDispatchedUnits.Any(t => t.Item1 == ped);
                            }

                            // Consider it an officer down if it's a tracked unit, a PR backup ped, or just a nearby cop
                            if (isTrackedUnit || isPRBackupPed || ped.DistanceTo(Game.LocalPlayer.Character) < 150f)
                            {
                                return true;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Game.LogTrivial($"AutoBackup+: Error checking officer status: {ex.Message}");
                        continue;
                    }
                }
            }
            catch (Exception e)
            {
                Game.LogTrivial($"AutoBackup+: Error checking for downed officers: {e.Message}");
            }
            return false;
        }

        private static void ProcessTimeBasedEscalation()
        {
            while (isRunning)
            {
                try
                {
                    GameFiber.Yield();

                    if (!ValidateGameState()) continue;

                    LHandle localPursuitCopy;
                    DateTime localLastUnitAdded;
                    int local;

                    // Use a lock to safely get copies of the shared variables
                    lock (pursuitLock)
                    {
                        localPursuitCopy = currentPursuit;
                        localLastUnitAdded = lastUnitAdded;
                    }

                    if (localPursuitCopy != null && settings.TimeEscalation && IsPursuitStillActive(localPursuitCopy))
                    {
                        // Check if we need to add more units based on time
                        if (localLastUnitAdded != DateTime.MinValue /*&& local < settings.MaxUnits*/)
                        {
                            TimeSpan timeSinceLastUnit = DateTime.Now - localLastUnitAdded;
                            if (timeSinceLastUnit.TotalSeconds >= settings.AddUnitEveryXSeconds)
                            {
                                DispatchAdditionalBackup(localPursuitCopy); // Pass pursuit handle

                                lock (pursuitLock)
                                {
                                    lastUnitAdded = DateTime.Now;
                                }
                            }
                        }
                    }
                }
                catch (ThreadAbortException) { Game.LogTrivial("AutoBackup+: Time Escalation terminated gracefully."); return; }
                catch (Exception e)
                {
                    Game.LogTrivial($"AutoBackup+: Error in time-based escalation: {e.Message}");
                    SafeSleep(5000); // Wait a bit before continuing to prevent spam
                }

                SafeSleep(1000);
            }
        }

        private static void DispatchAdditionalBackup(LHandle pursuitHandle) // Pass pursuit handle
        {
            try
            {
                //int local;

                lock (pursuitLock)
                {
                }

                //if (local < settings.MaxUnits)
                {
                    DispatchRegularUnit(pursuitHandle); // Pass pursuit handle

                    lock (pursuitLock)
                    {
                    }

                    DisplaySubtitle("Additional backup dispatched."); //Total units: " + local);
                    Game.LogTrivial($"AutoBackup+: Additional unit dispatched."); //, total: {local}");
                }
            }
            catch (Exception e)
            {
                Game.LogTrivial($"AutoBackup+: Error dispatching additional backup: {e.Message}");
            }
        }

        private static void DispatchRegularUnit(LHandle pursuitHandle)
        {
            try
            {
                if (pursuitHandle == null || !IsPursuitStillActive(pursuitHandle)) return;

                LSPD_First_Response.EBackupResponseType lspdfrResponseType = LSPD_First_Response.EBackupResponseType.Pursuit;
                LSPD_First_Response.EBackupUnitType lspdfrUnitTypeToRequest;
                AutoBackupUnitType internalUnitType = AutoBackupUnitType.LocalUnit;
                bool dispatchedViaPR = false;

                // Determine unit type based on configured chances
                Random random = new Random();
                int unitTypeRoll = random.Next(0, 100);

                // Ensure exclusive unit selection based on chances (ordered by priority if chances overlap)
                if (settings.EnableK9Unit && unitTypeRoll < settings.K9UnitChance)
                {
                    internalUnitType = AutoBackupUnitType.K9;
                }
                else if (settings.EnableSheriffUnit && unitTypeRoll < settings.SheriffUnitChance)
                {
                    internalUnitType = AutoBackupUnitType.StateUnit; // Map Sheriff to StateUnit for LSPDFR API
                }
                else if (settings.EnableStateUnit && unitTypeRoll < settings.StateUnitChance)
                {
                    internalUnitType = AutoBackupUnitType.StateUnit;
                }
                else // Default to LocalUnit
                {
                    internalUnitType = AutoBackupUnitType.LocalUnit;
                }

                // Apply unit limits
                if (internalUnitType == AutoBackupUnitType.K9 && activeDispatchedUnits.Count(u => u.Item2 == AutoBackupUnitType.K9 && u.Item1.Exists()) >= settings.MaxK9Units)
                {
                    internalUnitType = AutoBackupUnitType.LocalUnit; // Fallback to Local if K9 maxed internally
                }
                if ((internalUnitType == AutoBackupUnitType.StateUnit) && activeDispatchedUnits.Count(u => u.Item2 == AutoBackupUnitType.StateUnit && u.Item1.Exists()) >= settings.MaxPatrolUnits) // Using MaxPatrolUnits for State/Sheriff
                {
                    internalUnitType = AutoBackupUnitType.LocalUnit; // Fallback to Local if State/Sheriff maxed internally
                }
                if (internalUnitType == AutoBackupUnitType.LocalUnit && activeDispatchedUnits.Count(u => u.Item2 == AutoBackupUnitType.LocalUnit && u.Item1.Exists()) >= settings.MaxPatrolUnits)
                {
                    Game.LogTrivial("AutoBackup+: Max patrol units reached, skipping dispatch.");
                    return; // Don't dispatch if default LocalUnit is also maxed
                }

                // Regional variations (simplified example)
                string currentZoneName = NativeFunction.Natives.GET_NAME_OF_ZONE(Game.LocalPlayer.Character.Position.X, Game.LocalPlayer.Character.Position.Y, Game.LocalPlayer.Character.Position.Z);
                if ((currentZoneName == "LOS_SANTOS_COUNTY" || currentZoneName == "BLAINE_COUNTY" || currentZoneName == "PALETO_BAY") && settings.EnableSheriffUnit) // Example zones for Sheriff
                {
                    // Only override if not already a more specialized unit (like K9, SWAT, Air already chosen)
                    if (internalUnitType == AutoBackupUnitType.LocalUnit)
                    {
                        internalUnitType = AutoBackupUnitType.StateUnit; // Request Sheriff if in county and not specialized
                    }
                }

                // --- PolicingRedefined Integration (Direct Calls) ---
                if (_prIntegrationEnabled)
                {
                    try
                    {
                        PolicingRedefined.API.EBackupUnit prBackupUnit;
                        switch (internalUnitType)
                        {
                            case AutoBackupUnitType.LocalUnit: prBackupUnit = PolicingRedefined.API.EBackupUnit.LocalPatrol; break;
                            case AutoBackupUnitType.StateUnit: prBackupUnit = PolicingRedefined.API.EBackupUnit.StatePatrol; break;
                            case AutoBackupUnitType.K9: prBackupUnit = PolicingRedefined.API.EBackupUnit.LocalK9Patrol; break;
                            default: prBackupUnit = PolicingRedefined.API.EBackupUnit.LocalPatrol; break;
                        }

                        PolicingRedefined.Backup.Entities.EBackupResponseCode prResponseCode = PolicingRedefined.Backup.Entities.EBackupResponseCode.Code2; // Default to Code2

                        dispatchedViaPR = PolicingRedefined.API.BackupAPI.RequestBackup(prBackupUnit, prResponseCode, true, false, true);

                        if (dispatchedViaPR)
                        {
                            Game.LogTrivial($"AutoBackup+: Dispatched {internalUnitType} via PR.BackupAPI.RequestBackup");
                            DisplaySubtitle($"PR {internalUnitType} unit responding to pursuit");
                        }
                    }
                    catch (Exception ex)
                    {
                        Game.LogTrivial($"AutoBackup+: Error dispatching regular unit via PR direct call: {ex.Message}");
                        dispatchedViaPR = false; // Fallback to LSPDFR if PR fails
                    }
                }

                // --- LSPDFR Fallback / Default Dispatch ---
                if (!dispatchedViaPR)
                {
                    // Map our internal type to LSPDFR's EBackupUnitType for the RequestBackup call
                    switch (internalUnitType)
                    {
                        case AutoBackupUnitType.LocalUnit:
                            lspdfrUnitTypeToRequest = LSPD_First_Response.EBackupUnitType.LocalUnit;
                            break;
                        case AutoBackupUnitType.StateUnit: // For both State and Sheriff
                            lspdfrUnitTypeToRequest = LSPD_First_Response.EBackupUnitType.StateUnit;
                            break;
                        case AutoBackupUnitType.K9:
                            lspdfrUnitTypeToRequest = LSPD_First_Response.EBackupUnitType.LocalUnit; // K9 often requested as LocalUnit in LSPDFR
                            break;
                        default:
                            lspdfrUnitTypeToRequest = LSPD_First_Response.EBackupUnitType.LocalUnit;
                            break;
                    }

                    Functions.RequestBackup(Game.LocalPlayer.Character.Position, lspdfrResponseType, lspdfrUnitTypeToRequest);
                    Game.LogTrivial($"AutoBackup+: Dispatched {internalUnitType} (LSPDFR requested as {lspdfrUnitTypeToRequest}) to pursuit");
                    DisplaySubtitle($"LSPDFR {internalUnitType} unit responding to pursuit");
                }

                SafeSleep(1500); // Wait for the backup unit to spawn

                Ped spawnedOfficer = null;
                // Find the newly spawned officer ped near player or pursuit
                foreach (Ped ped in World.EnumeratePeds())
                {
                    // Check if it's a cop, not player, and not already tracked by us
                    if (ped.Exists() && Functions.IsPedACop(ped) && !ped.IsPlayer &&
                        !activeDispatchedUnits.Any(t => t.Item1 == ped))
                    {
                        // Check if they are either close or part of the active pursuit (more robust)
                        if (ped.DistanceTo(Game.LocalPlayer.Character) < 200f || NativeFunction.Natives.IS_PED_IN_PURSUIT<bool>(ped))
                        {
                            // If PR dispatched, verify if this ped is a PR backup ped
                            if (dispatchedViaPR && _prIntegrationEnabled) // Direct call
                            {
                                try
                                {
                                    if (PolicingRedefined.API.PedAPI.IsPedBackupPed(ped)) // Direct call
                                    {
                                        spawnedOfficer = ped;
                                        break;
                                    }
                                }
                                catch (Exception ex)
                                {
                                    Game.LogTrivial($"AutoBackup+: Error verifying PR ped: {ex.Message}");
                                    // If verification fails, still consider it if it's the only candidate
                                    spawnedOfficer = ped;
                                    break;
                                }
                            }
                            else if (!dispatchedViaPR) // If LSPDFR dispatched
                            {
                                spawnedOfficer = ped;
                                break;
                            }
                        }
                    }
                }

                if (spawnedOfficer != null)
                {
                    // CHECK AVAILABILITY - This is the key part of the availability system
                    if (settings.EnableOfficerAvailabilitySystem && !IsOfficerAvailable(spawnedOfficer))
                    {
                        var status = officerAvailabilitySystem[spawnedOfficer];
                        spawnedOfficer.Dismiss();
                        Game.LogTrivial($"AutoBackup+: Officer {spawnedOfficer.Handle} unavailable ({status}), requesting another unit");
                        DisplaySubtitle($"Unit unavailable ({status}), requesting backup...");
                        // Recursively call to try dispatching again if officer was unavailable
                        DispatchRegularUnit(pursuitHandle);
                        return;
                    }

                    Functions.AddCopToPursuit(currentPursuit, spawnedOfficer); // Add to current pursuit
                    activeDispatchedUnits.Add(Tuple.Create(spawnedOfficer, internalUnitType, dispatchedViaPR)); // Track this unit with its requested type and source
                    if (settings.EnableOfficerAvailabilitySystem)
                    {
                        SetOfficerStatus(spawnedOfficer, OfficerAvailabilityStatus.OnCall); // Mark as OnCall
                    }
                    Game.LogTrivial($"AutoBackup+: Officer {spawnedOfficer.Handle} dispatched as {internalUnitType} (Source: {(dispatchedViaPR ? "PR" : "LSPDFR")})");
                }
                else
                {
                    Game.LogTrivial("AutoBackup+: Failed to find newly dispatched officer.");
                }
            }
            catch (Exception e)
            {
                Game.LogTrivial($"AutoBackup+: Error dispatching regular unit: {e.Message}");
            }
        }

        private static void DispatchAirUnit()
        {
            try
            {
                LHandle localPursuitCopy;

                lock (pursuitLock)
                {
                    localPursuitCopy = currentPursuit;
                }

                if (!settings.EnableAirUnit || localPursuitCopy == null || activeDispatchedUnits.Count(u => u.Item2 == AutoBackupUnitType.AirUnit && u.Item1.Exists()) >= settings.MaxAirUnits) return;

                bool dispatchedViaPR = false;

                // --- PolicingRedefined Integration (Direct Calls) ---
                if (_prIntegrationEnabled)
                {
                    try
                    {
                        PolicingRedefined.API.EBackupUnit prAirUnit = PolicingRedefined.API.EBackupUnit.LocalAir;
                        dispatchedViaPR = PolicingRedefined.API.BackupAPI.RequestAirBackup(prAirUnit, true, false, true); // dispatchNotif, dispatchAnim, dispatchAudio

                        if (dispatchedViaPR)
                        {
                            Game.LogTrivial("AutoBackup+: Air unit dispatched via PR.BackupAPI.RequestAirBackup");
                            DisplaySubtitle("PR Air support has been dispatched to your location.");
                        }
                    }
                    catch (Exception ex)
                    {
                        Game.LogTrivial($"AutoBackup+: Error dispatching air unit via PR direct call: {ex.Message}");
                        dispatchedViaPR = false; // Fallback to LSPDFR if PR fails
                    }
                }

                // --- LSPDFR Fallback / Default Dispatch ---
                if (!dispatchedViaPR)
                {
                    Functions.RequestBackup(Game.LocalPlayer.Character.Position,
                        LSPD_First_Response.EBackupResponseType.Pursuit,
                        LSPD_First_Response.EBackupUnitType.AirUnit);

                    Game.LogTrivial("AutoBackup+: Air unit dispatched via LSPDFR API.");
                    DisplaySubtitle("LSPDFR Air support has been dispatched to your location.");
                }

                // Air unit will be added to activeDispatchedUnits when found
            }
            catch (Exception e)
            {
                Game.LogTrivial($"AutoBackup+: Error dispatching air unit: {e.Message}");
            }
        }

        private static void DispatchSWAT()
        {
            try
            {
                LHandle localPursuitCopy;

                lock (pursuitLock)
                {
                    localPursuitCopy = currentPursuit;
                }

                if (!settings.EnableSWAT || localPursuitCopy == null || activeDispatchedUnits.Count(u => u.Item2 == AutoBackupUnitType.SwatTeam && u.Item1.Exists()) >= settings.MaxSWATUnits) return;

                bool dispatchedViaPR = false;

                // --- PolicingRedefined Integration (Direct Calls) ---
                if (_prIntegrationEnabled)
                {
                    try
                    {
                        PolicingRedefined.API.EBackupUnit prSwatUnit = PolicingRedefined.API.EBackupUnit.LocalSWAT;
                        PolicingRedefined.Backup.Entities.EBackupResponseCode prResponseCode = PolicingRedefined.Backup.Entities.EBackupResponseCode.Code3; // SWAT usually Code3
                        dispatchedViaPR = PolicingRedefined.API.BackupAPI.RequestBackup(prSwatUnit, prResponseCode, true, false, true); // dispatchNotif, dispatchAnim, dispatchAudio

                        if (dispatchedViaPR)
                        {
                            Game.LogTrivial("AutoBackup+: SWAT team dispatched via PR.BackupAPI.RequestBackup");
                            DisplaySubtitle("PR SWAT team has been dispatched to your location.");
                        }
                    }
                    catch (Exception ex)
                    {
                        Game.LogTrivial($"AutoBackup+: Error dispatching SWAT via PR direct call: {ex.Message}");
                        dispatchedViaPR = false; // Fallback to LSPDFR if PR fails
                    }
                }

                // --- LSPDFR Fallback / Default Dispatch ---
                if (!dispatchedViaPR)
                {
                    Functions.RequestBackup(Game.LocalPlayer.Character.Position,
                        LSPD_First_Response.EBackupResponseType.Pursuit,
                        LSPD_First_Response.EBackupUnitType.SwatTeam);

                    Game.LogTrivial("AutoBackup+: SWAT team dispatched via LSPDFR API.");
                    DisplaySubtitle("LSPDFR SWAT team has been dispatched to your location.");
                }

                // SWAT unit will be added to activeDispatchedUnits when found
            }
            catch (Exception e)
            {
                Game.LogTrivial($"AutoBackup+: Error dispatching SWAT: {e.Message}");
            }
        }

        private static void DisplaySubtitle(string message)
        {
            try
            {
                if (settings.ShowSubtitles)
                {
                    Game.DisplaySubtitle("~b~AutoBackup+: ~w~" + message, 5000);
                }
            }
            catch (Exception e)
            {
                Game.LogTrivial($"AutoBackup+: Error displaying subtitle: {e.Message}");
            }
        }

        private static void LoadSettings()
        {
            settings = new Settings();
            string path = "plugins/LSPDFR/AutoBackupPlus.ini";

            if (File.Exists(path))
            {
                try
                {
                    InitializationFile ini = new InitializationFile(path);

                    settings.InitialUnits = ini.ReadInt32("Settings", "InitialUnits", 2);
                    settings.AddUnitEveryXSeconds = ini.ReadInt32("Settings", "AddUnitEveryXSeconds", 45);
                    settings.MaxUnits = ini.ReadInt32("Settings", "MaxUnits", 6);

                    settings.EnableShotsFiredTrigger = ini.ReadBoolean("SeverityTriggers", "EnableShotsFiredTrigger", true);
                    settings.EnableOfficerDownTrigger = ini.ReadBoolean("SeverityTriggers", "EnableOfficerDownTrigger", true);
                    settings.EnableShotsFiredTrafficStopSWAT = ini.ReadBoolean("SeverityTriggers", "EnableShotsFiredTrafficStopSWAT", true);
                    settings.TimeEscalation = ini.ReadBoolean("SeverityTriggers", "EnableTimeEscalation", true);

                    // These are general enable/disable flags for unit types
                    settings.EnableAirUnit = ini.ReadBoolean("UnitTypes", "EnableAirUnit", true);
                    settings.EnableSWAT = ini.ReadBoolean("UnitTypes", "EnableSWAT", true);
                    settings.EnableK9Unit = ini.ReadBoolean("UnitTypes", "EnableK9Unit", true);
                    settings.EnableSheriffUnit = ini.ReadBoolean("UnitTypes", "EnableSheriffUnit", true);
                    settings.EnableStateUnit = ini.ReadBoolean("UnitTypes", "EnableStateUnit", true);

                    // Response chances for our internal unit types
                    settings.LocalUnitChance = ini.ReadInt32("ResponseChances", "LocalUnitChance", 70);
                    settings.StateUnitChance = ini.ReadInt32("ResponseChances", "StateUnitChance", 15);
                    settings.SheriffUnitChance = ini.ReadInt32("ResponseChances", "SheriffUnitChance", 15); // Kept for INI config
                    settings.K9UnitChance = ini.ReadInt32("ResponseChances", "K9UnitChance", 10); // Kept for INI config

                    settings.MaxPatrolUnits = ini.ReadInt32("MaxUnitTypes", "MaxPatrolUnits", 6);
                    settings.MaxK9Units = ini.ReadInt32("MaxUnitTypes", "MaxK9Units", 2);
                    settings.MaxAirUnits = ini.ReadInt32("MaxUnitTypes", "MaxAirUnits", 1);
                    settings.MaxSWATUnits = ini.ReadInt32("MaxUnitTypes", "MaxSWATUnits", 2);

                    settings.ShowSubtitles = ini.ReadBoolean("Display", "ShowSubtitles", true);
                    settings.EnableOfficerLostNotification = ini.ReadBoolean("Display", "EnableOfficerLostNotification", true);

                    settings.EnableOnSceneProtocol = ini.ReadBoolean("OnSceneProtocol", "EnableOnSceneProtocol", true);
                    settings.DismissAllUnitsKey = ini.ReadEnum<System.Windows.Forms.Keys>("OnSceneProtocol", "DismissAllUnitsKey", System.Windows.Forms.Keys.F11);
                    settings.DismissAllUnitsModifierKey = ini.ReadEnum<System.Windows.Forms.Keys>("OnSceneProtocol", "DismissAllUnitsModifierKey", System.Windows.Forms.Keys.None);

                    // Ultimate Backup compatibility setting
                    settings.DisableIfUltimateBackupActive = ini.ReadBoolean("Compatibility", "DisableIfUltimateBackupActive", true);
                    settings.EnablePRIntegration = ini.ReadBoolean("Compatibility", "EnablePRIntegration", true);

                    settings.OfficerAvailabilityChance = ini.ReadInt32("Settings", "OfficerAvailabilityChance", 75);
                    settings.EnableOfficerAvailabilitySystem = ini.ReadBoolean("Settings", "EnableOfficerAvailabilitySystem", true);

                    settings.EnableAIPursuitTactics = ini.ReadBoolean("PursuitTactics", "EnableAIPursuitTactics", true);
                    settings.EnablePITManeuvers = ini.ReadBoolean("PursuitTactics", "EnablePITManeuvers", true);
                    settings.EnableRollingRoadblocks = ini.ReadBoolean("PursuitTactics", "EnableRollingRoadblocks", true);
                    settings.EnableDynamicPositioning = ini.ReadBoolean("PursuitTactics", "EnableDynamicPositioning", true);
                    settings.EnableRandomTacticChanges = ini.ReadBoolean("PursuitTactics", "EnableRandomTacticChanges", true);
                    settings.ShowTacticNotifications = ini.ReadBoolean("PursuitTactics", "ShowTacticNotifications", true);
                    settings.TacticChangeInterval = ini.ReadInt32("PursuitTactics", "TacticChangeInterval", 15);
                    settings.TacticChangeChance = ini.ReadInt32("PursuitTactics", "TacticChangeChance", 20);
                    settings.StandardTacticWeight = ini.ReadInt32("PursuitTactics", "StandardTacticWeight", 40);
                    settings.PITManeuverWeight = ini.ReadInt32("PursuitTactics", "PITManeuverWeight", 20);
                    settings.RollingRoadblockWeight = ini.ReadInt32("PursuitTactics", "RollingRoadblockWeight", 15);
                    settings.DynamicPositioningWeight = ini.ReadInt32("PursuitTactics", "DynamicPositioningWeight", 25);

                    settings.EnableCalloutSpecificBackup = ini.ReadBoolean("CalloutSpecificBackup", "EnableCalloutSpecificBackup", true);
                    settings.ShowCalloutNotifications = ini.ReadBoolean("CalloutSpecificBackup", "ShowCalloutNotifications", true);

                    settings.EnableStandDownFunctionality = ini.ReadBoolean("StandDownFunctionality", "EnableStandDownFunctionality", true);
                    settings.StandDownKey = ini.ReadEnum<System.Windows.Forms.Keys>("StandDownFunctionality", "StandDownKey", System.Windows.Forms.Keys.F10);
                    settings.StandDownModifierKey = ini.ReadEnum<System.Windows.Forms.Keys>("StandDownFunctionality", "StandDownModifierKey", System.Windows.Forms.Keys.None);
                    settings.EnableSelectiveStandDown = ini.ReadBoolean("StandDownFunctionality", "EnableSelectiveStandDown", true);
                    settings.SelectiveStandDownKey = ini.ReadEnum<System.Windows.Forms.Keys>("StandDownFunctionality", "SelectiveStandDownKey", System.Windows.Forms.Keys.F9);
                    settings.SelectiveStandDownModifierKey = ini.ReadEnum<System.Windows.Forms.Keys>("StandDownFunctionality", "SelectiveStandDownModifierKey", System.Windows.Forms.Keys.LShiftKey);
                    settings.StandDownBehavior = ini.ReadEnum<StandDownBehavior>("StandDownFunctionality", "StandDownBehavior", StandDownBehavior.ReturnToPatrol);
                    settings.EnableAutomaticStandDown = ini.ReadBoolean("StandDownFunctionality", "EnableAutomaticStandDown", true);
                    settings.StandDownIfTooManyUnits = ini.ReadBoolean("StandDownFunctionality", "StandDownIfTooManyUnits", true);
                    settings.MaxUnitsBeforeStandDown = ini.ReadInt32("StandDownFunctionality", "MaxUnitsBeforeStandDown", 8);
                    settings.StandDownIfSuspectsDown = ini.ReadBoolean("StandDownFunctionality", "StandDownIfSuspectsDown", true);
                    settings.StandDownAfterTime = ini.ReadInt32("StandDownFunctionality", "StandDownAfterTime", 300);
                    settings.StandDownLocalUnits = ini.ReadBoolean("StandDownFunctionality", "StandDownLocalUnits", true);
                    settings.StandDownStateUnits = ini.ReadBoolean("StandDownFunctionality", "StandDownStateUnits", true);
                    settings.StandDownK9Units = ini.ReadBoolean("StandDownFunctionality", "StandDownK9Units", false);
                    settings.StandDownAirUnits = ini.ReadBoolean("StandDownFunctionality", "StandDownAirUnits", true);
                    settings.StandDownSwatUnits = ini.ReadBoolean("StandDownFunctionality", "StandDownSwatUnits", false);

                    // Validate settings
                    if (settings.InitialUnits < 0) settings.InitialUnits = 0;
                    if (settings.InitialUnits > 10) settings.InitialUnits = 10;
                    if (settings.AddUnitEveryXSeconds < 5) settings.AddUnitEveryXSeconds = 5; // Min 5 seconds
                    if (settings.AddUnitEveryXSeconds > 300) settings.AddUnitEveryXSeconds = 300;
                    if (settings.MaxUnits < settings.InitialUnits) settings.MaxUnits = settings.InitialUnits;
                    if (settings.MaxUnits > 20) settings.MaxUnits = 20;

                    settings.LocalUnitChance = Math.Max(0, Math.Min(100, settings.LocalUnitChance));
                    settings.StateUnitChance = Math.Max(0, Math.Min(100, settings.StateUnitChance));
                    settings.SheriffUnitChance = Math.Max(0, Math.Min(100, settings.SheriffUnitChance));
                    settings.K9UnitChance = Math.Max(0, Math.Min(100, settings.K9UnitChance));

                    settings.MaxPatrolUnits = Math.Max(0, Math.Min(10, settings.MaxPatrolUnits));
                    settings.MaxK9Units = Math.Max(0, Math.Min(3, settings.MaxK9Units));
                    settings.MaxAirUnits = Math.Max(0, Math.Min(2, settings.MaxAirUnits));
                    settings.MaxSWATUnits = Math.Max(0, Math.Min(4, settings.MaxSWATUnits));

                    Game.LogTrivial("AutoBackup+ settings loaded successfully.");
                }
                catch (Exception e)
                {
                    Game.LogTrivial("Error loading AutoBackup+ settings: " + e.Message);
                    Game.DisplayNotification("~r~AutoBackup+: Error loading settings. Using defaults.");
                    // Reset to default values on error
                    settings = new Settings();
                }
            }
            else
            {
                // Create default settings file
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    using (StreamWriter sw = File.CreateText(path))
                    {
                        sw.WriteLine("[Settings]");
                        sw.WriteLine("InitialUnits = 2");
                        sw.WriteLine("AddUnitEveryXSeconds = 45");
                        sw.WriteLine("MaxUnits = 6");
                        sw.WriteLine("OfficerAvailabilityChance = 75");
                        sw.WriteLine("EnableOfficerAvailabilitySystem = true");
                        sw.WriteLine();
                        sw.WriteLine("[SeverityTriggers]");
                        sw.WriteLine("EnableShotsFiredTrigger = true");
                        sw.WriteLine("EnableOfficerDownTrigger = true");
                        sw.WriteLine("EnableShotsFiredTrafficStopSWAT = true");
                        sw.WriteLine("EnableTimeEscalation = true");
                        sw.WriteLine();
                        sw.WriteLine("[UnitTypes]");
                        sw.WriteLine("EnableAirUnit = true");
                        sw.WriteLine("EnableSWAT = true");
                        sw.WriteLine("EnableK9Unit = true");
                        sw.WriteLine("EnableSheriffUnit = true");
                        sw.WriteLine("EnableStateUnit = true");
                        sw.WriteLine();
                        sw.WriteLine("[ResponseChances]"); // NEW SECTION
                        sw.WriteLine("LocalUnitChance = 70");
                        sw.WriteLine("StateUnitChance = 15");
                        sw.WriteLine("SheriffUnitChance = 15"); // For INI
                        sw.WriteLine("K9UnitChance = 10"); // For INI
                        sw.WriteLine();
                        sw.WriteLine("[MaxUnitTypes]"); // NEW SECTION
                        sw.WriteLine("MaxPatrolUnits = 6");
                        sw.WriteLine("MaxK9Units = 2");
                        sw.WriteLine("MaxAirUnits = 1");
                        sw.WriteLine("MaxSWATUnits = 2");
                        sw.WriteLine();
                        sw.WriteLine("[Display]");
                        sw.WriteLine("ShowSubtitles = true");
                        sw.WriteLine("EnableOfficerLostNotification = true");
                        sw.WriteLine();
                        sw.WriteLine("[OnSceneProtocol]");
                        sw.WriteLine("EnableOnSceneProtocol = true");
                        sw.WriteLine("DismissAllUnitsKey = F11");
                        sw.WriteLine("DismissAllUnitsModifierKey = None");
                        sw.WriteLine("[PursuitTactics]");
                        sw.WriteLine("EnableAIPursuitTactics = true");
                        sw.WriteLine("EnablePITManeuvers = true");
                        sw.WriteLine("EnableRollingRoadblocks = true");
                        sw.WriteLine("EnableDynamicPositioning = true");
                        sw.WriteLine("EnableRandomTacticChanges = true");
                        sw.WriteLine("ShowTacticNotifications = true");
                        sw.WriteLine("TacticChangeInterval = 15");
                        sw.WriteLine("TacticChangeChance = 20");
                        sw.WriteLine("StandardTacticWeight = 40");
                        sw.WriteLine("PITManeuverWeight = 20");
                        sw.WriteLine("RollingRoadblockWeight = 15");
                        sw.WriteLine("DynamicPositioningWeight = 25");
                        sw.WriteLine();
                        sw.WriteLine("[CalloutSpecificBackup]");
                        sw.WriteLine("EnableCalloutSpecificBackup = true");
                        sw.WriteLine("ShowCalloutNotifications = true");
                        sw.WriteLine();
                        sw.WriteLine("[StandDownFunctionality]");
                        sw.WriteLine("EnableStandDownFunctionality = true");
                        sw.WriteLine("StandDownKey = F10");
                        sw.WriteLine("StandDownModifierKey = None");
                        sw.WriteLine("EnableSelectiveStandDown = true");
                        sw.WriteLine("SelectiveStandDownKey = F9");
                        sw.WriteLine("SelectiveStandDownModifierKey = LShiftKey");
                        sw.WriteLine("StandDownBehavior = ReturnToPatrol");
                        sw.WriteLine("EnableAutomaticStandDown = true");
                        sw.WriteLine("StandDownIfTooManyUnits = true");
                        sw.WriteLine("MaxUnitsBeforeStandDown = 8");
                        sw.WriteLine("StandDownIfSuspectsDown = true");
                        sw.WriteLine("StandDownAfterTime = 300");
                        sw.WriteLine("StandDownLocalUnits = true");
                        sw.WriteLine("StandDownStateUnits = true");
                        sw.WriteLine("StandDownK9Units = false");
                        sw.WriteLine("StandDownAirUnits = true");
                        sw.WriteLine("StandDownSwatUnits = false");
                        sw.WriteLine();
                        sw.WriteLine("[Compatibility]");
                        sw.WriteLine();
                        sw.WriteLine("[Compatibility]");
                        sw.WriteLine("DisableIfUltimateBackupActive = true");
                        sw.WriteLine("EnablePRIntegration = true");
                    }
                    Game.LogTrivial("AutoBackup+ default settings created.");
                }
                catch (Exception e)
                {
                    Game.LogTrivial("Error creating AutoBackup+ settings file: " + e.Message);
                }
            }
        }

        private static bool IsUltimateBackupLoadedViaDLL()
        {
            try
            {
                // Get all loaded assemblies in the current AppDomain
                var loadedAssemblies = AppDomain.CurrentDomain.GetAssemblies();
                // Check if any of them have "UltimateBackup" in their name (case-insensitive)
                return loadedAssemblies.Any(a => a.GetName().Name.IndexOf("UltimateBackup", StringComparison.OrdinalIgnoreCase) >= 0);
            }
            catch (Exception e)
            {
                Game.LogTrivial($"AutoBackup+: Error checking Ultimate Backup DLL: {e.Message}");
                return false;
            }
        }

        private static void UnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            Exception ex = e.ExceptionObject as Exception;
            string errorMessage = ex != null ? ex.Message : "Unknown error";
            string stackTrace = ex != null ? ex.StackTrace : "";

            Game.LogTrivial($"AutoBackup+ crashed: {errorMessage}");
            Game.LogTrivial($"Stack trace: {stackTrace}");
            Game.DisplayNotification("~r~AutoBackup+ has crashed. See the log for more information.");
        }

        private static bool ValidateGameState()
        {
            try
            {
                return Game.LocalPlayer != null &&
                       Game.LocalPlayer.Character != null &&
                       Game.LocalPlayer.Character.Exists();
            }
            catch
            {
                return false;
            }
        }
    }
    public class Settings
    {
        #region Variables
        // Initial settings
        public int InitialUnits { get; set; } = 2;
        public int AddUnitEveryXSeconds { get; set; } = 45;
        public int MaxUnits { get; set; } = 6;

        // Severity triggers
        public bool EnableShotsFiredTrigger { get; set; } = true;
        public bool EnableOfficerDownTrigger { get; set; } = true;
        public bool EnableShotsFiredTrafficStopSWAT { get; set; } = true;
        public bool TimeEscalation { get; set; } = true;

        // Unit types (general enable/disable)
        public bool EnableAirUnit { get; set; } = true;
        public bool EnableSWAT { get; set; } = true;
        public bool EnableK9Unit { get; set; } = true;
        public bool EnableSheriffUnit { get; set; } = true;
        public bool EnableStateUnit { get; set; } = true;

        // Response chances (NEW SECTION)
        public int LocalUnitChance { get; set; } = 70;
        public int StateUnitChance { get; set; } = 15;
        public int SheriffUnitChance { get; set; } = 15; // Kept for INI config
        public int K9UnitChance { get; set; } = 10; // Kept for INI config

        // Max unit counts per type (NEW SECTION)
        public int MaxPatrolUnits { get; set; } = 6;
        public int MaxK9Units { get; set; } = 2;
        public int MaxAirUnits { get; set; } = 1;
        public int MaxSWATUnits { get; set; } = 2;

        // Display options
        public bool ShowSubtitles { get; set; } = true;
        public bool EnableOfficerLostNotification { get; set; } = true;

        // Policing Redefined Integration
        public bool EnablePRIntegration { get; set; } = true;

        // On-Scene Protocol (NEW SECTION)
        public bool EnableOnSceneProtocol { get; set; } = true;
        public System.Windows.Forms.Keys DismissAllUnitsKey { get; set; } = System.Windows.Forms.Keys.F11;
        public System.Windows.Forms.Keys DismissAllUnitsModifierKey { get; set; } = System.Windows.Forms.Keys.None;

        // NEW: Compatibility settings
        public bool DisableIfUltimateBackupActive { get; set; } = true;
        public int OfficerAvailabilityChance { get; set; } = 75;
        public bool EnableOfficerAvailabilitySystem { get; set; } = true;
        public bool EnableAIPursuitTactics { get; set; } = true;
        public bool EnablePITManeuvers { get; set; } = true;
        public bool EnableRollingRoadblocks { get; set; } = true;
        public bool EnableDynamicPositioning { get; set; } = true;
        public bool EnableRandomTacticChanges { get; set; } = true;
        public bool ShowTacticNotifications { get; set; } = true;
        public int TacticChangeInterval { get; set; } = 15; // seconds
        public int TacticChangeChance { get; set; } = 20; // per 1000 (2%)
        public int StandardTacticWeight { get; set; } = 40;
        public int PITManeuverWeight { get; set; } = 20;
        public int RollingRoadblockWeight { get; set; } = 15;
        public int DynamicPositioningWeight { get; set; } = 25;
        public bool EnableCalloutSpecificBackup { get; set; } = true;
        public bool ShowCalloutNotifications { get; set; } = true;

        // Stand Down Functionality
        public bool EnableStandDownFunctionality { get; set; } = true;
        public System.Windows.Forms.Keys StandDownKey { get; set; } = System.Windows.Forms.Keys.F10;
        public System.Windows.Forms.Keys StandDownModifierKey { get; set; } = System.Windows.Forms.Keys.None;
        public bool EnableSelectiveStandDown { get; set; } = true;
        public System.Windows.Forms.Keys SelectiveStandDownKey { get; set; } = System.Windows.Forms.Keys.F9;
        public System.Windows.Forms.Keys SelectiveStandDownModifierKey { get; set; } = System.Windows.Forms.Keys.LShiftKey;
        public StandDownBehavior StandDownBehavior { get; set; } = StandDownBehavior.ReturnToPatrol;

        // Automatic Stand Down
        public bool EnableAutomaticStandDown { get; set; } = true;
        public bool StandDownIfTooManyUnits { get; set; } = true;
        public int MaxUnitsBeforeStandDown { get; set; } = 8;
        public bool StandDownIfSuspectsDown { get; set; } = true;
        public int StandDownAfterTime { get; set; } = 300; // seconds

        // Unit Type Stand Down Settings
        public bool StandDownLocalUnits { get; set; } = true;
        public bool StandDownStateUnits { get; set; } = true;
        public bool StandDownK9Units { get; set; } = false;
        public bool StandDownAirUnits { get; set; } = true;
        public bool StandDownSwatUnits { get; set; } = false;
        #endregion Variables
    }
}