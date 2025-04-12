using System;
using System.IO;
using System.Collections.Generic;
using UnityEngine;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using System.Linq;

namespace AcesReTexture
{
    [BepInPlugin("com.ace.retexture", "Ace's Re-Texture", "1.3.1")]
    public class AceReTexturePlugin : BaseUnityPlugin
    {
        private static AceReTexturePlugin Instance;
        private static ManualLogSource Log;
        private bool showGUI = true;
        private Rect windowRect = new Rect(20, 20, 350, 480);
        private string logFilePath;
        private Vector2 textureScrollPosition = Vector2.zero;

        // Target paths for applying textures
        private const string PLAYER_PATH = "Player Objects/Local VRRig/Local Gorilla Player/gorilla_new";
        private const string GROUND_PATH = "Environment Objects/LocalObjects_Prefab/Forest/UnityTempFile-b84f6886612a714438ea771e4e82c31a (combined by EdMeshCombiner)";
        private const string FULL_MAP_PATH = "Environment Objects";

        // Targeting options
        public enum TargetType
        {
            Player,
            Ground,
            FullMap
        }

        // Track active state and selected textures for each target type
        private static TargetType CurrentTarget = TargetType.Player;
        private Dictionary<TargetType, bool> texturesActiveByTarget = new Dictionary<TargetType, bool>();
        private Dictionary<TargetType, CustomTextureInfo> selectedTextureByTarget = new Dictionary<TargetType, CustomTextureInfo>();
        private Dictionary<TargetType, float> textureTilingByTarget = new Dictionary<TargetType, float>();
        private Dictionary<TargetType, int> textureTilingIndexByTarget = new Dictionary<TargetType, int>();

        private GameObject targetObject = null;

        // Timer for player texture refresh
        private float playerTextureRefreshTimer = 0f;
        private const float PLAYER_REFRESH_INTERVAL = 5.0f; // Refresh player texture every 5 seconds

        // Custom textures settings
        private bool CustomTexturesActive
        {
            get { return texturesActiveByTarget[CurrentTarget]; }
            set { texturesActiveByTarget[CurrentTarget] = value; }
        }
        private string TextureFolderName = "CustomTextures";
        private string TextureFolderPath;
        private List<CustomTextureInfo> AvailableTextures = new List<CustomTextureInfo>();

        private CustomTextureInfo SelectedTexture
        {
            get { return selectedTextureByTarget[CurrentTarget]; }
            set { selectedTextureByTarget[CurrentTarget] = value; }
        }

        private float TextureTiling
        {
            get { return textureTilingByTarget[CurrentTarget]; }
            set { textureTilingByTarget[CurrentTarget] = value; }
        }

        private int TextureTilingIndex
        {
            get { return textureTilingIndexByTarget[CurrentTarget]; }
            set { textureTilingIndexByTarget[CurrentTarget] = value; }
        }

        // Material tracking
        private Dictionary<TargetType, Dictionary<Material, TextureInfo>> materialOriginalTexturesByTarget = new Dictionary<TargetType, Dictionary<Material, TextureInfo>>();
        private Dictionary<TargetType, List<Material>> customMaterialsByTarget = new Dictionary<TargetType, List<Material>>();

        // GUI Styles
        private GUIStyle windowStyle;
        private GUIStyle buttonStyle;
        private GUIStyle headerStyle;
        private GUIStyle statusStyle;
        private GUIStyle sliderStyle;
        private GUIStyle sliderLabelStyle;
        private GUIStyle textureButtonStyle;
        private GUIStyle textureSelectedButtonStyle;
        private GUIStyle scrollViewStyle;
        private bool stylesInitialized = false;

        // Texture loading status
        private bool texturesLoaded = false;
        private bool texturesRefreshing = false;

        // Classes for storing information
        public class CustomTextureInfo
        {
            public string Name;
            public string FilePath;
            public Texture2D Texture;

            public CustomTextureInfo(string name, string filePath)
            {
                Name = name;
                FilePath = filePath;
                Texture = null;
            }
        }

        public class TextureInfo
        {
            public Texture Texture;
            public Vector2 Tiling;

            public TextureInfo(Texture texture, Vector2 tiling)
            {
                Texture = texture;
                Tiling = tiling;
            }
        }

