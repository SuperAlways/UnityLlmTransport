using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;

public static class Phase3Build
{
    public static void BuildMono()
    {
        var sceneDir = "Assets/Scenes";
        Directory.CreateDirectory(sceneDir);
        var scenePath = sceneDir + "/Phase3.unity";

        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var runner = new GameObject("Phase3Runner");
        runner.AddComponent<Phase3Runner>();
        EditorSceneManager.SaveScene(scene, scenePath);

        EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(scenePath, true) };
        PlayerSettings.SetScriptingBackend(BuildTargetGroup.Standalone, ScriptingImplementation.Mono2x);

        var outputDir = Path.Combine(@"D:\UnitySpikes\Builds\Phase3", Application.unityVersion, "mono");
        Directory.CreateDirectory(outputDir);
        var outputExe = Path.Combine(outputDir, "Phase3.exe");

        var report = BuildPipeline.BuildPlayer(
            new[] { scenePath },
            outputExe,
            BuildTarget.StandaloneWindows64,
            BuildOptions.Development);

        Debug.Log("PHASE3_BUILD mono " + report.summary.result +
                  " errors=" + report.summary.totalErrors + " output=" + outputExe);
        EditorApplication.Exit(report.summary.result == BuildResult.Succeeded ? 0 : 1);
    }
}
