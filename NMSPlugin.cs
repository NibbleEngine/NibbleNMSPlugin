using System;
using System.IO;
using System.Reflection;
using System.Threading;
using NbCore;
using NbCore.Common;
using NbCore.Plugins;
using ImGuiNET;
using Newtonsoft.Json;
using System.Linq;

namespace NibbleNMSPlugin
{
    public class NMSPluginSettings : PluginSettings
    {
        [JsonIgnore]
        public static string DefaultSettingsFileName = "NbPlugin_NMS.ini";
        public string GameDir;
        public string UnpackDir;
        
        public new static PluginSettings GenerateDefaultSettings()
        {
            NMSPluginSettings settings = new()
            {
                GameDir = "",
                UnpackDir = ""
            };
            return settings;
        }

        public override void Draw()
        {
            ImGui.InputText("Game Directory", ref GameDir, 200);
            ImGui.InputText("Unpack Directory", ref GameDir, 200);
        }

        public override void DrawModals()
        {
            
        }

        public override void SaveToFile()
        {
            string jsondata = JsonConvert.SerializeObject(this);
            //Get Plugin Directory
            string plugindirectory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            File.WriteAllText(Path.Join(plugindirectory, DefaultSettingsFileName), jsondata);
        }
    }

    //Shared state across the NMSPlugin domain just to make import procedures easier 
    //(Avoid passing plugin settings everywhere)
    public static class PluginState
    {
        public static NMSPlugin PluginRef;
        public static Random Randgen = new Random(0x10);
    }

    public class NMSPlugin : PluginBase
    {
        public static string PluginName = "NMSPlugin";
        public static string PluginVersion = "1.0.0";
        public static string PluginDescription = "NMS Plugin for Nibble Engine.";
        public static string PluginCreator = "gregkwaste";

        private readonly ImGuiPakBrowser PakBrowser = new();
        private readonly NbCore.UI.ImGui.OpenFileDialog openFileDialog;
        private bool show_open_file_dialog_pak = false;
        private bool open_file_enabled = false;
        private bool show_update_libmbin_dialog = false;
        private string libMbinOnlineVersion = null;
        private string libMbinLocalVersion = null;

        public NMSPlugin(Engine e) : base(e)
        {
            Name = PluginName;
            Version = PluginVersion;
            Description = PluginDescription;
            Creator = PluginCreator;

            //Initialize OpenFileDialog
            openFileDialog = new("nms-open-file", ".mbin|.exml");
        }
        
        public void ShowOpenFileDialogPak()
        {
            show_open_file_dialog_pak = true;
        }

        public void ShowOpenFileDialog()
        {
            openFileDialog.Open();
        }

        public void ShowUpdateLibMBINDialog()
        {
            show_update_libmbin_dialog = true;
        }

        private void ProcessModals()
        {
            if (show_open_file_dialog_pak)
            {
                ImGui.OpenPopup("nms-open-file-pak");
                show_open_file_dialog_pak = false;
            }

            if (show_update_libmbin_dialog)
            {
                ImGui.OpenPopup("update-libmbin");
                show_update_libmbin_dialog = false;
            }

            bool isOpen = true;
            if (ImGui.BeginPopupModal("nms-open-file-pak", ref isOpen))
            {
                if (PakBrowser.isFinished())
                {
                    string filename = PakBrowser.SelectedItem;
                    PakBrowser.Clear();
                    
                    show_open_file_dialog_pak = false;
                    ImGui.CloseCurrentPopup();
                    Import(filename);
                }
                else
                {
                    PakBrowser.Draw();
                }

                if (ImGui.IsKeyPressed(ImGui.GetKeyIndex(ImGuiKey.Escape)))
                {
                    PakBrowser.Clear();
                    ImGui.CloseCurrentPopup();
                }

                ImGui.EndPopup();
            }


            if (openFileDialog.Draw(new System.Numerics.Vector2(600, 400)))
            {
                Import(openFileDialog.GetSelectedFile());
            }

            if (ImGui.BeginPopupModal("update-libmbin", ref isOpen, ImGuiWindowFlags.None))
            {
                if (libMbinLocalVersion == null)
                    libMbinLocalVersion = HTMLUtils.queryLibMBINDLLLocalVersion();

                if (libMbinOnlineVersion == null)
                {
                    libMbinOnlineVersion = HTMLUtils.queryLibMBINDLLOnlineVersion();
                }

                ImGui.Text("Old Version: " + libMbinLocalVersion);
                ImGui.Text("Online Version: " + libMbinOnlineVersion);
                ImGui.Text("Do you want to update?");

                bool updatelibmbin = false;
                if (ImGui.Button("YES"))
                {
                    updatelibmbin = true;
                }
                ImGui.SameLine();
                if (ImGui.Button("NO"))
                {
                    libMbinLocalVersion = null;
                    libMbinOnlineVersion = null;
                    ImGui.CloseCurrentPopup();
                }

                if (updatelibmbin)
                {
                    HTMLUtils.updateLibMBIN();
                    libMbinLocalVersion = null;
                    libMbinOnlineVersion = null;
                    ImGui.CloseCurrentPopup();
                }

                ImGui.EndPopup();

            }
        }