        private void Awake()
        {
            Instance = this;
            Log = Logger;

            // Initialize dictionaries
            foreach (TargetType target in Enum.GetValues(typeof(TargetType)))
            {
                texturesActiveByTarget[target] = false;
                selectedTextureByTarget[target] = null;
                textureTilingByTarget[target] = 1.0f;
                textureTilingIndexByTarget[target] = 5;
                materialOriginalTexturesByTarget[target] = new Dictionary<Material, TextureInfo>();
                customMaterialsByTarget[target] = new List<Material>();
            }

            // Set up log file
            string pluginFolder = Path.GetDirectoryName(Info.Location);
            logFilePath = Path.Combine(pluginFolder, "ace_retexture_log.txt");
            TextureFolderPath = Path.Combine(pluginFolder, TextureFolderName);

            WriteToLogFile("Ace's Re-Texture plugin loaded at: " + DateTime.Now.ToString());
            WriteToLogFile("Plugin path: " + Info.Location);
            WriteToLogFile("Texture folder path: " + TextureFolderPath);
            WriteToLogFile("Player path: " + PLAYER_PATH);
            WriteToLogFile("Ground path: " + GROUND_PATH);
            WriteToLogFile("Full map path: " + FULL_MAP_PATH);

            // Create texture folder if it doesn't exist
            if (!Directory.Exists(TextureFolderPath))
            {
                try
                {
                    Directory.CreateDirectory(TextureFolderPath);
                    WriteToLogFile("Created custom textures folder: " + TextureFolderPath);
                }
                catch (Exception ex)
                {
                    WriteToLogFile("Error creating textures folder: " + ex.Message);
                }
            }

            try
            {
                // Apply Harmony patches
                Harmony harmony = new Harmony("com.ace.retexture");
                harmony.PatchAll(typeof(AceReTexturePlugin));
                WriteToLogFile("Harmony patches applied successfully");
            }
            catch (Exception ex)
            {
                WriteToLogFile("Error applying Harmony patches: " + ex.Message);
            }

            WriteToLogFile("Ace's Re-Texture plugin initialized");
            Debug.Log("Ace's Re-Texture plugin initialized");
        }

        private void Start()
        {
            // Load textures in Start to ensure Unity is fully initialized
            RefreshAvailableTextures();

            // Find target object after a short delay to ensure all game objects are loaded
            Invoke("FindTargetObject", 2.0f);

            WriteToLogFile("Ace's Re-Texture plugin started. Target types available: Player, Ground, Full Map");
        }

        private void FindTargetObject()
        {
            try
            {
                string targetPath = GetCurrentTargetPath();

                // For FullMap, we don't need to find a specific object
                if (CurrentTarget == TargetType.FullMap)
                {
                    targetObject = GameObject.Find(targetPath);
                    WriteToLogFile("Using full map as target");
                    return;
                }

                targetObject = GameObject.Find(targetPath);
                if (targetObject != null)
                {
                    WriteToLogFile("Found target object: " + targetPath);
                }
                else
                {
                    WriteToLogFile("WARNING: Target object not found: " + targetPath);
                    // Try to find it periodically in case it loads later
                    Invoke("FindTargetObject", 5.0f);
                }
            }
            catch (Exception ex)
            {
                WriteToLogFile("Error finding target object: " + ex.Message);
            }
        }

        private string GetCurrentTargetPath()
        {
            switch (CurrentTarget)
            {
                case TargetType.Player:
                    return PLAYER_PATH;
                case TargetType.Ground:
                    return GROUND_PATH;
                case TargetType.FullMap:
                    return FULL_MAP_PATH;
                default:
                    return PLAYER_PATH;
            }
        }

        private void OnEnable()
        {
            WriteToLogFile("Ace's Re-Texture plugin enabled");
            Debug.Log("Ace's Re-Texture plugin enabled");
        }

        private void Update()
        {
            // Toggle GUI visibility with hotkey (F1 key is more reliable in VR)
            if (Input.GetKeyDown(KeyCode.F1))
            {
                showGUI = !showGUI;
                WriteToLogFile("GUI visibility toggled: " + showGUI);
            }

            // If target not found yet, try periodically
            if (targetObject == null && Time.frameCount % 300 == 0) // Check every ~5 seconds
            {
                FindTargetObject();
            }

            // Player texture refresh logic
            if (texturesActiveByTarget[TargetType.Player])
            {
                playerTextureRefreshTimer += Time.deltaTime;
                if (playerTextureRefreshTimer >= PLAYER_REFRESH_INTERVAL)
                {
                    playerTextureRefreshTimer = 0f;
                    RefreshPlayerTexture();
                }
            }
        }

