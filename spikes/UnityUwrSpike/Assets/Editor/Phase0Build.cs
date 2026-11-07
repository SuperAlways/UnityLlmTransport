using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

public static class Phase0Build
{
    public static void BuildMono() => Build("mono", ScriptingImplementation.Mono2x);
    public static void BuildIl2cpp() => Build("il2cpp", ScriptingImplementation.IL2CPP);

    private static void Build(string name, ScriptingImplementation backend)
    {
        var sceneDir = "Assets/Scenes";
        Directory.CreateDirectory(sceneDir);
        var scenePath = sceneDir + "/Phase0.unity";

        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var runner = new GameObject("Phase0Runner");
        runner.AddComponent<Phase0Runner>();
        EditorSceneManager.SaveScene(scene, scenePath);

        EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(scenePath, true) };
        PlayerSettings.SetScriptingBackend(BuildTargetGroup.Standalone, backend);

        var outputDir = Path.Combine(@"D:\UnitySpikes\Builds", Application.unityVersion, name);
        Directory.CreateDirectory(outputDir);
        var outputExe = Path.Combine(outputDir, "Phase0.exe");

        var report = BuildPipeline.BuildPlayer(
            new[] { scenePath },
            outputExe,
            BuildTarget.StandaloneWindows64,
            BuildOptions.Development);

        Debug.Log("PHASE0_BUILD " + name + " " + report.summary.result +
                  " errors=" + report.summary.totalErrors + " output=" + outputExe);

        EditorApplication.Exit(report.summary.result == BuildResult.Succeeded ? 0 : 1);
    }
}
