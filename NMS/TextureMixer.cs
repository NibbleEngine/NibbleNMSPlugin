#define DUMP_TEXTURES

using System;
using System.Collections.Generic;
using System.Text;
using OpenTK;
using NbCore.Math;
using NbCore.Platform.Graphics;
using OpenTK.Graphics.OpenGL4;
using NbCore;
using NbCore.Common;
using libMBIN.NMS.Toolkit;
using System.Diagnostics;


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
            int tex_width = 0;
            int tex_height = 0;
            NbTexture fbo_tex = null;
            int fbo = -1;

            bool fbo_status = setupFrameBuffer(ref fbo, ref fbo_tex, ref tex_width, ref tex_height);

            if (!fbo_status)
            {
                PluginState.PluginRef.Log("Unable to mix textures, probably 0x0 textures...\n", LogVerbosityLevel.ERROR);
                return;
            }

            NbTexture diffTex = mixDiffuseTextures(tex_width, tex_height);
            diffTex.Path = temp + "DDS";

            FBO.dumpChannelToImage(fbo, ReadBufferMode.ColorAttachment0, "fbo_dump", tex_width, tex_height);

            NbTexture maskTex = mixMaskTextures(tex_width, tex_height);
            maskTex.Path = temp + "MASKS.DDS";

            NbTexture normalTex = mixNormalTextures(tex_width, tex_height);
            normalTex.Path = temp + "NORMAL.DDS";

            //Bring Back screen
            GL.Viewport(0, 0, old_vp_size[2], old_vp_size[3]);
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
            GL.DeleteFramebuffer(fbo);

            //Delete Fraomebuffer Textures
            fbo_tex.Dispose();
            
            //Add the new procedural textures to the textureManager
            texMgr.Add(diffTex.Path, diffTex);
            texMgr.Add(maskTex.Path, maskTex);
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
                        tex.palOpt = palOpt;
                        tex.procColor = palColor;
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

        private static bool setupFrameBuffer(ref int fbo, ref NbTexture fbo_tex, ref int texWidth, ref int texHeight)
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
                return false;
            }

            
            //Diffuse Output
            fbo_tex = GraphicsAPI.CreateTexture(PixelInternalFormat.Rgba, texWidth, texHeight, PixelFormat.Rgba, PixelType.UnsignedByte, true);
            GraphicsAPI.setupTextureParameters(fbo_tex, (int)TextureWrapMode.Repeat,
                (int)TextureMagFilter.Linear, (int)TextureMinFilter.LinearMipmapLinear, 4.0f);
            

            //Create New RenderBuffer for the diffuse
            fbo = GL.GenFramebuffer();
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, fbo);

            //Attach Textures to this FBO
            GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, fbo_tex.texID, 0);

            //Check
            Debug.Assert(GL.CheckFramebufferStatus(FramebufferTarget.Framebuffer) == FramebufferErrorCode.FramebufferComplete);

            //Bind the FBO
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, fbo);

            //Set Viewport
            GL.GetInteger(GetPName.Viewport, old_vp_size);
            GL.Viewport(0, 0, texWidth, texHeight);

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

            NbShader shader = engine.renderSys.ShaderMgr.GetShaderByType(NbShaderType.TEXTURE_MIX_SHADER);
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
            shader.CurrentState.AddUniform("recolor_flag", 0.0f);

            //No need for extra alpha tetuxres
            shader.CurrentState.AddUniform("use_alpha_textures", 0.0f);

            //Diffuse Samplers
            for (int i = 0; i < 8; i++)
            {
                if (difftextures[i] != null)
                    tex = difftextures[i];
                else
                    tex = dMask;


                NbSamplerState s = new()
                {
                    Target = tex.Data.target,
                    TextureID = tex.texID
                };

                shader.CurrentState.AddSampler("mainTex" + "[" + i + "]", s);
            }


            //Upload Recolouring Information
            for (int i = 0; i < 8; i++)
            {
                NbVector4 vec = new(reColourings[i][0], reColourings[i][1],
                                    reColourings[i][2], reColourings[i][3]);
                shader.CurrentState.AddUniform("lRecolours" + "[" + i + "]", vec);
            }

            //Upload Average Colors Information
            NbVector4 avg_vec = new(0.5f);
            for (int i = 0; i < 8; i++)
            {
                shader.CurrentState.AddUniform("lAverageColors" + "[" + i + "]", avg_vec);
            }

            //Use the RenderQuad Method to do the job
            GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
            NbCore.Systems.RenderingSystem renderSystem = engine.renderSys;
            renderSystem.Renderer.RenderQuad(engine.GetMesh((ulong) "default_renderquad".GetHashCode()),
                shader, shader.CurrentState);


            //Console.WriteLine("MixTextures5, Last GL Error: " + GL.GetError());
            NbTexture out_tex_diffuse = GraphicsAPI.CreateTexture(PixelInternalFormat.Rgba8, texWidth, texHeight, PixelFormat.Rgba, PixelType.UnsignedByte, true);
            GraphicsAPI.setupTextureParameters(out_tex_diffuse, (int)TextureWrapMode.Repeat,
                (int)TextureMagFilter.Linear, (int)TextureMinFilter.LinearMipmapLinear, 4.0f);
            
            //Copy the read buffers to the 
            GL.BindTexture(GraphicsAPI.TextureTargetMap[out_tex_diffuse.Data.target], out_tex_diffuse.texID);
            GL.ReadBuffer(ReadBufferMode.ColorAttachment0);
            Callbacks.Assert(out_tex_diffuse.Data.target == NbTextureTarget.Texture2D,
                            "fbo texture target is not correct");
            GL.CopyTexSubImage2D(GraphicsAPI.TextureTargetMap[out_tex_diffuse.Data.target], 0, 0, 0, 0, 0, texWidth, texHeight);
            GL.GenerateMipmap(GenerateMipmapTarget.Texture2D);
            
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

            NbShader shader = engine.renderSys.ShaderMgr.GetShaderByType(NbShaderType.TEXTURE_MIX_SHADER);
            shader.ClearCurrentState();

            //Base Layers
            int baseLayerIndex = 0;
            for (int i = 0; i < 8; i++)
            {
                shader.CurrentState.AddUniform("lbaseLayersUsed" + "[" + i + "]", baseLayersUsed[i]);
                if (baseLayersUsed[i] > 0.0f)
                    baseLayerIndex = i;
            }

            shader.CurrentState.AddUniform("baseLayerIndex", (float)baseLayerIndex);

            //Activate Recoloring
            shader.CurrentState.AddUniform("recolor_flag", 0.0f);

            //No need for extra alpha tetuxres
            shader.CurrentState.AddUniform("use_alpha_textures", 1.0f);


            //Upload DiffuseTextures as alphaTextures
            for (int i = 0; i < 8; i++)
            {
                if (difftextures[i] != null)
                    tex = difftextures[i];
                else
                    tex = dMask;

                NbSamplerState s = new()
                {
                    Target = tex.Data.target,
                    TextureID = tex.texID
                };

                shader.CurrentState.AddSampler("alphaTex" + "[" + i + "]", s);
            }
            

            //Upload maskTextures
            for (int i = 0; i < 8; i++)
            {
                if (masktextures[i] != null)
                    tex = masktextures[i];
                else
                    tex = dMask;


                NbSamplerState s = new()
                {
                    Target = tex.Data.target,
                    TextureID = tex.texID
                };

                shader.CurrentState.AddSampler("mainTex" + "[" + i + "]", s);
            }


            //Use the RenderQuad Method to do the job

            NbCore.Systems.RenderingSystem renderSystem = engine.renderSys;
            GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
            renderSystem.Renderer.RenderQuad(engine.GetMesh((ulong)"default_renderquad".GetHashCode()),
                shader, shader.CurrentState);

            //Console.WriteLine("MixTextures5, Last GL Error: " + GL.GetError());
            NbTexture out_tex_mask = GraphicsAPI.CreateTexture(PixelInternalFormat.Rgba8, texWidth, texHeight, PixelFormat.Rgba, PixelType.UnsignedByte, true);
            GraphicsAPI.setupTextureParameters(out_tex_mask, (int)TextureWrapMode.Repeat,
                (int)TextureMagFilter.Linear, (int)TextureMinFilter.LinearMipmapLinear, 4.0f);
            
            //Copy the read buffers to the 
            GL.BindTexture(GraphicsAPI.TextureTargetMap[out_tex_mask.Data.target], out_tex_mask.texID);
            GL.ReadBuffer(ReadBufferMode.ColorAttachment0);
            GL.CopyTexSubImage2D(GraphicsAPI.TextureTargetMap[out_tex_mask.Data.target], 0, 0, 0, 0, 0, texWidth, texHeight);
            GL.GenerateMipmap(GenerateMipmapTarget.Texture2D);

            
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

            NbShader shader = engine.renderSys.ShaderMgr.GetShaderByType(NbShaderType.TEXTURE_MIX_SHADER);
            shader.ClearCurrentState();

            //Base Layers
            int baseLayerIndex = 0;
            for (int i = 0; i < 8; i++)
            {
                shader.CurrentState.AddUniform("lbaseLayersUsed" + "[" + i + "]", baseLayersUsed[i]);
                if (baseLayersUsed[i] > 0.0f)
                    baseLayerIndex = i;
            }

            shader.CurrentState.AddUniform("baseLayerIndex", (float)baseLayerIndex);

            //Activate Recoloring
            shader.CurrentState.AddUniform("recolor_flag", 0.0f);

            //Enable alpha tetuxres
            shader.CurrentState.AddUniform("use_alpha_textures", 1.0f);


            //Upload DiffuseTextures as alphaTextures
            for (int i = 0; i < 8; i++)
            {
                if (difftextures[i] != null)
                    tex = difftextures[i];
                else
                    tex = dMask;


                NbSamplerState s = new()
                {
                    Target = tex.Data.target,
                    TextureID = tex.texID
                };

                shader.CurrentState.AddSampler("alphaTex" + "[" + i + "]", s);

            }

            //Upload maskTextures
            for (int i = 0; i < 8; i++)
            {
                if (normaltextures[i] != null)
                    tex = normaltextures[i];
                else
                    tex = dMask;

                NbSamplerState s = new()
                {
                    Target = tex.Data.target,
                    TextureID = tex.texID
                };

                shader.CurrentState.AddSampler("mainTex" + "[" + i + "]", s);
            }
            
            
            //Use the RenderQuad Method to do the job

            NbCore.Systems.RenderingSystem renderSystem = engine.renderSys;
            GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
            renderSystem.Renderer.RenderQuad(engine.GetMesh((ulong)"default_renderquad".GetHashCode()),
                shader, shader.CurrentState);

            //Console.WriteLine("MixTextures5, Last GL Error: " + GL.GetError());

            NbTexture out_tex_normal = GraphicsAPI.CreateTexture(PixelInternalFormat.Rgba8, texWidth, texHeight, PixelFormat.Rgba, PixelType.UnsignedByte, true);
            GraphicsAPI.setupTextureParameters(out_tex_normal, (int)TextureWrapMode.Repeat,
                (int)TextureMagFilter.Linear, (int)TextureMinFilter.LinearMipmapLinear, 4.0f);
            
            //Copy the read buffers to the 

            GL.BindTexture(TextureTarget.Texture2D, out_tex_normal.texID);
            GL.ReadBuffer(ReadBufferMode.ColorAttachment0);
            GL.CopyTexSubImage2D(TextureTarget.Texture2D, 0, 0, 0, 0, 0, texWidth, texHeight);
            GL.GenerateMipmap(GenerateMipmapTarget.Texture2D);

#if (DUMP_TEXTURES)
            GL.ReadBuffer(ReadBufferMode.ColorAttachment0);
            GraphicsAPI.DumpTexture(out_tex_normal, "normal");
#endif
            return out_tex_normal;
        }
    }

}