        // New method to refresh the player texture
        private void RefreshPlayerTexture()
        {
            // Only refresh if player textures are active
            if (!texturesActiveByTarget[TargetType.Player] || selectedTextureByTarget[TargetType.Player] == null)
                return;

            try
            {
                // Store the current target
                TargetType previousTarget = CurrentTarget;

                // Switch to player target temporarily
                CurrentTarget = TargetType.Player;

                // Find the player object
                targetObject = GameObject.Find(PLAYER_PATH);
                if (targetObject != null)
                {
                    WriteToLogFile("Refreshing player texture");

                    // Disable and re-enable the texture to refresh it
                    DisableCustomTextures();
                    EnableCustomTextures();
                }

                // Restore previous target
                CurrentTarget = previousTarget;

                // If we were on a different target, restore its target object
                if (previousTarget != TargetType.Player)
                {
                    FindTargetObject();
                }
            }
            catch (Exception ex)
            {
                WriteToLogFile("Error refreshing player texture: " + ex.Message);
            }
        }

        private void OnGUI()
        {
            if (!showGUI) return;

            try
            {
                // Initialize styles within OnGUI
                InitializeGUIStyles();

                windowRect = GUILayout.Window(101, windowRect, DoMyWindow, "Ace's Re-Texture", windowStyle);
            }
            catch (Exception ex)
            {
                WriteToLogFile("Error in OnGUI: " + ex.Message);
            }
        }

        private void InitializeGUIStyles()
        {
            // Only initialize once and only within OnGUI
            if (stylesInitialized) return;

            try
            {
                // More colorful theme
                Color accentColor = new Color(0.0f, 0.8f, 1f);
                Color baseColor = new Color(0.05f, 0.05f, 0.05f, 0.98f); // Darker black background
                Color buttonColor = new Color(0.15f, 0.15f, 0.3f, 0.95f); // Darker blue button
                Color selectedColor = new Color(0.1f, 0.8f, 0.2f, 0.95f); // Brighter green for selected
                Color headerColor = new Color(1f, 0.6f, 0.0f); // Orange header text
                Color textColor = new Color(0.9f, 0.9f, 1f); // Slightly blue-tinted white text

                windowStyle = new GUIStyle(GUI.skin.window);
                windowStyle.normal.background = CreateColorTexture(baseColor);
                windowStyle.normal.textColor = headerColor; // Orange title text
                windowStyle.onNormal.background = CreateColorTexture(baseColor);
                windowStyle.onNormal.textColor = headerColor;
                windowStyle.active.background = CreateColorTexture(baseColor);
                windowStyle.active.textColor = headerColor;
                windowStyle.onActive.background = CreateColorTexture(baseColor);
                windowStyle.onActive.textColor = headerColor;
                windowStyle.fontStyle = FontStyle.Bold;
                windowStyle.fontSize = 16; // Larger title text

                buttonStyle = new GUIStyle(GUI.skin.button);
                buttonStyle.normal.background = CreateColorTexture(buttonColor);
                buttonStyle.normal.textColor = textColor;
                buttonStyle.hover.background = CreateColorTexture(new Color(0.25f, 0.25f, 0.4f, 0.95f)); // Lighter when hovered
                buttonStyle.hover.textColor = Color.white;
                buttonStyle.active.background = CreateColorTexture(new Color(0.3f, 0.3f, 0.5f, 0.95f)); // Even lighter when pressed
                buttonStyle.active.textColor = Color.white;
                buttonStyle.fontSize = 14;
                buttonStyle.fontStyle = FontStyle.Bold;
                buttonStyle.padding = new RectOffset(10, 10, 8, 8);

                textureButtonStyle = new GUIStyle(GUI.skin.button);
                textureButtonStyle.normal.background = CreateColorTexture(buttonColor);
                textureButtonStyle.normal.textColor = textColor;
                textureButtonStyle.hover.background = CreateColorTexture(new Color(0.25f, 0.25f, 0.4f, 0.95f));
                textureButtonStyle.hover.textColor = Color.white;
                textureButtonStyle.fontSize = 12;
                textureButtonStyle.alignment = TextAnchor.MiddleLeft;
                textureButtonStyle.padding = new RectOffset(10, 10, 5, 5);

                textureSelectedButtonStyle = new GUIStyle(textureButtonStyle);
                textureSelectedButtonStyle.normal.background = CreateColorTexture(selectedColor);
                textureSelectedButtonStyle.hover.background = CreateColorTexture(new Color(0.15f, 0.9f, 0.3f, 0.95f)); // Brighter when hovered

                headerStyle = new GUIStyle(GUI.skin.label);
                headerStyle.fontSize = 14;
                headerStyle.normal.textColor = headerColor; // Orange header text
                headerStyle.alignment = TextAnchor.MiddleCenter;
                headerStyle.fontStyle = FontStyle.Bold;

                statusStyle = new GUIStyle(GUI.skin.label);
                statusStyle.normal.textColor = textColor;
                statusStyle.fontSize = 12;
                statusStyle.alignment = TextAnchor.MiddleCenter;

                sliderStyle = new GUIStyle(GUI.skin.horizontalSlider);
                sliderStyle.margin = new RectOffset(10, 10, 10, 10);

                sliderLabelStyle = new GUIStyle(GUI.skin.label);
                sliderLabelStyle.normal.textColor = textColor;
                sliderLabelStyle.fontSize = 11;
                sliderLabelStyle.alignment = TextAnchor.MiddleLeft;

                scrollViewStyle = new GUIStyle(GUI.skin.scrollView);
                scrollViewStyle.normal.background = CreateColorTexture(new Color(0.08f, 0.08f, 0.1f, 0.7f)); // Slightly lighter than base

                stylesInitialized = true;
                WriteToLogFile("GUI styles initialized with colorful theme");
            }
            catch (Exception ex)
            {
                WriteToLogFile("Error in InitializeGUIStyles: " + ex.Message);
            }
        }

