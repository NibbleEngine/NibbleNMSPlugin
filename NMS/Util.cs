﻿using System;
using System.Collections.Generic;
using System.IO;
using libMBIN;
using NbCore.Math;
using libMBIN.NMS.Toolkit;
using System.Security.Permissions;
using System.Linq;
using OpenTK.Graphics.OpenGL4;
using Microsoft.Win32;
using Newtonsoft.Json;
using Path = System.IO.Path;
using NbCore;
using System.Windows;
using System.Reflection;
using libMBIN.NMS;

namespace NibbleNMSPlugin
{
    public static class Util
    {
        public static Dictionary<string, int> MapTexUnitToSampler = new()
        {
            { "gDiffuseMap", 0 },
            { "gMasksMap", 1 },
            { "gNormalMap", 2 },
            { "gDiffuse2Map", 3 },
            { "gDetailDiffuseMap", 4 },
            { "gDetailNormalMap", 5 }
        };

        private static TkAnimNodeFrameData[] _frames = new TkAnimNodeFrameData[2]; //Used during parsing
        
        //Animation frame data collection methods
        public static NbQuaternion fetchRotQuaternion(TkAnimNodeData node, TkAnimMetadata animMeta, 
            int frameCounter)
        {
            //Load Frames
            //Console.WriteLine("Setting Frame Index {0}", frameIndex);
            TkAnimNodeFrameData frame = animMeta.AnimFrameData[frameCounter];
            TkAnimNodeFrameData stillframe = animMeta.StillFrameData;
            
            int rotIndex;
            TkAnimNodeFrameData activeFrame;
            //Load Rotations
            if (node.RotIndex < frame.Rotations.Count)
            {
                rotIndex = node.RotIndex;
                activeFrame = frame;
            }
            else //Load stillframedata
            {
                rotIndex = node.RotIndex - frame.Rotations.Count;
                activeFrame = stillframe;
            }

            NbQuaternion q = new();
            q.X = activeFrame.Rotations[rotIndex].x;
            q.Y = activeFrame.Rotations[rotIndex].y;
            q.Z = activeFrame.Rotations[rotIndex].z;
            q.W = activeFrame.Rotations[rotIndex].w;

            return q;
        }


        public static NbVector3 fetchTransVector(TkAnimNodeData node, TkAnimMetadata animMeta, int frameCounter)
        {
            //Load Frames
            //Console.WriteLine("Setting Frame Index {0}", frameIndex);
            TkAnimNodeFrameData frame = animMeta.AnimFrameData[frameCounter];
            TkAnimNodeFrameData stillframe = animMeta.StillFrameData;
            TkAnimNodeFrameData activeFrame;

            int transIndex = -1;

            //Load Translations
            if (node.TransIndex < frame.Translations.Count)
            {
                transIndex = node.TransIndex;
                activeFrame = frame;
                
            }
            else //Load stillframedata
            {
                transIndex = node.TransIndex - frame.Translations.Count;
                activeFrame = stillframe;
            }

            NbVector3 v = new();
            v.X = activeFrame.Translations[transIndex].x;
            v.Y = activeFrame.Translations[transIndex].y;
            v.Z = activeFrame.Translations[transIndex].z;

            return v;
        }

        public static NbVector3 fetchScaleVector(TkAnimNodeData node, TkAnimMetadata animMeta, int frameCounter)
        {
            //Load Frames
            //Console.WriteLine("Setting Frame Index {0}", frameIndex);
            TkAnimNodeFrameData frame = animMeta.AnimFrameData[frameCounter];
            TkAnimNodeFrameData stillframe = animMeta.StillFrameData;
            TkAnimNodeFrameData activeFrame = null;
            int scaleIndex = -1;

            if (node.ScaleIndex < frame.Scales.Count)
            {
                scaleIndex = node.ScaleIndex;
                activeFrame = frame;
            }
            else //Load stillframedata
            {
                scaleIndex = node.ScaleIndex - frame.Scales.Count;
                activeFrame = stillframe;
            }

            NbVector3 s = new NbVector3();
            s.X = activeFrame.Scales[scaleIndex].x;
            s.Y = activeFrame.Scales[scaleIndex].y;
            s.Z = activeFrame.Scales[scaleIndex].z;

            return s;
        }


        //Texture Utilities

        public static NbTexture LoadNMSTexture(string path)
        {
            Stream s = FileUtils.LoadNMSFileStream(path);
            if (s is null)
                return null;
            byte[] data = new byte[s.Length];
            s.Read(data, 0, data.Length);
            s.Close();
            return PluginState.PluginRef.EngineRef.CreateTexture(data, path, false);
        }

        public static void loadSamplerTexture(NbSampler sampler, TextureManager texMgr)
        {
            if (sampler.Map == "")
                return;

            NbTexture tex;
            //Try to load the texture
            if (texMgr.Contains(sampler.Map))
            {
                tex = texMgr.Get(sampler.Map);
            }
            else
            {
                tex = LoadNMSTexture(sampler.Map);
                if (tex is null)
                {
                    //Reset shader binding if no texture is loaded
                    sampler.State.TextureID = -1;
                    sampler.State.SamplerID = -1;
                    sampler.State.ShaderBinding = "";
                    return;
                }
                    
                tex.palOpt = new PaletteOpt(false);
                tex.procColor = new NbVector4(1.0f, 1.0f, 1.0f, 0.0f);
            }

            //Set Sampler Properties
            sampler.SetTexture(tex);
        }

        public static void PrepareProcGenSamplers(MeshMaterial mat, TextureManager texMgr)
        {
            //Workaround for Procedurally Generated Samplers
            //I need to check if the diffuse sampler is procgen and then force the maps
            //on the other samplers with the appropriate names
            //TODO: Go through the process of loading procedural textures again. I don't like this at all

            foreach (NbSampler s in mat.Samplers)
            {
                //Check if the first sampler is procgen
                if (s.isProcGen)
                {
                    string name = s.Map;

                    //Properly assemble the mask and the normal map names

                    string[] split = name.Split('.');
                    string pre_ext_name = "";
                    for (int i = 0; i < split.Length - 1; i++)
                        pre_ext_name += split[i] + '.';

                    if (mat.SamplerMap.ContainsKey("mpCustomPerMaterial.gMasksMap"))
                    {
                        string new_name = pre_ext_name + "MASKS.DDS";
                        mat.SamplerMap["mpCustomPerMaterial.gMasksMap"].Map = new_name;
                        mat.SamplerMap["mpCustomPerMaterial.gMasksMap"].SetTexture(texMgr.Get(new_name));
                    }
                    else if (mat.SamplerMap.ContainsKey("mpCustomPerMaterial.gNormalMap"))
                    {
                        string new_name = pre_ext_name + "NORMAL.DDS";
                        mat.SamplerMap["mpCustomPerMaterial.gNormalMap"].Map = new_name;
                        mat.SamplerMap["mpCustomPerMaterial.gNormalMap"].SetTexture(texMgr.Get(new_name));
                    }
                    break;
                }
            }
        }

        
        
    }
}
