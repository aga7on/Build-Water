using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;

namespace BuildWater
{
	internal sealed class BuildWaterPieceBehaviour : MonoBehaviour
	{
		private const string WaterRootName = "BuildWater_WaterVolume";
		private const string ConnectionsRootName = "BuildWater_Connections";
		private const string HighlightRootName = "BuildWater_Highlight";
		private const float ConnectionGapMax = 1.0f;
		private const float ConnectionGapVisualMin = 0.06f;
		private const float ConnectionSurfaceYOffset = 0.01f;
		private const float ConnectionEdgeOverlap = 0.03f;
		private const float ConnectionSquareTolerance = 0.15f;
		private const float ConnectionBridgeMinOverlap = 0.05f;
		private const float ConnectionSearchRadius = 1.2f;
		private const float ConnectionLevelTolerance = 0.25f;
		private const float ConnectionVerticalMax = 1.25f;
		private const float ConnectionMinOverlap = 0.2f;
		private const float ConnectionWallThickness = 0.25f;
		private const float ConnectionRebuildInterval = 0.4f;
		private const float GridCellSize = 4f;
		private const float ConnectionAngleTolerance = 12f;
		private const int ConnectionCascadeSegments = 8;
		private const float ConnectionCascadeDepthMin = 0.08f;
		private const float ConnectionCascadeDepthMax = 0.6f;
		private const bool DebugCascadeMaterial = false;
		private const bool DebugCascadeSolid = false;
		private const bool UseDynamicBridgeSurface = false;
		private const bool DebugBridgeMaterial = false;
		private const bool UseClusterSurface = true;
		private const float ClusterRebuildInterval = 0.5f;
		private const float ClusterAdjacencyTolerance = 0.35f;
		private const float ClusterEdgeOverlap = 0.01f;
		private const float ClusterUvScale = 4f;
		private const float HighlightThickness = 0.06f;
		private const float HighlightThicknessMin = 0.01f;
		private const float HighlightInset = 0.005f;
		private const float HighlightFallbackDistance = 6f;
		private const bool UseSurfaceWaveDeform = false;
		private const bool UseClusterCurtains = true;
		private const int ClusterCurtainSegments = 6;
		private const float ClusterCurtainDepthScale = 0.35f;
		private const float ClusterCurtainDepthMin = 0.05f;
		private const float ClusterCurtainDepthMax = 0.45f;
		private const float ClusterCurtainMinHeight = 0.08f;
		private const float ClusterCurtainMinDepth = 10f;
		private const float ClusterCurtainNeighborOverlap = 0.35f;
		private const float WaterWaveAmplitudeScale = 0.25f;
		private const float WaterWaveSpeedScale = 0.6f;

		private static readonly System.Collections.Generic.Dictionary<Vector3Int, System.Collections.Generic.HashSet<BuildWaterPieceBehaviour>> _grid =
			new System.Collections.Generic.Dictionary<Vector3Int, System.Collections.Generic.HashSet<BuildWaterPieceBehaviour>>();

		private static Material _cachedWaterMaterial;
		private static string _cachedWaterMaterialSource;
		private static readonly System.Collections.Generic.Dictionary<int, Material> _cachedScaledWaterMaterials =
			new System.Collections.Generic.Dictionary<int, Material>();
		private static bool _loggedWaterShaderProps;
		private static Material _cachedCascadeDebugMaterial;
		private static Material _cachedBridgeDebugMaterial;
		private static Material _cachedHighlightMaterial;
		private static MethodInfo _playerGetHoverObjectMethod;
		private static bool _playerGetHoverObjectSearched;
		private static MethodInfo _playerInPlaceModeMethod;
		private static bool _playerInPlaceModeSearched;
		private static MethodInfo _playerGetRightItemMethod;
		private static bool _playerGetRightItemSearched;
		private static FieldInfo _playerHoverObjectField;
		private static FieldInfo _playerHoverComponentField;
		private static bool _playerHoverFieldSearched;
		private static bool _loggedLiquidVolumeFields;
		private static bool _loggedWaterVolumeFields;
		private static bool _terrainToolMode;
		private static readonly System.Collections.Generic.HashSet<BuildWaterPieceBehaviour> _queryBuffer =
			new System.Collections.Generic.HashSet<BuildWaterPieceBehaviour>();
		private MeshRenderer _surfaceRenderer;
		private WaterVolume _waterVolume;
		private Collider _volumeCollider;
		private float _volumeDepth;
		private float _surfaceWorldY;
		private float _surfaceOffset;
		private float _manualDown;
		private float _manualUp;
		private float _boundsRefreshTimer;
		private bool _boundsDirty;
		private Transform _waterRoot;
		private Bounds _worldBounds;
		private float _playerCheckTimer;
		private bool _manualApplied;
		private bool _playerInsideVolume;
		private float _debugTimer;
		private int _instanceKey;
		private Vector3Int _gridCell;
		private bool _registered;
		private Transform _connectionsRoot;
		private readonly System.Collections.Generic.Dictionary<string, GameObject> _connections =
			new System.Collections.Generic.Dictionary<string, GameObject>();
		private readonly System.Collections.Generic.List<ConnectionInfo> _connectionInfos =
			new System.Collections.Generic.List<ConnectionInfo>();
		private bool _connectionsDirty;
		private float _connectionsRebuildTimer;
		private bool _clusterDirty;
		private float _clusterRebuildTimer;
		private Mesh _clusterMesh;
		private int _supportClusterCount;
		private float _supportClusterTime;
		private Vector3 _lastPosition;
		private Quaternion _lastRotation;
		private Vector3 _lastScale;
		private float _lastSurfaceY;
		private Bounds _lastBounds;
		private Renderer[] _highlightRenderers;
		private Transform _highlightRoot;
		private bool _highlightVisible;
		private float _highlightTimer;

		private void Awake()
		{
			_instanceKey = GetInstanceID();
			ZNetView view = GetComponent<ZNetView>();
			if (view != null && !view.IsValid())
			{
				return;
			}
			if (!gameObject.scene.IsValid())
			{
				return;
			}
			if (!BuildWaterPlugin.Enabled.Value)
			{
				return;
			}
			if (ZNetScene.instance == null)
			{
				// Menu/loader scenes: don't touch water setup.
				return;
			}
			if (ZNet.instance == null)
			{
				// Avoid touching water systems while in main menu or before network is ready.
				return;
			}

			int ghostLayer = LayerMask.NameToLayer("ghost");
			if (gameObject.layer == ghostLayer)
			{
				return;
			}

			_boundsDirty = true;
			_boundsRefreshTimer = 0f;

			HideRenderers();
			CacheHighlightRenderers();
			EnsureColliderLayer();
			EnsureWaterVolume();
			EnsureConnectionsRoot();
			if (_highlightRenderers == null || _highlightRenderers.Length == 0)
			{
				EnsureHighlightOutline();
			}
			RegisterInGrid();
			MarkConnectionsDirty();
			NotifyGrassChanged();
		}

		private void Start()
		{
			if (_surfaceRenderer != null)
			{
				ApplyWaterMaterial(_surfaceRenderer);
			}
		}

		private void Update()
		{
			if (!BuildWaterPlugin.Enabled.Value)
			{
				return;
			}
			if (_highlightRenderers == null || _highlightRenderers.Length == 0)
			{
				EnsureHighlightOutline();
				if (_highlightRenderers == null || _highlightRenderers.Length == 0)
				{
					return;
				}
			}

			_highlightTimer += Time.deltaTime;
			if (_highlightTimer < 0.1f)
			{
				return;
			}
			_highlightTimer = 0f;
			UpdateHighlight();
		}

		private void FixedUpdate()
		{
			if (_waterVolume == null || _volumeCollider == null)
			{
				return;
			}
			if (Player.m_localPlayer == null)
			{
				return;
			}

			_playerCheckTimer += Time.fixedDeltaTime;
			if (_playerCheckTimer < 0.25f)
			{
				return;
			}
			_playerCheckTimer = 0f;

			Vector3 playerPos = Player.m_localPlayer.transform.position;
			bool inside = IsPlayerInsideManual(playerPos);
			float waterSurface = inside ? GetSurfaceHeight(playerPos) : float.MinValue;
			if (!inside)
			{
				if (TryGetConnectionSurface(playerPos, out float connectionSurface))
				{
					inside = true;
					waterSurface = connectionSurface;
				}
			}
			if (inside)
			{
				Player.m_localPlayer.SetLiquidLevel(waterSurface, LiquidType.Water, _waterVolume);
				_manualApplied = true;
				if (!_playerInsideVolume)
				{
					_playerInsideVolume = true;
				LogPlayerState("enter-volume", playerPos, waterSurface);
				}
			}
			else
			{
				if (_manualApplied)
				{
					Player.m_localPlayer.SetLiquidLevel(-10000f, LiquidType.Water, _waterVolume);
					_manualApplied = false;
				}
				if (_playerInsideVolume)
				{
					_playerInsideVolume = false;
				LogPlayerState("exit-volume", playerPos, -10000f);
				}
			}

			_debugTimer += Time.fixedDeltaTime;
			if (_debugTimer >= 2f)
			{
				_debugTimer = 0f;
				LogPlayerState(inside ? "inside-volume" : "outside-volume", playerPos, inside ? waterSurface : GetSurfaceHeight(playerPos));
			}
		}

		private void LateUpdate()
		{
			if (_waterRoot == null)
			{
				return;
			}

			if (HasTransformChanged())
			{
				_boundsDirty = true;
			}

			_boundsRefreshTimer += Time.deltaTime;
			if (_boundsDirty && _boundsRefreshTimer >= 0.5f)
			{
				_boundsRefreshTimer = 0f;
				RefreshBounds();
				_boundsDirty = false;
				TrackTransformChanges();
			}
			UpdateConnectionsIfNeeded();
			UpdateClusterSurfaceIfNeeded();

			// Keep local rotation; avoid forcing world rotation which can skew placement.
		}

		private void HideRenderers()
		{
			foreach (Renderer renderer in GetComponentsInChildren<Renderer>(true))
			{
				if (renderer == null)
				{
					continue;
				}
				if (IsConnectionsElement(renderer.transform) || IsHighlightElement(renderer.transform))
				{
					continue;
				}
				if (IsWaterSurface(renderer.transform))
				{
					continue;
				}
				if (IsGhostOnly(renderer.transform))
				{
					continue;
				}
				renderer.enabled = false;
			}
		}

		private void CacheHighlightRenderers()
		{
			foreach (Renderer renderer in GetComponentsInChildren<Renderer>(true))
			{
				if (renderer == null)
				{
					continue;
				}
				if (IsConnectionsElement(renderer.transform) || IsWaterSurface(renderer.transform) ||
					IsGhostOnly(renderer.transform) || IsHighlightElement(renderer.transform))
				{
					continue;
				}

				renderer.enabled = false;
				renderer.shadowCastingMode = ShadowCastingMode.Off;
				renderer.receiveShadows = false;
			}

			EnsureHighlightOutline();
		}

		private void UpdateHighlight()
		{
			Player player = Player.m_localPlayer;
			bool shouldShow = false;
			if (player != null && IsPlayerInBuildMode(player))
			{
				shouldShow = IsPlayerHovering(player);
				if (!shouldShow)
				{
					Vector3 center = GetHighlightCenter();
					float distance = Vector3.Distance(center, player.transform.position);
					if (distance <= HighlightFallbackDistance)
					{
						shouldShow = true;
					}
				}
			}
			SetHighlightVisible(shouldShow);
		}

		private void SetHighlightVisible(bool visible)
		{
			if (_highlightRenderers == null || _highlightVisible == visible)
			{
				return;
			}

			_highlightVisible = visible;
			Material highlightMaterial = visible ? GetHighlightMaterial() : null;
			for (int i = 0; i < _highlightRenderers.Length; i++)
			{
				Renderer renderer = _highlightRenderers[i];
				if (renderer == null)
				{
					continue;
				}
				if (visible && highlightMaterial != null)
				{
					renderer.sharedMaterial = highlightMaterial;
				}
				renderer.enabled = visible;
			}
		}

		private void EnsureHighlightOutline()
		{
			if (_highlightRoot == null)
			{
				Transform existing = transform.Find(HighlightRootName);
				if (existing != null)
				{
					_highlightRoot = existing;
				}
				else
				{
					GameObject root = new GameObject(HighlightRootName);
					root.transform.SetParent(transform, false);
					_highlightRoot = root.transform;
				}
			}

			if (_highlightRoot == null)
			{
				_highlightRenderers = null;
				return;
			}

			MeshFilter filter = _highlightRoot.GetComponent<MeshFilter>();
			if (filter == null)
			{
				filter = _highlightRoot.gameObject.AddComponent<MeshFilter>();
			}

			MeshRenderer renderer = _highlightRoot.GetComponent<MeshRenderer>();
			if (renderer == null)
			{
				renderer = _highlightRoot.gameObject.AddComponent<MeshRenderer>();
			}

			renderer.sharedMaterial = GetHighlightMaterial();
			renderer.shadowCastingMode = ShadowCastingMode.Off;
			renderer.receiveShadows = false;
			renderer.enabled = false;

			Bounds localBounds;
			if (!TryGetLocalBounds(out localBounds))
			{
				if (!TryGetBounds(out Bounds worldBounds))
				{
					if (_volumeCollider != null)
					{
						worldBounds = _volumeCollider.bounds;
					}
					else if (_worldBounds.size.sqrMagnitude > 0.0001f)
					{
						worldBounds = _worldBounds;
					}
					else
					{
						_highlightRenderers = null;
						return;
					}
				}

				localBounds = ToLocalBounds(worldBounds);
			}
			Vector3 min = localBounds.min + new Vector3(HighlightInset, HighlightInset, HighlightInset);
			Vector3 max = localBounds.max - new Vector3(HighlightInset, HighlightInset, HighlightInset);
			if (min.x < max.x && min.y < max.y && min.z < max.z)
			{
				localBounds.SetMinMax(min, max);
			}

			DestroyGeneratedMesh(filter.sharedMesh);
			filter.sharedMesh = CreateHighlightMesh(localBounds);
			_highlightRenderers = new Renderer[] { renderer };
			_highlightVisible = false;
		}

		private bool TryGetLocalBounds(out Bounds bounds)
		{
			bounds = new Bounds();
			bool haveBounds = false;

			MeshFilter[] filters = GetComponentsInChildren<MeshFilter>(true);
			Matrix4x4 rootFromWorld = transform.worldToLocalMatrix;
			for (int i = 0; i < filters.Length; i++)
			{
				MeshFilter filter = filters[i];
				if (filter == null || filter.sharedMesh == null)
				{
					continue;
				}
				Transform t = filter.transform;
				if (IsWaterSurface(t) || IsGhostOnly(t) || IsConnectionsElement(t) || IsHighlightElement(t))
				{
					continue;
				}
				Matrix4x4 localToRoot = rootFromWorld * t.localToWorldMatrix;
				Bounds localBounds = TransformBounds(filter.sharedMesh.bounds, localToRoot);
				if (!haveBounds)
				{
					bounds = localBounds;
					haveBounds = true;
				}
				else
				{
					bounds.Encapsulate(localBounds);
				}
			}

			return haveBounds;
		}

		private Bounds ToLocalBounds(Bounds worldBounds)
		{
			Vector3 min = worldBounds.min;
			Vector3 max = worldBounds.max;
			Vector3[] corners =
			{
				new Vector3(min.x, min.y, min.z),
				new Vector3(max.x, min.y, min.z),
				new Vector3(max.x, min.y, max.z),
				new Vector3(min.x, min.y, max.z),
				new Vector3(min.x, max.y, min.z),
				new Vector3(max.x, max.y, min.z),
				new Vector3(max.x, max.y, max.z),
				new Vector3(min.x, max.y, max.z)
			};

			Vector3 localMin = new Vector3(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity);
			Vector3 localMax = new Vector3(float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity);

			for (int i = 0; i < corners.Length; i++)
			{
				Vector3 local = transform.InverseTransformPoint(corners[i]);
				localMin = Vector3.Min(localMin, local);
				localMax = Vector3.Max(localMax, local);
			}

			Bounds localBounds = new Bounds();
			localBounds.SetMinMax(localMin, localMax);
			return localBounds;
		}

		private static Mesh CreateHighlightMesh(Bounds bounds)
		{
			Mesh mesh = new Mesh();
			mesh.name = "BuildWater_HighlightMesh";
			mesh.hideFlags = HideFlags.HideAndDontSave;

			Vector3 min = bounds.min;
			Vector3 max = bounds.max;
			float lenX = max.x - min.x;
			float lenY = max.y - min.y;
			float lenZ = max.z - min.z;
			float thickness = Mathf.Clamp(HighlightThickness, HighlightThicknessMin, Mathf.Min(lenX, lenY, lenZ) * 0.4f);

			List<Vector3> vertices = new List<Vector3>();
			List<int> triangles = new List<int>();
			List<Vector3> normals = new List<Vector3>();

			float midX = (min.x + max.x) * 0.5f;
			float midY = (min.y + max.y) * 0.5f;
			float midZ = (min.z + max.z) * 0.5f;

			// X edges
			AppendBox(vertices, triangles, normals, new Vector3(midX, min.y, min.z), new Vector3(lenX, thickness, thickness));
			AppendBox(vertices, triangles, normals, new Vector3(midX, min.y, max.z), new Vector3(lenX, thickness, thickness));
			AppendBox(vertices, triangles, normals, new Vector3(midX, max.y, min.z), new Vector3(lenX, thickness, thickness));
			AppendBox(vertices, triangles, normals, new Vector3(midX, max.y, max.z), new Vector3(lenX, thickness, thickness));

			// Y edges
			AppendBox(vertices, triangles, normals, new Vector3(min.x, midY, min.z), new Vector3(thickness, lenY, thickness));
			AppendBox(vertices, triangles, normals, new Vector3(min.x, midY, max.z), new Vector3(thickness, lenY, thickness));
			AppendBox(vertices, triangles, normals, new Vector3(max.x, midY, min.z), new Vector3(thickness, lenY, thickness));
			AppendBox(vertices, triangles, normals, new Vector3(max.x, midY, max.z), new Vector3(thickness, lenY, thickness));

			// Z edges
			AppendBox(vertices, triangles, normals, new Vector3(min.x, min.y, midZ), new Vector3(thickness, thickness, lenZ));
			AppendBox(vertices, triangles, normals, new Vector3(min.x, max.y, midZ), new Vector3(thickness, thickness, lenZ));
			AppendBox(vertices, triangles, normals, new Vector3(max.x, min.y, midZ), new Vector3(thickness, thickness, lenZ));
			AppendBox(vertices, triangles, normals, new Vector3(max.x, max.y, midZ), new Vector3(thickness, thickness, lenZ));

			mesh.SetVertices(vertices);
			mesh.SetNormals(normals);
			mesh.SetTriangles(triangles, 0);
			mesh.RecalculateBounds();
			return mesh;
		}

