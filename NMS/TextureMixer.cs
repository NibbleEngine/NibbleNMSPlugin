//#define DUMP_TEXTURES

using System;
using System.Collections.Generic;
using System.Text;
using NbCore.Math;
using NbCore.Platform.Graphics;
using NbCore;
using NbCore.Common;
using libMBIN.NMS.Toolkit;
using System.Diagnostics;
using System.Security.AccessControl;

namespace NibbleNMSPlugin
{
    public static class TextureMixer
    {
        //Local storage
        public static Dictionary<string, Dictionary<string, NbVector4>> palette = new();
        public static List<PaletteOpt> palOpts = new List<PaletteOpt>();
        public static List<NbTexture> difftextures = new List<NbTexture>(8);
        public static List<NbTexture> masktextures = new List<NbTexture>(8);
        public static List<NbTexture> normaltextures = new List<NbTexture>(8);
        public static float[] baseLayersUsed = new float[8];
        public static float[] alphaLayersUsed = new float[8];
        public static List<float[]> reColourings = new List<float[]>(8);
        public static List<float[]> avgColourings = new List<float[]>(8);
        private static int[] old_vp_size = new int[4];


        public static void clear()
        {
            //Cleanup temp buffers
            difftextures.Clear();
            masktextures.Clear();
            normaltextures.Clear();
            reColourings.Clear();
            avgColourings.Clear();
            for (int i = 0; i < 8; i++)
            {
                difftextures.Add(null);
                masktextures.Add(null);
                normaltextures.Add(null);
                reColourings.Add(new float[] { 0.0f, 0.0f, 0.0f, 0.0f });
                avgColourings.Add(new float[] { 0.5f, 0.5f, 0.5f, 0.5f });
                palOpts.Add(null);
            }
        }

        public static void combineTextures(string path, Dictionary<string, Dictionary<string, NbVector4>> pal_input, ref TextureManager texMgr)
        {
            clear();
            palette = pal_input;

            //Contruct .mbin file from dds
            string[] split = path.Split('.');
            //Construct main filename
            string temp = split[0] + ".";

            string mbinPath = temp + "TEXTURE.MBIN";
            prepareTextures(texMgr, mbinPath);

            //Init framebuffer
            GraphicsAPI renderer = RenderState.engineRef.GetRenderer();

            int tex_width = 0;
            int tex_height = 0;
            NbTexture fbo_tex = null;

            bool fbo_status = setupFrameBuffer(out FBO mix_fbo, ref fbo_tex, ref tex_width, ref tex_height, renderer);

            if (!fbo_status)
            {
                PluginState.PluginRef.Log("Unable to mix textures, probably 0x0 textures...\n", LogVerbosityLevel.ERROR);
                return;
            }

            NbTexture diffTex = mixDiffuseTextures(tex_width, tex_height);
            diffTex.Path = temp + "DDS";

#if DUMP_TEXTURES
            FBO.dumpChannelToImage(fbo, ReadBufferMode.ColorAttachment0, "fbo_dump", tex_width, tex_height);
#endif
            NbTexture maskTex = mixMaskTextures(tex_width, tex_height);
            maskTex.Path = temp + "MASKS.DDS";

            NbTexture normalTex = mixNormalTextures(tex_width, tex_height);
            normalTex.Path = temp + "NORMAL.DDS";

            //Bring Back screen
            renderer.DeleteFrameBuffer(mix_fbo);
            GraphicsAPI.SetViewPortSize(0, 0, old_vp_size[2], old_vp_size[3]);
            mix_fbo.Dispose();
            
            //Delete Framebuffer Texture
            fbo_tex.Dispose();
            
            //Add the new procedural textures to the textureManager
            if (texMgr.Contains(diffTex.Path))
                texMgr.Remove(diffTex.Path);
            texMgr.Add(diffTex.Path, diffTex);

            if (texMgr.Contains(maskTex.Path))
                texMgr.Remove(maskTex.Path);
            texMgr.Add(maskTex.Path, maskTex);

            if (texMgr.Contains(normalTex.Path))
                texMgr.Remove(normalTex.Path);
            texMgr.Add(normalTex.Path, normalTex);

        }