        private Texture2D CreateColorTexture(Color color)
        {
            Texture2D texture = new Texture2D(1, 1);
            texture.SetPixel(0, 0, color);
            texture.Apply();
            return texture;
        }

        private void DoMyWindow(int windowID)
        {
            try
            {
                GUILayout.Space(10);

                // Target selection
                GUILayout.Label("TARGET SELECTION", headerStyle);
                GUILayout.BeginHorizontal();

                if (GUILayout.Button("PLAYER", CurrentTarget == TargetType.Player ? textureSelectedButtonStyle : textureButtonStyle, GUILayout.Width(100)))
                {
                    if (CurrentTarget != TargetType.Player)
                    {
                        CurrentTarget = TargetType.Player;
                        WriteToLogFile("Target changed to: Player");
                        FindTargetObject();
                    }
                }

                if (GUILayout.Button("GROUND", CurrentTarget == TargetType.Ground ? textureSelectedButtonStyle : textureButtonStyle, GUILayout.Width(100)))
                {
                    if (CurrentTarget != TargetType.Ground)
                    {
                        CurrentTarget = TargetType.Ground;
                        WriteToLogFile("Target changed to: Ground");
                        FindTargetObject();
                    }
                }

                if (GUILayout.Button("FULL MAP", CurrentTarget == TargetType.FullMap ? textureSelectedButtonStyle : textureButtonStyle, GUILayout.Width(100)))
                {
                    if (CurrentTarget != TargetType.FullMap)
                    {
                        CurrentTarget = TargetType.FullMap;
                        WriteToLogFile("Target changed to: Full Map");
                        FindTargetObject();
                    }
                }

                GUILayout.EndHorizontal();

                GUILayout.Space(10);

                // Show target status
                string targetStatus;
                if (CurrentTarget == TargetType.FullMap)
                {
                    targetStatus = "Target: Full Map";
                }
                else
                {
                    targetStatus = targetObject != null ?
                        $"Target: {CurrentTarget} Found ✓" :
                        $"Target: {CurrentTarget} Not Found ✗";
                }
                GUILayout.Label(targetStatus, statusStyle);

                // Custom Textures Toggle
                string customTexturesStatus = CustomTexturesActive ? "✨ TEXTURES: ON ✨" : "TEXTURES: OFF";
                GUILayout.Label(customTexturesStatus, headerStyle);

                GUILayout.Space(5);

                // Add a reset all button
                if (GUILayout.Button("RESET ALL", buttonStyle, GUILayout.Height(30)))
                {
                    ResetAllTextures();
                }

                GUILayout.Space(10);

                // Toggle button
                GUIStyle customButtonStyle = buttonStyle;
                if (CustomTexturesActive)
                {
                    // Use a green highlight for the active button
                    customButtonStyle = new GUIStyle(buttonStyle);
                    customButtonStyle.normal.background = CreateColorTexture(new Color(0.1f, 0.8f, 0.2f, 0.95f));
                }

                if (GUILayout.Button(CustomTexturesActive ? "TURN OFF" : "TURN ON", customButtonStyle, GUILayout.Height(40)))
                {
                    if (!CustomTexturesActive)
                    {
                        if (CurrentTarget != TargetType.FullMap && targetObject == null)
                        {
                            WriteToLogFile("Cannot enable: Target object not found");
                        }
                        else if (SelectedTexture == null)
                        {
                            WriteToLogFile("Cannot enable: No texture selected");
                        }
                        else
                        {
                            EnableCustomTextures();
                        }
                    }
                    else
                    {
                        DisableCustomTextures();
                    }
                }

                GUILayout.Space(15);

                // Texture Tiling slider
                GUILayout.BeginHorizontal();
                GUILayout.Label("Texture Tiling:", sliderLabelStyle, GUILayout.Width(120));
                GUILayout.Label(TextureTiling.ToString("F1"), sliderLabelStyle, GUILayout.Width(30));
                GUILayout.EndHorizontal();

                float newTilingValue = GUILayout.HorizontalSlider(TextureTilingIndex, 1f, 50f);
                if ((int)newTilingValue != TextureTilingIndex)
                {
                    TextureTilingIndex = (int)newTilingValue;
                    TextureTiling = TextureTilingIndex / 10f;
                    WriteToLogFile("Texture Tiling changed: " + TextureTiling);

                    if (CustomTexturesActive && SelectedTexture != null)
                    {
                        UpdateTextureTiling();
                    }
                }

                GUILayout.Space(15);

                // Refresh textures button
                if (GUILayout.Button("↻ REFRESH TEXTURES ↻", buttonStyle))
                {
                    RefreshAvailableTextures();
                }

                GUILayout.Space(5);

                // Available textures
                GUILayout.Label("AVAILABLE TEXTURES", headerStyle);

                if (texturesRefreshing)
                {
                    GUILayout.Label("Loading textures...", statusStyle);
                }
                else if (AvailableTextures.Count == 0)
                {
                    GUILayout.Label("No textures found in " + TextureFolderName + " folder.", statusStyle);
                    GUILayout.Label("Add PNG files to the folder and refresh.", statusStyle);
                }
                else
                {
                    // Scrollable list of textures
                    textureScrollPosition = GUILayout.BeginScrollView(textureScrollPosition, scrollViewStyle, GUILayout.Height(150));

                    foreach (var texture in AvailableTextures)
                    {
                        bool isSelected = SelectedTexture == texture;

                        if (GUILayout.Button(texture.Name, isSelected ? textureSelectedButtonStyle : textureButtonStyle))
                        {
                            SelectedTexture = texture;
                            WriteToLogFile("Selected texture: " + texture.Name);

                            // If textures are already enabled, update with the new texture
                            if (CustomTexturesActive)
                            {
                                DisableCustomTextures();
                                EnableCustomTextures();
                            }
                        }
                    }

                    GUILayout.EndScrollView();
                }

                GUILayout.Space(15);

                // Hide GUI button
                if (GUILayout.Button("HIDE GUI (F1)", buttonStyle))
                {
                    showGUI = false;
                    WriteToLogFile("GUI hidden via button");
                }

                // Make window draggable
                GUI.DragWindow();
            }
            catch (Exception ex)
            {
                WriteToLogFile("Error in DoMyWindow: " + ex.Message);
            }
        }