		private static void AppendBox(List<Vector3> vertices, List<int> triangles, List<Vector3> normals, Vector3 center, Vector3 size)
		{
			Vector3 half = size * 0.5f;
			Vector3 v0 = center + new Vector3(-half.x, -half.y, -half.z);
			Vector3 v1 = center + new Vector3(half.x, -half.y, -half.z);
			Vector3 v2 = center + new Vector3(half.x, -half.y, half.z);
			Vector3 v3 = center + new Vector3(-half.x, -half.y, half.z);
			Vector3 v4 = center + new Vector3(-half.x, half.y, -half.z);
			Vector3 v5 = center + new Vector3(half.x, half.y, -half.z);
			Vector3 v6 = center + new Vector3(half.x, half.y, half.z);
			Vector3 v7 = center + new Vector3(-half.x, half.y, half.z);

			AddQuad(vertices, triangles, normals, v0, v1, v2, v3, Vector3.down);
			AddQuad(vertices, triangles, normals, v4, v5, v6, v7, Vector3.up);
			AddQuad(vertices, triangles, normals, v3, v2, v6, v7, Vector3.forward);
			AddQuad(vertices, triangles, normals, v1, v0, v4, v5, Vector3.back);
			AddQuad(vertices, triangles, normals, v0, v3, v7, v4, Vector3.left);
			AddQuad(vertices, triangles, normals, v2, v1, v5, v6, Vector3.right);
		}

		private static void AddQuad(List<Vector3> vertices, List<int> triangles, List<Vector3> normals,
			Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 normal)
		{
			int start = vertices.Count;
			vertices.Add(a);
			vertices.Add(b);
			vertices.Add(c);
			vertices.Add(d);

			normals.Add(normal);
			normals.Add(normal);
			normals.Add(normal);
			normals.Add(normal);

			triangles.Add(start + 0);
			triangles.Add(start + 1);
			triangles.Add(start + 2);
			triangles.Add(start + 0);
			triangles.Add(start + 2);
			triangles.Add(start + 3);
		}

		private static bool IsPlayerInBuildMode(Player player)
		{
			if (player == null)
			{
				return false;
			}

			if (!_playerInPlaceModeSearched)
			{
				_playerInPlaceModeMethod = AccessTools.Method(typeof(Player), "InPlaceMode");
				_playerInPlaceModeSearched = true;
			}

			if (_playerInPlaceModeMethod != null)
			{
				try
				{
					if ((bool)_playerInPlaceModeMethod.Invoke(player, null))
					{
						return true;
					}
				}
				catch
				{
					// ignore
				}
			}

			if (!_playerGetRightItemSearched)
			{
				_playerGetRightItemMethod = AccessTools.Method(typeof(Player), "GetRightItem");
				_playerGetRightItemSearched = true;
			}

			if (_playerGetRightItemMethod != null)
			{
				try
				{
					ItemDrop.ItemData item = _playerGetRightItemMethod.Invoke(player, null) as ItemDrop.ItemData;
					if (item != null && item.m_shared != null && item.m_shared.m_buildPieces != null)
					{
						return true;
					}
				}
				catch
				{
					// ignore
				}
			}

			return false;
		}

		private bool IsPlayerHovering(Player player)
		{
			GameObject hover = GetPlayerHoverObject(player);
			if (hover == null)
			{
				return false;
			}

			if (hover == gameObject || hover.transform.IsChildOf(transform))
			{
				return true;
			}

			BuildWaterPieceBehaviour owner = hover.GetComponentInParent<BuildWaterPieceBehaviour>();
			return owner == this;
		}

		private static GameObject GetPlayerHoverObject(Player player)
		{
			if (player == null)
			{
				return null;
			}

			if (!_playerGetHoverObjectSearched)
			{
				_playerGetHoverObjectMethod = AccessTools.Method(typeof(Player), "GetHoverObject");
				_playerGetHoverObjectSearched = true;
			}

			if (_playerGetHoverObjectMethod != null)
			{
				try
				{
					object value = _playerGetHoverObjectMethod.Invoke(player, null);
					GameObject resolved = ResolveHoverObject(value);
					if (resolved != null)
					{
						return resolved;
					}
				}
				catch
				{
					// ignore
				}
			}

			if (!_playerHoverFieldSearched)
			{
				FieldInfo[] fields = typeof(Player).GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
				for (int i = 0; i < fields.Length; i++)
				{
					FieldInfo field = fields[i];
					string name = field.Name ?? string.Empty;
					if (name.IndexOf("hover", StringComparison.OrdinalIgnoreCase) < 0)
					{
						continue;
					}
					if (field.FieldType == typeof(GameObject))
					{
						_playerHoverObjectField = field;
						break;
					}
					if (typeof(Component).IsAssignableFrom(field.FieldType))
					{
						_playerHoverComponentField = field;
					}
				}
				_playerHoverFieldSearched = true;
			}

			if (_playerHoverObjectField != null)
			{
				try
				{
					object value = _playerHoverObjectField.GetValue(player);
					GameObject resolved = ResolveHoverObject(value);
					if (resolved != null)
					{
						return resolved;
					}
				}
				catch
				{
					// ignore
				}
			}

			if (_playerHoverComponentField != null)
			{
				try
				{
					object value = _playerHoverComponentField.GetValue(player);
					GameObject resolved = ResolveHoverObject(value);
					if (resolved != null)
					{
						return resolved;
					}
				}
				catch
				{
					// ignore
				}
			}

			return null;
		}

		private static GameObject ResolveHoverObject(object value)
		{
			if (value == null)
			{
				return null;
			}
			if (value is GameObject gameObject)
			{
				return gameObject;
			}
			if (value is Component component)
			{
				return component.gameObject;
			}
			PropertyInfo property = value.GetType().GetProperty("gameObject", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
			if (property != null && typeof(GameObject).IsAssignableFrom(property.PropertyType))
			{
				try
				{
					return property.GetValue(value, null) as GameObject;
				}
				catch
				{
					// ignore
				}
			}
			return null;
		}

		private Vector3 GetHighlightCenter()
		{
			if (_volumeCollider != null)
			{
				return _volumeCollider.bounds.center;
			}
			if (_worldBounds.size.sqrMagnitude > 0.0001f)
			{
				return _worldBounds.center;
			}
			return transform.position;
		}

		private static Material GetHighlightMaterial()
		{
			if (_cachedHighlightMaterial != null)
			{
				return _cachedHighlightMaterial;
			}

			Shader shader = Shader.Find("Hidden/Internal-Colored");
			if (shader == null)
			{
				shader = Shader.Find("Unlit/Color");
			}
			if (shader == null)
			{
				shader = Shader.Find("UI/Default");
			}
			if (shader == null)
			{
				shader = Shader.Find("Standard");
			}
			if (shader == null)
			{
				return null;
			}

			Material material = new Material(shader);
			material.name = "BuildWater_Highlight";
			Color color = new Color(0.2f, 0.9f, 1f, 0.8f);
			material.color = color;
			material.renderQueue = (int)RenderQueue.Overlay;
			material.SetInt("_Cull", (int)CullMode.Off);
			material.SetInt("_ZWrite", 0);
			material.SetInt("_ZTest", (int)CompareFunction.Always);
			material.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
			material.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);

			if (shader.name == "Standard")
			{
				material.DisableKeyword("_ALPHATEST_ON");
				material.EnableKeyword("_ALPHABLEND_ON");
				material.DisableKeyword("_ALPHAPREMULTIPLY_ON");
				material.SetFloat("_Mode", 3f);
				material.EnableKeyword("_EMISSION");
				material.SetColor("_EmissionColor", new Color(0.4f, 0.9f, 1f, 1.2f));
				material.renderQueue = (int)RenderQueue.Overlay;
			}
			else
			{
				try
				{
					material.EnableKeyword("_EMISSION");
					material.SetColor("_EmissionColor", new Color(0.2f, 0.8f, 1f, 0.9f));
				}
				catch
				{
					// ignore missing emission properties
				}
			}

			_cachedHighlightMaterial = material;
			return _cachedHighlightMaterial;
		}

		private bool IsWaterSurface(Transform t)
		{
			Transform current = t;
			while (current != null)
			{
				if (current.name == WaterRootName || current.name == "WaterSurface")
				{
					return true;
				}
				current = current.parent;
			}
			return false;
		}

		private bool IsGhostOnly(Transform t)
		{
			Transform current = t;
			while (current != null)
			{
				if (current.name == "_GhostOnly")
				{
					return true;
				}
				current = current.parent;
			}
			return false;
		}

		private bool IsConnectionsElement(Transform t)
		{
			Transform current = t;
			while (current != null)
			{
				if (current.name == ConnectionsRootName)
				{
					return true;
				}
				current = current.parent;
			}
			return false;
		}

		private bool IsHighlightElement(Transform t)
		{
			Transform current = t;
			while (current != null)
			{
				if (current.name == HighlightRootName)
				{
					return true;
				}
				current = current.parent;
			}
			return false;
		}

		private static bool IsGeneratedMesh(Mesh mesh)
		{
			if (mesh == null)
			{
				return false;
			}
			string name = mesh.name ?? string.Empty;
			return name.StartsWith("BuildWater_", StringComparison.OrdinalIgnoreCase);
		}

		private static void DestroyGeneratedMesh(Mesh mesh)
		{
			if (!IsGeneratedMesh(mesh))
			{
				return;
			}
			try
			{
				UnityEngine.Object.Destroy(mesh);
			}
			catch
			{
				// ignore
			}
		}

		private static void DestroyGeneratedMeshes(Transform root)
		{
			if (root == null)
			{
				return;
			}

			MeshFilter[] filters = root.GetComponentsInChildren<MeshFilter>(true);
			foreach (MeshFilter filter in filters)
			{
				if (filter == null)
				{
					continue;
				}
				DestroyGeneratedMesh(filter.sharedMesh);
				filter.sharedMesh = null;
			}
		}

		private void EnsureColliderLayer()
		{
			int nonSolid = LayerMask.NameToLayer("piece_nonsolid");
			int targetLayer = nonSolid;
			if (_terrainToolMode)
			{
				int waterLayer = ResolveWaterLayer();
				if (waterLayer >= 0)
				{
					targetLayer = waterLayer;
				}
			}
			foreach (Collider collider in GetComponentsInChildren<Collider>(true))
			{
				if (collider == null)
				{
					continue;
				}
				if (IsWaterSurface(collider.transform))
				{
					// Keep water colliders on water layer so triggers work.
					continue;
				}
				collider.gameObject.layer = targetLayer;
			}
		}

		private void EnsureWaterVolume()
		{
			Transform existing = transform.Find(WaterRootName);
			if (existing != null)
			{
				_waterRoot = existing;
				_volumeCollider = existing.GetComponent<BoxCollider>();
				_waterVolume = existing.GetComponent<WaterVolume>();
				Transform surface = existing.Find("WaterSurface");
				if (surface != null)
				{
					_surfaceRenderer = surface.GetComponent<MeshRenderer>();
				}

				_surfaceOffset = BuildWaterPlugin.WaterLevelOffset.Value;
				_volumeDepth = BuildWaterPlugin.WaterDepth.Value;
				_manualDown = Mathf.Max(_volumeDepth, 3f);
				_manualUp = GetPlayerCheckAboveSurface();

				WaterVolume referenceVolumeExisting = FindReferenceWaterVolume(_waterVolume);
				ApplyWaterLayerAndTag(existing, referenceVolumeExisting);
				ApplyWaveDamping(_waterVolume);
				if (_surfaceRenderer != null)
				{
					ApplyWaterMaterial(_surfaceRenderer, _waterVolume);
				}
				if (_volumeCollider != null)
				{
					_volumeCollider.isTrigger = true;
				}
				TryAddWaterTrigger(existing.gameObject, _waterVolume, _volumeCollider, _surfaceRenderer);
				TryAddLiquidComponents(existing.gameObject, _surfaceRenderer, _volumeCollider, _waterVolume);

				RefreshBounds();
				EnsureSurfaceSizingFromBounds();
				return;
			}

			Bounds bounds;
			if (!TryGetBounds(out bounds))
			{
				bounds = new Bounds(transform.position, new Vector3(2f, 0.2f, 2f));
			}
			bounds = NormalizeBounds(bounds);
			Bounds waterBounds = ExpandBoundsXZ(bounds, GetSurfacePadding());

			WaterVolume referenceVolume = FindReferenceWaterVolume(null);
			if (referenceVolume == null)
			{
				if (BuildWaterPlugin.Log != null)
				{
					BuildWaterPlugin.Log.LogWarning("BuildWater: no reference WaterVolume found; skipping water volume setup to avoid null state.");
				}
				return;
			}

			float depth = BuildWaterPlugin.WaterDepth.Value;
			float surfaceOffset = BuildWaterPlugin.WaterLevelOffset.Value;
			_surfaceOffset = surfaceOffset;
			_manualDown = Mathf.Max(depth, 3f + bounds.size.y);
			_manualUp = GetPlayerCheckAboveSurface();
			_volumeDepth = depth;
			_worldBounds = waterBounds;
			_surfaceWorldY = ComputeSurfaceWorldY(bounds, surfaceOffset);

			GameObject waterRoot = new GameObject(WaterRootName);
			waterRoot.transform.SetParent(transform, false);
			waterRoot.transform.localPosition = Vector3.zero;
			waterRoot.transform.localRotation = Quaternion.identity;
			_waterRoot = waterRoot.transform;

			Vector3 centerXZ = ResolveCenterXZ(waterBounds);
			Vector3 surfaceWorldPos = new Vector3(centerXZ.x, _surfaceWorldY, centerXZ.z);
			Vector3 localCenter = waterRoot.transform.InverseTransformPoint(centerXZ);
			Vector3 localSurface = waterRoot.transform.InverseTransformPoint(surfaceWorldPos);
			float localSurfaceY = localSurface.y;
			int waterLayer = ResolveWaterLayer();
			if (referenceVolume != null)
			{
				waterLayer = referenceVolume.gameObject.layer;
				if (!string.IsNullOrEmpty(referenceVolume.gameObject.tag))
				{
					waterRoot.tag = referenceVolume.gameObject.tag;
				}
			}
			waterRoot.layer = waterLayer;

			BoxCollider volumeCollider = waterRoot.AddComponent<BoxCollider>();
			volumeCollider.isTrigger = true;
			float volumeHeight = _manualDown + _manualUp;
			volumeCollider.size = new Vector3(waterBounds.size.x, volumeHeight, waterBounds.size.z);
			volumeCollider.center = new Vector3(localCenter.x, localSurfaceY + (_manualUp - _manualDown) * 0.5f, localCenter.z);
			volumeCollider.gameObject.layer = waterLayer;

			MeshRenderer surfaceRenderer = CreateSurfaceFromReference(waterBounds.size.x, waterBounds.size.z, localCenter, localSurfaceY, waterRoot.transform, waterLayer, waterRoot.tag);
			_surfaceRenderer = surfaceRenderer;

			WaterVolume waterVolume = waterRoot.AddComponent<WaterVolume>();
			CopyWaterVolumeDefaults(waterVolume, referenceVolume);
			ApplyWaveDamping(waterVolume);
			waterVolume.m_waterSurface = surfaceRenderer;
			TrySetWaterVolumeCollider(waterVolume, volumeCollider);
			// Keep heightmap reference from the original volume if available to avoid null state.
			// Use forced depth for custom volumes; avoid heightmap coupling.
			waterVolume.m_heightmap = null;
			waterVolume.m_forceDepth = Mathf.Clamp01(depth / 10f);
			waterVolume.m_surfaceOffset = localSurfaceY;
			waterVolume.m_useGlobalWind = true;
			ApplyWaterVolumeSurfaceRefs(waterVolume, surfaceRenderer, volumeCollider);
			SetBoolFieldIfExists(waterVolume, "m_isWater", true);
			SetBoolFieldIfExists(waterVolume, "m_isLiquid", true);
			SetEnumFieldIfExists(waterVolume, "m_liquidType", "Water");

			ApplyWaterMaterial(surfaceRenderer, waterVolume);
			TryAddWaterTrigger(waterRoot, waterVolume, volumeCollider, surfaceRenderer);
			TryAddLiquidComponents(waterRoot, surfaceRenderer, volumeCollider, waterVolume);
			BuildWaterDebugReporter reporter = waterRoot.AddComponent<BuildWaterDebugReporter>();
			reporter.Initialize();

			_waterVolume = waterVolume;
			_volumeCollider = volumeCollider;
			_surfaceWorldY = surfaceRenderer != null ? surfaceRenderer.transform.position.y : volumeCollider.bounds.max.y;

			LogVolumeSetup(bounds, localCenter, localSurfaceY, volumeCollider, surfaceRenderer, waterRoot.transform);
		}

		private void EnsureConnectionsRoot()
		{
			if (_connectionsRoot != null)
			{
				return;
			}
			Transform existing = transform.Find(ConnectionsRootName);
			if (existing != null)
			{
				_connectionsRoot = existing;
				return;
			}
			GameObject root = new GameObject(ConnectionsRootName);
			root.transform.SetParent(_waterRoot != null ? _waterRoot : transform, false);
			root.transform.localPosition = Vector3.zero;
			root.transform.localRotation = Quaternion.identity;
			_connectionsRoot = root.transform;
		}