        //Generate procedural textures
        private static void prepareTextures(TextureManager texMgr, string path)
        {
            //At this point, at least one sampler exists, so for now I assume that the first sampler
            //is always the diffuse sampler and I can initiate the mixing process
            Console.WriteLine("Procedural Texture Detected: " + path);
            PluginState.PluginRef.Log(string.Format("Parsing Procedural Texture"), LogVerbosityLevel.INFO);

            TkProceduralTextureList template = FileUtils.LoadNMSTemplate(path) as TkProceduralTextureList;

            List<TkProceduralTexture> texList = new List<TkProceduralTexture>(8);
            for (int i = 0; i < 8; i++) texList.Add(null);
            ModelProcGen.parse_procTexture(ref texList, template);

            PluginState.PluginRef.Log("Proc Texture Selection", LogVerbosityLevel.INFO);
            for (int i = 0; i < 8; i++)
            {
                if (texList[i] != null)
                {
                    PluginState.PluginRef.Log(texList[i].Diffuse, LogVerbosityLevel.INFO);
                    PluginState.PluginRef.Log(texList[i].Mask, LogVerbosityLevel.INFO);
                    PluginState.PluginRef.Log(texList[i].Normal, LogVerbosityLevel.INFO);
                }
            }

            PluginState.PluginRef.Log("Procedural Material. Trying to generate procTextures...", LogVerbosityLevel.INFO);

            for (int i = 0; i < 8; i++)
            {

                TkProceduralTexture ptex = texList[i];
                //Add defaults
                if (ptex == null)
                {
                    baseLayersUsed[i] = 0.0f;
                    alphaLayersUsed[i] = 0.0f;
                    continue;
                }

                string partNameDiff = ptex.Diffuse;
                string partNameMask = ptex.Mask;
                string partNameNormal = ptex.Normal;

                TkPaletteTexture paletteNode = ptex.Palette;
                string paletteName = paletteNode.Palette.ToString();
                string colorName = paletteNode.ColourAlt.ToString();
                NbVector4 palColor;
                if (palette.ContainsKey(paletteName))
                    palColor = palette[paletteName][colorName];
                else
                    palColor = new NbVector4(0.8f, 0.3f, 0.7f, 1.0f);
                //Randomize palette Color every single time
                //Vector3 palColor = Model_Viewer.Palettes.get_color(paletteName, colorName);

                //Store pallete color to Recolouring List
                reColourings[i] = new float[] { palColor[0], palColor[1], palColor[2], palColor[3] };
                if (ptex.OverrideAverageColour)
                    avgColourings[i] = new float[] { ptex.AverageColour.R, ptex.AverageColour.G, ptex.AverageColour.B, ptex.AverageColour.A };

                //Create Palette Option
                PaletteOpt palOpt = new PaletteOpt();
                palOpt.PaletteName = paletteName;
                palOpt.ColorName = colorName;
                palOpts[i] = palOpt;
                Console.WriteLine("Index {0} Palette Selection {1} {2} ", i, palOpt.PaletteName, palOpt.ColorName);
                Console.WriteLine("Index {0} Color {1} {2} {3} {4}", i, palColor[0], palColor[1], palColor[2], palColor[3]);

                //DIFFUSE
                if (partNameDiff == "")
                {
                    //Add White
                    baseLayersUsed[i] = 0.0f;
                }
                else if (!texMgr.Contains(partNameDiff))
                {
                    //Configure the Diffuse Texture
                    try
                    {
                        //Get NMS texture data
                        NbTexture tex = Util.LoadNMSTexture(partNameDiff);
                        //Store to master texture manager
                        texMgr.Add(tex.Path, tex);

                        //Save Texture to material
                        difftextures[i] = tex;
                        baseLayersUsed[i] = 1.0f;
                        alphaLayersUsed[i] = 1.0f;
                    }
                    catch (System.IO.FileNotFoundException)
                    {
                        //Texture Not Found Continue
                        Console.WriteLine("Diffuse Texture " + partNameDiff + " Not Found, Appending White Tex");
                        PluginState.PluginRef.Log(string.Format("Diffuse Texture {0} Not Found", partNameDiff), LogVerbosityLevel.WARNING);
                        baseLayersUsed[i] = 0.0f;
                    }
                }
                else
                //Load texture from dict
                {
                    NbTexture tex = texMgr.Get(partNameDiff);
                    //Save Texture to material
                    difftextures[i] = tex;
                    baseLayersUsed[i] = 1.0f;
                }

                //MASK
                if (partNameMask == "")
                {
                    //Skip
                    alphaLayersUsed[i] = 0.0f;
                }
                else if (!texMgr.Contains(partNameMask))
                {
                    //Configure Mask
                    try
                    {
                        NbTexture texmask = Util.LoadNMSTexture(partNameMask);
                        //Store to master texture manager
                        texMgr.AddTexture(texmask);
                        //Store Texture to material
                        masktextures[i] = texmask;
                        alphaLayersUsed[i] = 0.0f;
                    }
                    catch (System.IO.FileNotFoundException)
                    {
                        //Mask Texture not found
                        PluginState.PluginRef.Log(string.Format("Mask Texture {0} Not Found", partNameMask), LogVerbosityLevel.WARNING);
                        alphaLayersUsed[i] = 0.0f;
                    }
                }
                else
                //Load texture from dict
                {
                    NbTexture tex = texMgr.Get(partNameMask);
                    //Store Texture to material
                    masktextures[i] = tex;
                    alphaLayersUsed[i] = 1.0f;
                }


                //NORMALS
                if (partNameNormal == "")
                {
                    //Skip

                }
                else if (!texMgr.Contains(partNameNormal))
                {
                    try
                    {
                        NbTexture texnormal = Util.LoadNMSTexture(partNameNormal);
                        //Store to master texture manager
                        texMgr.AddTexture(texnormal);
                        //Store Texture to material
                        normaltextures[i] = texnormal;
                    }
                    catch (System.IO.FileNotFoundException)
                    {
                        //Normal Texture not found
                        PluginState.PluginRef.Log(string.Format("Normal Texture {0} Not Found", partNameNormal), LogVerbosityLevel.WARNING);
                    }
                }
                else
                //Load texture from dict
                {
                    NbTexture tex = texMgr.Get(partNameNormal);
                    //Store Texture to material
                    normaltextures[i] = tex;
                }
            }
        }

