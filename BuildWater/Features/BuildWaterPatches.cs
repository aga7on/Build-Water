using HarmonyLib;
using System;
using UnityEngine;

namespace BuildWater
{
	[HarmonyPatch]
	internal static class BuildWaterPatches
	{
		internal const float BuildWaterWaveScale = 1f;
		private const int BuildWaterSupportLimit = 100;
		private static readonly int WaterLayer = ResolveWaterLayer();
		private static readonly AccessTools.FieldRef<Player, int> PlaceGroundRayMaskRef =
			AccessTools.FieldRefAccess<Player, int>("m_placeGroundRayMask");
		private static readonly AccessTools.FieldRef<Player, ItemDrop.ItemData> RightItemRef =
			AccessTools.FieldRefAccess<Player, ItemDrop.ItemData>("m_rightItem");
		private static readonly AccessTools.FieldRef<Heightmap, float[]> OceanDepthRef =
			AccessTools.FieldRefAccess<Heightmap, float[]>("m_oceanDepth");
		private static readonly AccessTools.FieldRef<Heightmap, Material> HeightmapMaterialRef =
			AccessTools.FieldRefAccess<Heightmap, Material>("m_materialInstance");
		private static int _savedGroundMask;
		private static bool _groundMaskAdjusted;

		[HarmonyPatch(typeof(ZNetScene), "Awake")]
		[HarmonyPostfix]
		private static void ZNetScene_Awake_Postfix()
		{
			if (ZNet.instance == null)
			{
				return;
			}
			BuildWaterPrefab.TryRegisterPrefab();
		}

		[HarmonyPatch(typeof(ObjectDB), "Awake")]
		[HarmonyPostfix]
		private static void ObjectDB_Awake_Postfix()
		{
			if (ZNet.instance == null)
			{
				return;
			}
			BuildWaterPrefab.TryRegisterPieceTable();
		}

		[HarmonyPatch(typeof(ObjectDB), nameof(ObjectDB.CopyOtherDB))]
		[HarmonyPostfix]
		private static void ObjectDB_CopyOtherDB_Postfix()
		{
			if (ZNet.instance == null)
			{
				return;
			}
			BuildWaterPrefab.TryRegisterPieceTable();
		}

		[HarmonyPatch(typeof(Player), "Awake")]
		[HarmonyPostfix]
		private static void Player_Awake_Postfix(Player __instance)
		{
			if (ZNet.instance == null)
			{
				return;
			}
			BuildWaterPrefab.TryRegisterPieceTable();
			BuildWaterPrefab.TryUnlockPieceForPlayer(__instance);
		}

		[HarmonyPatch(typeof(WaterVolume), "GetWaterSurface", new System.Type[] { typeof(Vector3), typeof(float) })]
		[HarmonyPostfix]
		private static void WaterVolume_GetWaterSurface_Postfix(WaterVolume __instance, Vector3 point, float waveFactor, ref float __result)
		{
			if (__instance == null)
			{
				return;
			}
			if (!IsBuildWaterVolume(__instance))
			{
				return;
			}

			float baseSurface = __instance.m_waterSurface != null
				? __instance.m_waterSurface.transform.position.y
				: __instance.transform.position.y + __instance.m_surfaceOffset;

			float amplitude = __result - baseSurface;
			__result = baseSurface + amplitude * BuildWaterWaveScale;
		}

		[HarmonyPatch(typeof(WearNTear), "HaveSupport")]
		[HarmonyPrefix]
		private static bool WearNTear_HaveSupport_Prefix(WearNTear __instance, ref bool __result)
		{
			if (__instance == null || !BuildWaterPlugin.Enabled.Value)
			{
				return true;
			}

			BuildWaterPieceBehaviour water = __instance.GetComponent<BuildWaterPieceBehaviour>();
			if (water == null)
			{
				water = __instance.GetComponentInParent<BuildWaterPieceBehaviour>();
			}
			if (water == null)
			{
				return true;
			}

			int count = water.GetSupportClusterCount(BuildWaterSupportLimit + 1);
			if (count <= BuildWaterSupportLimit)
			{
				__result = true;
				return false;
			}

			return true;
		}

		private static bool IsBuildWaterVolume(WaterVolume volume)
		{
			if (volume == null)
			{
				return false;
			}
			if (volume.gameObject != null && volume.gameObject.name == "BuildWater_WaterVolume")
			{
				return true;
			}
			return volume.GetComponentInParent<BuildWaterPieceBehaviour>() != null;
		}

		[HarmonyPatch(typeof(Player), "UpdatePlacement")]
		[HarmonyPrefix]
		private static void Player_UpdatePlacement_Prefix(Player __instance)
		{
			if (__instance == null || !BuildWaterPlugin.Enabled.Value)
			{
				BuildWaterPieceBehaviour.SetTerrainToolMode(false);
				return;
			}
			if (WaterLayer < 0)
			{
				BuildWaterPieceBehaviour.SetTerrainToolMode(false);
				return;
			}
			bool isTerrainTool = IsTerrainTool(__instance);
			BuildWaterPieceBehaviour.SetTerrainToolMode(isTerrainTool);
			if (!isTerrainTool)
			{
				return;
			}

			int mask = PlaceGroundRayMaskRef(__instance);
			int layerBit = 1 << WaterLayer;
			if ((mask & layerBit) == 0)
			{
				return;
			}

			_savedGroundMask = mask;
			PlaceGroundRayMaskRef(__instance) = mask & ~layerBit;
			_groundMaskAdjusted = true;
		}