		private void TrackTransformChanges()
		{
			Vector3 position = transform.position;
			Quaternion rotation = transform.rotation;
			Vector3 scale = transform.lossyScale;

			bool changed = Vector3.Distance(position, _lastPosition) > 0.05f ||
				Quaternion.Angle(rotation, _lastRotation) > 1f ||
				Vector3.Distance(scale, _lastScale) > 0.02f;

			if (!changed)
			{
				if (Mathf.Abs(_surfaceWorldY - _lastSurfaceY) > 0.05f)
				{
					changed = true;
				}
				else
				{
					Bounds bounds = _worldBounds;
					if (Mathf.Abs(bounds.size.x - _lastBounds.size.x) > 0.05f ||
						Mathf.Abs(bounds.size.z - _lastBounds.size.z) > 0.05f)
					{
						changed = true;
					}
				}
			}

			if (!changed)
			{
				return;
			}

			_lastPosition = position;
			_lastRotation = rotation;
			_lastScale = scale;
			_lastSurfaceY = _surfaceWorldY;
			_lastBounds = _worldBounds;

			UpdateGridCell();
			MarkConnectionsDirty();
			NotifyNeighbors(_worldBounds);
		}

		private bool HasTransformChanged()
		{
			Vector3 position = transform.position;
			Quaternion rotation = transform.rotation;
			Vector3 scale = transform.lossyScale;

			return Vector3.Distance(position, _lastPosition) > 0.05f ||
				Quaternion.Angle(rotation, _lastRotation) > 1f ||
				Vector3.Distance(scale, _lastScale) > 0.02f;
		}

		private void UpdateConnectionsIfNeeded()
		{
			if (!_connectionsDirty)
			{
				return;
			}

			_connectionsRebuildTimer += Time.deltaTime;
			if (_connectionsRebuildTimer < ConnectionRebuildInterval)
			{
				return;
			}

			_connectionsRebuildTimer = 0f;
			_connectionsDirty = false;
			RebuildConnections();
		}

		private void UpdateClusterSurfaceIfNeeded()
		{
			if (!UseClusterSurface || _surfaceRenderer == null)
			{
				return;
			}
			if (!_clusterDirty)
			{
				return;
			}

			_clusterRebuildTimer += Time.deltaTime;
			if (_clusterRebuildTimer < ClusterRebuildInterval)
			{
				return;
			}

			_clusterRebuildTimer = 0f;
			_clusterDirty = false;
			RebuildClusterSurface();
		}

		private void MarkConnectionsDirty()
		{
			_connectionsDirty = true;
			_connectionsRebuildTimer = 0f;
			_supportClusterCount = 0;
			_supportClusterTime = 0f;
			if (UseClusterSurface)
			{
				_clusterDirty = true;
				_clusterRebuildTimer = 0f;
			}
		}

		private void RebuildClusterSurface()
		{
			if (!UseClusterSurface || _surfaceRenderer == null || _waterVolume == null)
			{
				return;
			}

			System.Collections.Generic.List<BuildWaterPieceBehaviour> cluster = CollectClusterTiles();
			if (cluster.Count == 0)
			{
				return;
			}

			float cacheTime = Time.time;
			int clusterCount = cluster.Count;
			for (int i = 0; i < cluster.Count; i++)
			{
				BuildWaterPieceBehaviour tile = cluster[i];
				if (tile == null)
				{
					continue;
				}
				tile._supportClusterCount = clusterCount;
				tile._supportClusterTime = cacheTime;
			}

			BuildWaterPieceBehaviour owner = cluster[0];
			for (int i = 1; i < cluster.Count; i++)
			{
				BuildWaterPieceBehaviour candidate = cluster[i];
				if (candidate != null && candidate._instanceKey < owner._instanceKey)
				{
					owner = candidate;
				}
			}

			if (owner != this)
			{
				if (_surfaceRenderer != null)
				{
					_surfaceRenderer.enabled = false;
				}
				return;
			}

			for (int i = 0; i < cluster.Count; i++)
			{
				BuildWaterPieceBehaviour tile = cluster[i];
				if (tile == null || tile == this)
				{
					continue;
				}
				if (tile._surfaceRenderer != null)
				{
					tile._surfaceRenderer.enabled = false;
				}
			}

			_surfaceRenderer.enabled = true;
			BuildClusterMesh(cluster);
		}

		private System.Collections.Generic.List<BuildWaterPieceBehaviour> CollectClusterTiles()
		{
			System.Collections.Generic.List<BuildWaterPieceBehaviour> result = new System.Collections.Generic.List<BuildWaterPieceBehaviour>();
			if (_waterVolume == null)
			{
				return result;
			}

			float surfaceY = GetSurfaceWorldY();
			System.Collections.Generic.List<BuildWaterPieceBehaviour> queue = new System.Collections.Generic.List<BuildWaterPieceBehaviour>();
			System.Collections.Generic.HashSet<BuildWaterPieceBehaviour> visited = new System.Collections.Generic.HashSet<BuildWaterPieceBehaviour>();

			queue.Add(this);
			visited.Add(this);

			int queueIndex = 0;
			while (queueIndex < queue.Count)
			{
				BuildWaterPieceBehaviour current = queue[queueIndex++];
				if (current == null)
				{
					continue;
				}
				result.Add(current);

				Bounds currentBounds = NormalizeBounds(current.GetSurfaceBounds());
				foreach (BuildWaterPieceBehaviour neighbor in QueryNeighbors(currentBounds))
				{
					if (neighbor == null || visited.Contains(neighbor))
					{
						continue;
					}
					if (neighbor._waterVolume == null)
					{
						continue;
					}
					if (Mathf.Abs(neighbor.GetSurfaceWorldY() - surfaceY) > ConnectionLevelTolerance)
					{
						continue;
					}
					if (!AreTilesAdjacent(current, neighbor))
					{
						continue;
					}

					visited.Add(neighbor);
					queue.Add(neighbor);
				}
			}

			return result;
		}

		internal int GetSupportClusterCount(int limit)
		{
			if (limit <= 0)
			{
				return 0;
			}

			float now = Time.time;
			if (_supportClusterCount > 0 && now - _supportClusterTime < 0.5f)
			{
				return Mathf.Min(_supportClusterCount, limit);
			}

			if (_waterVolume == null)
			{
				return 0;
			}

			float surfaceY = GetSurfaceWorldY();
			System.Collections.Generic.List<BuildWaterPieceBehaviour> queue = new System.Collections.Generic.List<BuildWaterPieceBehaviour>();
			System.Collections.Generic.HashSet<BuildWaterPieceBehaviour> visited = new System.Collections.Generic.HashSet<BuildWaterPieceBehaviour>();

			queue.Add(this);
			visited.Add(this);

			int queueIndex = 0;
			while (queueIndex < queue.Count)
			{
				if (queue.Count >= limit)
				{
					break;
				}

				BuildWaterPieceBehaviour current = queue[queueIndex++];
				if (current == null)
				{
					continue;
				}

				Bounds currentBounds = NormalizeBounds(current.GetSurfaceBounds());
				foreach (BuildWaterPieceBehaviour neighbor in QueryNeighbors(currentBounds))
				{
					if (neighbor == null || visited.Contains(neighbor))
					{
						continue;
					}
					if (neighbor._waterVolume == null)
					{
						continue;
					}
					if (Mathf.Abs(neighbor.GetSurfaceWorldY() - surfaceY) > ConnectionLevelTolerance)
					{
						continue;
					}
					if (!AreTilesAdjacent(current, neighbor))
					{
						continue;
					}

					visited.Add(neighbor);
					queue.Add(neighbor);
					if (queue.Count >= limit)
					{
						break;
					}
				}
			}

			_supportClusterCount = queue.Count;
			_supportClusterTime = now;
			return queue.Count;
		}

		private bool AreTilesAdjacent(BuildWaterPieceBehaviour a, BuildWaterPieceBehaviour b)
		{
			if (a == null || b == null)
			{
				return false;
			}

			float tileSize = Mathf.Max(a.GetTileSizeXZ(), b.GetTileSizeXZ());
			if (tileSize <= 0.01f)
			{
				return false;
			}

			float tolerance = tileSize * ClusterAdjacencyTolerance;
			Vector3 aCenter = a.GetSurfaceBounds().center;
			Vector3 bCenter = b.GetSurfaceBounds().center;
			float dx = Mathf.Abs(aCenter.x - bCenter.x);
			float dz = Mathf.Abs(aCenter.z - bCenter.z);

			bool adjacentX = Mathf.Abs(dx - tileSize) <= tolerance && dz <= tolerance;
			bool adjacentZ = Mathf.Abs(dz - tileSize) <= tolerance && dx <= tolerance;
			return adjacentX || adjacentZ;
		}

		private float GetTileSizeXZ()
		{
			Bounds bounds = NormalizeBounds(GetSurfaceBounds());
			return Mathf.Max(bounds.size.x, bounds.size.z);
		}

		private void BuildClusterMesh(System.Collections.Generic.List<BuildWaterPieceBehaviour> cluster)
		{
			if (_surfaceRenderer == null)
			{
				return;
			}

			MeshFilter filter = _surfaceRenderer.GetComponent<MeshFilter>();
			if (filter == null)
			{
				filter = _surfaceRenderer.gameObject.AddComponent<MeshFilter>();
			}

			if (_clusterMesh == null)
			{
				_clusterMesh = new Mesh();
				_clusterMesh.name = "BuildWater_SurfaceCluster";
				_clusterMesh.hideFlags = HideFlags.HideAndDontSave;
				_clusterMesh.MarkDynamic();
			}
			_clusterMesh.Clear();

			float overlap = ClusterEdgeOverlap;
			float surfaceY = GetSurfaceWorldY();

			float minX = float.PositiveInfinity;
			float maxX = float.NegativeInfinity;
			float minZ = float.PositiveInfinity;
			float maxZ = float.NegativeInfinity;

			for (int i = 0; i < cluster.Count; i++)
			{
				BuildWaterPieceBehaviour tile = cluster[i];
				if (tile == null)
				{
					continue;
				}
				Bounds b = NormalizeBounds(tile.GetSurfaceBounds());
				minX = Mathf.Min(minX, b.min.x - overlap);
				maxX = Mathf.Max(maxX, b.max.x + overlap);
				minZ = Mathf.Min(minZ, b.min.z - overlap);
				maxZ = Mathf.Max(maxZ, b.max.z + overlap);
			}

			Vector2 uvOrigin = new Vector2(minX, minZ);
			System.Collections.Generic.List<Vector3> vertices = new System.Collections.Generic.List<Vector3>();
			System.Collections.Generic.List<Vector3> normals = new System.Collections.Generic.List<Vector3>();
			System.Collections.Generic.List<Vector2> uvs = new System.Collections.Generic.List<Vector2>();
			System.Collections.Generic.List<int> triangles = new System.Collections.Generic.List<int>();

			for (int i = 0; i < cluster.Count; i++)
			{
				BuildWaterPieceBehaviour tile = cluster[i];
				if (tile == null)
				{
					continue;
				}

				Bounds b = NormalizeBounds(tile.GetSurfaceBounds());
				float minTileX = b.min.x - overlap;
				float maxTileX = b.max.x + overlap;
				float minTileZ = b.min.z - overlap;
				float maxTileZ = b.max.z + overlap;

				AppendClusterQuad(vertices, normals, uvs, triangles, minTileX, maxTileX, minTileZ, maxTileZ, surfaceY, uvOrigin);
			}

			if (UseClusterCurtains)
			{
				for (int i = 0; i < cluster.Count; i++)
				{
					BuildWaterPieceBehaviour tile = cluster[i];
					if (tile == null)
					{
						continue;
					}
					AppendClusterCurtainsForTile(tile, vertices, normals, uvs, triangles, uvOrigin);
				}
			}

			_clusterMesh.SetVertices(vertices);
			_clusterMesh.SetNormals(normals);
			_clusterMesh.SetUVs(0, uvs);
			_clusterMesh.SetTriangles(triangles, 0);
			_clusterMesh.RecalculateBounds();
			filter.sharedMesh = _clusterMesh;

			BoxCollider surfaceTrigger = _surfaceRenderer.GetComponent<BoxCollider>();
			if (surfaceTrigger != null && IsFinite(minX) && IsFinite(minZ))
			{
				Vector3 centerWorld = new Vector3((minX + maxX) * 0.5f, surfaceY, (minZ + maxZ) * 0.5f);
				Vector3 localCenter = _surfaceRenderer.transform.InverseTransformPoint(centerWorld);
				surfaceTrigger.size = new Vector3(maxX - minX, surfaceTrigger.size.y, maxZ - minZ);
				surfaceTrigger.center = new Vector3(localCenter.x, 0f, localCenter.z);
			}

			if (UseSurfaceWaveDeform)
			{
				WaterBridgeUpdater updater = _surfaceRenderer.GetComponent<WaterBridgeUpdater>();
				if (updater == null)
				{
					updater = _surfaceRenderer.gameObject.AddComponent<WaterBridgeUpdater>();
				}
				updater.Initialize(_waterVolume, filter);
			}
			else
			{
				WaterBridgeUpdater updater = _surfaceRenderer.GetComponent<WaterBridgeUpdater>();
				if (updater != null)
				{
					Destroy(updater);
				}
			}
		}

		private void AppendClusterQuad(
			System.Collections.Generic.List<Vector3> vertices,
			System.Collections.Generic.List<Vector3> normals,
			System.Collections.Generic.List<Vector2> uvs,
			System.Collections.Generic.List<int> triangles,
			float minX,
			float maxX,
			float minZ,
			float maxZ,
			float surfaceY,
			Vector2 uvOrigin)
		{
			Vector3 worldA = new Vector3(minX, surfaceY, minZ);
			Vector3 worldB = new Vector3(maxX, surfaceY, minZ);
			Vector3 worldC = new Vector3(maxX, surfaceY, maxZ);
			Vector3 worldD = new Vector3(minX, surfaceY, maxZ);

			Vector3 a = _surfaceRenderer.transform.InverseTransformPoint(worldA);
			Vector3 b = _surfaceRenderer.transform.InverseTransformPoint(worldB);
			Vector3 c = _surfaceRenderer.transform.InverseTransformPoint(worldC);
			Vector3 d = _surfaceRenderer.transform.InverseTransformPoint(worldD);

			float u0 = (minX - uvOrigin.x) / ClusterUvScale;
			float u1 = (maxX - uvOrigin.x) / ClusterUvScale;
			float v0 = (minZ - uvOrigin.y) / ClusterUvScale;
			float v1 = (maxZ - uvOrigin.y) / ClusterUvScale;

			int baseIndex = vertices.Count;
			vertices.Add(a);
			vertices.Add(b);
			vertices.Add(c);
			vertices.Add(d);
			vertices.Add(a);
			vertices.Add(b);
			vertices.Add(c);
			vertices.Add(d);

			normals.Add(Vector3.up);
			normals.Add(Vector3.up);
			normals.Add(Vector3.up);
			normals.Add(Vector3.up);
			normals.Add(Vector3.down);
			normals.Add(Vector3.down);
			normals.Add(Vector3.down);
			normals.Add(Vector3.down);

			uvs.Add(new Vector2(u0, v0));
			uvs.Add(new Vector2(u1, v0));
			uvs.Add(new Vector2(u1, v1));
			uvs.Add(new Vector2(u0, v1));
			uvs.Add(new Vector2(u0, v0));
			uvs.Add(new Vector2(u1, v0));
			uvs.Add(new Vector2(u1, v1));
			uvs.Add(new Vector2(u0, v1));

			triangles.Add(baseIndex + 0);
			triangles.Add(baseIndex + 1);
			triangles.Add(baseIndex + 2);
			triangles.Add(baseIndex + 0);
			triangles.Add(baseIndex + 2);
			triangles.Add(baseIndex + 3);

			triangles.Add(baseIndex + 7);
			triangles.Add(baseIndex + 6);
			triangles.Add(baseIndex + 5);
			triangles.Add(baseIndex + 7);
			triangles.Add(baseIndex + 5);
			triangles.Add(baseIndex + 4);
		}