        private static bool setupFrameBuffer(out FBO fbo, ref NbTexture fbo_tex, ref int texWidth, ref int texHeight, GraphicsAPI renderer)
        {
            for (int i = 0; i < 8; i++)
            {
                if (difftextures[i] != null)
                {
                    texHeight = difftextures[i].Data.Height;
                    texWidth = difftextures[i].Data.Width;
                    break;
                }
            }

            if (texWidth == 0 || texHeight == 0)
            {
                //FUCKING HG HAS FUCKING EMPTY TEXTURES WTF AM I SUPPOSED TO MIX HERE
                fbo = null;
                return false;
            }


            //Diffuse Output
            fbo_tex = new NbTexture()
            {
                Data = new()
                {
                    target = NbTextureTarget.Texture2D,
                    pif = NbTextureInternalFormat.RGBA8,
                    Width = texWidth,
                    Height = texHeight,
                },
            };
            
            GraphicsAPI.GenerateTexture(fbo_tex);
            
            GraphicsAPI.setupTextureParameters(fbo_tex, NbTextureWrapMode.Repeat,
                NbTextureFilter.Linear, NbTextureFilter.LinearMipmapNearest, 4.0f);

            //Create New RenderBuffer for the diffuse
            //Get Old Viewport Size
            GraphicsAPI.GetViewPortSize(ref old_vp_size[0], ref old_vp_size[1]);
            
            fbo = renderer.CreateFrameBuffer(texWidth, texHeight, FBOOptions.None);

            //Attach Textures to this FBO
            renderer.AddFrameBufferAttachment(fbo, fbo_tex, NbFBOAttachment.Attachment0);

            //Bind the FBO and activate it
            renderer.ActivateFrameBuffer(fbo);

            return true;
        }

        
        public static NbTexture mixDiffuseTextures(int texWidth, int texHeight)
        {
            //Upload Textures

            //BIND TEXTURES
            NbTexture tex;

            NbTexture dMask = RenderState.engineRef.GetTexture("default_mask.dds");
            NbTexture dDiff = RenderState.engineRef.GetTexture("default.dds");

            Engine engine = RenderState.engineRef;

            NbShader shader = engine.GetSystem<NbCore.Systems.RenderingSystem>().ShaderMgr.GetShaderByType(NbShaderType.TEXTURE_MIX_SHADER);
            shader.ClearCurrentState();

            ////Base Layers
            int baseLayerIndex = 0;
            for (int i = 0; i < 8; i++)
            {
                shader.CurrentState.AddUniform("lbaseLayersUsed" + "[" + i + "]", baseLayersUsed[i]);
                if (baseLayersUsed[i] > 0.0f)
                    baseLayerIndex = i;
            }

            shader.CurrentState.AddUniform("baseLayerIndex", (float)baseLayerIndex);

            //Activate Recoloring
            shader.CurrentState.AddUniform("recolor_flag", 1.0f);

            //No need for extra alpha tetuxres
            shader.CurrentState.AddUniform("use_alpha_textures", 0.0f);

            //Diffuse Samplers
            for (int i = 0; i < 8; i++)
            {
                if (difftextures[i] != null)
                    tex = difftextures[i];
                else
                    tex = dMask;

                string uniform_str = "mainTex" + "[" + i + "]";
                NbSampler s = new()
                {
                    SamplerID = i,
                    ShaderLocation = shader.uniformLocations[uniform_str].loc,
                    ShaderBinding = uniform_str,
                    Texture = tex
                };
                
                shader.CurrentState.AddSampler(uniform_str, s);
            }

            //Upload Recolouring Information
            for (int i = 0; i < 8; i++)
            {
                NbVector4 vec = new(reColourings[i][0], reColourings[i][1],
                                    reColourings[i][2], reColourings[i][3]);
                shader.CurrentState.AddUniform("lRecolours" + "[" + i + "]", vec);
            }

            //Use the RenderQuad Method to do the job
            GraphicsAPI.ClearDrawBuffer(NbBufferMask.Color | NbBufferMask.Depth);
            NbCore.Systems.RenderingSystem renderSystem = engine.GetSystem<NbCore.Systems.RenderingSystem>();
            renderSystem.Renderer.RenderQuad(engine.GetMesh(NbHasher.Hash("default_renderquad")),
                shader, shader.CurrentState);

            //Console.WriteLine("MixTextures5, Last GL Error: " + GL.GetError());
            NbTexture out_tex_diffuse = new()
            {
                Data = new()
                {
                    target = NbTextureTarget.Texture2D,
                    Width = texWidth,
                    Height = texHeight,
                    pif = NbTextureInternalFormat.RGBA8,
                    MagFilter = NbTextureFilter.Linear,
                    MinFilter = NbTextureFilter.Linear,
                    WrapMode = NbTextureWrapMode.Repeat
                }
            };
                
            GraphicsAPI.GenerateTexture(out_tex_diffuse);
            if (out_tex_diffuse.texID == 20)
                Console.WriteLine("asdasdasd");
            GraphicsAPI.UploadTexture(out_tex_diffuse);
            GraphicsAPI.setupTextureParameters(out_tex_diffuse, NbTextureWrapMode.Repeat,
                NbTextureFilter.Linear, NbTextureFilter.LinearMipmapLinear, 4.0f);

            renderSystem.Renderer.CopyFrameBufferChannelToTexture(NbFBOAttachment.Attachment0, out_tex_diffuse);
            
            
            
            //Find name for textures

#if (DUMP_TEXTURES)
            GL.ReadBuffer(ReadBufferMode.ColorAttachment0);
            GraphicsAPI.DumpTexture(out_tex_diffuse, "diffuse");
#endif
            return out_tex_diffuse;
        }

