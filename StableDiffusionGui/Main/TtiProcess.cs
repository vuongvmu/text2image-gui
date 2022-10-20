﻿using StableDiffusionGui.Io;
using StableDiffusionGui.MiscUtils;
using StableDiffusionGui.Os;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace StableDiffusionGui.Main
{
    internal class TtiProcess
    {
        public static Process CurrentProcess;
        public static StreamWriter CurrentStdInWriter;
        public static bool ProcessExistWasIntentional = false;
        public static bool IsAiProcessRunning { get { return CurrentProcess != null && !CurrentProcess.HasExited; } }

        public static void Finish()
        {
            return;
        }

        private static string _lastDreamPyStartupSettings;

        public static async Task RunStableDiffusion(string[] prompts, int iterations, Dictionary<string, string> paramsDict, string outPath)
        {
            string[] initImgs = paramsDict.Get("initImgs").FromJson<string[]>();
            string embedding = paramsDict.Get("embedding").FromJson<string>();
            float[] initStrengths = paramsDict.Get("initStrengths").FromJson<float[]>();
            int steps = paramsDict.Get("steps").FromJson<int>();
            float[] scales = paramsDict.Get("scales").FromJson<float[]>();
            long seed = paramsDict.Get("seed").FromJson<long>();
            string sampler = paramsDict.Get("sampler").FromJson<string>();
            Size res = paramsDict.Get("res").FromJson<Size>();
            bool seamless = paramsDict.Get("seamless").FromJson<bool>();
            string model = paramsDict.Get("model").FromJson<string>();

            if (!TtiUtils.CheckIfSdModelExists())
                return;

            Dictionary<string, string> initImages = initImgs != null && initImgs.Length > 0 ? await TtiUtils.CreateResizedInitImagesIfNeeded(initImgs.ToList(), res) : null;

            TtiUtils.WriteModelsYaml(model);

            long startSeed = seed;

            List<string> cmds = new List<string>();

            string upscale = ArgsDreamPy.GetUpscaleArgs();
            string faceFix = ArgsDreamPy.GetFaceRestoreArgs();

            int imgs = 0;

            foreach (string prompt in prompts)
            {
                for (int i = 0; i < iterations; i++)
                {
                    foreach (float scale in scales)
                    {
                        if(initImages == null) // No init image(s)
                        {
                            cmds.Add($"{prompt} -n {1} -s {steps} -C {scale.ToStringDot()} -A {sampler} -W {res.Width} -H {res.Height} -S {seed} {upscale} {faceFix} {(seamless ? "--seamless" : "")} {ArgsDreamPy.GetDefaultArgsCommand()}");
                            imgs++;
                        }
                        else // With init image(s)
                        {
                            foreach(string initImg in initImages.Values)
                            {
                                foreach (float strength in initStrengths)
                                {
                                    string init = $"--init_img {initImg.Wrap()} --strength {strength.ToStringDot("0.###")}";
                                    cmds.Add($"{prompt} {init} -n {1} -s {steps} -C {scale.ToStringDot()} -A {sampler} -W {res.Width} -H {res.Height} -S {seed} {upscale} {faceFix} {(seamless ? "--seamless" : "")} {ArgsDreamPy.GetDefaultArgsCommand()}");
                                    imgs++;
                                }
                            }
                        }
                    }

                    seed++;
                }

                if (Config.GetBool(Config.Key.checkboxMultiPromptsSameSeed))
                    seed = startSeed;
            }

            Logger.Log($"Running Stable Diffusion - {iterations} Iterations, {steps} Steps, Scales {(scales.Length < 4 ? string.Join(", ", scales.Select(x => x.ToStringDot())) : $"{scales.First()}->{scales.Last()}")}, {res.Width}x{res.Height}, Starting Seed: {startSeed}");

            string precArg = ArgsDreamPy.GetPrecisionArg();
            string embArg = ArgsDreamPy.GetEmbeddingArg(embedding);

            string newStartupSettings = $"{model}{precArg}{embArg}"; // Check if startup settings match - If not, we need to restart the process

            string initsStr = initImages != null ? $" and {initImages.Count} image{(initImages.Count != 1 ? "s" : "")} using {initStrengths.Length} strength{(initStrengths.Length != 1 ? "s" : "")}" : "";
            Logger.Log($"{prompts.Length} prompt{(prompts.Length != 1 ? "s" : "")} with {iterations} iteration{(iterations != 1 ? "s" : "")} each and {scales.Length} scale{(scales.Length != 1 ? "s" : "")}{initsStr} each = {imgs} images total.");

            if (!IsAiProcessRunning || (IsAiProcessRunning && _lastDreamPyStartupSettings != newStartupSettings))
            {
                _lastDreamPyStartupSettings = newStartupSettings;

                if (!string.IsNullOrWhiteSpace(embedding))
                {
                    if (!File.Exists(embedding))
                        embedding = "";
                    else
                        Logger.Log($"Using learned concept: {Path.GetFileName(embedding)}");
                }

                Process dream = OsUtils.NewProcess(!OsUtils.ShowHiddenCmd());
                TextToImage.CurrentTask.Processes.Add(dream);

                dream.StartInfo.RedirectStandardInput = true;
                dream.StartInfo.Arguments = $"{OsUtils.GetCmdArg()} cd /D {Paths.GetDataPath().Wrap()} && {TtiUtils.GetEnvVarsSd()} && call activate.bat ldo && " +
                    $"python {Constants.Dirs.RepoSd}/scripts/dream.py --model default -o {outPath.Wrap(true)} {ArgsDreamPy.GetDefaultArgsStartup()} {precArg} " +
                    $"{embArg} ";

                Logger.Log("cmd.exe " + dream.StartInfo.Arguments, true);

                if (!OsUtils.ShowHiddenCmd())
                {
                    dream.OutputDataReceived += (sender, line) => { TtiProcessOutputHandler.LogOutput(line.Data); };
                    dream.ErrorDataReceived += (sender, line) => { TtiProcessOutputHandler.LogOutput(line.Data, true); };
                }

                if (CurrentProcess != null)
                {
                    ProcessExistWasIntentional = true;
                    OsUtils.KillProcessTree(CurrentProcess.Id);
                }

                TtiProcessOutputHandler.Start();
                Logger.Log($"Loading Stable Diffusion with model {Path.ChangeExtension(model, null).Wrap()}...");
                CurrentProcess = dream;
                ProcessExistWasIntentional = false;
                dream.Start();
                OsUtils.AttachOrphanHitman(dream);
                CurrentStdInWriter = dream.StandardInput;

                if (!OsUtils.ShowHiddenCmd())
                {
                    dream.BeginOutputReadLine();
                    dream.BeginErrorReadLine();
                }

                Task.Run(() => CheckStillRunning());
                //while (!dream.HasExited) await Task.Delay(1); // We don't wait for it to quit since it keeps running in background.
            }
            else
            {
                TextToImage.CurrentTask.Processes.Add(CurrentProcess);
            }

            Logger.Log($"Writing to stdin...", true);

            await WriteStdIn("!reset");

            foreach (string command in cmds)
                await WriteStdIn(command);

            Finish();
        }

        public static async Task RunStableDiffusionOpt(string[] prompts, int iterations, Dictionary<string, string> paramsDict, string outPath)
        {
            // NOTE: Currently not implemented: Embeddings, Samplers, Seamless Mode
            string[] initImgs = paramsDict.Get("initImgs").FromJson<string[]>();
            string embedding = paramsDict.Get("embedding").FromJson<string>();
            float[] initStrengths = paramsDict.Get("initStrengths").FromJson<float[]>();
            int steps = paramsDict.Get("steps").FromJson<int>();
            float[] scales = paramsDict.Get("scales").FromJson<float[]>();
            long seed = paramsDict.Get("seed").FromJson<long>();
            string sampler = paramsDict.Get("sampler").FromJson<string>();
            Size res = paramsDict.Get("res").FromJson<Size>();
            bool seamless = paramsDict.Get("seamless").FromJson<bool>();
            string model = paramsDict.Get("model").FromJson<string>();
            string modelNoExt = Path.ChangeExtension(model, null);

            if (!TtiUtils.CheckIfSdModelExists())
                return;

            Dictionary<string, string> initImages = initImgs != null && initImgs.Length > 0 ? await TtiUtils.CreateResizedInitImagesIfNeeded(initImgs.ToList(), res) : null;

            long startSeed = seed;

            string promptFilePath = Path.Combine(Paths.GetSessionDataPath(), "prompts.txt");
            List<string> promptFileLines = new List<string>();

            // int upscaleSetting = Config.GetInt("comboxUpscale");
            // string upscaling = upscaleSetting == 0 ? "" : $"-U {Math.Pow(2, upscaleSetting)}";
            // 
            // float gfpganSetting = Config.GetFloat("sliderGfpgan");
            // string gfpgan = gfpganSetting > 0.01f ? $"-G {gfpganSetting.ToStringDot("0.00")}" : "";

            int imgs = 0;

            foreach (string prompt in prompts)
            {
                for (int i = 0; i < iterations; i++)
                {
                    foreach (float scale in scales)
                    {
                        if (initImages == null) // No init image(s)
                        {
                            promptFileLines.Add($"--prompt {prompt.Wrap()} --ddim_steps {steps} --scale {scale.ToStringDot()} --W {res.Width} --H {res.Height} --seed {seed}");
                            imgs++;
                        }
                        else // With init image(s)
                        {
                            foreach (string initImg in initImages.Values)
                            {
                                foreach (float strength in initStrengths)
                                {
                                    string init = $"--init_img {initImg.Wrap()} --strength {strength.ToStringDot("0.###")}";
                                    promptFileLines.Add($"--prompt {prompt.Wrap()} {init} --ddim_steps {steps} --scale {scale.ToStringDot()} --W {res.Width} --H {res.Height} --seed {seed}");
                                    imgs++;
                                }
                            }
                        }
                    }

                    seed++;
                }

                if (Config.GetBool(Config.Key.checkboxMultiPromptsSameSeed))
                    seed = startSeed;
            }

            IoUtils.TryDeleteIfExists(promptFilePath); // idk if this is needed, but the line below MIGHT append something so better make sure the previous prompts are deleted
            File.WriteAllLines(promptFilePath, promptFileLines);

            Logger.Log($"Running Stable Diffusion - {iterations} Iterations, {steps} Steps, Scales {(scales.Length < 4 ? string.Join(", ", scales.Select(x => x.ToStringDot())) : $"{scales.First()}->{scales.Last()}")}, {res.Width}x{res.Height}, Starting Seed: {startSeed}");

            string precArg = $"--precision {(Config.GetBool("checkboxFullPrecision") ? "full" : "autocast")}";

            string newStartupSettings = $"opt{modelNoExt}{precArg}"; // Check if startup settings match - If not, we need to restart the process

            string initsStr = initImages != null ? $" and {initImages.Count} image{(initImages.Count != 1 ? "s" : "")} using {initStrengths.Length} strength{(initStrengths.Length != 1 ? "s" : "")}" : "";
            Logger.Log($"{prompts.Length} prompt{(prompts.Length != 1 ? "s" : "")} with {iterations} iteration{(iterations != 1 ? "s" : "")} each and {scales.Length} scale{(scales.Length != 1 ? "s" : "")}{initsStr} each = {imgs} images total.");

            if (!IsAiProcessRunning || (IsAiProcessRunning && _lastDreamPyStartupSettings != newStartupSettings))
            {
                _lastDreamPyStartupSettings = newStartupSettings;

                Process dream = OsUtils.NewProcess(!OsUtils.ShowHiddenCmd());
                TextToImage.CurrentTask.Processes.Add(dream);

                dream.StartInfo.Arguments = $"{OsUtils.GetCmdArg()} cd /D {Paths.GetDataPath().Wrap()} && {TtiUtils.GetEnvVarsSd()} && call activate.bat ldo && " +
                    $"python {Constants.Dirs.RepoSd}/optimizedSD/optimized_txt2img_loop.py --model {modelNoExt.Wrap()} --outdir {outPath.Wrap(true)} --from_file_loop={promptFilePath.Wrap()} {precArg} ";
                Logger.Log("cmd.exe " + dream.StartInfo.Arguments, true);

                if (!OsUtils.ShowHiddenCmd())
                {
                    dream.OutputDataReceived += (sender, line) => { TtiProcessOutputHandler.LogOutput(line.Data); };
                    dream.ErrorDataReceived += (sender, line) => { TtiProcessOutputHandler.LogOutput(line.Data, true); };
                }

                if (CurrentProcess != null)
                {
                    ProcessExistWasIntentional = true;
                    OsUtils.KillProcessTree(CurrentProcess.Id);
                }

                TtiProcessOutputHandler.Start();
                Logger.Log($"Loading Stable Diffusion with model {modelNoExt.Wrap()}...");
                CurrentProcess = dream;
                ProcessExistWasIntentional = false;
                dream.Start();
                OsUtils.AttachOrphanHitman(dream);

                if (!OsUtils.ShowHiddenCmd())
                {
                    dream.BeginOutputReadLine();
                    dream.BeginErrorReadLine();
                }

                Task.Run(() => CheckStillRunning());
                //while (!dream.HasExited) await Task.Delay(1); // We don't wait for it to quit since it keeps running in background.
            }
            else
            {
                TextToImage.CurrentTask.Processes.Add(CurrentProcess);
            }

            Finish();
        }

        public static async Task RunStableDiffusionCli(string outPath)
        {
            if (Program.Busy)
                return;

            if (!TtiUtils.CheckIfSdModelExists())
                return;

            TtiUtils.WriteModelsYaml(Config.Get(Config.Key.comboxSdModel));

            string batPath = Path.Combine(Paths.GetSessionDataPath(), "dream.bat");

            string batText = $"@echo off\n" +
                $"title Dream.py CLI\n" +
                $"cd /D {Paths.GetDataPath().Wrap()}\n" +
                $"SET PATH={OsUtils.GetTemporaryPathVariable(new string[] { "./mb", "./mb/Scripts", "./mb/condabin", "./mb/Library/bin" })}\n" +
                $"call activate.bat mb/envs/ldo\n" +
                $"python {Constants.Dirs.RepoSd}/scripts/dream.py --model default -o {outPath.Wrap(true)} {ArgsDreamPy.GetPrecisionArg()} {ArgsDreamPy.GetDefaultArgsStartup()}";

            File.WriteAllText(batPath, batText);
            ProcessManager.FindAndKillOrphans($"*dream.py*{outPath}*");
            Process cli = Process.Start(batPath);
            OsUtils.AttachOrphanHitman(cli);
        }

        public static async Task<bool> WriteStdIn(string text, bool submitLine = true)
        {
            try
            {
                if (TextToImage.Canceled || CurrentStdInWriter == null)
                    return false;

                Logger.Log($"=> {text}", true);

                if (submitLine)
                    await CurrentStdInWriter.WriteLineAsync(text);
                else
                    await CurrentStdInWriter.WriteAsync(text);

                return true;
            }
            catch
            {
                return false;
            }
        }

        public static void Kill()
        {
            Logger.Log($"Killing current task's processes.", true);

            if (TextToImage.CurrentTask != null)
            {
                foreach (var process in TextToImage.CurrentTask.Processes)
                {
                    try
                    {
                        if (process != null && !process.HasExited)
                        {
                            if (process == CurrentProcess)
                                ProcessExistWasIntentional = true;

                            OsUtils.KillProcessTree(process.Id);
                        }
                    }
                    catch (Exception e)
                    {
                        Logger.Log($"Failed to kill process tree: {e.Message}", true);
                    }
                }
            }
        }

        public static async Task CheckStillRunning()
        {
            while (CurrentProcess != null && !CurrentProcess.HasExited)
                await Task.Delay(1);

            if (TextToImage.Canceled)
                return;

            if (ProcessExistWasIntentional)
            {
                ProcessExistWasIntentional = false;
            }
            else
            {
                string log = "...\n" + string.Join("\n", Logger.GetSessionLogLastLines(Constants.Lognames.Sd, 8));
                TextToImage.Cancel($"Process has exited unexpectedly.\n\nOutput:\n{log}");
            }
        }
    }
}