		private void AppendClusterCurtainsForTile(
			BuildWaterPieceBehaviour tile,
			System.Collections.Generic.List<Vector3> vertices,
			System.Collections.Generic.List<Vector3> normals,
			System.Collections.Generic.List<Vector2> uvs,
			System.Collections.Generic.List<int> triangles,
			Vector2 uvOrigin)
		{
			if (tile == null)
			{
				return;
			}

			float tileSize = tile.GetTileSizeXZ();
			if (tileSize <= 0.01f)
			{
				return;
			}

			float tolerance = tileSize * ClusterAdjacencyTolerance;
			float tileSurface = tile.GetSurfaceWorldY();
			Bounds bounds = NormalizeBounds(tile.GetSurfaceBounds());
			Vector3 center = bounds.center;

			BuildWaterPieceBehaviour posX = null;
			BuildWaterPieceBehaviour negX = null;
			BuildWaterPieceBehaviour posZ = null;
			BuildWaterPieceBehaviour negZ = null;
			float posXDiff = float.PositiveInfinity;
			float negXDiff = float.PositiveInfinity;
			float posZDiff = float.PositiveInfinity;
			float negZDiff = float.PositiveInfinity;

			foreach (BuildWaterPieceBehaviour neighbor in QueryNeighbors(bounds))
			{
				if (neighbor == null || neighbor == tile || neighbor._waterVolume == null)
				{
					continue;
				}

				Bounds neighborBounds = NormalizeBounds(neighbor.GetSurfaceBounds());
				Vector3 neighborCenter = neighborBounds.center;
				float dx = neighborCenter.x - center.x;
				float dz = neighborCenter.z - center.z;
				float absDx = Mathf.Abs(dx);
				float absDz = Mathf.Abs(dz);

				if (Mathf.Abs(absDx - tileSize) <= tolerance && absDz <= tolerance)
				{
					float diff = Mathf.Abs(neighbor.GetSurfaceWorldY() - tileSurface);
					if (dx > 0f)
					{
						if (diff < posXDiff)
						{
							posXDiff = diff;
							posX = neighbor;
						}
					}
					else if (dx < 0f)
					{
						if (diff < negXDiff)
						{
							negXDiff = diff;
							negX = neighbor;
						}
					}
				}
				if (Mathf.Abs(absDz - tileSize) <= tolerance && absDx <= tolerance)
				{
					float diff = Mathf.Abs(neighbor.GetSurfaceWorldY() - tileSurface);
					if (dz > 0f)
					{
						if (diff < posZDiff)
						{
							posZDiff = diff;
							posZ = neighbor;
						}
					}
					else if (dz < 0f)
					{
						if (diff < negZDiff)
						{
							negZDiff = diff;
							negZ = neighbor;
						}
					}
				}
			}

			float overlap = ClusterEdgeOverlap;
			float minX = bounds.min.x - overlap;
			float maxX = bounds.max.x + overlap;
			float minZ = bounds.min.z - overlap;
			float maxZ = bounds.max.z + overlap;

			bool hasPosX = TryAppendClusterCurtainEdge(vertices, normals, uvs, triangles, uvOrigin, tile, posX, tileSurface,
				new Vector3(maxX, 0f, minZ), new Vector3(maxX, 0f, maxZ), Vector3.right, out float bottomPosX);
			bool hasNegX = TryAppendClusterCurtainEdge(vertices, normals, uvs, triangles, uvOrigin, tile, negX, tileSurface,
				new Vector3(minX, 0f, minZ), new Vector3(minX, 0f, maxZ), Vector3.left, out float bottomNegX);
			bool hasPosZ = TryAppendClusterCurtainEdge(vertices, normals, uvs, triangles, uvOrigin, tile, posZ, tileSurface,
				new Vector3(minX, 0f, maxZ), new Vector3(maxX, 0f, maxZ), Vector3.forward, out float bottomPosZ);
			bool hasNegZ = TryAppendClusterCurtainEdge(vertices, normals, uvs, triangles, uvOrigin, tile, negZ, tileSurface,
				new Vector3(minX, 0f, minZ), new Vector3(maxX, 0f, minZ), Vector3.back, out float bottomNegZ);

			if (hasPosX && hasPosZ)
			{
				float cornerBottom = Mathf.Max(bottomPosX, bottomPosZ);
				AppendClusterCurtainCorner(vertices, normals, uvs, triangles, uvOrigin,
					new Vector3(maxX, 0f, maxZ), tileSurface, cornerBottom, Vector3.right, Vector3.forward);
			}
			if (hasPosX && hasNegZ)
			{
				float cornerBottom = Mathf.Max(bottomPosX, bottomNegZ);
				AppendClusterCurtainCorner(vertices, normals, uvs, triangles, uvOrigin,
					new Vector3(maxX, 0f, minZ), tileSurface, cornerBottom, Vector3.right, Vector3.back);
			}
			if (hasNegX && hasPosZ)
			{
				float cornerBottom = Mathf.Max(bottomNegX, bottomPosZ);
				AppendClusterCurtainCorner(vertices, normals, uvs, triangles, uvOrigin,
					new Vector3(minX, 0f, maxZ), tileSurface, cornerBottom, Vector3.left, Vector3.forward);
			}
			if (hasNegX && hasNegZ)
			{
				float cornerBottom = Mathf.Max(bottomNegX, bottomNegZ);
				AppendClusterCurtainCorner(vertices, normals, uvs, triangles, uvOrigin,
					new Vector3(minX, 0f, minZ), tileSurface, cornerBottom, Vector3.left, Vector3.back);
			}
		}

		private bool TryAppendClusterCurtainEdge(
			System.Collections.Generic.List<Vector3> vertices,
			System.Collections.Generic.List<Vector3> normals,
			System.Collections.Generic.List<Vector2> uvs,
			System.Collections.Generic.List<int> triangles,
			Vector2 uvOrigin,
			BuildWaterPieceBehaviour tile,
			BuildWaterPieceBehaviour neighbor,
			float tileSurface,
			Vector3 edgeStart,
			Vector3 edgeEnd,
			Vector3 outward,
			out float bottomY)
		{
			if (!TryGetCurtainBottom(tile, neighbor, tileSurface, out bottomY))
			{
				return false;
			}

			float height = tileSurface - bottomY;
			if (height <= ClusterCurtainMinHeight)
			{
				return false;
			}

			AppendClusterCurtain(vertices, normals, uvs, triangles, edgeStart, edgeEnd, tileSurface, bottomY, outward, uvOrigin);
			return true;
		}

		private bool TryGetCurtainBottom(BuildWaterPieceBehaviour tile, BuildWaterPieceBehaviour neighbor, float tileSurface, out float bottomY)
		{
			bottomY = tileSurface;
			if (tile == null)
			{
				return false;
			}

			float minBottom = tileSurface - GetCurtainMinDepth();
			float volumeBottom = tile.GetVolumeBottomWorldY();

			if (neighbor != null)
			{
				float neighborSurface = neighbor.GetSurfaceWorldY();
				if (tileSurface <= neighborSurface + ConnectionLevelTolerance)
				{
					return false;
				}
				float neighborBottom = neighborSurface - ClusterCurtainNeighborOverlap;
				bottomY = Mathf.Min(minBottom, volumeBottom, neighborBottom);
				return true;
			}

			bottomY = Mathf.Min(minBottom, volumeBottom);
			return true;
		}

		private void AppendClusterCurtainCorner(
			System.Collections.Generic.List<Vector3> vertices,
			System.Collections.Generic.List<Vector3> normals,
			System.Collections.Generic.List<Vector2> uvs,
			System.Collections.Generic.List<int> triangles,
			Vector2 uvOrigin,
			Vector3 corner,
			float topY,
			float bottomY,
			Vector3 outwardA,
			Vector3 outwardB)
		{
			float height = topY - bottomY;
			if (height <= ClusterCurtainMinHeight)
			{
				return;
			}

			int segs = Mathf.Clamp(ClusterCurtainSegments, 1, 24);
			float depth = Mathf.Clamp(height * ClusterCurtainDepthScale, ClusterCurtainDepthMin, ClusterCurtainDepthMax);
			Vector3 outward = (outwardA + outwardB).normalized;

			int baseIndex = vertices.Count;
			for (int i = 0; i <= segs; i++)
			{
				float t = i / (float)segs;
				float y = Mathf.Lerp(topY, bottomY, t);
				float offset = ComputeCurtainOffset(t, depth);

				Vector3 aWorld = new Vector3(corner.x, y, corner.z) + outwardA.normalized * offset;
				Vector3 bWorld = new Vector3(corner.x, y, corner.z) + outwardB.normalized * offset;

				Vector3 a = _surfaceRenderer.transform.InverseTransformPoint(aWorld);
				Vector3 b = _surfaceRenderer.transform.InverseTransformPoint(bWorld);

				vertices.Add(a);
				vertices.Add(b);
				normals.Add(outward);
				normals.Add(outward);

				float v = (topY - y) / ClusterUvScale;
				uvs.Add(new Vector2(0f, v));
				uvs.Add(new Vector2(1f, v));
			}

			int frontCount = (segs + 1) * 2;
			for (int i = 0; i < frontCount; i++)
			{
				vertices.Add(vertices[baseIndex + i]);
				normals.Add(-outward);
				uvs.Add(uvs[baseIndex + i]);
			}

			for (int i = 0; i < segs; i++)
			{
				int row = baseIndex + i * 2;
				int next = row + 2;
				triangles.Add(row);
				triangles.Add(next + 1);
				triangles.Add(row + 1);
				triangles.Add(row);
				triangles.Add(next);
				triangles.Add(next + 1);
			}

			int backBase = baseIndex + frontCount;
			for (int i = 0; i < segs; i++)
			{
				int row = backBase + i * 2;
				int next = row + 2;
				triangles.Add(row);
				triangles.Add(row + 1);
				triangles.Add(next + 1);
				triangles.Add(row);
				triangles.Add(next + 1);
				triangles.Add(next);
			}
		}

		private void AppendClusterCurtain(
			System.Collections.Generic.List<Vector3> vertices,
			System.Collections.Generic.List<Vector3> normals,
			System.Collections.Generic.List<Vector2> uvs,
			System.Collections.Generic.List<int> triangles,
			Vector3 edgeStart,
			Vector3 edgeEnd,
			float topY,
			float bottomY,
			Vector3 outward,
			Vector2 uvOrigin)
		{
			float height = topY - bottomY;
			if (height <= 0.001f)
			{
				return;
			}

			int segs = Mathf.Clamp(ClusterCurtainSegments, 1, 24);
			float depth = Mathf.Clamp(height * ClusterCurtainDepthScale, ClusterCurtainDepthMin, ClusterCurtainDepthMax);

			Vector3 edgeDir = new Vector3(edgeEnd.x - edgeStart.x, 0f, edgeEnd.z - edgeStart.z);
			float width = edgeDir.magnitude;
			if (width <= 0.01f)
			{
				return;
			}
			edgeDir /= width;
			outward = outward.normalized;

			int rows = segs + 1;
			int baseIndex = vertices.Count;

			for (int i = 0; i <= segs; i++)
			{
				float t = i / (float)segs;
				float y = Mathf.Lerp(topY, bottomY, t);
				float offset = ComputeCurtainOffset(t, depth);

				Vector3 leftWorld = new Vector3(edgeStart.x, y, edgeStart.z) + outward * offset;
				Vector3 rightWorld = new Vector3(edgeEnd.x, y, edgeEnd.z) + outward * offset;

				Vector3 left = _surfaceRenderer.transform.InverseTransformPoint(leftWorld);
				Vector3 right = _surfaceRenderer.transform.InverseTransformPoint(rightWorld);

				vertices.Add(left);
				vertices.Add(right);

				normals.Add(outward);
				normals.Add(outward);

				float edgeU = (width * 0.5f) / ClusterUvScale;
				float uLeft = -edgeU;
				float uRight = edgeU;
				float v = (topY - y) / ClusterUvScale;
				uvs.Add(new Vector2(uLeft, v));
				uvs.Add(new Vector2(uRight, v));
			}

			int frontCount = rows * 2;
			for (int i = 0; i < frontCount; i++)
			{
				vertices.Add(vertices[baseIndex + i]);
				normals.Add(-outward);
				uvs.Add(uvs[baseIndex + i]);
			}

			for (int i = 0; i < segs; i++)
			{
				int row = baseIndex + i * 2;
				int next = row + 2;
				triangles.Add(row);
				triangles.Add(next + 1);
				triangles.Add(row + 1);
				triangles.Add(row);
				triangles.Add(next);
				triangles.Add(next + 1);
			}

			int backBase = baseIndex + frontCount;
			for (int i = 0; i < segs; i++)
			{
				int row = backBase + i * 2;
				int next = row + 2;
				triangles.Add(row);
				triangles.Add(row + 1);
				triangles.Add(next + 1);
				triangles.Add(row);
				triangles.Add(next + 1);
				triangles.Add(next);
			}
		}

		private float ComputeCurtainOffset(float t, float depth)
		{
			float curve = Mathf.Sin(t * Mathf.PI) * depth * 0.35f;
			return t * depth + curve;
		}

		private void RebuildConnections()
		{
			if (_connectionsRoot == null || _waterVolume == null)
			{
				return;
			}

			ClearConnections();

			Bounds bounds = NormalizeBounds(_worldBounds);
			if (!IsFinite(bounds.center) || !IsFinite(bounds.size))
			{
				return;
			}

			foreach (BuildWaterPieceBehaviour neighbor in QueryNeighbors(bounds))
			{
				if (neighbor == null || neighbor == this)
				{
					continue;
				}
				if (neighbor._waterVolume == null)
				{
					continue;
				}
				if (_instanceKey >= neighbor._instanceKey)
				{
					// Only lower instance creates the connection to avoid duplicates.
					continue;
				}

				CreateConnectionsWith(neighbor);
			}
		}

		private void ClearConnections()
		{
			for (int i = _connectionsRoot.childCount - 1; i >= 0; i--)
			{
				Transform child = _connectionsRoot.GetChild(i);
				if (child != null)
				{
					DestroyGeneratedMeshes(child);
					Destroy(child.gameObject);
				}
			}
			_connections.Clear();
			_connectionInfos.Clear();
		}

		private void CreateConnectionsWith(BuildWaterPieceBehaviour other)
		{
			if (UseClusterSurface || UseDynamicBridgeSurface)
			{
				CreateConnectionsWorldAligned(other);
				return;
			}

			if (!AreSurfacesAligned(other))
			{
				return;
			}

			Quaternion yawRotation = Quaternion.Euler(0f, GetYaw(transform.rotation), 0f);
			Quaternion invRotation = Quaternion.Inverse(yawRotation);

			Bounds a = TransformBoundsByRotation(NormalizeBounds(GetSurfaceBounds()), invRotation);
			Bounds b = TransformBoundsByRotation(NormalizeBounds(other.GetSurfaceBounds()), invRotation);

			float aSurface = GetSurfaceWorldY();
			float bSurface = other.GetSurfaceWorldY();
			float heightDiff = Mathf.Abs(aSurface - bSurface);

			// X axis adjacency
			if (TryGetFaceGap(a.max.x, b.min.x, out float gapPosX, out float gapX))
			{
				TryCreateFaceConnections(gapPosX, gapX, true, a, b, aSurface, bSurface, heightDiff, other, yawRotation);
			}
			if (TryGetFaceGap(b.max.x, a.min.x, out float gapNegX, out float gapX2))
			{
				TryCreateFaceConnections(gapNegX, gapX2, true, a, b, aSurface, bSurface, heightDiff, other, yawRotation);
			}

			// Z axis adjacency
			if (TryGetFaceGap(a.max.z, b.min.z, out float gapPosZ, out float gapZ))
			{
				TryCreateFaceConnections(gapPosZ, gapZ, false, a, b, aSurface, bSurface, heightDiff, other, yawRotation);
			}
			if (TryGetFaceGap(b.max.z, a.min.z, out float gapNegZ, out float gapZ2))
			{
				TryCreateFaceConnections(gapNegZ, gapZ2, false, a, b, aSurface, bSurface, heightDiff, other, yawRotation);
			}
		}

		private void CreateConnectionsWorldAligned(BuildWaterPieceBehaviour other)
		{
			if (other == null)
			{
				return;
			}

			float aSurface = GetSurfaceWorldY();
			float bSurface = other.GetSurfaceWorldY();
			float heightDiff = Mathf.Abs(aSurface - bSurface);
			if (heightDiff > ConnectionLevelTolerance)
			{
				return;
			}

			Bounds a = NormalizeBounds(GetSurfaceBounds());
			Bounds b = NormalizeBounds(other.GetSurfaceBounds());
			float surfaceY = (aSurface + bSurface) * 0.5f;

			// X axis adjacency
			if (TryGetFaceGap(a.max.x, b.min.x, out float gapPosX, out float gapX))
			{
				TryCreateWorldBridge(gapPosX, gapX, true, a, b, surfaceY);
			}
			if (TryGetFaceGap(b.max.x, a.min.x, out float gapNegX, out float gapX2))
			{
				TryCreateWorldBridge(gapNegX, gapX2, true, a, b, surfaceY);
			}

			// Z axis adjacency
			if (TryGetFaceGap(a.max.z, b.min.z, out float gapPosZ, out float gapZ))
			{
				TryCreateWorldBridge(gapPosZ, gapZ, false, a, b, surfaceY);
			}
			if (TryGetFaceGap(b.max.z, a.min.z, out float gapNegZ, out float gapZ2))
			{
				TryCreateWorldBridge(gapNegZ, gapZ2, false, a, b, surfaceY);
			}
		}

		private void TryCreateWorldBridge(float gapCenter, float gap, bool alongX, Bounds a, Bounds b, float surfaceY)
		{
			if (gap > ConnectionGapMax)
			{
				return;
			}

			float overlapMin;
			float overlapMax;
			float overlapLength;
			if (alongX)
			{
				GetOverlap(a.min.z, a.max.z, b.min.z, b.max.z, out overlapMin, out overlapMax, out overlapLength);
			}
			else
			{
				GetOverlap(a.min.x, a.max.x, b.min.x, b.max.x, out overlapMin, out overlapMax, out overlapLength);
			}

			if (overlapLength < ConnectionBridgeMinOverlap)
			{
				return;
			}

			float visualGap = Mathf.Max(gap, ConnectionGapVisualMin);
			Vector3 center = alongX
				? new Vector3(gapCenter, surfaceY + ConnectionSurfaceYOffset, (overlapMin + overlapMax) * 0.5f)
				: new Vector3((overlapMin + overlapMax) * 0.5f, surfaceY + ConnectionSurfaceYOffset, gapCenter);

			float sizeX = alongX ? visualGap : overlapLength;
			float sizeZ = alongX ? overlapLength : visualGap;
			sizeX += ConnectionEdgeOverlap * 2f;
			sizeZ += ConnectionEdgeOverlap * 2f;

			string key = $"{_instanceKey}:{(alongX ? "wx" : "wz")}:{gapCenter:0.00}:{overlapMin:0.00}:{overlapMax:0.00}";
			CreateHorizontalConnection(key, center, sizeX, sizeZ, surfaceY, Quaternion.identity);
		}

		private void TryCreateFaceConnections(float gapCenter, float gap, bool alongX, Bounds a, Bounds b, float aSurface, float bSurface, float heightDiff, BuildWaterPieceBehaviour other, Quaternion yawRotation)
		{
			if (gap < 0f)
			{
				return;
			}
			if (gap > ConnectionGapMax)
			{
				return;
			}

			float overlapMin;
			float overlapMax;
			float overlapLength;
			if (alongX)
			{
				GetOverlap(a.min.z, a.max.z, b.min.z, b.max.z, out overlapMin, out overlapMax, out overlapLength);
			}
			else
			{
				GetOverlap(a.min.x, a.max.x, b.min.x, b.max.x, out overlapMin, out overlapMax, out overlapLength);
			}

			if (overlapLength < ConnectionMinOverlap)
			{
				return;
			}

			string keyPrefix = $"{other._instanceKey}:{(alongX ? "x" : "z")}:{gapCenter:0.00}:{overlapMin:0.00}:{overlapMax:0.00}";

			if (heightDiff <= ConnectionLevelTolerance)
			{
				float surfaceY = (aSurface + bSurface) * 0.5f;
				float visualGap = Mathf.Max(gap, ConnectionGapVisualMin);
				Vector3 localCenter = alongX
					? new Vector3(gapCenter, surfaceY + ConnectionSurfaceYOffset, (overlapMin + overlapMax) * 0.5f)
					: new Vector3((overlapMin + overlapMax) * 0.5f, surfaceY + ConnectionSurfaceYOffset, gapCenter);
				Vector3 center = yawRotation * localCenter;
				float sizeX = alongX ? visualGap : overlapLength;
				float sizeZ = alongX ? overlapLength : visualGap;
				sizeX += ConnectionEdgeOverlap * 2f;
				sizeZ += ConnectionEdgeOverlap * 2f;
				CreateHorizontalConnection($"{keyPrefix}:h", center, sizeX, sizeZ, surfaceY, yawRotation);
				return;
			}

			// Skip vertical cascades for now (they look incorrect with the current material).
		}