        private static NbTexture mixMaskTextures(int texWidth, int texHeight)
        {
            //Upload Textures

            //BIND TEXTURES
            NbTexture tex;

            NbTexture dMask = RenderState.engineRef.GetTexture("default_mask.dds");
            NbTexture dDiff = RenderState.engineRef.GetTexture("default.dds");

            Engine engine = RenderState.engineRef;

            NbShader shader = engine.GetSystem<NbCore.Systems.RenderingSystem>().ShaderMgr.GetShaderByType(NbShaderType.TEXTURE_MIX_SHADER);
            shader.ClearCurrentState();

            //Base Layers
            int baseLayerIndex = 0;
            for (int i = 0; i < 8; i++)
            {
                //Recalculate layerUsed Status
                if (masktextures[i] != null)
                    baseLayersUsed[i] = 1.0f;
                else
                    baseLayersUsed[i] = 0.0f;

                shader.CurrentState.AddUniform("lbaseLayersUsed" + "[" + i + "]", baseLayersUsed[i]);
                if (baseLayersUsed[i] > 0.0f)
                    baseLayerIndex = i;
            }

            shader.CurrentState.AddUniform("baseLayerIndex", (float) baseLayerIndex);

            //Activate Recoloring
            shader.CurrentState.AddUniform("recolor_flag", 0.0f);

            //No need for extra alpha tetuxres
            shader.CurrentState.AddUniform("use_alpha_textures", 1.0f);

            //Upload DiffuseTextures as alphaTextures
            int sampler_id = 0;
            for (int i = 0; i < 8; i++)
            {
                if (difftextures[i] != null)
                    tex = difftextures[i];
                else
                    tex = dMask;

                string uniform_str = "alphaTex" + "[" + i + "]";
                NbSampler s = new()
                {
                    SamplerID = sampler_id,
                    ShaderLocation = shader.uniformLocations[uniform_str].loc,
                    ShaderBinding = uniform_str,
                    Texture = tex
                };

                shader.CurrentState.AddSampler(uniform_str, s);
                sampler_id += 1;
            }
            
            //Upload maskTextures
            for (int i = 0; i < 8; i++)
            {
                if (masktextures[i] != null)
                    tex = masktextures[i];
                else
                    tex = dMask;

                string uniform_str = "mainTex" + "[" + i + "]";
                NbSampler s = new()
                {
                    SamplerID = sampler_id,
                    ShaderLocation = shader.uniformLocations[uniform_str].loc,
                    ShaderBinding = uniform_str,
                    Texture = tex
                };
                sampler_id += 1;
                shader.CurrentState.AddSampler(uniform_str, s);
            }


            //Use the RenderQuad Method to do the job
            GraphicsAPI.ClearDrawBuffer(NbBufferMask.Color | NbBufferMask.Depth);
            NbCore.Systems.RenderingSystem renderSystem = engine.GetSystem<NbCore.Systems.RenderingSystem>();
            renderSystem.Renderer.RenderQuad(engine.GetMesh(NbHasher.Hash("default_renderquad")),
                shader, shader.CurrentState);

            //Console.WriteLine("MixTextures5, Last GL Error: " + GL.GetError());
            NbTexture out_tex_mask = new()
            {
                Data = new()
                {
                    target = NbTextureTarget.Texture2D,
                    Width = texWidth,
                    Height = texHeight,
                    pif = NbTextureInternalFormat.RGBA8,
                    MagFilter = NbTextureFilter.Linear,
                    MinFilter = NbTextureFilter.Linear,
                    WrapMode = NbTextureWrapMode.Repeat
                }
            };

            GraphicsAPI.GenerateTexture(out_tex_mask);
            GraphicsAPI.UploadTexture(out_tex_mask);
            GraphicsAPI.setupTextureParameters(out_tex_mask, NbTextureWrapMode.Repeat,
                NbTextureFilter.Linear, NbTextureFilter.LinearMipmapLinear, 4.0f);

            renderSystem.Renderer.CopyFrameBufferChannelToTexture(NbFBOAttachment.Attachment0, out_tex_mask);


#if (DUMP_TEXTURES)
            GL.ReadBuffer(ReadBufferMode.ColorAttachment0);
            GraphicsAPI.DumpTexture(out_tex_mask, "mask");
#endif
            return out_tex_mask;
        }