		[HarmonyPatch(typeof(Player), "UpdatePlacement")]
		[HarmonyPostfix]
		private static void Player_UpdatePlacement_Postfix(Player __instance)
		{
			if (!_groundMaskAdjusted)
			{
				return;
			}
			_groundMaskAdjusted = false;
			if (__instance == null)
			{
				return;
			}
			PlaceGroundRayMaskRef(__instance) = _savedGroundMask;
		}

		[HarmonyPatch(typeof(Heightmap), "GetOceanDepth", new System.Type[] { typeof(Vector3) })]
		[HarmonyPostfix]
		private static void Heightmap_GetOceanDepth_Postfix(Heightmap __instance, Vector3 worldPos, ref float __result)
		{
			if (__instance == null || !BuildWaterPlugin.Enabled.Value)
			{
				return;
			}

			if (!Heightmap.GetHeight(worldPos, out float groundHeight))
			{
				return;
			}

			Vector3 samplePos = new Vector3(worldPos.x, groundHeight, worldPos.z);
			if (BuildWaterPieceBehaviour.TryGetWaterSurfaceAtPoint(samplePos, out float surfaceY))
			{
				float depth = surfaceY - groundHeight;
				if (depth > __result)
				{
					__result = depth;
				}
			}
		}

		[HarmonyPatch(typeof(Heightmap), "UpdateCornerDepths")]
		[HarmonyPostfix]
		private static void Heightmap_UpdateCornerDepths_Postfix(Heightmap __instance)
		{
			if (__instance == null || !BuildWaterPlugin.Enabled.Value)
			{
				return;
			}

			float[] depths = OceanDepthRef(__instance);
			if (depths == null || depths.Length < 4)
			{
				return;
			}

			float half = __instance.m_width * __instance.m_scale * 0.5f;
			Vector3 center = __instance.transform.position;

			Vector3[] corners =
			{
				new Vector3(center.x - half, center.y, center.z + half),
				new Vector3(center.x + half, center.y, center.z + half),
				new Vector3(center.x + half, center.y, center.z - half),
				new Vector3(center.x - half, center.y, center.z - half)
			};

			bool updated = false;
			for (int i = 0; i < corners.Length; i++)
			{
				Vector3 corner = corners[i];
				if (!Heightmap.GetHeight(corner, out float groundHeight))
				{
					continue;
				}

				Vector3 samplePos = new Vector3(corner.x, groundHeight, corner.z);
				if (!BuildWaterPieceBehaviour.TryGetWaterSurfaceAtPoint(samplePos, out float surfaceY))
				{
					continue;
				}

				float depth = surfaceY - groundHeight;
				if (depth <= depths[i])
				{
					continue;
				}

				depths[i] = depth;
				updated = true;
			}

			if (updated)
			{
				Material material = HeightmapMaterialRef(__instance);
				if (material != null)
				{
					material.SetFloatArray("_depth", depths);
				}
			}
		}

		[ThreadStatic]
		private static bool _hasClutterWaterLevel;
		[ThreadStatic]
		private static float _cachedClutterWaterLevel;

		[HarmonyPatch(typeof(ClutterSystem), "GetGroundInfo")]
		[HarmonyPostfix]
		private static void ClutterSystem_GetGroundInfo_Postfix(ClutterSystem __instance, ref bool __result, ref Vector3 point)
		{
			if (!__result)
			{
				_hasClutterWaterLevel = false;
				return;
			}

			float waterLevel = __instance != null ? __instance.m_waterLevel : 0f;
			if (BuildWaterPlugin.Enabled.Value && BuildWaterPieceBehaviour.TryGetWaterSurfaceAtPoint(point, out float surfaceY))
			{
				waterLevel = surfaceY;
			}

			_cachedClutterWaterLevel = waterLevel;
			_hasClutterWaterLevel = true;
		}

		private static float GetClutterWaterLevel(ClutterSystem instance)
		{
			if (_hasClutterWaterLevel)
			{
				return _cachedClutterWaterLevel;
			}
			return instance != null ? instance.m_waterLevel : 0f;
		}

		[HarmonyPatch(typeof(ClutterSystem), "GenerateVegPatch")]
		[HarmonyTranspiler]
		private static System.Collections.Generic.IEnumerable<HarmonyLib.CodeInstruction> ClutterSystem_GenerateVegPatch_Transpiler(
			System.Collections.Generic.IEnumerable<HarmonyLib.CodeInstruction> instructions)
		{
			var waterLevelField = AccessTools.Field(typeof(ClutterSystem), "m_waterLevel");
			var getWaterLevel = AccessTools.Method(typeof(BuildWaterPatches), nameof(GetClutterWaterLevel));

			foreach (var instruction in instructions)
			{
				if (instruction.opcode == System.Reflection.Emit.OpCodes.Ldfld &&
					Equals(instruction.operand, waterLevelField))
				{
					yield return new HarmonyLib.CodeInstruction(System.Reflection.Emit.OpCodes.Call, getWaterLevel);
					continue;
				}

				yield return instruction;
			}
		}

		private static bool IsTerrainTool(Player player)
		{
			ItemDrop.ItemData item = RightItemRef(player);
			if (item == null || item.m_shared == null)
			{
				return false;
			}
			string name = item.m_shared.m_name ?? string.Empty;
			if (name == "$item_hoe" || name == "$item_cultivator")
			{
				return true;
			}
			return name.Equals("Hoe", System.StringComparison.OrdinalIgnoreCase) ||
				name.Equals("Cultivator", System.StringComparison.OrdinalIgnoreCase);
		}

		private static int ResolveWaterLayer()
		{
			int layer = LayerMask.NameToLayer("water");
			if (layer >= 0)
			{
				return layer;
			}
			layer = LayerMask.NameToLayer("Water");
			return layer;
		}
	}
}