		private void CreateHorizontalConnection(string key, Vector3 center, float sizeX, float sizeZ, float surfaceY, Quaternion rotation)
		{
			if (_connections.ContainsKey(key))
			{
				return;
			}

			GameObject root = new GameObject("WaterConnection_H");
			root.transform.SetParent(_connectionsRoot, false);
			root.transform.position = center;
			root.transform.rotation = rotation;

			float meshX = Mathf.Max(sizeX, 0.1f);
			float meshZ = Mathf.Max(sizeZ, 0.1f);
			bool createVisual = !UseClusterSurface || DebugBridgeMaterial;
			if (createVisual)
			{
				MeshFilter meshFilter = root.AddComponent<MeshFilter>();
				meshFilter.sharedMesh = CreateDoubleSidedQuadMesh(meshX, meshZ);

				MeshRenderer renderer = root.AddComponent<MeshRenderer>();
				if (DebugBridgeMaterial)
				{
					Material debugMat = GetBridgeDebugMaterial();
					if (debugMat != null)
					{
						renderer.sharedMaterial = debugMat;
					}
					else
					{
						ApplyWaterMaterial(renderer, _waterVolume);
					}
				}
				else
				{
					ApplyWaterMaterial(renderer, _waterVolume);
				}
				renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
				renderer.receiveShadows = false;
				renderer.enabled = true;
				if (UseDynamicBridgeSurface)
				{
					WaterBridgeUpdater updater = root.AddComponent<WaterBridgeUpdater>();
					updater.Initialize(_waterVolume, meshFilter);
				}
			}

			BoxCollider collider = root.AddComponent<BoxCollider>();
			collider.isTrigger = true;
			collider.size = new Vector3(meshX, 0.5f, meshZ);
			collider.center = Vector3.zero;

			RegisterConnectionInfo(collider, surfaceY);
			ApplyWaterLayerAndTag(root.transform, _waterVolume);

			_connections[key] = root;
			if (BuildWaterPlugin.Log != null)
			{
				BuildWaterPlugin.Log.LogInfo(
					$"BuildWater: bridge created key={key} center={center} size=({meshX:0.00},{meshZ:0.00}) surfaceY={surfaceY:0.00} parent={_connectionsRoot?.name}");
			}
		}

		private void CreateVerticalConnection(string key, Vector3 center, float width, float height, bool alongX, Quaternion yawRotation, float surfaceY)
		{
			if (_connections.ContainsKey(key))
			{
				return;
			}

			GameObject root = new GameObject("WaterConnection_V");
			root.transform.SetParent(_connectionsRoot, false);
			root.transform.rotation = yawRotation * (alongX ? Quaternion.Euler(0f, 90f, 0f) : Quaternion.identity);

			float curveDepth = Mathf.Clamp(height * 0.45f, ConnectionCascadeDepthMin, ConnectionCascadeDepthMax);
			// Center the mesh on the gap by offsetting the root back half the depth.
			Vector3 depthOffset = root.transform.rotation * (Vector3.forward * (curveDepth * 0.5f));
			root.transform.position = center - depthOffset;
			MeshFilter filter = root.AddComponent<MeshFilter>();
			filter.sharedMesh = CreateCurvedCascadeMesh(width, height, ConnectionCascadeSegments, curveDepth);

			MeshRenderer renderer = root.AddComponent<MeshRenderer>();
			ApplyCascadeMaterial(renderer, _waterVolume);
			renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
			renderer.receiveShadows = false;
			renderer.enabled = true;

			if (DebugCascadeSolid && DebugCascadeMaterial)
			{
				GameObject solid = GameObject.CreatePrimitive(PrimitiveType.Cube);
				solid.name = "WaterConnection_V_DebugSolid";
				solid.transform.SetParent(root.transform, false);
				solid.transform.localPosition = Vector3.zero;
				solid.transform.localRotation = Quaternion.identity;
				solid.transform.localScale = new Vector3(Mathf.Max(width, 0.1f), Mathf.Max(height, 0.1f), Mathf.Max(curveDepth, 0.1f));
				Renderer solidRenderer = solid.GetComponent<Renderer>();
				if (solidRenderer != null)
				{
					solidRenderer.sharedMaterial = GetCascadeDebugMaterial();
					solidRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
					solidRenderer.receiveShadows = false;
				}
				Collider solidCollider = solid.GetComponent<Collider>();
				if (solidCollider != null)
				{
					Destroy(solidCollider);
				}
			}

			BoxCollider collider = root.AddComponent<BoxCollider>();
			collider.isTrigger = true;
			if (alongX)
			{
				collider.size = new Vector3(ConnectionWallThickness, Mathf.Max(height, 0.1f), Mathf.Max(width, 0.1f));
			}
			else
			{
				collider.size = new Vector3(Mathf.Max(width, 0.1f), Mathf.Max(height, 0.1f), ConnectionWallThickness);
			}
			collider.center = Vector3.zero;

			RegisterConnectionInfo(collider, surfaceY);
			if (DebugCascadeMaterial)
			{
				int defaultLayer = LayerMask.NameToLayer("Default");
				if (defaultLayer >= 0)
				{
					ApplyLayerRecursive(root.transform, defaultLayer);
				}
				root.tag = "Untagged";
			}
			else
			{
				ApplyWaterLayerAndTag(root.transform, _waterVolume);
			}

			_connections[key] = root;

			if (BuildWaterPlugin.Log != null)
			{
				BuildWaterPlugin.Log.LogInfo(
					$"BuildWater: cascade created key={key} center={center} height={height:0.00} width={width:0.00} depth={curveDepth:0.00} alongX={alongX} rootPos={root.transform.position}");
			}
		}

		private void RegisterConnectionInfo(BoxCollider collider, float surfaceY)
		{
			if (collider == null)
			{
				return;
			}
			ConnectionInfo info = new ConnectionInfo
			{
				Collider = collider,
				SurfaceY = surfaceY
			};
			_connectionInfos.Add(info);
		}

		private bool TryGetConnectionSurface(Vector3 playerPos, out float surfaceY)
		{
			surfaceY = float.MinValue;
			bool found = false;
			for (int i = 0; i < _connectionInfos.Count; i++)
			{
				ConnectionInfo info = _connectionInfos[i];
				if (info.Collider == null)
				{
					continue;
				}
				if (info.Collider.bounds.Contains(playerPos))
				{
					surfaceY = Mathf.Max(surfaceY, info.SurfaceY);
					found = true;
				}
			}
			return found;
		}

		private bool AreSurfacesAligned(BuildWaterPieceBehaviour other)
		{
			if (other == null)
			{
				return false;
			}
			// If both surfaces are roughly square, allow any yaw (rotations don't affect adjacency).
			if (IsSurfaceSquare(GetSurfaceBounds()) && IsSurfaceSquare(other.GetSurfaceBounds()))
			{
				return true;
			}
			float yaw = GetYaw(transform.rotation);
			float otherYaw = GetYaw(other.transform.rotation);
			float delta = Mathf.Abs(Mathf.DeltaAngle(yaw, otherYaw));
			return delta <= ConnectionAngleTolerance;
		}

		private static bool IsSurfaceSquare(Bounds bounds)
		{
			Vector3 size = NormalizeBoundsStatic(bounds).size;
			float max = Mathf.Max(size.x, size.z);
			if (max <= 0.0001f)
			{
				return false;
			}
			return Mathf.Abs(size.x - size.z) <= max * ConnectionSquareTolerance;
		}

		private static float GetYaw(Quaternion rotation)
		{
			return rotation.eulerAngles.y;
		}

		private struct ConnectionInfo
		{
			public BoxCollider Collider;
			public float SurfaceY;
		}

		private static void GetOverlap(float aMin, float aMax, float bMin, float bMax, out float overlapMin, out float overlapMax, out float overlapLength)
		{
			overlapMin = Mathf.Max(aMin, bMin);
			overlapMax = Mathf.Min(aMax, bMax);
			overlapLength = Mathf.Max(0f, overlapMax - overlapMin);
		}

		private static bool TryGetFaceGap(float leftMax, float rightMin, out float gapCenter, out float gap)
		{
			gap = rightMin - leftMax;
			gapCenter = (leftMax + rightMin) * 0.5f;
			return gap >= 0f;
		}

		private void RegisterInGrid()
		{
			if (_registered)
			{
				return;
			}

			_gridCell = GetCellForPosition(transform.position);
			AddToGrid(_gridCell, this);
			_registered = true;
		}

		private void UpdateGridCell()
		{
			Vector3Int newCell = GetCellForPosition(transform.position);
			if (!_registered)
			{
				_gridCell = newCell;
				AddToGrid(_gridCell, this);
				_registered = true;
				return;
			}
			if (newCell == _gridCell)
			{
				return;
			}

			RemoveFromGrid(_gridCell, this);
			_gridCell = newCell;
			AddToGrid(_gridCell, this);
		}

		private static Vector3Int GetCellForPosition(Vector3 position)
		{
			return new Vector3Int(
				Mathf.FloorToInt(position.x / GridCellSize),
				Mathf.FloorToInt(position.y / GridCellSize),
				Mathf.FloorToInt(position.z / GridCellSize));
		}

		private static void AddToGrid(Vector3Int cell, BuildWaterPieceBehaviour instance)
		{
			if (!_grid.TryGetValue(cell, out var bucket))
			{
				bucket = new System.Collections.Generic.HashSet<BuildWaterPieceBehaviour>();
				_grid[cell] = bucket;
			}
			bucket.Add(instance);
		}

		private static void RemoveFromGrid(Vector3Int cell, BuildWaterPieceBehaviour instance)
		{
			if (_grid.TryGetValue(cell, out var bucket))
			{
				bucket.Remove(instance);
				if (bucket.Count == 0)
				{
					_grid.Remove(cell);
				}
			}
		}

		private static System.Collections.Generic.HashSet<BuildWaterPieceBehaviour> QueryNeighbors(Bounds bounds)
		{
			System.Collections.Generic.HashSet<BuildWaterPieceBehaviour> result = new System.Collections.Generic.HashSet<BuildWaterPieceBehaviour>();
			Vector3 expanded = new Vector3(ConnectionSearchRadius, ConnectionVerticalMax + 0.5f, ConnectionSearchRadius);
			Bounds expandedBounds = bounds;
			expandedBounds.Expand(expanded * 2f);

			Vector3Int minCell = GetCellForPosition(expandedBounds.min);
			Vector3Int maxCell = GetCellForPosition(expandedBounds.max);

			for (int x = minCell.x; x <= maxCell.x; x++)
			{
				for (int y = minCell.y; y <= maxCell.y; y++)
				{
					for (int z = minCell.z; z <= maxCell.z; z++)
					{
						Vector3Int cell = new Vector3Int(x, y, z);
						if (_grid.TryGetValue(cell, out var bucket))
						{
							foreach (BuildWaterPieceBehaviour instance in bucket)
							{
								if (instance != null)
								{
									result.Add(instance);
								}
							}
						}
					}
				}
			}

			return result;
		}

		private static void NotifyNeighbors(Bounds bounds)
		{
			foreach (BuildWaterPieceBehaviour neighbor in QueryNeighbors(bounds))
			{
				if (neighbor != null)
				{
					neighbor.MarkConnectionsDirty();
				}
			}
		}

		private void OnDestroy()
		{
			DestroyGeneratedMeshes(transform);
			_clusterMesh = null;

			if (_registered)
			{
				RemoveFromGrid(_gridCell, this);
				_registered = false;
				NotifyNeighbors(_worldBounds);
			}
			NotifyGrassChanged();
		}

		private void LogPlayerState(string label, Vector3 playerPos, float waterSurface)
		{
			if (BuildWaterPlugin.Log == null)
			{
				return;
			}
			Player player = Player.m_localPlayer;
			if (player == null)
			{
				return;
			}
			float depth = waterSurface - playerPos.y;
			float swimDepth = player.m_swimDepth;
			BuildWaterPlugin.Log.LogInfo($"BuildWater: {label} pos={playerPos} waterSurface={waterSurface:0.00} depth={depth:0.00} swimDepth={swimDepth:0.00} inWater={player.InWater()}");
		}

		private float GetSurfaceHeight(Vector3 playerPos)
		{
			if (_surfaceWorldY != 0f && IsFinite(_surfaceWorldY))
			{
				return _surfaceWorldY;
			}
			if (_surfaceRenderer != null)
			{
				return _surfaceRenderer.transform.position.y;
			}
			if (_volumeCollider != null)
			{
				return _volumeCollider.bounds.max.y;
			}
			return _waterVolume != null ? _waterVolume.GetWaterSurface(playerPos, 1f) : playerPos.y;
		}

		private float GetSurfaceWorldY()
		{
			if (_surfaceRenderer != null)
			{
				return _surfaceRenderer.transform.position.y;
			}
			if (_surfaceWorldY != 0f && IsFinite(_surfaceWorldY))
			{
				return _surfaceWorldY;
			}
			if (_volumeCollider != null)
			{
				return _volumeCollider.bounds.max.y;
			}
			return transform.position.y;
		}

		private float GetVolumeBottomWorldY()
		{
			if (_volumeCollider != null)
			{
				return _volumeCollider.bounds.min.y;
			}
			float depth = Mathf.Max(0.1f, _manualDown > 0f ? _manualDown : _volumeDepth);
			return GetSurfaceWorldY() - depth;
		}

		private Bounds GetSurfaceBounds()
		{
			if (UseClusterSurface)
			{
				if (_volumeCollider != null)
				{
					return _volumeCollider.bounds;
				}
				if (_worldBounds.size.sqrMagnitude > 0.0001f)
				{
					return _worldBounds;
				}
			}
			if (_surfaceRenderer != null)
			{
				return _surfaceRenderer.bounds;
			}
			if (_volumeCollider != null)
			{
				return _volumeCollider.bounds;
			}
			return _worldBounds;
		}

		private bool IsPointInsideWater(Vector3 point)
		{
			Bounds referenceBounds = _volumeCollider != null ? _volumeCollider.bounds : _worldBounds;
			referenceBounds = NormalizeBounds(referenceBounds);
			if (referenceBounds.size.sqrMagnitude <= 0.0001f && _surfaceRenderer != null)
			{
				referenceBounds = _surfaceRenderer.bounds;
			}
			if (referenceBounds.size.sqrMagnitude <= 0.0001f)
			{
				return false;
			}

			bool withinXZ = point.x >= referenceBounds.min.x && point.x <= referenceBounds.max.x &&
				point.z >= referenceBounds.min.z && point.z <= referenceBounds.max.z;
			float surfaceY = _surfaceWorldY != 0f ? _surfaceWorldY : (_surfaceRenderer != null ? _surfaceRenderer.transform.position.y : referenceBounds.max.y);
			float depth = Mathf.Max(0.1f, _manualDown > 0f ? _manualDown : _volumeDepth);
			float up = _manualUp > 0f ? _manualUp : 0.5f;
			bool withinY = point.y <= surfaceY + up && point.y >= surfaceY - depth;
			return withinXZ && withinY;
		}

		private Bounds GetTerrainInfluenceBounds()
		{
			Bounds baseBounds;
			if (TryGetBounds(out Bounds bounds))
			{
				baseBounds = NormalizeBounds(bounds);
			}
			else
			{
				baseBounds = _worldBounds;
				if (baseBounds.size.sqrMagnitude <= 0.0001f && _volumeCollider != null)
				{
					baseBounds = _volumeCollider.bounds;
				}
				baseBounds = NormalizeBounds(baseBounds);
			}

			return baseBounds;
		}

		private bool IsPointInsideWaterForTerrain(Vector3 point)
		{
			Bounds referenceBounds = GetTerrainInfluenceBounds();
			if (referenceBounds.size.sqrMagnitude <= 0.0001f)
			{
				return false;
			}

			float radius = GetTerrainInfluenceRadius();
			float radiusSqr = radius * radius;
			float dx = 0f;
			if (point.x < referenceBounds.min.x)
			{
				dx = referenceBounds.min.x - point.x;
			}
			else if (point.x > referenceBounds.max.x)
			{
				dx = point.x - referenceBounds.max.x;
			}

			float dz = 0f;
			if (point.z < referenceBounds.min.z)
			{
				dz = referenceBounds.min.z - point.z;
			}
			else if (point.z > referenceBounds.max.z)
			{
				dz = point.z - referenceBounds.max.z;
			}

			if (dx * dx + dz * dz > radiusSqr)
			{
				return false;
			}

			float surfaceY = _surfaceWorldY != 0f ? _surfaceWorldY : (_surfaceRenderer != null ? _surfaceRenderer.transform.position.y : referenceBounds.max.y);
			float depth = GetTerrainInfluenceMaxDepth();
			float up = _manualUp > 0f ? _manualUp : 0.5f;
			bool withinY = point.y <= surfaceY + up && point.y >= surfaceY - depth;
			return withinY;
		}

		private bool IsPlayerInsideManual(Vector3 playerPos)
		{
			return IsPointInsideWater(playerPos);
		}

		private void RefreshBounds()
		{
			if (!TryGetBounds(out Bounds bounds))
			{
				return;
			}

			bounds = NormalizeBounds(bounds);
			Bounds waterBounds = ExpandBoundsXZ(bounds, GetSurfacePadding());
			_worldBounds = waterBounds;
			_surfaceWorldY = ComputeSurfaceWorldY(bounds, _surfaceOffset);
			_manualUp = GetPlayerCheckAboveSurface();

			if (_waterRoot == null)
			{
				return;
			}

			Vector3 centerXZ = ResolveCenterXZ(waterBounds);
			Vector3 surfaceWorldPos = new Vector3(centerXZ.x, _surfaceWorldY, centerXZ.z);
			Vector3 localCenter = _waterRoot.InverseTransformPoint(centerXZ);
			Vector3 localSurface = _waterRoot.InverseTransformPoint(surfaceWorldPos);

			if (_volumeCollider is BoxCollider box)
			{
				float volumeHeight = _manualDown + _manualUp;
				box.size = new Vector3(waterBounds.size.x, volumeHeight, waterBounds.size.z);
				box.center = new Vector3(localCenter.x, localSurface.y + (_manualUp - _manualDown) * 0.5f, localCenter.z);
			}

			if (_surfaceRenderer != null)
			{
				_surfaceRenderer.transform.localPosition = new Vector3(localCenter.x, localSurface.y, localCenter.z);
			}

			if (_waterVolume != null)
			{
				_waterVolume.m_surfaceOffset = localSurface.y;
				_waterVolume.m_forceDepth = Mathf.Clamp01(_volumeDepth / 10f);
			}

			EnsureSurfaceSizingFromBounds();
		}

