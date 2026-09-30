using System.Collections;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace TEngine.Localization
{
	// This class is used to spawn coroutines from outside of MonoBehaviors
	public class CoroutineManager : MonoBehaviour 
	{
		static CoroutineManager pInstance
		{
			get{
				if (mInstance==null)
				{
					GameObject GO = new GameObject( "_Coroutiner" );
                    GO.hideFlags = HideFlags.HideAndDontSave;
                    mInstance = GO.AddComponent<CoroutineManager>();
                    if (Application.isPlaying)
                        DontDestroyOnLoad(GO);
                }
                return mInstance;
			}
		}
        static CoroutineManager mInstance;


        private void Awake()
        {
            if (Application.isPlaying)
                DontDestroyOnLoad(gameObject);
        }

        public static Coroutine Start(IEnumerator coroutine)
		{
			#if UNITY_EDITOR
				// Special case to allow coroutines to run in the Editor
				if (!Application.isPlaying)
				{
					EditorApplication.CallbackFunction delg=null;
					delg = delegate
					{
						if (!coroutine.MoveNext())
							EditorApplication.update -= delg;
					};
					EditorApplication.update += delg;
					return null;
				}
			#endif

			return pInstance.StartCoroutine(coroutine);
		}
        
        #if UNITY_EDITOR
        // store editor-mode callbacks so we can remove them later
        static Dictionary<int, EditorApplication.CallbackFunction> s_EditorCoroutines = new Dictionary<int, EditorApplication.CallbackFunction>();
        static int s_nextEditorCoroutineId = 0;
#endif

        /// <summary>
        /// A handle returned to caller. Contains either a runtime Coroutine (when playing)
        /// or an editor-side id (when in editor and not playing).
        /// </summary>
        public class CoroutineHandle
        {
            internal Coroutine runtimeCoroutine;   // valid in play mode
#if UNITY_EDITOR
            internal int editorId = 0;             // valid in editor mode
#endif
            public bool IsPlayingMode => runtimeCoroutine != null;
#if UNITY_EDITOR
            public bool IsEditorMode => editorId != 0 && !Application.isPlaying;
#endif
        }

        /// <summary>
        /// Start a coroutine and get a handle that can be used to stop it later.
        /// Use Stop(handle) to cancel.
        /// </summary>
        public static CoroutineHandle StartManaged(IEnumerator coroutine)
        {
            if (coroutine == null) return null;

            // Play mode: use normal Unity coroutine and return handle with runtime Coroutine
            if (Application.isPlaying)
            {
                var ch = new CoroutineHandle();
                ch.runtimeCoroutine = pInstance.StartCoroutine(coroutine);
                return ch;
            }

            // Editor mode (not playing): use EditorApplication.update and record delegate so we can stop it
#if UNITY_EDITOR
            int id = ++s_nextEditorCoroutineId;
            EditorApplication.CallbackFunction delg = null;

            // wrap the IEnumerator so we can detect completion and unregister
            delg = delegate
            {
                bool hasNext = coroutine.MoveNext();
                if (!hasNext)
                {
                    // finished -> unregister
                    EditorApplication.update -= delg;
                    s_EditorCoroutines.Remove(id);
                }
            };

            s_EditorCoroutines[id] = delg;
            EditorApplication.update += delg;

            var handle = new CoroutineHandle();
            handle.editorId = id;
            return handle;
#else
            return null; // should not reach here normally
#endif
        }

        /// <summary>
        /// Stop a coroutine previously started with StartManaged.
        /// Safe to call with null.
        /// </summary>
        public static void Stop(CoroutineHandle handle)
        {
            if (handle == null) return;

            // runtime coroutine
            if (handle.runtimeCoroutine != null && Application.isPlaying)
            {
                pInstance.StopCoroutine(handle.runtimeCoroutine);
                handle.runtimeCoroutine = null;
            }
#if UNITY_EDITOR
            // editor coroutine
            if (!Application.isPlaying && handle.editorId != 0)
            {
                if (s_EditorCoroutines.TryGetValue(handle.editorId, out var delg))
                {
                    EditorApplication.update -= delg;
                    s_EditorCoroutines.Remove(handle.editorId);
                }
                handle.editorId = 0;
            }
#endif
        }

        /// <summary>
        /// Convenience: stop by passing the Unity Coroutine (works only in Play mode).
        /// </summary>
        public static void Stop(Coroutine coroutine)
        {
            if (coroutine == null) return;
            if (Application.isPlaying)
            {
                pInstance.StopCoroutine(coroutine);
            }
            else
            {
                // nothing to do in edit mode for raw Coroutine (we don't create them there)
            }
        }
	}
}
