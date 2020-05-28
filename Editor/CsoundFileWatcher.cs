﻿//#define FILEWATCHER_ON


#if FILEWATCHER_ON
#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

[InitializeOnLoad]
public class CsoundFileWatcher
{
    static CsoundUnity[] csoundInstances;
    static List<FileSystemWatcher> fswInstances = new List<FileSystemWatcher>();
    static Dictionary<string, List<CsoundUnity>> _pathsCsdListDict = new Dictionary<string, List<CsoundUnity>>();
    static Dictionary<string, DateTime> _lastFileChangeDict = new Dictionary<string, DateTime>();
    static Queue<Action> _actionsQueue = new Queue<Action>();
    static float _lastUpdate;
    static float _timeBetweenUpdates = .2f;
    static bool _executeActions = true;
    static bool _quitting = false;

    static CsoundFileWatcher()
    {
        //(UN)COMMENT THE FOLLOWING LINES TO RESTORE/DISABLE FILE WATCHING
        FindInstancesAndStartWatching();
        EditorApplication.hierarchyChanged += OnHierarchyChanged;
        EditorApplication.update += EditorUpdate;
        EditorApplication.quitting += EditorQuitting;
        EditorApplication.playModeStateChanged += EditorPlayModeStateChanged;
    }

    private static void EditorPlayModeStateChanged(PlayModeStateChange obj)
    {
        switch (obj)
        {
            case PlayModeStateChange.EnteredEditMode:
                _executeActions = true;
                break;
            case PlayModeStateChange.ExitingEditMode:
                _executeActions = false;
                break;
            case PlayModeStateChange.EnteredPlayMode:
                break;
            case PlayModeStateChange.ExitingPlayMode:
                break;
        }
    }

    private static void EditorQuitting()
    {
        _executeActions = false;
        _quitting = true;
        fswInstances.Clear();
        EditorApplication.hierarchyChanged -= OnHierarchyChanged;
        EditorApplication.update -= EditorUpdate;
        EditorApplication.quitting -= EditorQuitting;
        EditorApplication.playModeStateChanged -= EditorPlayModeStateChanged;
    }

    private static void EditorUpdate()
    {
        var startTime = Time.realtimeSinceStartup;
        if (startTime > _lastUpdate + _timeBetweenUpdates)
            lock (_actionsQueue)
            {
                if (_quitting)
                    _actionsQueue.Clear();

                if (_executeActions)
                    while (_actionsQueue.Count > 0)
                    {
                        var action = _actionsQueue.Dequeue();
                        if (action == null)
                            continue;
                        Debug.Log("fileWatcher: action!");
                        action();
                    }
                _lastUpdate = Time.realtimeSinceStartup;
            }
    }

