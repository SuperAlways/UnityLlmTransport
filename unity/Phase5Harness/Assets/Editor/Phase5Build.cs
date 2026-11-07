using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;

public static class Phase5Build
{
    public static void BuildMono()
    {
        Build("mono", ScriptingImplementation.Mono2x);
    }

    public static void BuildIl2cpp()
    {
        Build("il2cpp", ScriptingImplementation.IL2CPP);
    }

    private static void Build(string name, ScriptingImplementation backend)
    {
        var sceneDir = "Assets/Scenes";
        Directory.CreateDirectory(sceneDir);
        var scenePath = sceneDir + "/Phase5.unity";

        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var runner = new GameObject("Phase5Runner");
        runner.AddComponent<Phase5Runner>();
        EditorSceneManager.SaveScene(scene, scenePath);

        EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(scenePath, true) };
        PlayerSettings.SetScriptingBackend(BuildTargetGroup.Standalone, backend);

        var outputDir = Path.Combine(@"D:\UnitySpikes\Builds\Phase5", Application.unityVersion, name);
        Directory.CreateDirectory(outputDir);
        var outputExe = Path.Combine(outputDir, "Phase5.exe");

        var report = BuildPipeline.BuildPlayer(
            new[] { scenePath },
            outputExe,
            BuildTarget.StandaloneWindows64,
            BuildOptions.Development);

        Debug.Log("PHASE5_BUILD " + name + " " + report.summary.result +
                  " errors=" + report.summary.totalErrors + " output=" + outputExe);
        EditorApplication.Exit(report.summary.result == BuildResult.Succeeded ? 0 : 1);
    }
}