		private void EnsureSurfaceSizingFromBounds()
		{
			if (_surfaceRenderer == null)
			{
				return;
			}
			if (UseClusterSurface)
			{
				return;
			}

			MeshFilter filter = _surfaceRenderer.GetComponent<MeshFilter>();
			if (filter == null)
			{
				filter = _surfaceRenderer.gameObject.AddComponent<MeshFilter>();
			}

			Bounds bounds = NormalizeBounds(_worldBounds);
			if (!IsFinite(bounds.center) || !IsFinite(bounds.size))
			{
				return;
			}
			float sizeX = Mathf.Clamp(Mathf.Max(0.1f, bounds.size.x), 0.1f, 100f);
			float sizeZ = Mathf.Clamp(Mathf.Max(0.1f, bounds.size.z), 0.1f, 100f);

			ComputeEdgePadding(bounds, out float padLeft, out float padRight, out float padBack, out float padForward);
			float halfX = sizeX * 0.5f;
			float halfZ = sizeZ * 0.5f;
			float extentLeft = halfX + padLeft;
			float extentRight = halfX + padRight;
			float extentBack = halfZ + padBack;
			float extentForward = halfZ + padForward;
			float paddedSizeX = extentLeft + extentRight;
			float paddedSizeZ = extentBack + extentForward;
			Vector3 paddedCenter = new Vector3((extentRight - extentLeft) * 0.5f, 0f, (extentForward - extentBack) * 0.5f);

			bool needsMesh = filter.sharedMesh == null;
			if (!needsMesh)
			{
				Vector3 meshSize = filter.sharedMesh.bounds.size;
				Vector3 meshCenter = filter.sharedMesh.bounds.center;
				needsMesh = Mathf.Abs(meshSize.x - paddedSizeX) > 0.02f ||
					Mathf.Abs(meshSize.z - paddedSizeZ) > 0.02f ||
					Vector3.Distance(meshCenter, paddedCenter) > 0.02f;
			}

			if (needsMesh)
			{
				DestroyGeneratedMesh(filter.sharedMesh);
				filter.sharedMesh = CreateDoubleSidedQuadMesh(extentLeft, extentRight, extentBack, extentForward);
			}

			BoxCollider surfaceTrigger = _surfaceRenderer.GetComponent<BoxCollider>();
			if (surfaceTrigger != null)
			{
				surfaceTrigger.size = new Vector3(paddedSizeX, surfaceTrigger.size.y, paddedSizeZ);
				surfaceTrigger.center = paddedCenter;
			}
		}

		private void ComputeEdgePadding(Bounds bounds, out float padLeft, out float padRight, out float padBack, out float padForward)
		{
			padLeft = 0f;
			padRight = 0f;
			padBack = 0f;
			padForward = 0f;

			if (_waterVolume == null)
			{
				return;
			}

			Quaternion yawRotation = Quaternion.Euler(0f, GetYaw(transform.rotation), 0f);
			Quaternion invRotation = Quaternion.Inverse(yawRotation);
			Bounds a = TransformBoundsByRotation(NormalizeBounds(bounds), invRotation);
			float aSurface = GetSurfaceWorldY();

			foreach (BuildWaterPieceBehaviour neighbor in QueryNeighbors(bounds))
			{
				if (neighbor == null || neighbor == this)
				{
					continue;
				}
				if (neighbor._waterVolume == null)
				{
					continue;
				}
				if (!AreSurfacesAligned(neighbor))
				{
					continue;
				}

				float bSurface = neighbor.GetSurfaceWorldY();
				float heightDiff = Mathf.Abs(aSurface - bSurface);
				if (heightDiff > ConnectionLevelTolerance)
				{
					continue;
				}

				Bounds b = TransformBoundsByRotation(NormalizeBounds(neighbor.GetSurfaceBounds()), invRotation);

				// +X neighbor
				if (TryGetFaceGap(a.max.x, b.min.x, out _, out float gapPosX) && gapPosX <= ConnectionGapMax)
				{
					GetOverlap(a.min.z, a.max.z, b.min.z, b.max.z, out _, out _, out float overlap);
					if (overlap >= ConnectionMinOverlap)
					{
						padRight = Mathf.Max(padRight, gapPosX + ConnectionEdgeOverlap);
					}
				}
				// -X neighbor
				if (TryGetFaceGap(b.max.x, a.min.x, out _, out float gapNegX) && gapNegX <= ConnectionGapMax)
				{
					GetOverlap(a.min.z, a.max.z, b.min.z, b.max.z, out _, out _, out float overlap);
					if (overlap >= ConnectionMinOverlap)
					{
						padLeft = Mathf.Max(padLeft, gapNegX + ConnectionEdgeOverlap);
					}
				}
				// +Z neighbor
				if (TryGetFaceGap(a.max.z, b.min.z, out _, out float gapPosZ) && gapPosZ <= ConnectionGapMax)
				{
					GetOverlap(a.min.x, a.max.x, b.min.x, b.max.x, out _, out _, out float overlap);
					if (overlap >= ConnectionMinOverlap)
					{
						padForward = Mathf.Max(padForward, gapPosZ + ConnectionEdgeOverlap);
					}
				}
				// -Z neighbor
				if (TryGetFaceGap(b.max.z, a.min.z, out _, out float gapNegZ) && gapNegZ <= ConnectionGapMax)
				{
					GetOverlap(a.min.x, a.max.x, b.min.x, b.max.x, out _, out _, out float overlap);
					if (overlap >= ConnectionMinOverlap)
					{
						padBack = Mathf.Max(padBack, gapNegZ + ConnectionEdgeOverlap);
					}
				}
			}
		}

		private void LogVolumeSetup(Bounds bounds, Vector3 localCenter, float localSurfaceY, Collider volumeCollider, MeshRenderer surfaceRenderer, Transform waterRoot)
		{
			if (BuildWaterPlugin.Log == null || volumeCollider == null || surfaceRenderer == null || waterRoot == null)
			{
				return;
			}
			BuildWaterPlugin.Log.LogInfo($"BuildWater: volume-setup rootPos={waterRoot.position} boundsCenter={bounds.center} boundsSize={bounds.size} localCenter={localCenter} surfaceY={localSurfaceY:0.00} colliderCenter={volumeCollider.bounds.center} colliderSize={volumeCollider.bounds.size} surfacePos={surfaceRenderer.transform.position}");
		}

		private Bounds NormalizeBounds(Bounds bounds)
		{
			if (!IsFinite(bounds.center) || !IsFinite(bounds.size) || bounds.size.sqrMagnitude <= 0.0001f)
			{
				return new Bounds(transform.position, new Vector3(2f, 0.2f, 2f));
			}

			Vector3 size = bounds.size;
			size.x = Mathf.Clamp(size.x, 0.25f, 50f);
			size.z = Mathf.Clamp(size.z, 0.25f, 50f);
			size.y = Mathf.Clamp(size.y, 0.05f, 10f);

			Vector3 center = bounds.center;
			float pivotY = transform.position.y;
			float maxDelta = Mathf.Max(0.5f, size.y);
			if (Mathf.Abs(center.y - pivotY) > maxDelta)
			{
				center.y = pivotY;
			}

			return new Bounds(center, size);
		}

		private static Bounds NormalizeBoundsStatic(Bounds bounds)
		{
			if (!IsFinite(bounds.center) || !IsFinite(bounds.size) || bounds.size.sqrMagnitude <= 0.0001f)
			{
				return new Bounds(Vector3.zero, new Vector3(2f, 0.2f, 2f));
			}

			Vector3 size = bounds.size;
			size.x = Mathf.Clamp(size.x, 0.25f, 50f);
			size.z = Mathf.Clamp(size.z, 0.25f, 50f);
			size.y = Mathf.Clamp(size.y, 0.05f, 10f);

			return new Bounds(bounds.center, size);
		}

		private float GetSurfacePadding()
		{
			if (BuildWaterPlugin.SurfacePadding == null)
			{
				return 0f;
			}
			return BuildWaterPlugin.SurfacePadding.Value;
		}

		private float GetTerrainInfluenceRadius()
		{
			if (BuildWaterPlugin.TerrainInfluenceRadius == null)
			{
				return 0f;
			}
			return Mathf.Max(0f, BuildWaterPlugin.TerrainInfluenceRadius.Value);
		}

		private float GetTerrainInfluenceMaxDepth()
		{
			if (BuildWaterPlugin.TerrainInfluenceMaxDepth == null)
			{
				return Mathf.Max(0.1f, _volumeDepth);
			}
			return Mathf.Max(0.1f, BuildWaterPlugin.TerrainInfluenceMaxDepth.Value);
		}

		private float GetPlayerCheckAboveSurface()
		{
			if (BuildWaterPlugin.PlayerCheckAboveSurface == null)
			{
				return 1f;
			}
			return Mathf.Max(0f, BuildWaterPlugin.PlayerCheckAboveSurface.Value);
		}

		private float GetCurtainMinDepth()
		{
			if (BuildWaterPlugin.CurtainMinDepth == null)
			{
				return ClusterCurtainMinDepth;
			}
			return Mathf.Max(0.1f, BuildWaterPlugin.CurtainMinDepth.Value);
		}

		internal static void NotifyConfigChanged()
		{
			BuildWaterPieceBehaviour[] instances = Resources.FindObjectsOfTypeAll<BuildWaterPieceBehaviour>();
			if (instances == null || instances.Length == 0)
			{
				return;
			}

			for (int i = 0; i < instances.Length; i++)
			{
				BuildWaterPieceBehaviour instance = instances[i];
				if (instance == null)
				{
					continue;
				}
				instance.ApplyConfigChanges();
			}
		}

		internal static void SetTerrainToolMode(bool enabled)
		{
			if (_terrainToolMode == enabled)
			{
				return;
			}
			_terrainToolMode = enabled;

			int targetLayer = enabled ? ResolveWaterLayer() : LayerMask.NameToLayer("piece_nonsolid");
			if (targetLayer < 0)
			{
				return;
			}

			BuildWaterPieceBehaviour[] instances = Resources.FindObjectsOfTypeAll<BuildWaterPieceBehaviour>();
			if (instances == null || instances.Length == 0)
			{
				return;
			}

			for (int i = 0; i < instances.Length; i++)
			{
				BuildWaterPieceBehaviour instance = instances[i];
				if (instance == null)
				{
					continue;
				}
				instance.ApplyNonWaterColliderLayer(targetLayer);
			}
		}

		private void ApplyConfigChanges()
		{
			if (!BuildWaterPlugin.Enabled.Value)
			{
				return;
			}

			_surfaceOffset = BuildWaterPlugin.WaterLevelOffset != null ? BuildWaterPlugin.WaterLevelOffset.Value : _surfaceOffset;
			_volumeDepth = BuildWaterPlugin.WaterDepth != null ? BuildWaterPlugin.WaterDepth.Value : _volumeDepth;
			_manualUp = GetPlayerCheckAboveSurface();

			if (TryGetBounds(out Bounds bounds))
			{
				bounds = NormalizeBounds(bounds);
				_manualDown = Mathf.Max(_volumeDepth, 3f + bounds.size.y);
			}
			else
			{
				_manualDown = Mathf.Max(_volumeDepth, 3f);
			}

			RefreshBounds();
			MarkConnectionsDirty();
			NotifyGrassChanged();
		}

		private void ApplyNonWaterColliderLayer(int layer)
		{
			if (layer < 0)
			{
				return;
			}

			foreach (Collider collider in GetComponentsInChildren<Collider>(true))
			{
				if (collider == null)
				{
					continue;
				}
				if (IsWaterSurface(collider.transform))
				{
					continue;
				}
				collider.gameObject.layer = layer;
			}
		}

		private void NotifyGrassChanged()
		{
			if (ClutterSystem.instance == null)
			{
				return;
			}

			Bounds bounds = _worldBounds;
			if (bounds.size.sqrMagnitude <= 0.0001f && _volumeCollider != null)
			{
				bounds = _volumeCollider.bounds;
			}
			if (bounds.size.sqrMagnitude <= 0.0001f)
			{
				return;
			}

			float radius = Mathf.Max(bounds.extents.x, bounds.extents.z) + 4f;
			ClutterSystem.instance.ResetGrass(bounds.center, radius);
		}

		internal static bool TryGetWaterSurfaceAtPoint(Vector3 worldPos, out float surfaceY)
		{
			surfaceY = float.MinValue;
			if (BuildWaterPlugin.Enabled == null || !BuildWaterPlugin.Enabled.Value)
			{
				return false;
			}
			if (_grid.Count == 0)
			{
				return false;
			}

			float radius = BuildWaterPlugin.TerrainInfluenceRadius != null ? Mathf.Max(0f, BuildWaterPlugin.TerrainInfluenceRadius.Value) : 0f;
			float maxDepth = BuildWaterPlugin.TerrainInfluenceMaxDepth != null ? Mathf.Max(0.1f, BuildWaterPlugin.TerrainInfluenceMaxDepth.Value) : 3f;
			float up = BuildWaterPlugin.PlayerCheckAboveSurface != null ? Mathf.Max(0f, BuildWaterPlugin.PlayerCheckAboveSurface.Value) : 1f;
			float searchXZ = radius + GridCellSize;
			Vector3 min = new Vector3(worldPos.x - searchXZ, worldPos.y - (maxDepth + up), worldPos.z - searchXZ);
			Vector3 max = new Vector3(worldPos.x + searchXZ, worldPos.y + (maxDepth + up), worldPos.z + searchXZ);
			_queryBuffer.Clear();
			CollectInstancesInCells(min, max, _queryBuffer);

			bool found = false;
			foreach (BuildWaterPieceBehaviour instance in _queryBuffer)
			{
				if (instance == null)
				{
					continue;
				}
				if (instance.IsPointInsideWaterForTerrain(worldPos))
				{
					float surface = instance.GetSurfaceHeight(worldPos);
					if (!IsFinite(surface))
					{
						continue;
					}
					surfaceY = Mathf.Max(surfaceY, surface);
					found = true;
					continue;
				}
				if (instance.TryGetConnectionSurface(worldPos, out float connectionSurface))
				{
					if (!IsFinite(connectionSurface))
					{
						continue;
					}
					surfaceY = Mathf.Max(surfaceY, connectionSurface);
					found = true;
				}
			}

			_queryBuffer.Clear();
			return found;
		}

		private static void CollectInstancesInCells(Vector3 min, Vector3 max, System.Collections.Generic.HashSet<BuildWaterPieceBehaviour> result)
		{
			Vector3Int minCell = GetCellForPosition(min);
			Vector3Int maxCell = GetCellForPosition(max);
			for (int x = minCell.x; x <= maxCell.x; x++)
			{
				for (int y = minCell.y; y <= maxCell.y; y++)
				{
					for (int z = minCell.z; z <= maxCell.z; z++)
					{
						Vector3Int cell = new Vector3Int(x, y, z);
						if (_grid.TryGetValue(cell, out var bucket))
						{
							foreach (BuildWaterPieceBehaviour instance in bucket)
							{
								if (instance != null)
								{
									result.Add(instance);
								}
							}
						}
					}
				}
			}
		}

		private static void CollectInstances(Bounds bounds, System.Collections.Generic.HashSet<BuildWaterPieceBehaviour> result)
		{
			Vector3Int minCell = GetCellForPosition(bounds.min);
			Vector3Int maxCell = GetCellForPosition(bounds.max);
			for (int x = minCell.x; x <= maxCell.x; x++)
			{
				for (int y = minCell.y; y <= maxCell.y; y++)
				{
					for (int z = minCell.z; z <= maxCell.z; z++)
					{
						Vector3Int cell = new Vector3Int(x, y, z);
						if (_grid.TryGetValue(cell, out var bucket))
						{
							foreach (BuildWaterPieceBehaviour instance in bucket)
							{
								if (instance != null)
								{
									result.Add(instance);
								}
							}
						}
					}
				}
			}
		}

		private static Bounds ExpandBoundsXZ(Bounds bounds, float padding)
		{
			if (Mathf.Abs(padding) <= 0.0001f)
			{
				return bounds;
			}

			Vector3 size = bounds.size;
			size.x = Mathf.Max(0.05f, size.x + padding * 2f);
			size.z = Mathf.Max(0.05f, size.z + padding * 2f);
			bounds.size = size;
			return bounds;
		}

		private static Bounds ApplyPaddingXZ(Bounds bounds, float padding)
		{
			if (Mathf.Abs(padding) <= 0.0001f)
			{
				return bounds;
			}

			Vector3 size = bounds.size;
			size.x = Mathf.Max(0.05f, size.x + padding * 2f);
			size.z = Mathf.Max(0.05f, size.z + padding * 2f);
			bounds.size = size;
			return bounds;
		}

		private float ComputeSurfaceWorldY(Bounds bounds, float surfaceOffset)
		{
			float pivotY = transform.position.y + surfaceOffset;
			float surfaceY = bounds.max.y + surfaceOffset;
			float maxDelta = Mathf.Max(1f, bounds.extents.y);
			if (!IsFinite(surfaceY) || Mathf.Abs(surfaceY - pivotY) > maxDelta)
			{
				return pivotY;
			}
			return surfaceY;
		}

		private Vector3 ResolveCenterXZ(Bounds bounds)
		{
			Vector3 pivot = transform.position;
			Vector3 center = bounds.center;
			Vector2 delta = new Vector2(center.x - pivot.x, center.z - pivot.z);
			if (delta.sqrMagnitude > 0.5f * 0.5f)
			{
				return new Vector3(pivot.x, pivot.y, pivot.z);
			}
			return new Vector3(center.x, pivot.y, center.z);
		}