        // Reset all textures for all targets
        private void ResetAllTextures()
        {
            WriteToLogFile("Resetting all textures for all targets");

            // Save current target
            TargetType originalTarget = CurrentTarget;

            // Disable textures for each target
            foreach (TargetType target in Enum.GetValues(typeof(TargetType)))
            {
                CurrentTarget = target;
                if (CustomTexturesActive)
                {
                    DisableCustomTextures();
                }
            }

            // Clear all texture selections
            foreach (TargetType target in Enum.GetValues(typeof(TargetType)))
            {
                selectedTextureByTarget[target] = null;
                textureTilingByTarget[target] = 1.0f;
                textureTilingIndexByTarget[target] = 5;
            }

            // Restore original target
            CurrentTarget = originalTarget;

            WriteToLogFile("All textures reset");
        }

        // Load and refresh textures
        private void RefreshAvailableTextures()
        {
            try
            {
                texturesRefreshing = true;
                WriteToLogFile("Refreshing available textures...");

                // Clear existing textures
                AvailableTextures.Clear();

                // Get all PNG files from the textures folder
                if (Directory.Exists(TextureFolderPath))
                {
                    string[] pngFiles = Directory.GetFiles(TextureFolderPath, "*.png");

                    foreach (string filePath in pngFiles)
                    {
                        string fileName = Path.GetFileNameWithoutExtension(filePath);
                        var textureInfo = new CustomTextureInfo(fileName, filePath);
                        AvailableTextures.Add(textureInfo);
                    }

                    WriteToLogFile($"Found {AvailableTextures.Count} textures");

                    // Load actual textures
                    StartCoroutine(LoadTexturesAsync());
                }
                else
                {
                    WriteToLogFile("Texture folder not found: " + TextureFolderPath);
                    texturesRefreshing = false;
                }
            }
            catch (Exception ex)
            {
                WriteToLogFile("Error refreshing textures: " + ex.Message);
                texturesRefreshing = false;
            }
        }