        public override void OnLoad()
        {
            Log(" Loading NMS Plugin...", LogVerbosityLevel.INFO);

            string plugindirectory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            string settingsfilepath = Path.Join(plugindirectory, NMSPluginSettings.DefaultSettingsFileName);

            //Load Plugin Settings
            if (File.Exists(settingsfilepath))
            {
                Log("Loading plugin settings file.", LogVerbosityLevel.INFO);
                string filedata = File.ReadAllText(settingsfilepath);
                Settings = JsonConvert.DeserializeObject<NMSPluginSettings>(filedata);
                Log($"GameDir: {(Settings as NMSPluginSettings).GameDir}", LogVerbosityLevel.INFO);
                Log($"UnpackDir: {(Settings as NMSPluginSettings).UnpackDir}", LogVerbosityLevel.INFO);
            }
            else
            {
                Log("Plugin Settings file not found.", LogVerbosityLevel.INFO);
                Settings = NMSPluginSettings.GenerateDefaultSettings() as NMSPluginSettings;
                Settings.SaveToFile();
            }
            
            //Set State
            PluginState.PluginRef = this;
            
            //Create a separate thread to try and load PAK archives
            //Issue work request 

            Thread t = new Thread(() => {
                FileUtils.loadNMSArchives(this, Path.Combine(((NMSPluginSettings) Settings).GameDir, "PCBANKS"), 
                    ref open_file_enabled);
            });
            Log("Starting Work Thread.", LogVerbosityLevel.INFO);
            t.Start();
            
            //Add Defaults
            AddDefaultTextures();
            AddDefaultShaders();
        }

        private void AddDefaultShaders()
        {
            //Add Shader Sources
            GLSLShaderSource vs = EngineRef.GetShaderSourceByFilePath("Shaders/texture_mixer_VS.glsl");
            if (vs == null)
                vs = new("Shaders/texture_mixer_VS.glsl", true);
            
            GLSLShaderSource fs = EngineRef.GetShaderSourceByFilePath("Shaders/texture_mixer_FS.glsl");
            if (fs == null)
                fs = new("Shaders/texture_mixer_FS.glsl", true);

            //Texture Mixing Shader
            GLSLShaderConfig conf = EngineRef.CreateShaderConfig(vs, fs,
                                      null, null, null,
                                      new() { }, NbShaderMode.DEFFERED, "TextureMix");
            EngineRef.RegisterEntity(conf);


            //Compile Shader
            NbShader shader = new()
            {
                Type = NbShaderType.TEXTURE_MIX_SHADER
            };
            NbCore.Platform.Graphics.GraphicsAPI.CompileShader(ref shader, conf);
            EngineRef.RegisterEntity(shader);

        }

        private void AddDefaultTextures()
        {
            Assembly currentAssembly = Assembly.GetExecutingAssembly();
            NbTexture tex = EngineRef.CreateTexture(Callbacks.getResourceFromAssembly(currentAssembly, "default.dds"), "default.dds");
            EngineRef.AddTexture(tex);
            tex = EngineRef.CreateTexture(Callbacks.getResourceFromAssembly(currentAssembly, "default_mask.dds"), "default_mask.dds");
            EngineRef.AddTexture(tex);
        }
        
        public override void Import(string filepath)
        {
            Log(string.Format("Importing {0}", filepath), LogVerbosityLevel.INFO);

            //Re-init pallete
            Palettes.set_palleteColors();
            Importer.ClearState();
            
            Importer.SetEngineReference(EngineRef);
            SceneGraphNode root = Importer.ImportScene(filepath);
            EngineRef.ClearActiveSceneGraph();
            EngineRef.ImportScene(root);

            //Dispose Unused Assets
            Importer.ClearState();
            Callbacks.updateStatus("Ready");
        }

        public override void Export(string filepath)
        {
            
        }

        public override void OnUnload()
        {
            FileUtils.unloadNMSArchives();
            //TODO: Add possibly other cleanups
        }

        public override void DrawImporters()
        {
            if (ImGui.BeginMenu("NMS"))
            {
                if (ImGui.MenuItem("Import from PAK", "", false, open_file_enabled))
                {
                    ShowOpenFileDialogPak();
                }

                if (ImGui.MenuItem("Import from file", "", false, open_file_enabled))
                {
                    ShowOpenFileDialog();
                }

                if (ImGui.MenuItem("Update LibMBIN"))
                {
                    ShowUpdateLibMBINDialog();
                }

                ImGui.EndMenu();
            }

        }

        public override void DrawExporters(SceneGraph scn)
        {
            
        }

        public override void Draw()
        {
            ProcessModals();
        }
    }
}