		private static bool IsFinite(Vector3 value)
		{
			return IsFinite(value.x) && IsFinite(value.y) && IsFinite(value.z);
		}

		private static bool IsFinite(float value)
		{
			return !float.IsNaN(value) && !float.IsInfinity(value);
		}

		private static void ApplyWaterVolumeSurfaceRefs(WaterVolume waterVolume, MeshRenderer surfaceRenderer, Collider volumeCollider)
		{
			if (waterVolume == null)
			{
				return;
			}

			SetFieldIfExists(waterVolume, "m_collider", volumeCollider);
			SetFieldIfExists(waterVolume, "m_surface", surfaceRenderer);
			SetFieldIfExists(waterVolume, "m_surfaceRenderer", surfaceRenderer);
			SetFieldIfExists(waterVolume, "m_surfaceTransform", surfaceRenderer != null ? surfaceRenderer.transform : null);
			SetFieldIfExists(waterVolume, "m_surfaceGO", surfaceRenderer != null ? surfaceRenderer.gameObject : null);
			SetFieldIfExists(waterVolume, "m_surfaceObject", surfaceRenderer != null ? surfaceRenderer.gameObject : null);
			SetFieldIfExists(waterVolume, "m_waterSurface", surfaceRenderer);
		}

		private MeshRenderer CreateSurfaceFromReference(float sizeX, float sizeZ, Vector3 localCenter, float localSurfaceY, Transform parent, int waterLayer, string tag)
		{
			Material referenceMaterial = GetReferenceWaterMaterial(null);
			GameObject surfaceRoot = new GameObject("WaterSurface");
			surfaceRoot.transform.SetParent(parent, false);
			surfaceRoot.transform.localPosition = new Vector3(localCenter.x, localSurfaceY, localCenter.z);
			surfaceRoot.transform.localRotation = Quaternion.identity;
			surfaceRoot.layer = waterLayer;
			if (!string.IsNullOrEmpty(tag))
			{
				surfaceRoot.tag = tag;
			}

			MeshFilter meshFilter = surfaceRoot.AddComponent<MeshFilter>();
			meshFilter.sharedMesh = CreateDoubleSidedQuadMesh(sizeX, sizeZ);

			MeshRenderer renderer = surfaceRoot.AddComponent<MeshRenderer>();
			if (referenceMaterial != null)
			{
				renderer.sharedMaterial = referenceMaterial;
			}
			renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
			renderer.receiveShadows = false;

			BoxCollider surfaceTrigger = surfaceRoot.AddComponent<BoxCollider>();
			surfaceTrigger.isTrigger = true;
			surfaceTrigger.size = new Vector3(sizeX, 0.5f, sizeZ);
			surfaceTrigger.center = Vector3.zero;
			surfaceTrigger.gameObject.layer = waterLayer;
			return renderer;
		}

		private static Mesh CreateDoubleSidedQuadMesh(float sizeX, float sizeZ)
		{
			float halfX = sizeX * 0.5f;
			float halfZ = sizeZ * 0.5f;
			return CreateDoubleSidedQuadMesh(halfX, halfX, halfZ, halfZ);
		}

		private static Mesh CreateDoubleSidedQuadMesh(float extentLeft, float extentRight, float extentBack, float extentForward)
		{
			Vector3[] vertices =
			{
				new Vector3(-extentLeft, 0f, -extentBack),
				new Vector3(extentRight, 0f, -extentBack),
				new Vector3(extentRight, 0f, extentForward),
				new Vector3(-extentLeft, 0f, extentForward)
			};

			Vector3[] normals =
			{
				Vector3.up, Vector3.up, Vector3.up, Vector3.up,
				Vector3.down, Vector3.down, Vector3.down, Vector3.down
			};

			Vector2[] uv =
			{
				new Vector2(0f, 0f),
				new Vector2(1f, 0f),
				new Vector2(1f, 1f),
				new Vector2(0f, 1f),
				new Vector2(0f, 0f),
				new Vector2(1f, 0f),
				new Vector2(1f, 1f),
				new Vector2(0f, 1f)
			};

			Vector3[] verticesDouble =
			{
				vertices[0], vertices[1], vertices[2], vertices[3],
				vertices[0], vertices[1], vertices[2], vertices[3]
			};

			int[] triangles =
			{
				0, 1, 2, 0, 2, 3,
				7, 6, 5, 7, 5, 4
			};

			Mesh mesh = new Mesh();
			mesh.name = "BuildWater_DoubleSidedQuad";
			mesh.hideFlags = HideFlags.HideAndDontSave;
			mesh.vertices = verticesDouble;
			mesh.normals = normals;
			mesh.uv = uv;
			mesh.triangles = triangles;
			mesh.RecalculateBounds();
			return mesh;
		}

		private static Mesh CreateDoubleSidedQuadMeshXY(float sizeX, float sizeY)
		{
			float halfX = sizeX * 0.5f;
			float halfY = sizeY * 0.5f;

			Vector3[] vertices =
			{
				new Vector3(-halfX, -halfY, 0f),
				new Vector3(halfX, -halfY, 0f),
				new Vector3(halfX, halfY, 0f),
				new Vector3(-halfX, halfY, 0f)
			};

			Vector3[] normals =
			{
				Vector3.forward, Vector3.forward, Vector3.forward, Vector3.forward,
				Vector3.back, Vector3.back, Vector3.back, Vector3.back
			};

			Vector2[] uv =
			{
				new Vector2(0f, 0f),
				new Vector2(1f, 0f),
				new Vector2(1f, 1f),
				new Vector2(0f, 1f),
				new Vector2(0f, 0f),
				new Vector2(1f, 0f),
				new Vector2(1f, 1f),
				new Vector2(0f, 1f)
			};

			Vector3[] verticesDouble =
			{
				vertices[0], vertices[1], vertices[2], vertices[3],
				vertices[0], vertices[1], vertices[2], vertices[3]
			};

			int[] triangles =
			{
				0, 1, 2, 0, 2, 3,
				7, 6, 5, 7, 5, 4
			};

			Mesh mesh = new Mesh();
			mesh.name = "BuildWater_DoubleSidedQuad_XY";
			mesh.hideFlags = HideFlags.HideAndDontSave;
			mesh.vertices = verticesDouble;
			mesh.normals = normals;
			mesh.uv = uv;
			mesh.triangles = triangles;
			mesh.RecalculateBounds();
			return mesh;
		}

		private static Mesh CreateCurvedCascadeMesh(float width, float height, int segments, float depth)
		{
			int segs = Mathf.Clamp(segments, 2, 24);
			float halfWidth = width * 0.5f;
			float halfHeight = height * 0.5f;

			int vertCount = (segs + 1) * 2;
			Vector3[] vertices = new Vector3[vertCount];
			Vector3[] normals = new Vector3[vertCount * 2];
			Vector2[] uv = new Vector2[vertCount * 2];
			Vector3[] verticesDouble = new Vector3[vertCount * 2];

			for (int i = 0; i <= segs; i++)
			{
				float t = i / (float)segs;
				float y = Mathf.Lerp(-halfHeight, halfHeight, t);
				float curve = Mathf.Sin(t * Mathf.PI) * depth;
				float z = t * depth;

				int idx = i * 2;
				vertices[idx] = new Vector3(-halfWidth, y, z + curve);
				vertices[idx + 1] = new Vector3(halfWidth, y, z + curve);
			}

			for (int i = 0; i < vertCount; i++)
			{
				verticesDouble[i] = vertices[i];
				verticesDouble[i + vertCount] = vertices[i];

				normals[i] = Vector3.up;
				normals[i + vertCount] = Vector3.down;

				float u = (i % 2 == 0) ? 0f : 1f;
				float v = i / 2f / segs;
				uv[i] = new Vector2(u, v);
				uv[i + vertCount] = new Vector2(u, v);
			}

			int triCount = segs * 6;
			int[] tris = new int[triCount * 2];
			int tri = 0;
			for (int i = 0; i < segs; i++)
			{
				int baseIdx = i * 2;
				// top surface
				tris[tri++] = baseIdx;
				tris[tri++] = baseIdx + 3;
				tris[tri++] = baseIdx + 1;
				tris[tri++] = baseIdx;
				tris[tri++] = baseIdx + 2;
				tris[tri++] = baseIdx + 3;
			}
			for (int i = 0; i < segs; i++)
			{
				int baseIdx = i * 2 + vertCount;
				// underside
				tris[tri++] = baseIdx;
				tris[tri++] = baseIdx + 1;
				tris[tri++] = baseIdx + 3;
				tris[tri++] = baseIdx;
				tris[tri++] = baseIdx + 3;
				tris[tri++] = baseIdx + 2;
			}

			Mesh mesh = new Mesh();
			mesh.name = "BuildWater_Cascade";
			mesh.hideFlags = HideFlags.HideAndDontSave;
			mesh.vertices = verticesDouble;
			mesh.normals = normals;
			mesh.uv = uv;
			mesh.triangles = tris;
			mesh.RecalculateBounds();
			return mesh;
		}
		private void ApplyWaterMaterial(MeshRenderer renderer, WaterVolume selfVolume = null)
		{
			if (renderer == null)
			{
				return;
			}

			Material referenceMaterial = GetReferenceWaterMaterial(selfVolume);
			if (referenceMaterial != null)
			{
				renderer.sharedMaterial = GetScaledWaterMaterial(referenceMaterial);
				return;
			}
		}

		private static Material GetScaledWaterMaterial(Material referenceMaterial)
		{
			if (referenceMaterial == null)
			{
				return null;
			}

			string name = referenceMaterial.name ?? string.Empty;
			if (name.EndsWith("_BuildWater", StringComparison.OrdinalIgnoreCase))
			{
				return referenceMaterial;
			}

			int key = referenceMaterial.GetInstanceID();
			if (_cachedScaledWaterMaterials.TryGetValue(key, out Material cached) && cached != null)
			{
				return cached;
			}

			Material material = new Material(referenceMaterial);
			material.name = $"{referenceMaterial.name}_BuildWater";
			ApplyWaveMaterialScale(material, BuildWaterPatches.BuildWaterWaveScale);
			_cachedScaledWaterMaterials[key] = material;
			return material;
		}

		private static void ApplyWaveMaterialScale(Material material, float scale)
		{
			if (material == null || material.shader == null)
			{
				return;
			}

			Shader shader = material.shader;
			int propertyCount;
			try
			{
				propertyCount = shader.GetPropertyCount();
			}
			catch
			{
				return;
			}

			System.Collections.Generic.List<string> scaledProps = null;
			for (int i = 0; i < propertyCount; i++)
			{
				string prop = shader.GetPropertyName(i);
				if (!IsWaveShaderProperty(prop))
				{
					continue;
				}

				ShaderPropertyType type = shader.GetPropertyType(i);
				bool scaled = false;
				if (type == ShaderPropertyType.Float || type == ShaderPropertyType.Range)
				{
					float value = material.GetFloat(prop);
					material.SetFloat(prop, value * scale);
					scaled = true;
				}
				else if (type == ShaderPropertyType.Vector)
				{
					Vector4 value = material.GetVector(prop);
					material.SetVector(prop, value * scale);
					scaled = true;
				}

				if (scaled && !_loggedWaterShaderProps)
				{
					if (scaledProps == null)
					{
						scaledProps = new System.Collections.Generic.List<string>();
					}
					scaledProps.Add(prop);
				}
			}

			if (!_loggedWaterShaderProps && scaledProps != null && BuildWaterPlugin.Log != null)
			{
				BuildWaterPlugin.Log.LogInfo(
					$"BuildWater: scaled wave shader props ({scale:0.##}) on '{material.name}': {string.Join(", ", scaledProps)}");
				_loggedWaterShaderProps = true;
			}
		}

		private static bool IsWaveShaderProperty(string propertyName)
		{
			if (string.IsNullOrEmpty(propertyName))
			{
				return false;
			}

			string lower = propertyName.ToLowerInvariant();
			if (lower.Contains("tex") || lower.Contains("map") || lower.Contains("normal"))
			{
				return false;
			}

			bool isWave = lower.Contains("wave") || lower.Contains("ripple") || lower.Contains("swell") || lower.Contains("chop");
			if (!isWave)
			{
				return false;
			}

			if (lower.Contains("speed") || lower.Contains("freq") || lower.Contains("frequency") || lower.Contains("dir") ||
				lower.Contains("direction"))
			{
				return false;
			}

			return true;
		}

		private void ApplyCascadeMaterial(MeshRenderer renderer, WaterVolume selfVolume = null)
		{
			if (renderer == null)
			{
				return;
			}

			if (DebugCascadeMaterial)
			{
				Material debugMat = GetCascadeDebugMaterial();
				if (debugMat != null)
				{
					renderer.sharedMaterial = debugMat;
					return;
				}
			}

			ApplyWaterMaterial(renderer, selfVolume);
		}

		private static Material GetCascadeDebugMaterial()
		{
			if (_cachedCascadeDebugMaterial != null)
			{
				return _cachedCascadeDebugMaterial;
			}

			Shader shader = Shader.Find("Unlit/Color");
			if (shader == null)
			{
				shader = Shader.Find("Standard");
			}
			if (shader == null)
			{
				return null;
			}

			Material mat = new Material(shader);
			mat.name = "BuildWater_CascadeDebug";
			mat.color = new Color(0.1f, 0.8f, 1f, 0.6f);
			_cachedCascadeDebugMaterial = mat;
			return _cachedCascadeDebugMaterial;
		}

		private static Material GetBridgeDebugMaterial()
		{
			if (_cachedBridgeDebugMaterial != null)
			{
				return _cachedBridgeDebugMaterial;
			}

			Shader shader = Shader.Find("Unlit/Color");
			if (shader == null)
			{
				shader = Shader.Find("Standard");
			}
			if (shader == null)
			{
				return null;
			}

			Material mat = new Material(shader);
			mat.name = "BuildWater_BridgeDebug";
			mat.color = new Color(1f, 0.2f, 0.6f, 0.6f);
			_cachedBridgeDebugMaterial = mat;
			return _cachedBridgeDebugMaterial;
		}

		private static int ResolveWaterLayer()
		{
			int layerFromMask = ResolveWaterLayerFromMask();
			if (layerFromMask >= 0)
			{
				return layerFromMask;
			}

			int layer = LayerMask.NameToLayer("water");
			if (layer >= 0)
			{
				return layer;
			}
			layer = LayerMask.NameToLayer("Water");
			if (layer >= 0)
			{
				return layer;
			}
			layer = LayerMask.NameToLayer("WaterVolume");
			if (layer >= 0)
			{
				return layer;
			}
			return LayerMask.NameToLayer("piece_nonsolid");
		}

		private static int ResolveWaterLayerFromMask()
		{
			Type volumeType = typeof(WaterVolume);
			FieldInfo[] fields = volumeType.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
			foreach (FieldInfo field in fields)
			{
				string name = field.Name.ToLowerInvariant();
				if (!name.Contains("mask"))
				{
					continue;
				}
				object value = field.GetValue(null);
				if (value is LayerMask mask)
				{
					int layer = FirstLayerFromMask(mask.value);
					if (layer >= 0)
					{
						return layer;
					}
				}
				else if (value is int intMask)
				{
					int layer = FirstLayerFromMask(intMask);
					if (layer >= 0)
					{
						return layer;
					}
				}
			}
			return -1;
		}

		private static int FirstLayerFromMask(int mask)
		{
			for (int i = 0; i < 32; i++)
			{
				if ((mask & (1 << i)) != 0)
				{
					return i;
				}
			}
			return -1;
		}

		private static void TryAddWaterTrigger(GameObject waterRoot, WaterVolume waterVolume, Collider volumeCollider, MeshRenderer surfaceRenderer)
		{
			if (waterRoot == null)
			{
				return;
			}
			Type triggerType = Type.GetType("WaterTrigger, assembly_valheim");
			if (triggerType == null)
			{
				if (BuildWaterPlugin.Log != null)
				{
					BuildWaterPlugin.Log.LogWarning("BuildWater: WaterTrigger type not found.");
				}
				return;
			}
			Component trigger = waterRoot.GetComponent(triggerType);
			if (trigger == null)
			{
				trigger = waterRoot.AddComponent(triggerType);
				if (BuildWaterPlugin.Log != null)
				{
					BuildWaterPlugin.Log.LogInfo("BuildWater: WaterTrigger added.");
				}
			}
			if (trigger != null)
			{
				SetFieldIfExists(trigger, "m_collider", volumeCollider);
				SetFieldIfExists(trigger, "m_surface", surfaceRenderer);
				SetFieldIfExists(trigger, "m_surfaceRenderer", surfaceRenderer);
				SetFieldIfExists(trigger, "m_surfaceTransform", surfaceRenderer != null ? surfaceRenderer.transform : null);
				SetFieldIfExists(trigger, "m_surfaceGO", surfaceRenderer != null ? surfaceRenderer.gameObject : null);
				SetFieldIfExists(trigger, "m_surfaceObject", surfaceRenderer != null ? surfaceRenderer.gameObject : null);
				SetFieldIfExists(trigger, "m_waterSurface", surfaceRenderer);
				SetFieldIfExists(trigger, "m_waterVolume", waterVolume);
				SetFieldIfExists(trigger, "m_volume", waterVolume);
			}
		}

