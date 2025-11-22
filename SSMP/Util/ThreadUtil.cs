using System;
using System.Collections.Generic;
using SSMP.Logging;
using UnityEngine;

namespace SSMP.Util;

/// <summary>
/// Class for utilities regarding threading.
/// </summary>
internal static class ThreadUtil {
    private static readonly object Lock = new object();
    private static readonly List<Action> ActionsToRun = new List<Action>();
    private static Dispatcher? _dispatcher;

    /// <summary>
    /// Instantiate the ThreadUtil dispatcher if we are in a Unity environment.
    /// </summary>
    public static void Instantiate() {
        try {
            var threadUtilObject = new GameObject("ThreadUtil");
            _dispatcher = threadUtilObject.AddComponent<Dispatcher>();
            UnityEngine.Object.DontDestroyOnLoad(threadUtilObject);
        } catch (Exception) {
            // Ignore exceptions, as this likely means we are not in a Unity environment
        }
    }

    /// <summary>
    /// Runs the given action on the main thread of Unity.
    /// </summary>
    public static void RunActionOnMainThread(Action action) {
        if (_dispatcher == null) {
            // If the dispatcher is not instantiated, we simply run the action immediately
            // This is the case for the server or if the dispatcher failed to instantiate
            Try(action, "ThreadUtil.RunActionOnMainThread");
            return;
        }
        
        lock (Lock) {
            ActionsToRun.Add(action);
        }
    }

    /// <summary>
    /// Runs the given action on the main Unity thread wrapped in a try/catch with logging.
    /// </summary>
    public static void RunSafeOnMainThread(Action action, string? context = null) {
        RunActionOnMainThread(() => Try(action, context));
    }

    /// <summary>
    /// Execute an action and log any exception without throwing.
    /// </summary>
    public static void Try(Action action, string? context = null) {
        try {
            action();
        } catch (Exception e) {
            SSMP.Logging.Logger.Error(string.IsNullOrEmpty(context) 
                ? $"Unhandled exception in action: {e}" 
                : $"Error in {context}: {e}");
        }
    }

    /// <summary>
    /// Execute a function and return defaultValue on exception; logs the error.
    /// </summary>
    public static T Try<T>(Func<T> func, string? context = null, T defaultValue = default!) {
        try {
            return func();
        } catch (Exception e) {
            SSMP.Logging.Logger.Error(string.IsNullOrEmpty(context) 
                ? $"Unhandled exception in func: {e}" 
                : $"Error in {context}: {e}");
            return defaultValue;
        }
    }

    /// <summary>
    /// Safely invoke a parameterless event/delegate.
    /// </summary>
    public static void InvokeSafe(Action? action, string? context = null) {
        if (action != null) Try(action, context);
    }

    /// <summary>
    /// Safely invoke a single-parameter event/delegate.
    /// </summary>
    public static void InvokeSafe<T>(Action<T>? action, T arg, string? context = null) {
        if (action != null) Try(() => action(arg), context);
    }
    
    /// <summary>
    /// Internal MonoBehaviour class to dispatch actions on the main thread.
    /// </summary>
    public class Dispatcher : MonoBehaviour {
        public void Update() {
            List<Action> actions;
            lock (Lock) {
                actions = new List<Action>(ActionsToRun);
                ActionsToRun.Clear();
            }

            foreach (var action in actions) {
                action.Invoke();
            }
        }
    }
}