        private System.Collections.IEnumerator LoadTexturesAsync()
        {
            foreach (var textureInfo in AvailableTextures)
            {
                try
                {
                    // Load texture from file
                    byte[] fileData = File.ReadAllBytes(textureInfo.FilePath);
                    Texture2D texture = new Texture2D(2, 2);
                    texture.LoadImage(fileData);

                    // Set texture parameters
                    texture.wrapMode = TextureWrapMode.Repeat;
                    texture.filterMode = FilterMode.Bilinear;

                    textureInfo.Texture = texture;
                    WriteToLogFile($"Loaded texture: {textureInfo.Name} ({texture.width}x{texture.height})");
                }
                catch (Exception ex)
                {
                    WriteToLogFile($"Error loading texture {textureInfo.Name}: {ex.Message}");
                }

                // Yield to prevent freezing
                yield return null;
            }

            // If previously selected texture is still in the list, keep it selected
            foreach (TargetType target in Enum.GetValues(typeof(TargetType)))
            {
                if (selectedTextureByTarget[target] != null)
                {
                    var existingTexture = AvailableTextures.FirstOrDefault(t => t.Name == selectedTextureByTarget[target].Name);
                    if (existingTexture != null)
                    {
                        selectedTextureByTarget[target] = existingTexture;
                    }
                    else
                    {
                        selectedTextureByTarget[target] = null;
                    }
                }
            }

            texturesRefreshing = false;
            texturesLoaded = true;
            WriteToLogFile("Finished loading textures");
        }

        // Custom Textures methods
        private void EnableCustomTextures()
        {
            if (CurrentTarget != TargetType.FullMap && targetObject == null)
            {
                WriteToLogFile("Cannot enable custom textures: Target object not found");
                return;
            }

            if (SelectedTexture == null || SelectedTexture.Texture == null)
            {
                WriteToLogFile("Cannot enable custom textures: No texture selected or texture not loaded");
                return;
            }

            // Set current target active
            CustomTexturesActive = true;
            WriteToLogFile($"Custom Textures enabled with: {SelectedTexture.Name} for {CurrentTarget}");

            // Create new material with the custom texture
            Material customMaterial = CreateCustomMaterial();

            // Apply the material to the target object(s)
            ApplyToTargetObject(customMaterial);
        }

        private void DisableCustomTextures()
        {
            // Set current target inactive
            CustomTexturesActive = false;
            WriteToLogFile($"Custom Textures disabled for {CurrentTarget}");

            // Restore original materials for current target
            RestoreOriginalTextures();

            // Clean up created materials for current target
            foreach (Material mat in customMaterialsByTarget[CurrentTarget])
            {
                if (mat != null)
                {
                    Destroy(mat);
                }
            }

            customMaterialsByTarget[CurrentTarget].Clear();
            materialOriginalTexturesByTarget[CurrentTarget].Clear();
        }

        private void UpdateTextureTiling()
        {
            if (SelectedTexture == null || SelectedTexture.Texture == null)
                return;

            // Update tiling on custom materials for current target
            foreach (Material mat in customMaterialsByTarget[CurrentTarget])
            {
                if (mat != null && mat.HasProperty("_MainTex"))
                {
                    mat.mainTextureScale = new Vector2(TextureTiling, TextureTiling);
                }
            }

            WriteToLogFile($"Updated texture tiling to: {TextureTiling} for {CurrentTarget}");
        }

        // Create a custom material with the selected texture
        private Material CreateCustomMaterial()
        {
            try
            {
                // Create a new material with the unlit shader (should work in most VR games)
                Material material = new Material(Shader.Find("Unlit/Texture"));

                // Try Standard shader if Unlit isn't available
                if (material.shader == null || material.shader.name == "Hidden/InternalErrorShader")
                {
                    material = new Material(Shader.Find("Standard"));
                    WriteToLogFile("Using Standard shader instead of Unlit/Texture");
                }

                // Set the texture and tiling
                material.mainTexture = SelectedTexture.Texture;
                material.mainTextureScale = new Vector2(TextureTiling, TextureTiling);

                // Add to our list of created materials for current target
                customMaterialsByTarget[CurrentTarget].Add(material);

                WriteToLogFile($"Created custom material with texture: {SelectedTexture.Name} for {CurrentTarget}");
                return material;
            }
            catch (Exception ex)
            {
                WriteToLogFile("Error creating custom material: " + ex.Message);
                return null;
            }
        }

