﻿using System;
using System.Collections.Generic;
using System.Collections;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using HarmonyLib;

using LabFusion.Network;
using LabFusion.Representation;
using LabFusion.Syncables;
using LabFusion.Utilities;
using LabFusion.Data;

using SLZ.Marrow.Pool;

using UnityEngine;
using static MelonLoader.MelonLogger;
using MelonLoader;
using SLZ.Marrow.Warehouse;
using SLZ.Zones;
using LabFusion.Extensions;

namespace LabFusion.Patching
{
    [HarmonyPatch(typeof(AssetPoolee), nameof(AssetPoolee.OnSpawn))]
    public class PooleeOnSpawnPatch {
        public static void Postfix(AssetPoolee __instance, ulong spawnId) {
            if (PooleeUtilities.IsPlayer(__instance))
                return;

            try {
                if (NetworkInfo.HasServer && __instance.spawnableCrate)
                {
                    var barcode = __instance.spawnableCrate.Barcode;
                    bool hasSyncable = PropSyncable.Cache.ContainsSource(__instance.gameObject);

                    if (!NetworkInfo.IsServer)
                    {
                        // Check if we should prevent this object from spawning
                        if (hasSyncable || barcode == SpawnableWarehouseUtilities.FADE_OUT_BARCODE) {
                            __instance.gameObject.SetActive(false);
                        }
                        else if (!PooleeUtilities.IsForceEnabled(__instance) && PooleeUtilities.CanForceDespawn(__instance)) {
                            __instance.gameObject.SetActive(false);
                            MelonCoroutines.Start(CoForceDespawnRoutine(__instance));
                        }
                    }
                    else if (!hasSyncable)
                    {
                        if (PooleeUtilities.CanSendSpawn(__instance)) {
                            MelonCoroutines.Start(CoVerifySpawnedRoutine(__instance));
                        }
                    }
                }
            }
            catch (Exception e)
            {
#if DEBUG
                FusionLogger.LogException("to execute patch AssetPoolee.OnSpawn", e);
#endif
            }
        }

        private static IEnumerator CoForceDespawnRoutine(AssetPoolee __instance) {
            var go = __instance.gameObject;

            for (var i = 0; i < 3; i++) {
                yield return null;

                if (!PooleeUtilities.CanForceDespawn(__instance)) {
                    go.SetActive(true);
                    yield break;
                }

                if (PooleeUtilities.CanSpawn(__instance) || PooleeUtilities.IsForceEnabled(__instance))
                    yield break;

                go.SetActive(false);
            }
        }

        private static IEnumerator CoVerifySpawnedRoutine(AssetPoolee __instance) {
            while (LevelWarehouseUtilities.IsLoading())
                yield return null;

            for (var i = 0; i < 4; i++)
                yield return null;

            PooleeUtilities.RemoveCheckingForSpawn(__instance);

            try
            {
                if (PooleeUtilities.CanSendSpawn(__instance) && !PooleeUtilities.DequeueServerSpawned(__instance))
                {
                    var barcode = __instance.spawnableCrate.Barcode;

                    var syncId = SyncManager.AllocateSyncID();
                    PooleeUtilities.OnServerLocalSpawn(syncId, __instance.gameObject);

                    var zoneTracker = ZoneTracker.Cache.Get(__instance.gameObject);
                    ZoneSpawner spawner = null;

                    if (zoneTracker) {
                        var collection = ZoneSpawner.Cache.m_Cache.Values;

                        // I have to do this garbage, because the ZoneTracker doesn't ever set ZoneTracker.spawner!
                        // Meaning we don't actually know where the fuck this was spawned from!
                        bool breakList = false;

                        foreach (var list in collection) {
                            foreach (var otherSpawner in list) {
                                foreach (var spawnedObj in otherSpawner.spawns) { 
                                    if (spawnedObj == __instance.gameObject) {
                                        spawner = otherSpawner;

                                        breakList = true;
                                        break;
                                    }
                                }

                                if (breakList)
                                    break;
                            }

                            if (breakList)
                                break;
                        }
                    }

                    PooleeUtilities.SendSpawn(0, barcode, syncId, new SerializedTransform(__instance.transform), true, spawner);
                }
            }
            catch (Exception e) {
#if DEBUG
                FusionLogger.LogException("to execute WaitForVerify", e);
#endif
            }
        }
    }

    [HarmonyPatch(typeof(AssetPoolee), nameof(AssetPoolee.Despawn))]
    public class PooleeDespawnPatch {
        public static bool IgnorePatch = false;

        public static bool Prefix(AssetPoolee __instance) {
            if (PooleeUtilities.IsPlayer(__instance) || IgnorePatch || __instance.IsNOC())
                return true;

            try {
                if (NetworkInfo.HasServer) {
                    if (!NetworkInfo.IsServer && !PooleeUtilities.CanDespawn && PropSyncable.Cache.TryGet(__instance.gameObject, out var syncable)) {
                        return false;
                    }
                    else if (NetworkInfo.IsServer) {
                        if (!CheckPropSyncable(__instance) && PooleeUtilities.IsCheckingForSpawn(__instance))
                            MelonCoroutines.Start(CoVerifyDespawnCoroutine(__instance));
                    }
                }
            } 
            catch (Exception e) {
#if DEBUG
                FusionLogger.LogException("to execute patch AssetPoolee.Despawn", e);
#endif
            }

            return true;
        }

        private static bool CheckPropSyncable(AssetPoolee __instance) {
            if (PropSyncable.Cache.TryGet(__instance.gameObject, out var syncable))
            {
                PooleeUtilities.SendDespawn(syncable.Id);
                SyncManager.RemoveSyncable(syncable);
                return true;
            }
            return false;
        }

        private static IEnumerator CoVerifyDespawnCoroutine(AssetPoolee __instance) {
            while (!__instance.IsNOC() && PooleeUtilities.IsCheckingForSpawn(__instance)) {
                yield return null;
            }

            CheckPropSyncable(__instance);
        }
    }
}
