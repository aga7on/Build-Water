using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using System.Collections;
using System;
using System.Reflection;
using UnityEngine;

namespace BuildWater
{
	[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
	public sealed class BuildWaterPlugin : BaseUnityPlugin
	{
		internal static BuildWaterPlugin Instance { get; private set; }
		internal static ManualLogSource Log;

		internal static ConfigEntry<bool> Enabled;
		internal static ConfigEntry<float> WaterLevelOffset;
		internal static ConfigEntry<float> WaterDepth;
		internal static ConfigEntry<float> SurfacePadding;
		internal static ConfigEntry<float> TerrainInfluenceRadius;
		internal static ConfigEntry<float> TerrainInfluenceMaxDepth;
		internal static ConfigEntry<float> PlayerCheckAboveSurface;
		internal static ConfigEntry<float> CurtainMinDepth;

		private Harmony _harmony;

		private void Awake()
		{
			Instance = this;
			Log = Logger;

			Enabled = Config.Bind(
				"General",
				"Enabled",
				true,
				new ConfigDescription("Enable BuildWater features."));

			WaterLevelOffset = Config.Bind(
				"Water",
				"LevelOffset",
				0f,
				new ConfigDescription(
					"Vertical offset applied when placing water-related pieces.",
					new AcceptableValueRange<float>(-5f, 5f)));

			WaterDepth = Config.Bind(
				"Water",
				"DepthMeters",
				3f,
				new ConfigDescription(
					"Depth of the placed water volume in meters.",
					new AcceptableValueRange<float>(0.5f, 20f)));

			SurfacePadding = Config.Bind(
				"Water",
				"SurfacePaddingMeters",
				-0.0105042f,
				new ConfigDescription(
					"Extra padding added to water surface/volume size to eliminate visible gaps between adjacent pieces.",
					new AcceptableValueRange<float>(-2f, 0.5f)));

			TerrainInfluenceRadius = Config.Bind(
				"Water",
				"TerrainInfluenceRadiusMeters",
				1f,
				new ConfigDescription(
					"Horizontal radius around water blocks that can turn terrain into water vegetation.",
					new AcceptableValueRange<float>(0f, 3f)));

			TerrainInfluenceMaxDepth = Config.Bind(
				"Water",
				"TerrainInfluenceMaxDepthMeters",
				3f,
				new ConfigDescription(
					"Maximum vertical distance below the water surface that can affect terrain.",
					new AcceptableValueRange<float>(0.1f, 20f)));

			PlayerCheckAboveSurface = Config.Bind(
				"Water",
				"PlayerCheckAboveSurface",
				1f,
				new ConfigDescription(
					"How far above the water surface (meters) the player is still considered inside the water volume.",
					new AcceptableValueRange<float>(0f, 5f)));

			CurtainMinDepth = Config.Bind(
				"Water",
				"CurtainMinDepth",
				10f,
				new ConfigDescription(
					"Minimum depth (meters) for water curtains falling from block edges.",
					new AcceptableValueRange<float>(0.5f, 30f)));

			Config.SettingChanged += OnConfigSettingChanged;

			_harmony = new Harmony(PluginGuid);
			_harmony.PatchAll(Assembly.GetExecutingAssembly());

			Log.LogInfo($"{PluginName} {PluginVersion} loaded.");

			StartCoroutine(EnsurePieceRoutine());
		}

		private void OnDestroy()
		{
			try
			{
				_harmony?.UnpatchSelf();
			}
			catch
			{
			}

			if (Config != null)
			{
				Config.SettingChanged -= OnConfigSettingChanged;
			}
		}

		public const string PluginGuid = "buildwater";
		public const string PluginName = "Build Water";
		public const string PluginVersion = "0.9.0";

		private IEnumerator EnsurePieceRoutine()
		{
			const float interval = 1f;
			const int attempts = 12;
			for (int i = 0; i < attempts; i++)
			{
				yield return new WaitForSeconds(interval);
				if (!Enabled.Value)
				{
					continue;
				}
				if (ZNetScene.instance == null || ObjectDB.instance == null)
				{
					continue;
				}
				if (ZNet.instance == null)
				{
					continue;
				}
				Player player = Player.m_localPlayer;
				BuildWaterPrefab.TryRegisterPieceTable();
				if (player != null)
				{
					BuildWaterPrefab.TryUnlockPieceForPlayer(player);
				}
				BuildWaterPrefab.EnsurePieceInAllPieceTables();
			}
		}

		private void OnConfigSettingChanged(object sender, SettingChangedEventArgs args)
		{
			if (!Enabled.Value)
			{
				BuildWaterPieceBehaviour.SetTerrainToolMode(false);
				return;
			}
			BuildWaterPieceBehaviour.NotifyConfigChanged();
		}
	}
}