    private static void StartWatching(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath)) return;
        // Debug.Log($"fileWatcher: START WATCHING {filePath}");
        FileSystemWatcher watcher = new FileSystemWatcher();
        watcher.Path = Path.GetDirectoryName(filePath);
        watcher.Filter = Path.GetFileName(filePath);
        watcher.NotifyFilter = NotifyFilters.LastWrite;
        watcher.Changed += Watcher_Changed;
        watcher.EnableRaisingEvents = true;
        fswInstances.Add(watcher);
    }

    private static void Watcher_Changed(object sender, System.IO.FileSystemEventArgs e)
    {
        if (e.ChangeType == WatcherChangeTypes.Changed)
        {
            var fileChanged = e.FullPath;
            //Debug.Log("fileWatcher: fileChanged! " + fileChanged);

            if (!_lastFileChangeDict.ContainsKey(fileChanged)) return;

            var lastChange = _lastFileChangeDict[fileChanged];
            //Debug.Log($"fileWatcher: {fileChanged} last change was at {lastChange}");
            //ignore duplicate calls detected by FileSystemWatcher on file save
            if (DateTime.Now.Subtract(lastChange).TotalMilliseconds < 1000)
            {
                //Debug.Log($"fileWatcher: IGNORING CHANGE AT {DateTime.Now}");
                return;
            }

            Debug.Log($"fileWatcher: CHANGE! {e.Name} changed at {DateTime.Now}, last change was {lastChange}");
            _lastFileChangeDict[fileChanged] = DateTime.Now;

            var result = TestCsoundForErrors(fileChanged);
            Debug.Log(result != 0 ?
                        $"fileWatcher: Heuston we have a problem... Disabling all CsoundUnity instances for file: {fileChanged}" :
                        "<color=green>Csound file has no errors!</color>"
            );

            //Debug.Log($"fileWatcher: CsoundUnity instances associated with this file: {_pathsCsdListDict[fileChanged].Count}");
            var list = _pathsCsdListDict[fileChanged];
            for (var i = 0; i < list.Count; i++)
            {
                var csound = list[i];
                lock (_actionsQueue)
                    _actionsQueue.Enqueue(() =>
                    {
                        if (result != 0)
                        {
                            csound.enabled = false;
                        }
                        else
                        {
                            csound.enabled = true;
                            //file changed but guid stays the same
                            csound.SetCsd(csound.csoundFileGUID);
                        }

                        EditorUtility.SetDirty(csound.gameObject);
                    });
            }
        }
    }

    static int TestCsoundForErrors(string file)
    {
#if UNITY_EDITOR_WIN
        var csoundProcess = new System.Diagnostics.Process
        {
            StartInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "csound.exe",
                Arguments = file,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            }
        };

        csoundProcess.Start();
        while (!csoundProcess.StandardOutput.EndOfStream)
        {
            string line = csoundProcess.StandardOutput.ReadLine();
            Debug.Log(line);// do something with line
        }

        return csoundProcess.ExitCode;

#elif UNITY_EDITOR_OSX
        return 0;
#endif
    }

    static void OnHierarchyChanged()
    {
        if (Application.isPlaying) return;

        // Debug.Log("fileWatcher: OnHierarchyChanged");
        foreach (var fsw in fswInstances)
        {
            fsw.Changed -= Watcher_Changed;
        }
        fswInstances.Clear();
        FindInstancesAndStartWatching();
    }

    private static void FindInstancesAndStartWatching()
    {
        csoundInstances = (CsoundUnity[])Resources.FindObjectsOfTypeAll(typeof(CsoundUnity));//as CsoundUnity[];
        _pathsCsdListDict.Clear();
        _lastFileChangeDict.Clear();

        // Debug.Log($"fileWatcher: found {csoundInstances.Length} instance(s) of csound");
        foreach (var csd in csoundInstances)
        {
            // get csd file path from the CsoundUnity instance and check if it exists
            var filePath = csd.GetFilePath();
            // Debug.Log("fileWatcher: FILEPATH " + filePath);
            if (!File.Exists(filePath)) continue;

            if (TestCsoundForErrors(filePath) != 0)
            {
                Debug.LogError($"fileWatcher: Heuston we have a problem... CsoundUnity disabled for file: {filePath}");
                csd.enabled = false;
            }
            else
            {
                // Debug.Log("<color=green>fileWatcher: Csound file has no errors!</color>");
                csd.enabled = true;
            }

            //Debug.Log("fileWatcher: found a csd asset at path: " + filePath);
            if (_pathsCsdListDict.ContainsKey(filePath))
            {
                // Debug.Log("fileWatcher: csd is already watched, add the csound script to the list of CsoundUnity instances to update");
                _pathsCsdListDict[filePath].Add(csd);
                _lastFileChangeDict[filePath] = DateTime.Now;
            }
            else
            {
                // Debug.Log("fileWatcher: new csd, creating a list of attached CsoundUnity instances");
                var list = new List<CsoundUnity> { csd };
                _pathsCsdListDict.Add(filePath, list);
                _lastFileChangeDict.Add(filePath, DateTime.Now);
                StartWatching(filePath);
                // Debug.Log($"fileWatcher: added {filePath} to fileWatch");
            }
        }
    }
}

#endif 
#endif