        // Apply custom material to target object(s)
        private void ApplyToTargetObject(Material customMaterial)
        {
            if (customMaterial == null)
                return;

            try
            {
                string targetPath = GetCurrentTargetPath();
                WriteToLogFile($"Applying custom material to target: {CurrentTarget}");

                if (CurrentTarget == TargetType.FullMap)
                {
                    // Apply to all environment objects for full map
                    GameObject environmentRoot = GameObject.Find(FULL_MAP_PATH);
                    if (environmentRoot != null)
                    {
                        ApplyToAllChildRenderers(environmentRoot, customMaterial);
                    }
                    else
                    {
                        WriteToLogFile("Environment root not found");
                    }
                    return;
                }

                // For player or ground
                if (targetObject == null)
                {
                    WriteToLogFile($"Target object not found: {targetPath}");
                    return;
                }

                // Get renderer from target object
                Renderer renderer = targetObject.GetComponent<Renderer>();
                if (renderer == null)
                {
                    WriteToLogFile("No renderer found on target object");

                    // Check for child renderers
                    Renderer[] childRenderers = targetObject.GetComponentsInChildren<Renderer>();
                    if (childRenderers.Length > 0)
                    {
                        WriteToLogFile($"Found {childRenderers.Length} renderers in children of target object");

                        foreach (Renderer childRenderer in childRenderers)
                        {
                            ApplyToRenderer(childRenderer, customMaterial);
                        }
                    }
                    else
                    {
                        WriteToLogFile("No renderers found in children of target object");
                    }
                }
                else
                {
                    // Apply to the main renderer
                    ApplyToRenderer(renderer, customMaterial);
                }
            }
            catch (Exception ex)
            {
                WriteToLogFile($"Error in ApplyToTargetObject: {ex.Message}");
            }
        }

        // Apply to all renderers in an object hierarchy
        private void ApplyToAllChildRenderers(GameObject root, Material customMaterial)
        {
            if (root == null)
                return;

            try
            {
                int count = 0;

                // Apply to renderers in all children
                Renderer[] allRenderers = root.GetComponentsInChildren<Renderer>(true);
                WriteToLogFile($"Found {allRenderers.Length} renderers in full map");

                foreach (Renderer renderer in allRenderers)
                {
                    // Skip UI elements, particles, etc.
                    if (ShouldSkipRenderer(renderer))
                        continue;

                    ApplyToRenderer(renderer, customMaterial);
                    count++;
                }

                WriteToLogFile($"Applied material to {count} renderers in full map");
            }
            catch (Exception ex)
            {
                WriteToLogFile($"Error applying to child renderers: {ex.Message}");
            }
        }

        // Apply to a specific renderer
        private void ApplyToRenderer(Renderer renderer, Material customMaterial)
        {
            try
            {
                // Back up original shared materials
                Material[] originalMaterials = renderer.sharedMaterials;
                WriteToLogFile($"Applying to renderer: {renderer.gameObject.name} with {originalMaterials.Length} materials for {CurrentTarget}");

                // Create new materials array of same length
                Material[] newMaterials = new Material[originalMaterials.Length];

                // Replace all materials with our custom material
                for (int i = 0; i < originalMaterials.Length; i++)
                {
                    Material originalMat = originalMaterials[i];
                    if (originalMat != null)
                    {
                        // Store original texture info if not already stored
                        if (!materialOriginalTexturesByTarget[CurrentTarget].ContainsKey(originalMat))
                        {
                            Texture originalTexture = null;
                            Vector2 originalTiling = Vector2.one;

                            if (originalMat.HasProperty("_MainTex"))
                            {
                                originalTexture = originalMat.mainTexture;
                                originalTiling = originalMat.mainTextureScale;
                            }

                            materialOriginalTexturesByTarget[CurrentTarget][originalMat] = new TextureInfo(originalTexture, originalTiling);
                        }

                        // Use our custom material
                        newMaterials[i] = customMaterial;
                    }
                    else
                    {
                        newMaterials[i] = null;
                    }
                }

                // Apply new materials
                renderer.sharedMaterials = newMaterials;
                WriteToLogFile($"Applied custom material to renderer: {renderer.gameObject.name} for {CurrentTarget}");
            }
            catch (Exception ex)
            {
                WriteToLogFile($"Error applying material to renderer {renderer.name}: {ex.Message}");
            }
        }

