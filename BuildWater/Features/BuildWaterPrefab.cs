using HarmonyLib;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace BuildWater
{
	internal static class BuildWaterPrefab
	{
		internal const string PrefabName = "buildwater_block";
		internal const string PieceNameKey = "piece_buildwater_block";
		internal const string PieceDescKey = "piece_buildwater_block_desc";
		internal const string PieceName = "$" + PieceNameKey;
		internal const string PieceDesc = "$" + PieceDescKey;

		internal static GameObject Prefab;
		private static bool _localizationHooked;
		private static bool _pieceTableDiagnosticsLogged;

		internal static void TryRegisterPrefab()
		{
			if (Prefab != null)
			{
				return;
			}
			if (!BuildWaterPlugin.Enabled.Value)
			{
				return;
			}
			if (ZNetScene.instance == null)
			{
				return;
			}

			GameObject basePrefab = FindStoneFloor2x2();
			if (basePrefab == null)
			{
				BuildWaterPlugin.Log.LogWarning("BuildWater: stone floor 2x2 prefab not found. Water block not created.");
				return;
			}

			GameObject supportPrefab = FindWoodFloor2x2();
			if (supportPrefab == null)
			{
				supportPrefab = basePrefab;
			}

			Prefab = UnityEngine.Object.Instantiate(basePrefab);
			Prefab.name = PrefabName;
			Prefab.hideFlags = HideFlags.HideAndDontSave;
			BuildWaterPlugin.Log.LogInfo("BuildWater: prefab cloned from stone floor.");

			ZNetView view = Prefab.GetComponent<ZNetView>();
			if (view == null)
			{
				view = Prefab.AddComponent<ZNetView>();
				view.m_persistent = true;
				BuildWaterPlugin.Log.LogWarning("BuildWater: base prefab had no ZNetView, added one.");
			}
			else
			{
				BuildWaterPlugin.Log.LogInfo($"BuildWater: prefab ZNetView persistent={view.m_persistent}.");
			}

			Piece piece = Prefab.GetComponent<Piece>();
			if (piece != null)
			{
				piece.m_name = PieceName;
				piece.m_description = PieceDesc;
				piece.m_enabled = true;
				piece.m_craftingStation = null;
				piece.m_resources = Array.Empty<Piece.Requirement>();
				piece.m_category = Piece.PieceCategory.Misc;
				piece.m_groundPiece = false;
				piece.m_groundOnly = false;
				piece.m_cultivatedGroundOnly = false;
				piece.m_notOnWood = false;
				piece.m_notOnTiltingSurface = false;
				piece.m_inCeilingOnly = false;
				piece.m_notOnFloor = false;
				piece.m_onlyInTeleportArea = false;
				piece.m_allowedInDungeons = true;
				piece.m_spaceRequirement = 0f;
				piece.m_clipGround = true;
				piece.m_clipEverything = true;
				piece.m_waterPiece = false;
				piece.m_noInWater = false;
				ApplyWetIcon(piece);
			}

			ApplySupportDefaults(Prefab, supportPrefab);

			foreach (Collider col in Prefab.GetComponentsInChildren<Collider>(true))
			{
				col.gameObject.layer = LayerMask.NameToLayer("piece_nonsolid");
			}

			if (Prefab.GetComponent<BuildWaterPieceBehaviour>() == null)
			{
				Prefab.AddComponent<BuildWaterPieceBehaviour>();
			}

			// Keep prefab active so ZNetView can bind ZDOs on instantiation.
			Prefab.SetActive(true);
			DetachRuntimeZNetView(Prefab);

			RegisterInZNetScene(Prefab);
		}

		internal static void TryRegisterPieceTable()
		{
			if (!BuildWaterPlugin.Enabled.Value)
			{
				return;
			}

			TryRegisterPrefab();
			if (Prefab == null)
			{
				return;
			}

			AddLocalization();
			AddToBuildTables();
			EnsurePieceInAllPieceTables();
		}

		internal static void TryUnlockPieceForPlayer(Player player)
		{
			if (!BuildWaterPlugin.Enabled.Value)
			{
				return;
			}
			if (player == null)
			{
				return;
			}
			if (Prefab == null)
			{
				TryRegisterPrefab();
			}
			if (Prefab == null)
			{
				return;
			}

			Piece piece = Prefab.GetComponent<Piece>();
			if (piece == null)
			{
				return;
			}

			EnsurePieceInPlayerBuildTable(player);
			EnsurePieceInAllPieceTables();

			MethodInfo addKnownPiece = AccessTools.Method(typeof(Player), "AddKnownPiece");
			if (addKnownPiece != null)
			{
				ParameterInfo[] parameters = addKnownPiece.GetParameters();
				try
				{
					if (parameters.Length == 1)
					{
						addKnownPiece.Invoke(player, new object[] { piece });
						BuildWaterPlugin.Log.LogInfo("BuildWater: unlock via Player.AddKnownPiece(prefab).");
						TryRefreshAllPieceTables(player);
						return;
					}
					if (parameters.Length == 2)
					{
						object buildPieces = AccessTools.Field(typeof(Player), "m_buildPieces")?.GetValue(player);
						addKnownPiece.Invoke(player, new object[] { buildPieces, piece });
						BuildWaterPlugin.Log.LogInfo("BuildWater: unlock via Player.AddKnownPiece(buildPieces, prefab).");
						TryRefreshAllPieceTables(player);
						return;
					}
				}
				catch
				{
					// fall through to reflection-based collection update
				}
			}

			FieldInfo knownRecipesField = AccessTools.Field(typeof(Player), "m_knownRecipes");
			HashSet<string> knownRecipes = knownRecipesField?.GetValue(player) as HashSet<string>;
			if (knownRecipes != null && knownRecipes.Add(piece.m_name))
			{
				BuildWaterPlugin.Log.LogInfo("BuildWater: unlock via Player.m_knownRecipes.");
				TryRefreshAllPieceTables(player);
			}
		}

		private static GameObject FindStoneFloor2x2()
		{
			ZNetScene scene = ZNetScene.instance;
			if (scene == null)
			{
				return null;
			}

			string[] prefabNames =
			{
				"stone_floor_2x2",
				"stone_floor_2x2x1",
				"stone_floor_2x2_1",
				"piece_stone_floor_2x2"
			};

			foreach (string name in prefabNames)
			{
				GameObject direct = scene.GetPrefab(name);
				if (direct != null)
				{
					return direct;
				}
			}

			foreach (GameObject prefab in scene.m_prefabs)
			{
				if (prefab == null)
				{
					continue;
				}
				Piece piece = prefab.GetComponent<Piece>();
				if (piece == null)
				{
					continue;
				}
				if (piece.m_name == "$piece_stone_floor_2x2")
				{
					return prefab;
				}
			}

			return null;
		}

		private static GameObject FindWoodFloor2x2()
		{
			ZNetScene scene = ZNetScene.instance;
			if (scene == null)
			{
				return null;
			}

			string[] prefabNames =
			{
				"wood_floor_2x2",
				"wood_floor_2x2x1",
				"wood_floor_2x2_1",
				"piece_wood_floor_2x2"
			};

			foreach (string name in prefabNames)
			{
				GameObject direct = scene.GetPrefab(name);
				if (direct != null)
				{
					return direct;
				}
			}

			foreach (GameObject prefab in scene.m_prefabs)
			{
				if (prefab == null)
				{
					continue;
				}
				Piece piece = prefab.GetComponent<Piece>();
				if (piece == null)
				{
					continue;
				}
				if (piece.m_name == "$piece_wood_floor_2x2")
				{
					return prefab;
				}
			}

			return null;
		}

		private static void ApplySupportDefaults(GameObject target, GameObject reference)
		{
			if (target == null || reference == null)
			{
				return;
			}

			WearNTear targetWear = target.GetComponent<WearNTear>();
			WearNTear referenceWear = reference.GetComponent<WearNTear>();
			if (targetWear == null || referenceWear == null)
			{
				return;
			}

			SetFieldIfExists(targetWear, "m_support", GetFieldValue<float>(referenceWear, "m_support"));
			SetFieldIfExists(targetWear, "m_supports", referenceWear.m_supports);
			SetFieldIfExists(targetWear, "m_noSupportWear", referenceWear.m_noSupportWear);
			SetFieldIfExists(targetWear, "m_supportValue", new List<float>());
			SetFieldIfExists(targetWear, "m_clearCachedSupport", true);
		}

		private static T GetFieldValue<T>(object target, string fieldName)
		{
			if (target == null || string.IsNullOrEmpty(fieldName))
			{
				return default;
			}
			FieldInfo field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
			if (field == null)
			{
				return default;
			}
			try
			{
				object value = field.GetValue(target);
				if (value is T typed)
				{
					return typed;
				}
			}
			catch
			{
				// ignore
			}
			return default;
		}

		private static void SetFieldIfExists(object target, string fieldName, object value)
		{
			if (target == null || string.IsNullOrEmpty(fieldName))
			{
				return;
			}
			FieldInfo field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
			if (field == null)
			{
				return;
			}
			if (value == null && field.FieldType.IsValueType)
			{
				return;
			}
			if (value != null && !field.FieldType.IsAssignableFrom(value.GetType()))
			{
				return;
			}
			try
			{
				field.SetValue(target, value);
			}
			catch
			{
				// ignore
			}
		}

		private static void RegisterInZNetScene(GameObject prefab)
		{
			ZNetScene scene = ZNetScene.instance;
			if (scene == null || prefab == null)
			{
				return;
			}

			if (!scene.m_prefabs.Contains(prefab))
			{
				scene.m_prefabs.Add(prefab);
			}

			Dictionary<int, GameObject> named = (Dictionary<int, GameObject>)AccessTools.Field(typeof(ZNetScene), "m_namedPrefabs").GetValue(scene);
			int hash = scene.GetPrefabHash(prefab);
			if (!named.ContainsKey(hash))
			{
				named.Add(hash, prefab);
			}
			else
			{
				named[hash] = prefab;
			}
			BuildWaterPlugin.Log.LogInfo("BuildWater: prefab registered in ZNetScene.");
		}

		private static void AddLocalization()
		{
			ApplyLocalization();
			if (!_localizationHooked)
			{
				HookLocalizationEvent();
				_localizationHooked = true;
			}
		}

		private static void ApplyLocalization()
		{
			Type locType = AccessTools.TypeByName("Localization");
			if (locType == null)
			{
				return;
			}
			object locInstance = AccessTools.Property(locType, "instance")?.GetValue(null, null);
			if (locInstance == null)
			{
				return;
			}
			MethodInfo getSelectedLanguage = AccessTools.Method(locType, "GetSelectedLanguage", Type.EmptyTypes);
			MethodInfo addWord = AccessTools.Method(locType, "AddWord", new[] { typeof(string), typeof(string) });
			if (addWord == null)
			{
				return;
			}

			string language = getSelectedLanguage != null
				? (getSelectedLanguage.Invoke(locInstance, null) as string)
				: "English";
			if (string.IsNullOrEmpty(language))
			{
				language = "English";
			}

			if (string.Equals(language, "Russian", StringComparison.OrdinalIgnoreCase) || string.Equals(language, "ru", StringComparison.OrdinalIgnoreCase))
			{
				addWord.Invoke(locInstance, new object[] { PieceNameKey, "Блок воды" });
				addWord.Invoke(locInstance, new object[] { PieceDescKey, "Размещаемый объём воды." });
			}
			else
			{
				addWord.Invoke(locInstance, new object[] { PieceNameKey, "Water Block" });
				addWord.Invoke(locInstance, new object[] { PieceDescKey, "Placeable water volume." });
			}
		}

		private static void HookLocalizationEvent()
		{
			Type locType = AccessTools.TypeByName("Localization");
			if (locType == null)
			{
				return;
			}
			EventInfo eventInfo = locType.GetEvent("OnLanguageChange");
			if (eventInfo != null)
			{
				eventInfo.AddEventHandler(null, (Action)ApplyLocalization);
				return;
			}
			FieldInfo fieldInfo = locType.GetField("OnLanguageChange");
			if (fieldInfo != null)
			{
				Action current = fieldInfo.GetValue(null) as Action;
				current = (Action)Delegate.Combine(current, (Action)ApplyLocalization);
				fieldInfo.SetValue(null, current);
			}
		}

		private static void AddToBuildTables()
		{
			if (ObjectDB.instance == null)
			{
				return;
			}
			int addedTables = 0;
			PieceTable hammerTable = null;
			foreach (GameObject item in ObjectDB.instance.m_items)
			{
				ItemDrop drop = item ? item.GetComponent<ItemDrop>() : null;
				if (drop == null || drop.m_itemData == null || drop.m_itemData.m_shared == null)
				{
					continue;
				}
				if (hammerTable == null)
				{
					string name = drop.m_itemData.m_shared.m_name ?? string.Empty;
					if (name == "$item_hammer" || name == "Hammer")
					{
						hammerTable = drop.m_itemData.m_shared.m_buildPieces;
					}
				}
				PieceTable table = drop.m_itemData.m_shared.m_buildPieces;
				if (table == null)
				{
					continue;
				}
				if (!table.m_pieces.Contains(Prefab))
				{
					table.m_pieces.Add(Prefab);
					addedTables++;
				}
				if (!table.m_categories.Contains(Piece.PieceCategory.Misc))
				{
					table.m_categories.Add(Piece.PieceCategory.Misc);
					table.m_categoryLabels.Add("$piececategory_misc");
				}
			}
			BuildWaterPlugin.Log.LogInfo($"BuildWater: ensured piece in build tables, added to {addedTables} table(s).");
			if (hammerTable != null)
			{
				EnsurePieceInTable(hammerTable);
				TryRefreshPieceTable(hammerTable, Player.m_localPlayer);
				BuildWaterPlugin.Log.LogInfo($"BuildWater: hammer table pieces={hammerTable.m_pieces.Count}.");
			}
		}

		internal static void EnsurePieceInTable(PieceTable table)
		{
			if (Prefab == null || table == null)
			{
				return;
			}
			if (!table.m_pieces.Contains(Prefab))
			{
				table.m_pieces.Add(Prefab);
				BuildWaterPlugin.Log.LogInfo("BuildWater: piece injected into active build table.");
			}
			TryInjectAvailableList(table);
			if (!table.m_categories.Contains(Piece.PieceCategory.Misc))
			{
				table.m_categories.Add(Piece.PieceCategory.Misc);
				table.m_categoryLabels.Add("$piececategory_misc");
			}
		}

		private static void EnsurePieceInPlayerBuildTable(Player player)
		{
			if (player == null || Prefab == null)
			{
				return;
			}
			bool injected = false;
			foreach (FieldInfo field in typeof(Player).GetFields(AccessTools.all))
			{
				if (field.FieldType != typeof(PieceTable))
				{
					continue;
				}
				PieceTable table = field.GetValue(player) as PieceTable;
				if (table == null)
				{
					continue;
				}
				EnsurePieceInTable(table);
				injected = true;
			}
			if (injected)
			{
				BuildWaterPlugin.Log.LogInfo("BuildWater: injected into player PieceTable field(s).");
			}
		}

		internal static void EnsurePieceInAllPieceTables()
		{
			if (Prefab == null)
			{
				return;
			}
			PieceTable[] tables = Resources.FindObjectsOfTypeAll<PieceTable>();
			if (tables == null || tables.Length == 0)
			{
				return;
			}
			int injected = 0;
			Player player = Player.m_localPlayer;
			foreach (PieceTable table in tables)
			{
				if (table == null)
				{
					continue;
				}
				int before = table.m_pieces.Count;
				EnsurePieceInTable(table);
				LogPieceTableDiagnostics(table);
				TryRefreshPieceTable(table, player);
				if (table.m_pieces.Count > before)
				{
					injected++;
				}
			}
			if (injected > 0)
			{
				BuildWaterPlugin.Log.LogInfo($"BuildWater: injected into {injected} live PieceTable(s).");
			}
		}

		private static void TryInjectAvailableList(PieceTable table)
		{
			if (table == null || Prefab == null)
			{
				return;
			}
			foreach (FieldInfo field in typeof(PieceTable).GetFields(AccessTools.all))
			{
				string name = field.Name.ToLowerInvariant();
				if (!name.Contains("available"))
				{
					continue;
				}
				if (!typeof(IList).IsAssignableFrom(field.FieldType))
				{
					continue;
				}
				IList list = field.GetValue(table) as IList;
				if (list == null)
				{
					continue;
				}
				if (field.FieldType.IsGenericType)
				{
					Type arg = field.FieldType.GetGenericArguments()[0];
					if (arg == typeof(GameObject))
					{
						if (!list.Contains(Prefab))
						{
							list.Add(Prefab);
						}
					}
					else if (arg == typeof(Piece))
					{
						Piece piece = Prefab.GetComponent<Piece>();
						if (piece != null && !list.Contains(piece))
						{
							list.Add(piece);
						}
					}
				}
			}
		}

		private static void TryRefreshAllPieceTables(Player player)
		{
			TryRefreshPlayerBuildPieces(player);
		}

		private static void TryRefreshPieceTable(PieceTable table, Player player)
		{
			if (table == null)
			{
				return;
			}
			TryRefreshPlayerBuildPieces(player);
		}

		private static void TryRefreshPlayerBuildPieces(Player player)
		{
			if (player == null)
			{
				return;
			}

			MethodInfo updateAvailable = AccessTools.Method(typeof(Player), "UpdateAvailablePiecesList");
			if (updateAvailable != null)
			{
				try
				{
					updateAvailable.Invoke(player, null);
					return;
				}
				catch
				{
					// fall through to direct update
				}
			}

			PieceTable buildPieces = AccessTools.Field(typeof(Player), "m_buildPieces")?.GetValue(player) as PieceTable;
			HashSet<string> knownRecipes = AccessTools.Field(typeof(Player), "m_knownRecipes")?.GetValue(player) as HashSet<string>;
			if (buildPieces == null || knownRecipes == null)
			{
				return;
			}

			bool noPlacementCost = false;
			FieldInfo noPlacementCostField = AccessTools.Field(typeof(Player), "m_noPlacementCost");
			if (noPlacementCostField != null && noPlacementCostField.FieldType == typeof(bool))
			{
				noPlacementCost = (bool)noPlacementCostField.GetValue(player);
			}

			try
			{
				buildPieces.UpdateAvailable(knownRecipes, player, false, noPlacementCost);
			}
			catch
			{
				// ignore
			}
		}

		private static void LogPieceTableDiagnostics(PieceTable table)
		{
			if (_pieceTableDiagnosticsLogged || table == null)
			{
				return;
			}
			_pieceTableDiagnosticsLogged = true;

			List<string> updateMethods = new List<string>();
			foreach (MethodInfo method in typeof(PieceTable).GetMethods(AccessTools.all))
			{
				string name = method.Name;
				if (string.IsNullOrEmpty(name))
				{
					continue;
				}
				string lower = name.ToLowerInvariant();
				if (lower.Contains("update") || lower.Contains("refresh"))
				{
					updateMethods.Add(name);
				}
			}

			List<string> availableFields = new List<string>();
			foreach (FieldInfo field in typeof(PieceTable).GetFields(AccessTools.all))
			{
				string name = field.Name;
				if (string.IsNullOrEmpty(name))
				{
					continue;
				}
				string lower = name.ToLowerInvariant();
				if (lower.Contains("available") || lower.Contains("piece"))
				{
					availableFields.Add($"{name}:{field.FieldType.Name}");
				}
			}

			BuildWaterPlugin.Log.LogInfo("BuildWater: PieceTable methods=" + string.Join(", ", updateMethods.ToArray()));
			BuildWaterPlugin.Log.LogInfo("BuildWater: PieceTable fields=" + string.Join(", ", availableFields.ToArray()));
		}

		private static void ApplyWetIcon(Piece piece)
		{
			if (piece == null || ObjectDB.instance == null)
			{
				return;
			}
			try
			{
				StatusEffect wet = ObjectDB.instance.GetStatusEffect(SEMan.s_statusEffectWet);
				if (wet != null && wet.m_icon != null)
				{
					piece.m_icon = wet.m_icon;
				}
			}
			catch
			{
				// ignore, fallback to default icon
			}
		}

		private static void DetachRuntimeZNetView(GameObject prefab)
		{
			if (prefab == null || ZNetScene.instance == null)
			{
				return;
			}
			ZNetView view = prefab.GetComponent<ZNetView>();
			if (view == null)
			{
				return;
			}
			ZDO zdo = view.GetZDO();
			if (zdo != null)
			{
				Dictionary<ZDO, ZNetView> instances = (Dictionary<ZDO, ZNetView>)AccessTools.Field(typeof(ZNetScene), "m_instances").GetValue(ZNetScene.instance);
				if (instances != null)
				{
					instances.Remove(zdo);
				}
				if (ZDOMan.instance != null && zdo.IsOwner())
				{
					ZDOMan.instance.DestroyZDO(zdo);
				}
				view.ResetZDO();
			}
		}
	}
}