        private static NbTexture mixNormalTextures(int texWidth, int texHeight)
        {
            //Upload Textures

            //BIND TEXTURES
            NbTexture tex;

            NbTexture dMask = RenderState.engineRef.GetTexture("default_mask.dds");
            NbTexture dDiff = RenderState.engineRef.GetTexture("default.dds");


            Engine engine = RenderState.engineRef;

            NbShader shader = engine.GetSystem<NbCore.Systems.RenderingSystem>().ShaderMgr.GetShaderByType(NbShaderType.TEXTURE_MIX_SHADER);
            shader.ClearCurrentState();

            //Base Layers
            int baseLayerIndex = 0;
            for (int i = 0; i < 8; i++)
            {
                //Recalculate layerUsed Status
                if (normaltextures[i] != null)
                    baseLayersUsed[i] = 1.0f;
                else
                    baseLayersUsed[i] = 0.0f;
                
                shader.CurrentState.AddUniform("lbaseLayersUsed" + "[" + i + "]", baseLayersUsed[i]);
                if (baseLayersUsed[i] > 0.0f)
                    baseLayerIndex = i;
            }

            shader.CurrentState.AddUniform("baseLayerIndex", (float) baseLayerIndex);

            //Activate Recoloring
            shader.CurrentState.AddUniform("recolor_flag", 0.0f);

            //Enable alpha tetuxres
            shader.CurrentState.AddUniform("use_alpha_textures", 1.0f);


            //Upload DiffuseTextures as alphaTextures
            int sampler_id = 0;
            for (int i = 0; i < 8; i++)
            {
                if (difftextures[i] != null)
                    tex = difftextures[i];
                else
                    tex = dMask;

                string uniform_str = "alphaTex" + "[" + i + "]";
                NbSampler s = new()
                {
                    SamplerID = sampler_id,
                    ShaderLocation = shader.uniformLocations[uniform_str].loc,
                    ShaderBinding = uniform_str,
                    Texture = tex
                };
                sampler_id += 1;

                shader.CurrentState.AddSampler(uniform_str, s);

            }

            //Upload maskTextures
            for (int i = 0; i < 8; i++)
            {
                if (normaltextures[i] != null)
                    tex = normaltextures[i];
                else
                    tex = dMask;

                string uniform_str = "mainTex" + "[" + i + "]";
                NbSampler s = new()
                {
                    SamplerID = sampler_id,
                    ShaderLocation = shader.uniformLocations[uniform_str].loc,
                    ShaderBinding = uniform_str,
                    Texture = tex
                };
                sampler_id += 1;

                shader.CurrentState.AddSampler(uniform_str, s);
            }


            //Use the RenderQuad Method to do the job
            GraphicsAPI.ClearDrawBuffer(NbBufferMask.Color | NbBufferMask.Depth);
            NbCore.Systems.RenderingSystem renderSystem = engine.GetSystem<NbCore.Systems.RenderingSystem>();
            renderSystem.Renderer.RenderQuad(engine.GetMesh(NbHasher.Hash("default_renderquad")),
                shader, shader.CurrentState);

            //Console.WriteLine("MixTextures5, Last GL Error: " + GL.GetError());
            NbTexture out_tex_normal = new()
            {
                Data = new()
                {
                    target = NbTextureTarget.Texture2D,
                    Width = texWidth,
                    Height = texHeight,
                    pif = NbTextureInternalFormat.RGBA8,
                    MagFilter = NbTextureFilter.Linear,
                    MinFilter = NbTextureFilter.Linear,
                    WrapMode = NbTextureWrapMode.Repeat
                }
            };

            GraphicsAPI.GenerateTexture(out_tex_normal);
            GraphicsAPI.UploadTexture(out_tex_normal);
            GraphicsAPI.setupTextureParameters(out_tex_normal, NbTextureWrapMode.Repeat,
                NbTextureFilter.Linear, NbTextureFilter.LinearMipmapLinear, 4.0f);

            renderSystem.Renderer.CopyFrameBufferChannelToTexture(NbFBOAttachment.Attachment0, out_tex_normal);

#if (DUMP_TEXTURES)
            GL.ReadBuffer(ReadBufferMode.ColorAttachment0);
            GraphicsAPI.DumpTexture(out_tex_normal, "normal");
#endif
            return out_tex_normal;
        }
    }

}