		private static void TrySetWaterVolumeCollider(WaterVolume waterVolume, Collider collider)
		{
			if (waterVolume == null || collider == null)
			{
				return;
			}
			try
			{
				FieldInfo field = typeof(WaterVolume).GetField("m_collider", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
				if (field != null)
				{
					field.SetValue(waterVolume, collider);
				}
			}
			catch
			{
				// ignore
			}
		}

		private Material GetReferenceWaterMaterial(WaterVolume selfVolume)
		{
			if (_cachedWaterMaterial != null)
			{
				return _cachedWaterMaterial;
			}

			Material oceanMaterial = FindOceanWaterMaterial();
			if (oceanMaterial != null)
			{
				_cachedWaterMaterial = oceanMaterial;
				_cachedWaterMaterialSource = "ocean";
				LogSelectedWaterMaterial(_cachedWaterMaterial);
				return _cachedWaterMaterial;
			}

			MeshRenderer surface = FindReferenceWaterSurface(selfVolume);
			if (surface != null && surface.sharedMaterial != null)
			{
				_cachedWaterMaterial = surface.sharedMaterial;
				_cachedWaterMaterialSource = "volume";
				LogSelectedWaterMaterial(_cachedWaterMaterial);
				return _cachedWaterMaterial;
			}

			return null;
		}

		private Material FindOceanWaterMaterial()
		{
			foreach (Renderer renderer in Resources.FindObjectsOfTypeAll<Renderer>())
			{
				if (renderer == null)
				{
					continue;
				}

				Material material = renderer.sharedMaterial;
				if (material == null)
				{
					continue;
				}

				Shader shader = material.shader;
				string shaderName = shader != null ? shader.name : string.Empty;
				if (shaderName.IndexOf("water", StringComparison.OrdinalIgnoreCase) < 0)
				{
					continue;
				}
				if (shaderName.IndexOf("volume", StringComparison.OrdinalIgnoreCase) >= 0)
				{
					continue;
				}

				string materialName = material.name ?? string.Empty;
				if (materialName.IndexOf("water", StringComparison.OrdinalIgnoreCase) < 0)
				{
					continue;
				}

				return material;
			}

			return null;
		}

		private MeshRenderer FindReferenceWaterSurface(WaterVolume selfVolume)
		{
			foreach (WaterVolume volume in Resources.FindObjectsOfTypeAll<WaterVolume>())
			{
				if (volume == null || volume == selfVolume)
				{
					continue;
				}
				if (volume.m_waterSurface != null && volume.m_waterSurface.sharedMaterial != null)
				{
					return volume.m_waterSurface;
				}
			}
			return null;
		}

		private static WaterVolume FindReferenceWaterVolume(WaterVolume selfVolume)
		{
			foreach (WaterVolume volume in Resources.FindObjectsOfTypeAll<WaterVolume>())
			{
				if (volume == null || volume == selfVolume)
				{
					continue;
				}
				return volume;
			}
			return null;
		}

		private static void CopyWaterVolumeDefaults(WaterVolume target, WaterVolume reference)
		{
			if (target == null || reference == null)
			{
				return;
			}

			Type type = typeof(WaterVolume);
			FieldInfo[] fields = type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
			foreach (FieldInfo field in fields)
			{
				if (field.IsInitOnly)
				{
					continue;
				}
				string name = field.Name;
				if (name == "m_waterSurface" || name == "m_collider" || name == "m_heightmap" || name == "m_forceDepth" || name == "m_surfaceOffset")
				{
					continue;
				}

				try
				{
					object value = field.GetValue(reference);
					field.SetValue(target, value);
				}
				catch
				{
					// ignore
				}
			}

			if (!_loggedWaterVolumeFields && BuildWaterPlugin.Log != null)
			{
				string fieldNames = string.Join(", ", Array.ConvertAll(fields, f => f.Name));
				BuildWaterPlugin.Log.LogInfo($"BuildWater: WaterVolume fields={fieldNames}");
				_loggedWaterVolumeFields = true;
			}
		}

		private static void ApplyWaveDamping(WaterVolume target)
		{
			if (target == null)
			{
				return;
			}

			Type type = typeof(WaterVolume);
			FieldInfo[] fields = type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
			foreach (FieldInfo field in fields)
			{
				if (field.IsInitOnly)
				{
					continue;
				}
				string lower = field.Name.ToLowerInvariant();
				if (!IsWaveField(lower))
				{
					continue;
				}

				if (field.FieldType == typeof(float))
				{
					float scale = GetWaveScaleForField(lower);
					if (Mathf.Abs(scale - 1f) < 0.001f)
					{
						continue;
					}
					try
					{
						float value = (float)field.GetValue(target);
						field.SetValue(target, value * scale);
					}
					catch
					{
						// ignore
					}
				}
				else if (field.FieldType == typeof(Vector2))
				{
					if (!IsWaveAmplitudeField(lower))
					{
						continue;
					}
					try
					{
						Vector2 value = (Vector2)field.GetValue(target);
						field.SetValue(target, value * WaterWaveAmplitudeScale);
					}
					catch
					{
						// ignore
					}
				}
				else if (field.FieldType == typeof(Vector3))
				{
					if (!IsWaveAmplitudeField(lower))
					{
						continue;
					}
					try
					{
						Vector3 value = (Vector3)field.GetValue(target);
						field.SetValue(target, value * WaterWaveAmplitudeScale);
					}
					catch
					{
						// ignore
					}
				}
			}
		}

		private static bool IsWaveField(string nameLower)
		{
			return nameLower.Contains("wave") || nameLower.Contains("ripple");
		}

		private static bool IsWaveSpeedField(string nameLower)
		{
			return nameLower.Contains("speed") || nameLower.Contains("freq") || nameLower.Contains("frequency");
		}

		private static bool IsWaveAmplitudeField(string nameLower)
		{
			return nameLower.Contains("amp") || nameLower.Contains("height") || nameLower.Contains("scale") ||
				nameLower.Contains("strength") || nameLower.Contains("intensity") || nameLower.Contains("size") ||
				nameLower.Contains("chop");
		}

		private static float GetWaveScaleForField(string nameLower)
		{
			if (IsWaveSpeedField(nameLower))
			{
				return WaterWaveSpeedScale;
			}
			if (IsWaveAmplitudeField(nameLower))
			{
				return WaterWaveAmplitudeScale;
			}
			return 1f;
		}

		private void LogSelectedWaterMaterial(Material material)
		{
			if (material == null || BuildWaterPlugin.Log == null)
			{
				return;
			}

			string shaderName = material.shader != null ? material.shader.name : "null";
			BuildWaterPlugin.Log.LogInfo($"BuildWater: using {_cachedWaterMaterialSource} water material '{material.name}' shader '{shaderName}'.");
		}

		private static void TryAddLiquidComponents(GameObject waterRoot, MeshRenderer surfaceRenderer, Collider volumeCollider, WaterVolume waterVolume)
		{
			if (waterRoot == null)
			{
				return;
			}

			Type liquidVolumeType = Type.GetType("LiquidVolume, assembly_valheim");
			if (liquidVolumeType != null)
			{
				Component liquidVolume = waterRoot.GetComponent(liquidVolumeType);
				if (liquidVolume == null)
				{
					liquidVolume = waterRoot.AddComponent(liquidVolumeType);
					if (BuildWaterPlugin.Log != null)
					{
						BuildWaterPlugin.Log.LogInfo("BuildWater: LiquidVolume added.");
					}
				}

				if (liquidVolume != null)
				{
					SetFieldIfExists(liquidVolume, "m_collider", volumeCollider);
					SetFieldIfExists(liquidVolume, "m_surface", surfaceRenderer);
					SetFieldIfExists(liquidVolume, "m_surfaceRenderer", surfaceRenderer);
					SetFieldIfExists(liquidVolume, "m_surfaceTransform", surfaceRenderer != null ? surfaceRenderer.transform : null);
					SetFieldIfExists(liquidVolume, "m_surfaceGO", surfaceRenderer != null ? surfaceRenderer.gameObject : null);
					SetFieldIfExists(liquidVolume, "m_surfaceObject", surfaceRenderer != null ? surfaceRenderer.gameObject : null);
					SetFieldIfExists(liquidVolume, "m_waterVolume", waterVolume);
					SetFieldIfExists(liquidVolume, "m_volume", waterVolume);
					SetFieldIfExists(liquidVolume, "m_waterSurface", surfaceRenderer);
					SetBoolFieldIfExists(liquidVolume, "m_isWater", true);
					SetBoolFieldIfExists(liquidVolume, "m_isLiquid", true);
					SetBoolFieldIfExists(liquidVolume, "m_isSwimArea", true);
					SetEnumFieldIfExists(liquidVolume, "m_liquidType", "Water");
					SetEnumFieldIfExists(liquidVolume, "m_type", "Water");

					if (!_loggedLiquidVolumeFields && BuildWaterPlugin.Log != null)
					{
						FieldInfo[] fields = liquidVolumeType.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
						string fieldNames = string.Join(", ", Array.ConvertAll(fields, f => f.Name));
						BuildWaterPlugin.Log.LogInfo($"BuildWater: LiquidVolume fields={fieldNames}");
						_loggedLiquidVolumeFields = true;
					}
				}
			}

			Type liquidSurfaceType = Type.GetType("LiquidSurface, assembly_valheim");
			if (liquidSurfaceType != null && surfaceRenderer != null)
			{
				GameObject surfaceObject = surfaceRenderer.gameObject;
				if (surfaceObject.GetComponent(liquidSurfaceType) == null)
				{
					surfaceObject.AddComponent(liquidSurfaceType);
					if (BuildWaterPlugin.Log != null)
					{
						BuildWaterPlugin.Log.LogInfo("BuildWater: LiquidSurface added.");
					}
				}
			}
		}

		private static void SetFieldIfExists(object target, string fieldName, object value)
		{
			if (target == null)
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

		private static void SetBoolFieldIfExists(object target, string fieldName, bool value)
		{
			if (target == null)
			{
				return;
			}
			FieldInfo field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
			if (field == null || field.FieldType != typeof(bool))
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

		private static void SetEnumFieldIfExists(object target, string fieldName, string enumName)
		{
			if (target == null || string.IsNullOrEmpty(enumName))
			{
				return;
			}
			FieldInfo field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
			if (field == null || !field.FieldType.IsEnum)
			{
				return;
			}
			try
			{
				object value = Enum.Parse(field.FieldType, enumName, true);
				field.SetValue(target, value);
			}
			catch
			{
				// ignore
			}
		}

		private bool TryGetBounds(out Bounds bounds)
		{
			bool haveBounds = false;
			bounds = new Bounds();

			if (TryGetMeshBounds(out Bounds meshBounds))
			{
				bounds = meshBounds;
				haveBounds = true;
			}

			Collider[] colliders = GetComponentsInChildren<Collider>(true);
			foreach (Collider collider in colliders)
			{
				if (collider == null || collider.isTrigger)
				{
					continue;
				}
				if (!haveBounds)
				{
					bounds = collider.bounds;
					haveBounds = true;
				}
				else
				{
					bounds.Encapsulate(collider.bounds);
				}
			}

			Renderer[] renderers = GetComponentsInChildren<Renderer>(true);
			foreach (Renderer renderer in renderers)
			{
				if (renderer == null || IsGhostOnly(renderer.transform) || IsWaterSurface(renderer.transform) ||
					IsConnectionsElement(renderer.transform) || IsHighlightElement(renderer.transform))
				{
					continue;
				}
				if (!haveBounds)
				{
					bounds = renderer.bounds;
					haveBounds = true;
				}
				else
				{
					bounds.Encapsulate(renderer.bounds);
				}
			}

			return haveBounds;
		}

		private bool TryGetMeshBounds(out Bounds bounds)
		{
			MeshFilter[] filters = GetComponentsInChildren<MeshFilter>(true);
			bool haveBounds = false;
			bounds = new Bounds();

			foreach (MeshFilter filter in filters)
			{
				if (filter == null || filter.sharedMesh == null)
				{
					continue;
				}
				Transform t = filter.transform;
				if (IsWaterSurface(t) || IsGhostOnly(t) || IsConnectionsElement(t) || IsHighlightElement(t))
				{
					continue;
				}
				Bounds worldBounds = TransformBounds(filter.sharedMesh.bounds, t.localToWorldMatrix);
				if (!haveBounds)
				{
					bounds = worldBounds;
					haveBounds = true;
				}
				else
				{
					bounds.Encapsulate(worldBounds);
				}
			}

			return haveBounds;
		}

		private static Bounds TransformBounds(Bounds localBounds, Matrix4x4 matrix)
		{
			Vector3 center = matrix.MultiplyPoint3x4(localBounds.center);
			Vector3 extents = localBounds.extents;
			Vector3 axisX = matrix.MultiplyVector(new Vector3(extents.x, 0f, 0f));
			Vector3 axisY = matrix.MultiplyVector(new Vector3(0f, extents.y, 0f));
			Vector3 axisZ = matrix.MultiplyVector(new Vector3(0f, 0f, extents.z));

			Vector3 worldExtents = new Vector3(
				Mathf.Abs(axisX.x) + Mathf.Abs(axisY.x) + Mathf.Abs(axisZ.x),
				Mathf.Abs(axisX.y) + Mathf.Abs(axisY.y) + Mathf.Abs(axisZ.y),
				Mathf.Abs(axisX.z) + Mathf.Abs(axisY.z) + Mathf.Abs(axisZ.z));

			return new Bounds(center, worldExtents * 2f);
		}

		private static Bounds TransformBoundsByRotation(Bounds worldBounds, Quaternion rotation)
		{
			Vector3 center = worldBounds.center;
			Vector3 extents = worldBounds.extents;

			Vector3[] corners =
			{
				new Vector3(-extents.x, -extents.y, -extents.z),
				new Vector3(extents.x, -extents.y, -extents.z),
				new Vector3(extents.x, -extents.y, extents.z),
				new Vector3(-extents.x, -extents.y, extents.z),
				new Vector3(-extents.x, extents.y, -extents.z),
				new Vector3(extents.x, extents.y, -extents.z),
				new Vector3(extents.x, extents.y, extents.z),
				new Vector3(-extents.x, extents.y, extents.z)
			};

			Vector3 min = new Vector3(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity);
			Vector3 max = new Vector3(float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity);

			for (int i = 0; i < corners.Length; i++)
			{
				Vector3 world = center + corners[i];
				Vector3 local = rotation * world;
				min = Vector3.Min(min, local);
				max = Vector3.Max(max, local);
			}

			Bounds result = new Bounds();
			result.SetMinMax(min, max);
			return result;
		}

		private void ApplyWaterLayerAndTag(Transform waterRoot, WaterVolume referenceVolume)
		{
			if (waterRoot == null)
			{
				return;
			}

			int waterLayer = ResolveWaterLayer();
			string tag = waterRoot.tag;
			if (referenceVolume != null)
			{
				waterLayer = referenceVolume.gameObject.layer;
				if (!string.IsNullOrEmpty(referenceVolume.gameObject.tag))
				{
					tag = referenceVolume.gameObject.tag;
				}
			}

			ApplyLayerRecursive(waterRoot, waterLayer);
			if (!string.IsNullOrEmpty(tag))
			{
				waterRoot.tag = tag;
			}
		}

		private static void ApplyLayerRecursive(Transform root, int layer)
		{
			if (root == null || layer < 0)
			{
				return;
			}

			root.gameObject.layer = layer;
			for (int i = 0; i < root.childCount; i++)
			{
				Transform child = root.GetChild(i);
				if (child == null)
				{
					continue;
				}
				ApplyLayerRecursive(child, layer);
			}
		}
	}

	internal sealed class BuildWaterDebugReporter : MonoBehaviour
	{
		private float _lastStayLogTime;

		public void Initialize()
		{
			_lastStayLogTime = -10f;
		}

		private void OnTriggerEnter(Collider other)
		{
			LogTrigger("enter", other, false);
		}

		private void OnTriggerExit(Collider other)
		{
			LogTrigger("exit", other, false);
		}

		private void OnTriggerStay(Collider other)
		{
			LogTrigger("stay", other, true);
		}

		private void LogTrigger(string label, Collider other, bool rateLimit)
		{
			if (BuildWaterPlugin.Log == null || other == null)
			{
				return;
			}

			if (rateLimit)
			{
				if (Time.time - _lastStayLogTime < 2f)
				{
					return;
				}
				_lastStayLogTime = Time.time;
			}

			Player player = other.GetComponentInParent<Player>();
			Character character = other.GetComponentInParent<Character>();
			string actor = player != null ? $"Player:{player.GetPlayerName()}" : character != null ? $"Character:{character.name}" : "none";
			BuildWaterPlugin.Log.LogInfo($"BuildWater: trigger-{label} other={other.name} layer={other.gameObject.layer} actor={actor}");
		}
	}

	internal sealed class WaterBridgeUpdater : MonoBehaviour
	{
		private WaterVolume _waterVolume;
		private MeshFilter _filter;
		private Mesh _mesh;
		private Vector3[] _baseVertices;
		private Vector3[] _workingVertices;
		private float[] _vertexOffsets;

		public void Initialize(WaterVolume waterVolume, MeshFilter filter)
		{
			_waterVolume = waterVolume;
			_filter = filter;
			if (_filter == null)
			{
				return;
			}
			_mesh = _filter.mesh;
			_baseVertices = _mesh.vertices;
			_workingVertices = new Vector3[_baseVertices.Length];
			_vertexOffsets = new float[_baseVertices.Length];

			float maxLocalY = float.NegativeInfinity;
			for (int i = 0; i < _baseVertices.Length; i++)
			{
				if (_baseVertices[i].y > maxLocalY)
				{
					maxLocalY = _baseVertices[i].y;
				}
			}

			for (int i = 0; i < _baseVertices.Length; i++)
			{
				Vector3 baseLocal = _baseVertices[i];
				Vector3 world = transform.TransformPoint(baseLocal);
				float surfaceY = _waterVolume != null ? _waterVolume.GetWaterSurface(world, 1f) : world.y;
				float delta = world.y - surfaceY;
				if (maxLocalY - baseLocal.y <= 0.02f)
				{
					// Keep the top edge locked to the true water surface.
					_vertexOffsets[i] = 0f;
				}
				else
				{
					_vertexOffsets[i] = Mathf.Abs(delta) <= 0.05f ? 0f : delta;
				}
				_workingVertices[i] = baseLocal;
			}
		}

		private void LateUpdate()
		{
			if (_waterVolume == null || _filter == null || _mesh == null || _baseVertices == null || _vertexOffsets == null)
			{
				return;
			}

			for (int i = 0; i < _baseVertices.Length; i++)
			{
				Vector3 baseLocal = _baseVertices[i];
				Vector3 world = transform.TransformPoint(baseLocal);
				float surfaceY = _waterVolume.GetWaterSurface(world, 1f);
				world.y = surfaceY + _vertexOffsets[i];
				Vector3 local = transform.InverseTransformPoint(world);
				_workingVertices[i] = new Vector3(baseLocal.x, local.y, baseLocal.z);
			}

			_mesh.vertices = _workingVertices;
			_mesh.RecalculateBounds();
		}
	}
}