        // Restore materials for a specific renderer
        private void RestoreRendererMaterials(Renderer renderer)
        {
            try
            {
                // Check if this renderer has our custom material for current target
                bool hasCustomMaterial = false;
                foreach (Material mat in renderer.sharedMaterials)
                {
                    if (customMaterialsByTarget[CurrentTarget].Contains(mat))
                    {
                        hasCustomMaterial = true;
                        break;
                    }
                }

                if (hasCustomMaterial)
                {
                    WriteToLogFile($"Restoring materials for renderer: {renderer.gameObject.name} for {CurrentTarget}");

                    // Create a default material if needed
                    Material defaultMaterial = new Material(Shader.Find("Standard"));

                    // Get the original materials
                    Material[] currentMaterials = renderer.sharedMaterials;
                    Material[] restoredMaterials = new Material[currentMaterials.Length];

                    for (int i = 0; i < currentMaterials.Length; i++)
                    {
                        if (customMaterialsByTarget[CurrentTarget].Contains(currentMaterials[i]))
                        {
                            // This was a custom material - try to restore original
                            // Since we don't know which original material belonged here, just use default
                            restoredMaterials[i] = defaultMaterial;
                        }
                        else
                        {
                            // Keep non-custom materials
                            restoredMaterials[i] = currentMaterials[i];
                        }
                    }

                    // Apply restored materials
                    renderer.sharedMaterials = restoredMaterials;
                }
            }
            catch (Exception ex)
            {
                WriteToLogFile($"Error restoring materials for renderer {renderer.name}: {ex.Message}");
            }
        }

        // Helper method to determine if renderer should be skipped
        private bool ShouldSkipRenderer(Renderer renderer)
        {
            if (renderer == null)
                return true;

            // Skip UI elements, particles, etc.
            if (renderer.gameObject.layer == LayerMask.NameToLayer("UI") ||
                renderer is ParticleSystemRenderer)
                return true;

            // Skip objects with these names
            string name = renderer.gameObject.name.ToLower();
            if (name.Contains("ui") || name.Contains("text") ||
                name.Contains("canvas") || name.Contains("menu"))
                return true;

            return false;
        }

        // Restore original textures
        private void RestoreOriginalTextures()
        {
            try
            {
                WriteToLogFile($"Restoring original materials for {CurrentTarget}...");

                if (CurrentTarget == TargetType.FullMap)
                {
                    // Restore all environment objects for full map
                    GameObject environmentRoot = GameObject.Find(FULL_MAP_PATH);
                    if (environmentRoot != null)
                    {
                        RestoreAllChildRenderers(environmentRoot);
                    }
                    else
                    {
                        WriteToLogFile("Environment root not found for restoration");
                    }
                    WriteToLogFile("Original materials restored for full map");
                    return;
                }

                // For player or ground
                if (targetObject == null)
                {
                    WriteToLogFile("Target object not found, cannot restore materials");
                    return;
                }

                // Get renderer from target object
                Renderer renderer = targetObject.GetComponent<Renderer>();
                if (renderer == null)
                {
                    // Check for child renderers
                    Renderer[] childRenderers = targetObject.GetComponentsInChildren<Renderer>();
                    if (childRenderers.Length > 0)
                    {
                        foreach (Renderer childRenderer in childRenderers)
                        {
                            RestoreRendererMaterials(childRenderer);
                        }
                    }
                }
                else
                {
                    // Restore the main renderer
                    RestoreRendererMaterials(renderer);
                }

                WriteToLogFile($"Original materials restored for {CurrentTarget}");
            }
            catch (Exception ex)
            {
                WriteToLogFile($"Error in RestoreOriginalTextures: {ex.Message}");
            }
        }

        // Restore all renderers in an object hierarchy
        private void RestoreAllChildRenderers(GameObject root)
        {
            if (root == null)
                return;

            try
            {
                int count = 0;

                // Restore renderers in all children
                Renderer[] allRenderers = root.GetComponentsInChildren<Renderer>(true);

                foreach (Renderer renderer in allRenderers)
                {
                    if (ShouldSkipRenderer(renderer))
                        continue;

                    RestoreRendererMaterials(renderer);
                    count++;
                }

                WriteToLogFile($"Restored {count} renderers in full map");
            }
            catch (Exception ex)
            {
                WriteToLogFile($"Error restoring child renderers: {ex.Message}");
            }
        }

        // Logger method
        private void WriteToLogFile(string message)
        {
            try
            {
                using (StreamWriter writer = File.AppendText(logFilePath))
                {
                    writer.WriteLine($"[{DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")}] {message}");
                }

                // Also log to BepInEx console
                if (Log != null)
                {
                    Log.LogInfo(message);
                }
            }
            catch
            {
                // If logging fails, at least try to log to Unity's debug log
                Debug.LogWarning("Failed to write to log file: " + message);
            }
        }
    }
}